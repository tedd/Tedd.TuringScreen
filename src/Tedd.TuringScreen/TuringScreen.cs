using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.TuringScreen;

public sealed class TuringScreen : IDisposable
{
    // ########################################################################
    // 1. CONFIGURATION
    // ########################################################################

    private const int HwWidth = 320;
    private const int HwHeight = 480;
    private const int HeuristicCostPerPixel = 12;

    // DMA LIMIT: 320 * 40 = 12,800 pixels. Safe for 16-bit counters.
    private const int MaxBlockHeight = 40;
    // BENCHMARK CONFIGURATION:
    private static readonly int[] BenchmarkSteps =
        { 1000, 1200, 1400, 1500, 1600, 1700, 1800, 2000, 2500 };

    // PROTOCOL COMMANDS
    private const byte CmdReset = 101;
    private const byte CmdClear = 102;
    private const byte CmdScreenOff = 108;
    private const byte CmdScreenOn = 109;
    private const byte CmdBrightness = 110;
    private const byte CmdOrientation = 121;
    private const byte CmdDraw = 197;

    // ########################################################################
    // 2. STATE
    // ########################################################################

    private readonly int _comPortName;
    private readonly int _baudRate;
    private SerialPort? _port;
    private Stream? _baseStream; // Optimization: Cache the base stream

    private readonly byte[] _commandBuffer = new byte[16];
    private ScreenBuffer _screenBuffer;

    private int _cachedWidth;
    private int _cachedHeight;
    private bool _useSoftwareRotation;

    private int _lastBrightness = 100;
    private byte _lastOrientationIndex = 0;

    // ########################################################################
    // 3. PUBLIC API
    // ########################################################################

    public ScreenOrientation Orientation { get; private set; } = ScreenOrientation.Portrait;
    public int Width => _cachedWidth;
    public int Height => _cachedHeight;

    public TuringScreen(int comPort, int baudRate = 921600)
    {
        _comPortName = comPort;
        _baudRate = baudRate;
        _screenBuffer = new ScreenBuffer(HwWidth, HwHeight);
        _cachedWidth = HwWidth;
        _cachedHeight = HwHeight;
        Connect();
    }

    public void Dispose() => Close();

    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        var color = ScreenBuffer.FullRgbToColor565(r, g, b);
        _screenBuffer[x, y] = color;
        WritePixelImmediate(CmdDraw, x, y, color);
    }

    public void DisplayBuffer(int x, int y, ScreenBuffer buffer)
    {
        WriteSmartCommand(CmdDraw, x, y, buffer.Width, buffer.Height, buffer.Buffer);
    }

    public void Clear()
    {
        WriteCommand(CmdClear);
        _screenBuffer.Clear(Color656.White);
    }

    public void Reset()
    {
        WriteCommand(CmdReset);
        Close();
        Connect(waitForConnect: 5000);
    }

    public void ScreenOff() => WriteCommand(CmdScreenOff);
    public void ScreenOn() => WriteCommand(CmdScreenOn);

    public void SetBrightness(int level)
    {
        level = Math.Clamp(level, 0, 100);
        _lastBrightness = level;
        WriteCommand(CmdBrightness, level);
    }

    public void SetOrientation(ScreenOrientation orientation)
    {
        Orientation = orientation;

        if (orientation is ScreenOrientation.Landscape or ScreenOrientation.ReverseLandscape)
        {
            _cachedWidth = HwHeight;
            _cachedHeight = HwWidth;
            _useSoftwareRotation = true;
        }
        else
        {
            _cachedWidth = HwWidth;
            _cachedHeight = HwHeight;
            _useSoftwareRotation = false;
        }

        _lastOrientationIndex = (byte)orientation;
        WriteOrientationCommand(CmdOrientation, _lastOrientationIndex);

        _screenBuffer = new ScreenBuffer(_cachedWidth, _cachedHeight);
        Clear();
    }

    // ########################################################################
    // 4. RENDERING LOGIC (AVX2)
    // ########################################################################

    private void WriteSmartCommand(byte command, int left, int top, int width, int height, byte[] data)
    {
        var sourceSpan = MemoryMarshal.Cast<byte, ushort>(data.AsSpan());
        var bufferSpan = MemoryMarshal.Cast<byte, ushort>(_screenBuffer.Buffer.AsSpan());

        int screenWidth = _cachedWidth;
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        int changeCount = 0;

        bool useAvx2 = Avx2.IsSupported;

        ref ushort sourceHead = ref MemoryMarshal.GetReference(sourceSpan);
        ref ushort bufferHead = ref MemoryMarshal.GetReference(bufferSpan);

        // --- 1. DIFF STEP ---
        for (int y = 0; y < height; y++)
        {
            int globalY = top + y;
            int rowOffsetSrc = y * width;
            int rowOffsetDst = globalY * screenWidth + left;

            ref ushort rowSrc = ref Unsafe.Add(ref sourceHead, rowOffsetSrc);
            ref ushort rowDst = ref Unsafe.Add(ref bufferHead, rowOffsetDst);

            int x = 0;

            // AVX2 Vectorized Comparison
            if (useAvx2 && width >= 16)
            {
                int vecLimit = width - 16;
                for (; x <= vecLimit; x += 16)
                {
                    Vector256<short> vSrc = Vector256.LoadUnsafe(ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref rowSrc, x)));
                    Vector256<short> vDst = Vector256.LoadUnsafe(ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref rowDst, x)));

                    // CompareEqual returns 0xFFFF for equal, 0x0000 for not equal
                    Vector256<short> vEq = Avx2.CompareEqual(vSrc, vDst);
                    int mask = Avx2.MoveMask(vEq.AsByte());

                    if (mask == -1) continue; // All bytes identical

                    // Identify differing pixels
                    int diffMask = ~mask;
                    while (diffMask != 0)
                    {
                        int bitIndex = BitOperations.TrailingZeroCount(diffMask);
                        int pixelIdx = bitIndex / 2;
                        int px = x + pixelIdx;

                        changeCount++;
                        if (px < minX) minX = px;
                        if (px > maxX) maxX = px;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;

                        // Clear the 2 bits for this pixel so we find the next one
                        diffMask &= ~(3 << (pixelIdx * 2));
                    }
                }
            }

            // Scalar Cleanup
            for (; x < width; x++)
            {
                if (Unsafe.Add(ref rowDst, x) != Unsafe.Add(ref rowSrc, x))
                {
                    changeCount++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (changeCount == 0) return;

        // --- 2. DECISION STEP ---
        int diffW = maxX - minX + 1;
        int diffH = maxY - minY + 1;
        long boxCost = 6 + (diffW * diffH * 2);
        long pointCost = changeCount * HeuristicCostPerPixel;

        var sourcePixels = MemoryMarshal.Cast<ushort, Color656>(sourceSpan);
        var bufferPixels = MemoryMarshal.Cast<ushort, Color656>(bufferSpan);

        if (pointCost < boxCost)
        {
            // STRATEGY A: Pixel-by-Pixel (Sparse)
            for (int y = 0; y < height; y++)
            {
                int globalY = top + y;
                int rowOffsetSrc = y * width;
                int rowOffsetDst = globalY * screenWidth + left;

                for (int x = 0; x < width; x++)
                {
                    var newVal = sourcePixels[rowOffsetSrc + x];
                    if (bufferPixels[rowOffsetDst + x] != newVal)
                    {
                        bufferPixels[rowOffsetDst + x] = newVal;
                        WritePixelImmediate(command, left + x, globalY, newVal);
                    }
                }
            }
        }
        else
        {
            // STRATEGY B: Dirty Rectangle (Bulk)

            // Sync Backbuffer
            for (int y = minY; y <= maxY; y++)
            {
                int globalY = top + y;
                int rowOffsetSrc = y * width;
                int rowOffsetDst = globalY * screenWidth + left;

                var srcSlice = sourcePixels.Slice(rowOffsetSrc + minX, diffW);
                var dstSlice = bufferPixels.Slice(rowOffsetDst + minX, diffW);
                srcSlice.CopyTo(dstSlice);
            }

            // Transmit with Tiling
            int currentY = minY;
            int remainingH = diffH;

            while (remainingH > 0)
            {
                int tileH = Math.Min(remainingH, MaxBlockHeight);

                if (_useSoftwareRotation)
                {
                    SendRotatedPayload(command, left + minX, top + currentY, diffW, tileH, sourcePixels, width);
                }
                else
                {
                    SendRectangularUpdate(command, left + minX, top + currentY, diffW, tileH, sourcePixels, width, minX, currentY);
                }

                currentY += tileH;
                remainingH -= tileH;
            }
        }
    }

    private void SendRotatedPayload(byte command, int logX, int logY, int logW, int logH, ReadOnlySpan<Color656> sourceData, int sourceStride)
    {
        int physX = logY; int physY = logX;
        int physW = logH; int physH = logW;
        int payloadSize = physW * physH * 2;

        byte[] rent = ArrayPool<byte>.Shared.Rent(payloadSize);

        try
        {
            var packed = MemoryMarshal.Cast<byte, Color656>(rent.AsSpan(0, payloadSize));
            ref Color656 packedHead = ref MemoryMarshal.GetReference(packed);
            ref Color656 sourceHead = ref MemoryMarshal.GetReference(sourceData);
            int pIndex = 0;

            // Software Rotation: Transpose logical X/Y to physical Y/X
            for (int row = 0; row < physH; row++)
            {
                int lx = logX + row;
                for (int col = 0; col < physW; col++)
                {
                    int srcIdx = (logY + col) * sourceStride + lx;
                    Unsafe.Add(ref packedHead, pIndex++) = Unsafe.Add(ref sourceHead, srcIdx);
                }
            }

            PrepareHeader(command, physX, physY, physW, physH);
            // Optimization: Write header + payload in one call? 
            // Better to just ensure Stream handles it.
            SafeWrite(_commandBuffer, 6, rent, payloadSize);
        }
        finally { ArrayPool<byte>.Shared.Return(rent); }
    }

    private void SendRectangularUpdate(byte command, int x, int y, int w, int h, ReadOnlySpan<Color656> fullSource, int fullWidth, int offX, int offY)
    {
        int payloadSize = w * h * 2;
        byte[] rent = ArrayPool<byte>.Shared.Rent(payloadSize);
        try
        {
            // Block Copy row by row
            var packed = MemoryMarshal.Cast<byte, Color656>(rent.AsSpan(0, payloadSize));
            for (int row = 0; row < h; row++)
            {
                var src = fullSource.Slice((offY + row) * fullWidth + offX, w);
                var dst = packed.Slice(row * w, w);
                src.CopyTo(dst);
            }
            PrepareHeader(command, x, y, w, h);
            SafeWrite(_commandBuffer, 6, rent, payloadSize);
        }
        finally { ArrayPool<byte>.Shared.Return(rent); }
    }

    private void WritePixelImmediate(byte command, int x, int y, Color656 color)
    {
        int hwX = x; int hwY = y;
        if (_useSoftwareRotation) { hwX = y; hwY = x; }

        int ex = hwX; int ey = hwY;
        _commandBuffer[0] = (byte)(hwX >> 2);
        _commandBuffer[1] = (byte)(((hwX & 3) << 6) + (hwY >> 4));
        _commandBuffer[2] = (byte)(((hwY & 15) << 4) + (ex >> 6));
        _commandBuffer[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        _commandBuffer[4] = (byte)(ey & 255);
        _commandBuffer[5] = command;
        MemoryMarshal.Write(_commandBuffer.AsSpan(6, 2), ref color);
        SafeWrite(_commandBuffer, 8);
    }

    // ########################################################################
    // 5. I/O OPTIMIZATION (THE FIX)
    // ########################################################################

    private void PrepareHeader(byte command, int x, int y, int w, int h)
    {
        var ex = x + w - 1;
        var ey = y + h - 1;
        _commandBuffer[0] = (byte)(x >> 2);
        _commandBuffer[1] = (byte)(((x & 3) << 6) + (y >> 4));
        _commandBuffer[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        _commandBuffer[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        _commandBuffer[4] = (byte)(ey & 255);
        _commandBuffer[5] = command;
    }

    private void WriteCommand(byte command)
    {
        _commandBuffer[5] = command;
        SafeWrite(_commandBuffer, 6);
    }

    private void WriteCommand(byte command, int level)
    {
        _commandBuffer[0] = (byte)(level >> 2);
        _commandBuffer[1] = (byte)((level & 3) << 6);
        _commandBuffer[5] = command;
        SafeWrite(_commandBuffer, 6);
    }

    private void WriteOrientationCommand(byte command, byte orientation)
    {
        int w = HwWidth; int h = HwHeight;
        _commandBuffer[5] = command;
        _commandBuffer[6] = (byte)(orientation + 100);
        _commandBuffer[7] = (byte)(w >> 8);
        _commandBuffer[8] = (byte)(w & 255);
        _commandBuffer[9] = (byte)(h >> 8);
        _commandBuffer[10] = (byte)(h & 255);
        SafeWrite(_commandBuffer, 11);
    }

    private void Connect(int waitForConnect = 0)
    {
        Close();
        var sw = Stopwatch.StartNew();
        while (waitForConnect < 1 || sw.ElapsedMilliseconds < waitForConnect)
        {
            try
            {
                _port = new SerialPort("COM" + _comPortName)
                {
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = 1000,
                    BaudRate = _baudRate,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Parity = Parity.None,
                    // CRITICAL: Set the OS buffer huge. 
                    // This allows us to write an entire Frame (300KB) without blocking in user code.
                    WriteBufferSize = 524288 // 512 KB
                };
                _port.Open();

                // CRITICAL: Bypass the SerialPort wrapper for bulk writes to avoid overhead
                _baseStream = _port.BaseStream;

                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                if (_useSoftwareRotation || Orientation != ScreenOrientation.Portrait)
                {
                    int w = HwWidth; int h = HwHeight;
                    _commandBuffer[5] = CmdOrientation;
                    _commandBuffer[6] = (byte)(_lastOrientationIndex + 100);
                    _commandBuffer[7] = (byte)(w >> 8);
                    _commandBuffer[8] = (byte)(w & 255);
                    _commandBuffer[9] = (byte)(h >> 8);
                    _commandBuffer[10] = (byte)(h & 255);
                    _baseStream.Write(_commandBuffer, 0, 11);
                }
                break;
            }
            catch (IOException)
            {
                if (sw.ElapsedMilliseconds >= waitForConnect) throw;
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                // We don't have access while computer is locked ... presumably?
                Thread.Sleep(1000);
            }
        }
    }

    private void Close()
    {
        _baseStream = null;
        if (_port is { IsOpen: true }) try { _port.Close(); } catch { }
        _port = null;
    }

    private void SafeWrite(byte[] header, int headerLen, byte[]? payload = null, int payloadLen = 0)
    {
        // 1. Direct Stream Write
        // We do NOT manually check bytesToWrite or sleep. We rely on the 512KB Driver Buffer.
        // If the buffer fills, BaseStream.Write will block automatically (efficiently), 
        // rather than us spinning in a loop consuming CPU.

        try
        {
            if (_baseStream == null) throw new IOException("Disconnected");

            // Write Header
            _baseStream.Write(header, 0, headerLen);

            // Write Payload (One giant chunk)
            if (payload != null && payloadLen > 0)
            {
                _baseStream.Write(payload, 0, payloadLen);
            }
        }
        catch (IOException)
        {
            RecoverConnection();
        }
        catch (ObjectDisposedException)
        {
            // Ignore or signal disconnect
        }
    }

    private void RecoverConnection()
    {
        try
        {
            Debug.WriteLine("--- RECOVERING CONNECTION ---");
            Close();
            Connect(waitForConnect: 1000);
            if (_baseStream != null)
            {
                // Restore State
                _commandBuffer[5] = CmdReset;
                _baseStream.Write(_commandBuffer, 0, 6);
                Thread.Sleep(50);

                _commandBuffer[5] = CmdClear;
                _baseStream.Write(_commandBuffer, 0, 6);

                _commandBuffer[0] = (byte)(_lastBrightness >> 2);
                _commandBuffer[1] = (byte)((_lastBrightness & 3) << 6);
                _commandBuffer[5] = CmdBrightness;
                _baseStream.Write(_commandBuffer, 0, 6);

                // Trigger full refresh (omitted for brevity, same logic as before)
            }
        }
        catch { }
    }

    // ########################################################################
    // 9. BENCHMARKING
    // ########################################################################

    public void RunBenchmark()
    {
        Console.WriteLine("--- PRECISION STRATEGY BENCHMARK ---");
        Reset();
        Clear();

        const int w = 100;
        const int h = 100;
        const int boxPayloadBytes = w * h * 2;

        // Dummy payload
        byte[] boxPayload = new byte[boxPayloadBytes];

        Console.WriteLine($"{"Count",-8} | {"Point(ms)",-10} | {"Box(ms)",-10} | {"Winner",-8} | {"Calc Mult",-10}");
        Console.WriteLine(new string('-', 60));

        // Warmup
        WritePixelImmediate(CmdDraw, 0, 0, Color656.Red);

        foreach (var count in BenchmarkSteps)
        {
            GC.Collect();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                WritePixelImmediate(CmdDraw, i % w, i / w, Color656.Red);
            sw.Stop();
            double timePoints = sw.Elapsed.TotalMilliseconds;

            Thread.Sleep(50); // Drain

            GC.Collect();
            sw.Restart();
            SendRectangularUpdate(CmdDraw, 0, 0, w, h, MemoryMarshal.Cast<byte, Color656>(boxPayload), w, 0, 0);
            sw.Stop();
            double timeBox = sw.Elapsed.TotalMilliseconds;

            string winner = timePoints < timeBox ? "POINTS" : "BOX";
            int suggestedMult = (int)(boxPayloadBytes / (double)count);

            Console.WriteLine($"{count,-8} | {timePoints,-10:F2} | {timeBox,-10:F2} | {winner,-8} | {suggestedMult,-10}");
        }

        Console.WriteLine("--- DONE ---");
        Console.WriteLine("Update 'HeuristicCostPerPixel' with the 'Calc Mult' value where Point ~= Box.");
    }
}
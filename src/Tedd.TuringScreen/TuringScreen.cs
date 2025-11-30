namespace Tedd.TuringScreen;

using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Numerics; // Required for SIMD
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

public sealed class TuringScreen : IDisposable
{
    // ========================================================================
    // 1. HARDWARE CONSTANTS & CONFIGURATION
    // ========================================================================
    private const int HwWidth = 320;
    private const int HwHeight = 480;

    // Command Codes
    private const byte CmdReset = 101;
    private const byte CmdClear = 102;
    private const byte CmdScreenOff = 108;
    private const byte CmdScreenOn = 109;
    private const byte CmdBrightness = 110;
    private const byte CmdOrientation = 121;
    private const byte CmdDraw = 197;

    /// <summary>
    /// Calibrated Heuristic:
    /// Based on benchmark data, sending 1 scattered pixel costs roughly the same 
    /// latency as sending 12 bytes of contiguous bulk data.
    /// </summary>
    private const int HeuristicCostPerPixel = 12;

    private static readonly int[] BenchmarkSteps =
        { 1000, 1200, 1400, 1500, 1600, 1700, 1800, 2000, 2500 };

    // ========================================================================
    // 2. STATE & BUFFERS
    // ========================================================================
    private readonly int _comPortName;
    private SerialPort? _port;

    // Persistent command buffer (Header + 1 Pixel Payload)
    private readonly byte[] _commandBuffer = new byte[16];

    private ScreenBuffer _screenBuffer;

    // Cached dimensions
    private int _cachedWidth;
    private int _cachedHeight;
    private bool _useSoftwareRotation;

    // Recovery State
    private int _lastBrightness = 100;
    private byte _lastOrientationIndex = 0;

    // ========================================================================
    // 3. PUBLIC PROPERTIES
    // ========================================================================
    public ScreenOrientation Orientation { get; private set; } = ScreenOrientation.Portrait;
    public int Width => _cachedWidth;
    public int Height => _cachedHeight;

    // ========================================================================
    // 4. LIFECYCLE
    // ========================================================================
    public TuringScreen(int comPort)
    {
        _comPortName = comPort;
        _screenBuffer = new ScreenBuffer(HwWidth, HwHeight);
        _cachedWidth = HwWidth;
        _cachedHeight = HwHeight;
        Connect();
    }

    public void Dispose() => Close();

    // ========================================================================
    // 5. DRAWING API
    // ========================================================================
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

    // ========================================================================
    // 6. CONTROL API
    // ========================================================================
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

    // ========================================================================
    // 7. INTERNAL: SIMD SMART RENDERING
    // ========================================================================
    private void WriteSmartCommand(byte command, int left, int top, int width, int height, byte[] data)
    {
        // 1. Cast to ushort Spans (Zero allocation, type interpretation only)
        var sourceSpan = MemoryMarshal.Cast<byte, ushort>(data.AsSpan());
        var bufferSpan = MemoryMarshal.Cast<byte, ushort>(_screenBuffer.Buffer.AsSpan());

        int screenWidth = _cachedWidth;
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        int changeCount = 0;

        // JIT-time constant check
        bool useAvx2 = Avx2.IsSupported;

        // Get "Head" references. These act like pointers (ushort*), but tracked by GC.
        // We get the pointer to the 0th element of the entire buffer to avoid creating new spans inside loops.
        ref ushort sourceHead = ref MemoryMarshal.GetReference(sourceSpan);
        ref ushort bufferHead = ref MemoryMarshal.GetReference(bufferSpan);

        for (int y = 0; y < height; y++)
        {
            int globalY = top + y;

            // Calculate offsets
            int rowOffsetSrc = y * width;
            int rowOffsetDst = globalY * screenWidth + left;

            // Get references to the start of the current row
            // Unsafe.Add(ref T, int elementOffset) compiles to: mov rax, offset; add rax, base;
            ref ushort rowSrc = ref Unsafe.Add(ref sourceHead, rowOffsetSrc);
            ref ushort rowDst = ref Unsafe.Add(ref bufferHead, rowOffsetDst);

            int x = 0;

            // --- AVX2 PATH ---
            if (useAvx2 && width >= 16)
            {
                int vecLimit = width - 16;

                for (; x <= vecLimit; x += 16)
                {
                    // 1. Load Vectors from References
                    // Vector256.LoadUnsafe reads 256 bits starting at the memory address of the ref.
                    // Note: The 'Unsafe' suffix in method name means "No bounds check", not "Requires unsafe keyword".
                    Vector256<short> vSrc = Vector256.LoadUnsafe(ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref rowSrc, x)));
                    Vector256<short> vDst = Vector256.LoadUnsafe(ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref rowDst, x)));

                    // 2. Compare
                    Vector256<short> vEq = Avx2.CompareEqual(vSrc, vDst);

                    // 3. MoveMask
                    int mask = Avx2.MoveMask(vEq.AsByte());

                    if (mask == -1) continue;

                    // 4. Analysis
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

                        diffMask &= ~(3 << (pixelIdx * 2));
                    }
                }
            }

            // --- SCALAR CLEANUP ---
            for (; x < width; x++)
            {
                // Direct reference access (Standard indexer also optimized by JIT, but Unsafe.Add is explicit)
                ushort valSrc = Unsafe.Add(ref rowSrc, x);
                ushort valDst = Unsafe.Add(ref rowDst, x);

                if (valDst != valSrc)
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

        // --- PASS 2: HEURISTIC (CALIBRATED) ---

        int diffW = maxX - minX + 1;
        int diffH = maxY - minY + 1;
        long boxCost = 6 + (diffW * diffH * 2);

        // CALIBRATED MAGIC NUMBER: 12
        // Based on benchmark: 100x100 box (20k bytes) = 125ms. 
        // 1666 points = 125ms.
        // 20000 / 1666 ~= 12.
        long pointCost = changeCount * HeuristicCostPerPixel;

        // --- PASS 3: EXECUTION ---

        // Recast to Color656 for helpers
        var sourcePixels = MemoryMarshal.Cast<ushort, Color656>(sourceSpan);
        var bufferPixels = MemoryMarshal.Cast<ushort, Color656>(bufferSpan);

        if (pointCost < boxCost)
        {
            // STRATEGY A: Sparse Updates
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
            // STRATEGY B: Dirty Rectangle
            // Sync Buffer (Bulk Copy)
            for (int y = minY; y <= maxY; y++)
            {
                int globalY = top + y;
                int rowOffsetSrc = y * width;
                int rowOffsetDst = globalY * screenWidth + left;

                var srcSlice = sourcePixels.Slice(rowOffsetSrc + minX, diffW);
                var dstSlice = bufferPixels.Slice(rowOffsetDst + minX, diffW);
                srcSlice.CopyTo(dstSlice);
            }

            // Transmit
            if (_useSoftwareRotation)
                SendRotatedPayload(command, left + minX, top + minY, diffW, diffH, sourcePixels, width);
            else
                SendRectangularUpdate(command, left + minX, top + minY, diffW, diffH, sourcePixels, width, minX, minY);
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
            int pIndex = 0;
            for (int row = 0; row < physH; row++)
            {
                int lx = logX + row;
                for (int col = 0; col < physW; col++)
                {
                    packed[pIndex++] = sourceData[(logY + col) * sourceStride + lx];
                }
            }
            PrepareHeader(command, physX, physY, physW, physH);
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

    // ========================================================================
    // 8. HELPERS & COMMS
    // ========================================================================
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
                    BaudRate = 115200,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Parity = Parity.None
                };
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                if (_useSoftwareRotation || Orientation != ScreenOrientation.Portrait)
                {
                    // Use cached buffer for clean init
                    int w = HwWidth; int h = HwHeight;
                    _commandBuffer[5] = CmdOrientation;
                    _commandBuffer[6] = (byte)(_lastOrientationIndex + 100);
                    _commandBuffer[7] = (byte)(w >> 8);
                    _commandBuffer[8] = (byte)(w & 255);
                    _commandBuffer[9] = (byte)(h >> 8);
                    _commandBuffer[10] = (byte)(h & 255);
                    _port.Write(_commandBuffer, 0, 11);
                }
                break;
            }
            catch (IOException)
            {
                if (sw.ElapsedMilliseconds >= waitForConnect) throw;
                Thread.Sleep(100);
            }
        }
    }

    private void Close()
    {
        if (_port is { IsOpen: true }) try { _port.Close(); } catch { }
        _port = null;
    }

    private void SafeWrite(byte[] header, int headerLen, byte[]? payload = null, int payloadLen = 0)
    {
        long startTime = 0;

        while (true)
        {
            try
            {
                if (_port == null || !_port.IsOpen) throw new IOException("Disconnected");
                _port.Write(header, 0, headerLen);
                if (payload != null && payloadLen > 0) _port.Write(payload, 0, payloadLen);
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                if (startTime == 0) startTime = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(startTime).TotalSeconds > 2)
                {
                    RecoverConnection();
                    startTime = Stopwatch.GetTimestamp();
                }
                else Thread.Sleep(50);
            }
        }
    }

    private void RecoverConnection()
    {
        try
        {
            Close();
            Connect(waitForConnect: 1000);
            if (_port != null && _port.IsOpen)
            {
                _commandBuffer[0] = (byte)(_lastBrightness >> 2);
                _commandBuffer[1] = (byte)((_lastBrightness & 3) << 6);
                _commandBuffer[5] = CmdBrightness;
                _port.Write(_commandBuffer, 0, 6);

                int w = HwWidth; int h = HwHeight;
                _commandBuffer[5] = CmdOrientation;
                _commandBuffer[6] = (byte)(_lastOrientationIndex + 100);
                _commandBuffer[7] = (byte)(w >> 8);
                _commandBuffer[8] = (byte)(w & 255);
                _commandBuffer[9] = (byte)(h >> 8);
                _commandBuffer[10] = (byte)(h & 255);
                _port.Write(_commandBuffer, 0, 11);

                var span = _screenBuffer.GetSpan();
                // Full refresh required
                if (_useSoftwareRotation)
                    SendRotatedPayload(CmdDraw, 0, 0, _cachedWidth, _cachedHeight, span, _cachedWidth);
                else
                    SendRectangularUpdate(CmdDraw, 0, 0, _cachedWidth, _cachedHeight, span, _cachedWidth, 0, 0);
            }
        }
        catch (Exception) { /* Keep retrying via SafeWrite */ }
    }

    public void RunBenchmark()
    {
        Console.WriteLine("--- STARTING STRATEGY BENCHMARK ---");
        // Ensure we are connected and clean
        Reset();
        Clear();

        // We will test updating a 100x100 region (10,000 pixels total area)
        // We will simulate scattered changes inside this region.
        int width = 100;
        int height = 100;
        int totalPixels = width * height;

        // Create a dummy payload for the box strategy
        byte[] boxPayload = new byte[totalPixels * 2];

        // Define test steps (number of pixels to update)
        int[] pixelCounts = BenchmarkSteps;

        Console.WriteLine($"Region Size: {width}x{height} ({totalPixels} pixels)");
        Console.WriteLine($"Count      | Point(ms)  | Box(ms)   | Winner    ");
        Console.WriteLine(new string('-', 50));

        foreach (var count in pixelCounts)
        {
            // --- TEST 1: STRATEGY A (Sparse / Points) ---
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                // Simulate random writes inside the region
                // We use the direct internal method to bypass the "Smart" logic
                WritePixelImmediate(CmdDraw, i % width, i / width, Color656.Red);
            }
            sw.Stop();
            double timePoints = sw.Elapsed.TotalMilliseconds;

            // Small delay to let buffers drain
            Thread.Sleep(50);

            // --- TEST 2: STRATEGY B (Dirty Rectangle) ---
            // We send the ENTIRE 100x100 box to simulate the cost of updating this region
            // regardless of how many pixels actually changed inside it.
            sw.Restart();

            // Use the internal method directly to force Box Mode
            // We reuse the byte array to avoid allocation noise in the benchmark
            // Note: In real usage, we'd also pay a cost to copy pixels to the buffer, 
            // but here we focus on I/O cost.
            SendRectangularUpdate(CmdDraw, 0, 0, width, height,
                                  MemoryMarshal.Cast<byte, Color656>(boxPayload),
                                  width, 0, 0);

            sw.Stop();
            double timeBox = sw.Elapsed.TotalMilliseconds;

            // --- RESULT ---
            string winner = timePoints < timeBox ? "POINTS" : "BOX";
            Console.WriteLine($"{count,-10} | {timePoints,-10:F2} | {timeBox,-10:F2} | {winner}");
        }

        Console.WriteLine("--- BENCHMARK COMPLETE ---");
    }
}
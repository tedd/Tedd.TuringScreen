using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tedd.TuringScreen;

public class ScreenBuffer
{
    public int Width { get; }
    public int Height { get; }
    public readonly byte[] Buffer;

#pragma warning disable IDE0290
    public ScreenBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Buffer = new byte[width * height * 2];
    }

    public ScreenBuffer(int width, int height, byte[] buffer)
    {
        Width = width;
        Height = height;
        Buffer = buffer;
    }
#pragma warning restore IDE0290

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetByteIndex(int x, int y) => (x + y * Width) * 2;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetColor656Index(int x, int y) => x + y * Width;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<Color656> GetSpan() => MemoryMarshal.Cast<byte, Color656>(Buffer.AsSpan());

    public Color656 this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var span = GetSpan();
            return span[x + y * Width];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            var span = GetSpan();
            span[x + y * Width] = value;
        }
    }


    /// <summary>
    /// Converts the partial range of RGB (0-255) to RGB565 format.
    /// </summary>
    /// <param name="r"></param>
    /// <param name="g"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color656 RgbToColor565(byte r, byte g, byte b)
    {
        return (Color656)(
            ((r & 0xF8) << 8) |   // 5 bits of R
            ((g & 0xFC) << 3) |   // 6 bits of G
            (b >> 3));            // 5 bits of B
    }

    /// <summary>
    /// Converts the full range of RGB (0-255) to RGB565 format.
    /// </summary>
    /// <param name="r"></param>
    /// <param name="g"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color656 FullRgbToColor565(byte r, byte g, byte b)
    {
        int r5 = (r * 31 + 127) / 255;   // round to nearest
        int g6 = (g * 63 + 127) / 255;
        int b5 = (b * 31 + 127) / 255;

        return (Color656)((r5 << 11) | (g6 << 5) | b5);
    }

    public void Clear()
    {
        Array.Clear(Buffer, 0, Buffer.Length);
    }

    public void Clear(Color656 color)
    {
        var buffer = MemoryMarshal.Cast<byte, UInt16>(Buffer.AsSpan());
        buffer.Fill((UInt16)color);
    }

}

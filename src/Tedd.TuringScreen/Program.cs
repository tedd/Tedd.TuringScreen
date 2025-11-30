using SkiaSharp;

namespace Tedd.TuringScreen;

internal class Program
{
    private static int _comPort = 6;

    static void Main(string[] args)
    {
        //Test1();
        Test2();
    }

    static void Test1()
    {

        var screen = new TuringScreen(_comPort);
        screen.Reset();
        //screen.RunBenchmark(); return;
        screen.SetBrightness(100);
        screen.SetOrientation(ScreenOrientation.Portrait);

        var buffer = new ScreenBuffer(screen.Width, screen.Height);

        screen.Clear();
        var span = buffer.GetSpan();

        for (var i = 0; i < 1_000_000; i++)
        {
            var x = Random.Shared.Next(0, screen.Width); // * 4;
            var y = Random.Shared.Next(0, screen.Height); // * screen.Width;// * 4;
            var r = (byte)Random.Shared.Next(0, 256);
            var g = (byte)Random.Shared.Next(0, 256);
            var b = (byte)Random.Shared.Next(0, 256);

            var index = x + y * screen.Width;
            buffer[x, y] = ScreenBuffer.FullRgbToColor565(r, g, b);
            screen.DisplayBuffer(0, 0, buffer);
            //Thread.Sleep(1000);
        }
    }

    static void Test2()
    {
        var screen = new TuringScreen(_comPort);
        screen.Reset();
        // 1. Decode Image
        using var originalBitmap = SKBitmap.Decode("TEST.png");

        // 2. Resize to fit the screen dimensions
        var imageInfo = new SKImageInfo(screen.Width, screen.Height);
        using var resizedBitmap = originalBitmap.Resize(imageInfo, SKFilterQuality.High);

        // 3. Create ScreenBuffer
        var buffer = new ScreenBuffer(screen.Width, screen.Height);

        // 4. Convert Pixels (RGB888 -> RGB565)
        // Access raw pixels for performance
        var pixels = resizedBitmap.Pixels; // Returns SKColor array

        for (int i = 0; i < pixels.Length; i++)
        {
            var color = pixels[i];

            // Calculate coordinates based on index
            int x = i % screen.Width;
            int y = i / screen.Width;

            // Convert standard RGB to Color656
            buffer[x, y] = ScreenBuffer.FullRgbToColor565(color.Red, color.Green, color.Blue);
        }

        // 5. Render
        screen.DisplayBuffer(0, 0, buffer);
    }

}


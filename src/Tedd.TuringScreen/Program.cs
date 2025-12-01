using SkiaSharp;

namespace Tedd.TuringScreen;

internal class Program
{
    private static int _comPort = 6;

    static void Main(string[] args)
    {
        //Test1();
        //Test2();
        Test3();
        Test3();
        Test3();
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
        screen.SetOrientation(ScreenOrientation.Portrait);
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

    static void Test3()
    {
        var screen = new TuringScreen(_comPort);
        screen.Reset();
        screen.SetOrientation(ScreenOrientation.Portrait);

        using var stream = File.OpenRead("C:\\temp\\gif1.gif");
        using var codec = SKCodec.Create(stream);

        // 1. Get the NATIVE dimensions of the GIF. 
        // We cannot decode directly to Screen Dimensions using SKCodec without artifacts/failures.
        var nativeInfo = codec.Info;

        // 2. Create the "Accumulator" - this holds the persistent visual state of the animation.
        using var accumulatorBitmap = new SKBitmap(nativeInfo);
        using var accumulatorCanvas = new SKCanvas(accumulatorBitmap);

        // 3. Create a buffer for the "Delta" - the raw frame data we just decoded.
        using var deltaBitmap = new SKBitmap(nativeInfo);

        // 4. Create the final scaled bitmap for the screen.
        var screenInfo = new SKImageInfo(screen.Width, screen.Height);
        using var scaledBitmap = new SKBitmap(screenInfo);

        var buffer = new ScreenBuffer(screen.Width, screen.Height);

        // Initialize accumulator with transparent black or background color
        accumulatorCanvas.Clear(SKColors.Transparent);

        // Continuous Loop
        // while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < codec.FrameCount; i++)
            {
                var frameInfo = codec.FrameInfo[i];

                // --- DECODE ---
                // Decode the raw frame data (the delta) into our temporary buffer
                var opts = new SKCodecOptions(i);
                codec.GetPixels(nativeInfo, deltaBitmap.GetPixels(), opts);

                // --- COMPOSE ---
                // Handle Disposal Methods (Simplified for typical looping)
                // If the PREVIOUS frame said "RestoreBackground", we should have cleared.
                // For this implementation, we assume standard overlay behavior which fixes the "Black Frame" issue.

                // Draw the new frame ON TOP of the existing accumulator. 
                // This respects transparency; transparent pixels in deltaBitmap won't erase the accumulator.
                accumulatorCanvas.DrawBitmap(deltaBitmap, 0, 0);

                // --- SCALE ---
                // Resize the composed image to fit the TuringScreen
                // We use SKFilterQuality.Low for speed, or Medium/High if you want better quality at cost of FPS.
                accumulatorBitmap.ScalePixels(scaledBitmap, SKFilterQuality.Low);

                // --- CONVERT & PUSH ---
                var pixels = scaledBitmap.Pixels;

                // Optimization: Use unsafe pointers or Span for speed if .NET 9+, 
                // but array access is safe and clear for this context.
                for (int p = 0; p < pixels.Length; p++)
                {
                    var c = pixels[p];

                    // Direct mapping since scaledBitmap matches screen dimensions
                    // Note: If X/Y calculation is expensive, flatten the loop or use a lookup.
                    int x = p % screen.Width;
                    int y = p / screen.Width;

                    buffer[x, y] = ScreenBuffer.FullRgbToColor565(c.Red, c.Green, c.Blue);
                }

                screen.DisplayBuffer(0, 0, buffer);

                // --- DISPOSAL HANDLING ---
                // If this frame says "Restore Background", clear the accumulator for the NEXT frame.
                if (frameInfo.DisposalMethod == SKCodecAnimationDisposalMethod.RestoreBackgroundColor)
                {
                    accumulatorCanvas.Clear(SKColors.Transparent);
                }
                // Note: "RestorePrevious" is complex (requires a history stack), but rarely used in simple GIFs.

                // --- TIMING ---
                int duration = frameInfo.Duration;
                // Minimum delay to prevent CPU spinning on bad GIF metadata
                if (duration < 10) duration = 10;
                Thread.Sleep(duration);
            }
        }
    }

}


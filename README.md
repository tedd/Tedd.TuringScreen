# Tedd.TuringScreen

**High-Performance .NET 10 Driver for 3.5" Turing Smart Screens**

`Tedd.TuringScreen` is a specialized, hardware-accelerated library designed to control 3.5-inch USB-CDC LCD Smart Screens (Revision A/B/C). It leverages **.NET 10** features and **AVX2 Hardware Intrinsics** to deliver maximum frame rates with minimal CPU overhead.

The driver features a unique **Heuristic Rendering Engine** that dynamically switches transmission strategies based on real-time frame analysis, ensuring optimal latency for both sparse updates (starfields, text) and dense updates (full-frame video).

## Requirements

- **.NET 10 SDK** (Specific Span optimization and stack allocation features).
- **Hardware:** 3.5" Turing Smart Screen (USB-C to serial).
- **OS:** Windows, Linux, or macOS (System must expose the device as a Serial COM port).
- **CPU:** Processor with AVX2 support recommended for SIMD acceleration.

## Key Features

- **SIMD-Accelerated Diffing:** Utilizes `System.Runtime.Intrinsics.X86.Avx2` to scan frame buffers, comparing 16 pixels per instruction cycle.
- **Heuristic Flow Control:** Automatically selects between **Sparse Point** transmission (for <10% change coverage) and **Dirty Rectangle** bulk transfer (for >10% coverage) based on calibrated latency models.
- **Zero-Allocation Architecture:** Hot paths utilize `System.Runtime.CompilerServices.Unsafe` and `MemoryMarshal` to operate on managed pointers without pinning overhead or Garbage Collection pressure.
- **Software Rotation Pipeline:** Decouples logical application orientation from physical hardware addressing, resolving memory wrapping artifacts common in hardware-level rotation commands.
- **Auto-Recovery:** Integrated circuit-breaker pattern detects serial disconnections and automatically reconnects and restores screen state (Brightness, Orientation, Framebuffer) transparently.

## Usage

### 1. Initialization

```
using Tedd.TuringScreen;

// Initialize connection on COM6
using var screen = new TuringScreen(6);

// Reset hardware to clean state
screen.Reset();
screen.SetBrightness(100);

// Set Logical Orientation
// The driver handles the coordinate mapping to physical hardware automatically.
screen.SetOrientation(ScreenOrientation.Landscape);
```

### 2. High-Performance Rendering

For optimal performance, manipulate a `ScreenBuffer` and flush it. The driver will perform differential analysis and transmit only changed pixels.

```
var buffer = new ScreenBuffer(screen.Width, screen.Height);

// Draw Loop
while (true)
{
    // Draw a red pixel at 50, 50
    buffer[50, 50] = ScreenBuffer.FullRgbToColor565(255, 0, 0);

    // Flush to hardware
    // The driver determines the fastest transmission strategy automatically.
    screen.DisplayBuffer(0, 0, buffer);
}
```

### 3. Loading and Displaying Images

The library does not include image decoding logic to keep dependencies minimal. You can use any standard library (like `SkiaSharp` or `ImageSharp`) to load images, resize them, and convert them to the required `RGB565` format.

**Example using SkiaSharp:**

*Prerequisite: `dotnet add package SkiaSharp`*

```
using SkiaSharp;
using Tedd.TuringScreen;

public void LoadAndDisplayImage(TuringScreen screen, string filePath)
{
    // 1. Decode Image
    using var originalBitmap = SKBitmap.Decode(filePath);

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
```

### 4. Immediate Mode (Sparse Updates)

For extremely sparse updates (e.g., a single status indicator LED or particle), you can bypass the buffer analysis and write directly to the command queue.

```
// Teleport a single white pixel to 10,20 immediately
screen.SetPixel(10, 20, 255, 255, 255);
```

## Latency Calibration

USB Serial latency varies by host controller drivers. To ensure the Heuristic Engine correctly identifies the crossover point between "Sparse" and "Bulk" modes, run the built-in benchmark.

```
screen.RunBenchmark();
```

**Interpretation:**

1. Locate the row in the output where `Point(ms)` is approximately equal to `Box(ms)`.
2. Note the value in the `Calc Mult` column for that row.
3. Update the `HeuristicCostPerPixel` constant in `TuringScreen.cs` with this value (Default is 12).

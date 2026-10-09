using SkiaSharp;

namespace LabelScope.Core.Barcodes;

/// <summary>Draws the dark modules of a 2D symbol in local coordinates (top-left corner is 0,0).</summary>
internal static class MatrixDrawer
{
    /// <summary>Draws <paramref name="modules"/> with one rectangle per horizontal run of dark modules.</summary>
    /// <param name="canvas">Canvas already transformed so the symbol's top-left corner is at (0, 0).</param>
    /// <param name="modules">Dark and light modules.</param>
    /// <param name="moduleWidth">Width of one module in dots.</param>
    /// <param name="moduleHeight">Height of one module in dots.</param>
    /// <param name="ink">Paint for dark modules; its anti-aliasing setting is restored afterwards.</param>
    public static void Draw(SKCanvas canvas, BitMatrix modules, int moduleWidth, int moduleHeight, SKPaint ink)
    {
        // Modules are whole dots; anti-aliasing would grey the edges of 1-dot modules.
        var wasAa = ink.IsAntialias;
        ink.IsAntialias = false;
        try
        {
            for (var y = 0; y < modules.Height; y++)
            {
                var x = 0;
                while (x < modules.Width)
                {
                    if (!modules[x, y]) { x++; continue; }
                    // One rectangle per run keeps the draw calls few and leaves no hairline seams between modules.
                    var start = x;
                    while (x < modules.Width && modules[x, y]) x++;
                    canvas.DrawRect(start * moduleWidth, y * moduleHeight, (x - start) * moduleWidth, moduleHeight, ink);
                }
            }
        }
        finally
        {
            ink.IsAntialias = wasAa;
        }
    }
}

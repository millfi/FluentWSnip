using SkiaSharp;
using WSnip.Core.Editing;

namespace WSnip.App.Rendering;

public static class SnipDrawing
{
    public static void Draw(SKCanvas canvas, SharedImage image, SKRect destination, IReadOnlyList<AnnotationStroke> strokes, bool smooth, double whiteScale, bool checkerboard)
    {
        canvas.Save();
        try
        {
            if (checkerboard) DrawCheckerboard(canvas, destination);
            var sampling = smooth ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None) : new SKSamplingOptions(SKFilterMode.Nearest);
            canvas.DrawImage(image.Image, new SKRect(0, 0, image.Width, image.Height), destination, sampling);
            canvas.ClipRect(destination);
            canvas.Translate(destination.Left, destination.Top);
            canvas.Scale(destination.Width / image.Width, destination.Height / image.Height);
            using var linear = SKColorSpace.CreateSrgbLinear();
            foreach (var stroke in strokes)
            {
                var drawn = image.IsHdr ? stroke : stroke with { Color = stroke.Color.ClampToSdr() };
                using var path = drawn.ToPath();
                using var paint = drawn.CreatePaint(linear, drawn.Tool == AnnotationTool.Highlighter ? (float)(1 / whiteScale) : 1);
                canvas.DrawPath(path, paint);
            }
        }
        finally { canvas.Restore(); }
    }
    private static void DrawCheckerboard(SKCanvas canvas, SKRect area)
    {
        using var tile = new SKBitmap(16, 16);
        using (var c = new SKCanvas(tile))
        {
            c.Clear(SKColors.White);
            using var p = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC) };
            c.DrawRect(0, 0, 8, 8, p); c.DrawRect(8, 8, 8, 8, p);
        }
        using var shader = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateTranslation(area.Left, area.Top));
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(area, paint);
    }
}

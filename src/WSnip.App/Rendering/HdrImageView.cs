using SkiaSharp;
namespace WSnip.App.Rendering;

public sealed class HdrImageView : HdrSurface
{
    private SharedImage? image;
    public SharedImage? Image
    {
        get => image;
        set { if (ReferenceEquals(image, value)) return; var next = value?.AddRef(); image?.Release(); image = next; InvalidateVisual(); }
    }
    public bool Smooth { get; set; } = true;
    protected override void Draw(SKCanvas canvas)
    {
        if (image is not null) SnipDrawing.Draw(canvas, image, new SKRect(0, 0, (float)ActualWidth, (float)ActualHeight), [], Smooth, Surface.ReferenceWhiteScale, false);
    }
    public override void Dispose() { Image = null; base.Dispose(); }
}

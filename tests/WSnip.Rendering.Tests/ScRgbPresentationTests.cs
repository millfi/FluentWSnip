using System.Numerics;
using SkiaSharp;
using WSnip.App.Rendering;
using WSnip.Core.Editing;
using WSnip.Core.Imaging;
using Xunit;

namespace WSnip.Rendering.Tests;

public sealed class ScRgbPresentationTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(2.5f)]
    [InlineData(4f)]
    public void LinearPresentationPreservesHighlightsNegativeGamutAndSdrWhite(float whiteScale)
    {
        using var source = new HdrImage(4,4);
        Fill(source,new ScRgb(4,-0.25f,1.5f));
        var image = SharedImage.FromHdr(source);
        try
        {
            using var target = Present(image,[],whiteScale);
            AssertPixel(target,2,2,new ScRgb(4*whiteScale,-0.25f*whiteScale,1.5f*whiteScale));
        }
        finally { image.Release(); }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2.5f)]
    public void MultiplyHighlighterMatchesExportAtEveryPresentationWhite(float whiteScale)
    {
        using var source = new HdrImage(8,8);
        Fill(source,new ScRgb(2,1,0.5f));
        var stroke = new AnnotationStroke(AnnotationTool.Highlighter,new ScRgb(0.5f,0.25f,1),8,[new Vector2(1,4),new Vector2(7,4)]);
        using var exported = AnnotationRenderer.Render(source,[stroke]);
        var image = SharedImage.FromHdr(source);
        try
        {
            using var target = Present(image,[stroke],whiteScale);
            var row = exported.ReadRow(4);
            AssertPixel(target,4,4,new ScRgb((float)row[16]*whiteScale,(float)row[17]*whiteScale,(float)row[18]*whiteScale));
        }
        finally { image.Release(); }
    }

    [Fact]
    public void SharedDisplayImageRetainsPixelsAfterCaptureIsReleased()
    {
        var source = new HdrImage(4,4);
        Fill(source,new ScRgb(3,0.5f,-0.125f));
        var image = SharedImage.FromHdr(source);
        source.Dispose();
        try
        {
            using var target = Present(image,[],1);
            AssertPixel(target,2,2,new ScRgb(3,0.5f,-0.125f));
        }
        finally { image.Release(); }
    }

    private static HdrImage Present(SharedImage image,IReadOnlyList<AnnotationStroke> strokes,float whiteScale)
    {
        var target = new HdrImage(image.Width,image.Height);
        try
        {
            float[] m = SKColorSpaceXyz.Srgb.Values;
            float k = 1/whiteScale;
            using var space = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear,new SKColorSpaceXyz(m[0]*k,m[1]*k,m[2]*k,m[3]*k,m[4]*k,m[5]*k,m[6]*k,m[7]*k,m[8]*k));
            using var surface = SKSurface.Create(new SKImageInfo(image.Width,image.Height,SKColorType.RgbaF16,SKAlphaType.Premul,space),target.Pointer,image.Width*8);
            surface.Canvas.Clear(SKColors.Transparent);
            SnipDrawing.Draw(surface.Canvas,image,new SKRect(0,0,image.Width,image.Height),strokes,false,whiteScale,false);
            surface.Canvas.Flush();
            return target;
        }
        catch { target.Dispose(); throw; }
    }
    private static void Fill(HdrImage image,ScRgb color)
    {
        for(int y=0;y<image.Height;y++)
        {
            var row=image.Row(y);
            for(int x=0;x<image.Width;x++) { int i=x*4; row[i]=(Half)color.R;row[i+1]=(Half)color.G;row[i+2]=(Half)color.B;row[i+3]=(Half)1; }
        }
    }
    private static void AssertPixel(HdrImage image,int x,int y,ScRgb expected)
    {
        var row=image.ReadRow(y);
        Assert.InRange(Math.Abs((float)row[x*4]-expected.R),0,0.02f);
        Assert.InRange(Math.Abs((float)row[x*4+1]-expected.G),0,0.02f);
        Assert.InRange(Math.Abs((float)row[x*4+2]-expected.B),0,0.02f);
    }
}

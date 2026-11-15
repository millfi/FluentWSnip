using System.Numerics;
using WSnip.App.Editor;
using WSnip.App.Services;
using WSnip.Core.Capture;
using WSnip.Core.Editing;
using WSnip.Core.Imaging;

namespace WSnip.App.Rendering;

/// <summary>Debug-only deterministic UI fixture; it never captures the user's desktop.</summary>
internal static class PresentationFixture
{
    public static void ShowIfRequested(AppController controller,IReadOnlyList<string> arguments)
    {
        if(!arguments.Contains("--presentation-fixture",StringComparer.Ordinal)) return;
        using var image = new HdrImage(960,540);
        for(int y=0;y<image.Height;y++)
        {
            var row=image.Row(y);
            for(int x=0;x<image.Width;x++)
            {
                // SDR ramp, HDR ramp to 8x white, and a wide-gamut P3 red/green strip.
                float t=x/(float)(image.Width-1);
                ScRgb color=y<180 ? new ScRgb(t,t,t) : y<360 ? new ScRgb(8*t,4*t,2*t) : t<0.5f ? new ScRgb(1.225f,-0.042f,-0.02f) : new ScRgb(-0.225f,1.042f,-0.079f);
                int i=x*4;
                row[i]=(Half)color.R; row[i+1]=(Half)color.G; row[i+2]=(Half)color.B; row[i+3]=(Half)1;
            }
        }
        using var snip = new Snip(image.Share(),DateTimeOffset.Now,80,1600);
        var document=new EditorDocument(snip);
        document.AddStroke(new AnnotationStroke(AnnotationTool.Pen,new ScRgb(4,4,4),8,[new Vector2(32,32),new Vector2(240,120)]));
        controller.ShowEditor(document);
    }
}

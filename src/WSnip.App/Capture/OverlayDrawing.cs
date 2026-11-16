using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace WSnip.App.Capture;

/// <summary>GPU-only selection decoration. Never reads, converts, or uploads the frozen screen pixels.</summary>
internal static class OverlayDrawing
{
    private static readonly Color Dim = Color.FromArgb(144, 0, 0, 0);
    private static readonly Color Shadow = Color.FromArgb(160, 0, 0, 0);

    public static void Draw(CanvasDrawingSession canvas, Rect area, Rect? selection,
        IReadOnlyList<Vector2>? outline, float whiteScale)
    {
        using var border = CanvasSolidColorBrush.CreateHdr(canvas.Device, new Vector4(whiteScale, whiteScale, whiteScale, 1));
        if (outline is { Count: >= 2 })
        {
            using var path = new CanvasPathBuilder(canvas.Device);
            path.BeginFigure(outline[0]);
            for (int i = 1; i < outline.Count; i++) path.AddLine(outline[i]);
            path.EndFigure(CanvasFigureLoop.Closed);
            using var hole = CanvasGeometry.CreatePath(path);
            using var bounds = CanvasGeometry.CreateRectangle(canvas.Device, area);
            using var outside = bounds.CombineWith(hole, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            canvas.FillGeometry(outside, Dim);
            canvas.DrawGeometry(hole, Shadow, 3.5f);
            canvas.DrawGeometry(hole, border, 1.5f);
        }
        else if (selection is { Width: > 0, Height: > 0 } rect)
        {
            // Four disjoint rectangles avoid path clipping/tessellation on the common drag path.
            double left = Math.Clamp(rect.Left, area.Left, area.Right);
            double right = Math.Clamp(rect.Right, area.Left, area.Right);
            double top = Math.Clamp(rect.Top, area.Top, area.Bottom);
            double bottom = Math.Clamp(rect.Bottom, area.Top, area.Bottom);
            Fill(new Rect(area.Left, area.Top, area.Width, top - area.Top));
            Fill(new Rect(area.Left, bottom, area.Width, area.Bottom - bottom));
            Fill(new Rect(area.Left, top, left - area.Left, bottom - top));
            Fill(new Rect(right, top, area.Right - right, bottom - top));
            canvas.DrawRectangle(rect, Shadow, 3.5f);
            canvas.DrawRectangle(rect, border, 1.5f);
        }
        else canvas.FillRectangle(area, Dim);

        void Fill(Rect rect)
        {
            if (rect.Width > 0 && rect.Height > 0) canvas.FillRectangle(rect, Dim);
        }
    }
}

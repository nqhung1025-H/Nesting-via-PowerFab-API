using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal interface IPreviewDrawing : IDisposable
    {
        int Width { get; }
        int Height { get; }
        void Draw(Graphics graphics, RectangleF destination);
    }

    // Keep the NC strokes as paths. Zoom redraws those paths at screen scale
    // instead of enlarging the pixels of the exported PowerFab PNG.
    internal sealed class AbeVectorPreview : IPreviewDrawing
    {
        private readonly GraphicsPath cuts = new GraphicsPath();
        private readonly GraphicsPath marks = new GraphicsPath();
        public int Width => AbePreviewFrame.PixelWidth;
        public int Height { get; }

        public AbeVectorPreview(AbeNestDocument document, double? sheetLength = null, double? sheetWidth = null)
        {
            var frame = new AbePreviewFrame(document, sheetLength, sheetWidth); Height = frame.PixelHeight;
            if (sheetLength.HasValue && sheetWidth.HasValue)
            {
                PointF top = frame.Project(0, sheetWidth.Value);
                cuts.AddRectangle(new RectangleF(top.X, top.Y, (float)(sheetLength.Value * frame.Scale), (float)(sheetWidth.Value * frame.Scale)));
            }
            foreach (var path in document.Cuts.Where(p => p.Count > 1))
            {
                cuts.StartFigure(); cuts.AddLines(path.Select(p => frame.Project(p.x, p.y)).ToArray());
            }
            foreach (var path in document.Marks.Where(p => p.Count > 1))
            {
                marks.StartFigure(); marks.AddLines(path.Select(p => frame.Project(p.x, p.y)).ToArray());
            }
        }

        public void Draw(Graphics graphics, RectangleF destination)
        {
            if (destination.Width <= 0 || destination.Height <= 0) return;
            float scale = destination.Width / Width;
            float dpi = graphics.DpiX / 96F;
            var state = graphics.Save();
            try
            {
                graphics.SetClip(destination, CombineMode.Intersect);
                graphics.TranslateTransform(destination.X, destination.Y);
                graphics.ScaleTransform(scale, destination.Height / Height);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Constant screen widths keep enlarged markings legible rather
                // than turning their narrow strokes into thick blurred blocks.
                using var cutting = new Pen(Color.FromArgb(10, 47, 78), 1.2F * dpi / scale) { LineJoin = LineJoin.Round };
                using var marking = new Pen(Color.FromArgb(200, 118, 32), dpi / scale)
                    { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (cuts.PointCount > 0) graphics.DrawPath(cutting, cuts);
                if (marks.PointCount > 0) graphics.DrawPath(marking, marks);
            }
            finally { graphics.Restore(state); }
        }
        public void Dispose() { cuts.Dispose(); marks.Dispose(); }
    }
}

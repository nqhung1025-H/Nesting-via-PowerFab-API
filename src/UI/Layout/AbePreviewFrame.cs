using System;
using System.Drawing;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class AbePreviewFrame
    {
        public const int PixelWidth = 2400;
        public const int Margin = 40;
        public int PixelHeight { get; }
        public double Left { get; }
        public double Bottom { get; }
        public double HeightMm { get; }
        public double Scale { get; }

        public AbePreviewFrame(AbeNestDocument document, double? sheetLength = null, double? sheetWidth = null)
        {
            var bounds = document.Bounds;
            Left = Math.Min(0, bounds.Left - 5); Bottom = Math.Min(0, bounds.Bottom - 5);
            double length = Math.Max(sheetLength ?? 0, bounds.Right + 15) - Left;
            HeightMm = Math.Max(sheetWidth ?? 0, bounds.Top + 15) - Bottom;
            double fit = (PixelWidth - 2 * Margin) / length;
            PixelHeight = Math.Clamp((int)(HeightMm * fit + 2 * Margin), 800, 6000);
            Scale = Math.Min(fit, (PixelHeight - 2 * Margin) / HeightMm);
        }
        public PointF Project(double x, double y) => new PointF((float)(Margin + (x - Left) * Scale),
            (float)(Margin + (HeightMm - (y - Bottom)) * Scale));
    }
}

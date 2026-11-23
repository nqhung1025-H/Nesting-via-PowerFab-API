using System;

namespace Hanlon.PowerFab.Nesting
{
    // Image-space transform shared by the desktop viewer and offline checks.
    internal sealed class PreviewViewport
    {
        public double ImageWidth { get; }
        public double ImageHeight { get; }
        public double Width { get; private set; }
        public double Height { get; private set; }
        public double Zoom { get; private set; } = 1;
        public double OffsetX { get; private set; }
        public double OffsetY { get; private set; }
        public double Scale => Width > 0 && Height > 0 ? Math.Min(Width / ImageWidth, Height / ImageHeight) * Zoom : 0;
        public const double MaximumZoom = 32;

        public PreviewViewport(double imageWidth, double imageHeight)
        {
            if (!double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth <= 0 || imageHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(imageWidth));
            ImageWidth = imageWidth; ImageHeight = imageHeight;
        }

        public void Resize(double width, double height)
        {
            double before = Scale;
            double x = before > 0 ? (Width / 2 - OffsetX) / before : ImageWidth / 2;
            double y = before > 0 ? (Height / 2 - OffsetY) / before : ImageHeight / 2;
            Width = double.IsFinite(width) ? Math.Max(0, width) : 0;
            Height = double.IsFinite(height) ? Math.Max(0, height) : 0;
            OffsetX = Width / 2 - x * Scale; OffsetY = Height / 2 - y * Scale;
            // Preserve the viewed centre after resizing. Only Reset explicitly
            // fits/centres the image; automatic centring would move an anchor.
        }

        public void ZoomAt(double x, double y, double wheelSteps)
        {
            if (Scale <= 0 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(wheelSteps)) return;
            double px = (x - OffsetX) / Scale, py = (y - OffsetY) / Scale;
            Zoom = Math.Clamp(Zoom * Math.Pow(1.2, Math.Clamp(wheelSteps, -100, 100)), 1, MaximumZoom);
            OffsetX = x - px * Scale; OffsetY = y - py * Scale;
            // Do not clamp/recentre here: that moves the image point beneath
            // the pointer, especially with narrow layouts and blank margins.
        }

        public void Pan(double dx, double dy)
        {
            if (Scale <= 0 || !double.IsFinite(dx) || !double.IsFinite(dy)) return;
            OffsetX += dx; OffsetY += dy; ClampPan();
        }

        public void Reset()
        { Zoom = 1; OffsetX = (Width - ImageWidth * Scale) / 2; OffsetY = (Height - ImageHeight * Scale) / 2; }

        private void ClampPan()
        {
            double w = ImageWidth * Scale, h = ImageHeight * Scale;
            // Allow blank space on every side, while retaining a reachable
            // strip of the drawing during a long middle-button drag.
            double visibleX = Math.Min(32, Math.Min(w, Width)), visibleY = Math.Min(32, Math.Min(h, Height));
            OffsetX = Math.Clamp(OffsetX, visibleX - w, Width - visibleX);
            OffsetY = Math.Clamp(OffsetY, visibleY - h, Height - visibleY);
        }
    }
}

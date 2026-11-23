using System;
using System.Drawing;

namespace Hanlon.PowerFab.Nesting
{
    internal static class PreviewFrameSizing
    {
        // Fit the viewport itself around the image, then add space for the tab
        // strip, native borders and optional sheet navigation. These controls
        // must not be included in the drawing's width/height ratio.
        public static Rectangle Fit(Size available, double imageRatio, Size chrome, int padding)
        {
            int width = Math.Max(0, available.Width), height = Math.Max(0, available.Height);
            int px = Math.Min(Math.Max(0, padding), width / 2), py = Math.Min(Math.Max(0, padding), height / 2);
            int areaWidth = width - px * 2, areaHeight = height - py * 2;
            int cx = Math.Min(Math.Max(0, chrome.Width), areaWidth), cy = Math.Min(Math.Max(0, chrome.Height), areaHeight);
            double ratio = double.IsFinite(imageRatio) && imageRatio > 0 ? imageRatio : 2.0;
            double drawingWidth = Math.Min(areaWidth - cx, (areaHeight - cy) * ratio);
            int frameWidth = (int)Math.Floor(drawingWidth) + cx;
            int frameHeight = (int)Math.Floor(drawingWidth / ratio) + cy;
            return new Rectangle((width - frameWidth) / 2, (height - frameHeight) / 2, frameWidth, frameHeight);
        }
    }
}

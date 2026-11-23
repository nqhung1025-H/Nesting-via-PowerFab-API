using System;
using System.Drawing;

namespace Hanlon.PowerFab.Nesting
{
    internal static class NestingWindowSizing
    {
        // The sheet is landscape, 3000 x 1500 mm. Apply its ratio only to the
        // drawing area; the tabs, summary and buttons need their own height.
        // All arguments are measured pixels, after the form's DPI scaling.
        public static Size Fit(Size workingArea, Size nonClient, Size surroundingControls, int dpi)
        {
            double scale = Math.Max(96, dpi) / 96.0;
            int inset = (int)Math.Round(16 * scale);
            int availableWidth = Math.Max(1, workingArea.Width - Math.Min(inset * 2, workingArea.Width / 10));
            int availableHeight = Math.Max(1, workingArea.Height - Math.Min(inset * 2, workingArea.Height / 10));
            int frameWidth = Math.Max(0, nonClient.Width), frameHeight = Math.Max(0, nonClient.Height);
            int sideWidth = Math.Max(0, surroundingControls.Width), controlsHeight = Math.Max(0, surroundingControls.Height);
            int maxClientWidth = Math.Max(1, Math.Min((int)Math.Round(1100 * scale), availableWidth - frameWidth));
            int maxClientHeight = Math.Max(1, availableHeight - frameHeight);
            int minClientWidth = Math.Min(maxClientWidth, (int)Math.Round(720 * scale));
            int widthFromHeight = (int)Math.Floor(Math.Max(1, maxClientHeight - controlsHeight) * 2.0 + sideWidth);
            int width = Math.Clamp(widthFromHeight, minClientWidth, maxClientWidth);
            int height = Math.Min(maxClientHeight, controlsHeight + (int)Math.Ceiling(Math.Max(1, width - sideWidth) / 2.0));
            // Short screens still retain enough room for the request tables and
            // actions, even when a full 2:1 preview cannot fit at minimum width.
            height = Math.Max(height, Math.Min(maxClientHeight, (int)Math.Round(560 * scale) - frameHeight));
            return new Size(Math.Min(availableWidth, width + frameWidth), Math.Min(availableHeight, height + frameHeight));
        }
    }
}

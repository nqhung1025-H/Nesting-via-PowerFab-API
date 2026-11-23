using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class HanlonContextMenu : ContextMenuStrip
    {
        public HanlonContextMenu() { Renderer = new RoundedMenuRenderer(); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e); RoundWindow();
        }
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e); RoundWindow();
        }
        private void RoundWindow()
        {
            if (Width <= 0 || Height <= 0) return;
            using var path = Outline(new RectangleF(0, 0, Width, Height), 4F * DeviceDpi / 96F);
            var previous = Region;
            Region = new Region(path);
            previous?.Dispose();
        }
        private static GraphicsPath Outline(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            if (diameter <= 0) return path;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        private sealed class RoundedMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is not HanlonContextMenu) { base.OnRenderToolStripBackground(e); return; }
                e.Graphics.Clear(HanlonTheme.Paper);
            }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is not HanlonContextMenu) { base.OnRenderToolStripBorder(e); return; }
                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = Outline(new RectangleF(0.5F, 0.5F, Math.Max(0, e.ToolStrip.Width - 1),
                        Math.Max(0, e.ToolStrip.Height - 1)), 4F * e.ToolStrip.DeviceDpi / 96F);
                    using var pen = new Pen(HanlonTheme.InputLine);
                    e.Graphics.DrawPath(pen, path);
                }
                finally { e.Graphics.Restore(state); }
            }
        }
    }
}

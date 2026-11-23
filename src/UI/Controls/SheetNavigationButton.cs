using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class SheetNavigationButton : Button
    {
        public bool Forward { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = DeviceDpi / 96F;
            float cx = (ClientSize.Width - 1) / 2F, cy = (ClientSize.Height - 1) / 2F;
            float side = (Forward ? -1 : 1) * 2.5F * scale;
            var state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Enabled ? ForeColor : SystemColors.GrayText, 2F * scale)
                    { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                e.Graphics.DrawLines(pen, new[] { new PointF(cx + side, cy - 5 * scale),
                    new PointF(cx - side, cy), new PointF(cx + side, cy + 5 * scale) });
            }
            finally { e.Graphics.Restore(state); }
        }
    }
}

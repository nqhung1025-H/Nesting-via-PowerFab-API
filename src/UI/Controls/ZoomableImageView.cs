using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class ZoomableImageView : Control
    {
        private Image? image;
        private IPreviewDrawing? drawing;
        private readonly PreviewViewport viewport;
        private bool panning;
        private Point lastPointer;
        public double ImageAspectRatio => viewport.ImageWidth / viewport.ImageHeight;

        public ZoomableImageView(Image image, IPreviewDrawing? drawing = null)
        {
            this.image = image ?? throw new ArgumentNullException(nameof(image));
            this.drawing = drawing;
            viewport = new PreviewViewport(drawing?.Width ?? image.Width, drawing?.Height ?? image.Height);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.White; Dock = DockStyle.Fill; TabStop = true;
            AccessibleName = "Nesting layout";
            AccessibleDescription = "Mouse wheel: zoom at pointer. Hold middle mouse button and drag: pan. Double-click or Home: fit layout.";
        }

        protected override void OnResize(EventArgs e)
        { base.OnResize(e); if (viewport != null) viewport.Resize(ClientSize.Width, ClientSize.Height); Invalidate(); }

        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); Focus(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            viewport.ZoomAt(e.X, e.Y, e.Delta / 120.0);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            Invalidate(); base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if (e.Button != MouseButtons.Middle) return;
            panning = true; lastPointer = e.Location; Capture = true; Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!panning) return;
            viewport.Pan(e.X - lastPointer.X, e.Y - lastPointer.Y); lastPointer = e.Location; Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        { base.OnMouseUp(e); if (e.Button == MouseButtons.Middle) StopPan(); }

        protected override void OnMouseCaptureChanged(EventArgs e)
        { base.OnMouseCaptureChanged(e); if (!Capture) { panning = false; Cursor = Cursors.Default; } }

        private void StopPan()
        { panning = false; Capture = false; Cursor = Cursors.Default; }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) { viewport.Reset(); Invalidate(); } }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Home or Keys.F) { viewport.Reset(); e.Handled = true; }
            else if (e.KeyCode is Keys.Add or Keys.Oemplus) { viewport.ZoomAt(ClientSize.Width / 2.0, ClientSize.Height / 2.0, 1); e.Handled = true; }
            else if (e.KeyCode is Keys.Subtract or Keys.OemMinus) { viewport.ZoomAt(ClientSize.Width / 2.0, ClientSize.Height / 2.0, -1); e.Handled = true; }
            if (e.Handled) { e.SuppressKeyPress = true; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (image == null || viewport.Scale <= 0) return;
            var destination = new RectangleF((float)viewport.OffsetX, (float)viewport.OffsetY,
                (float)(viewport.ImageWidth * viewport.Scale), (float)(viewport.ImageHeight * viewport.Scale));
            if (drawing != null) { drawing.Draw(e.Graphics, destination); return; }
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(image, destination);
        }

        protected override void Dispose(bool disposing)
        { if (disposing) { drawing?.Dispose(); drawing = null; image?.Dispose(); image = null; } base.Dispose(disposing); }
    }
}

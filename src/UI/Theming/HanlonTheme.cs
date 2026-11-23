using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    // Light-theme tokens from Plate Template v41 / Plate Sorter v9 CSS.
    // This styles interface controls only; do not apply it to ABE preview trees.
    internal static class HanlonTheme
    {
        public static readonly Color Brand = Color.FromArgb(0x1B, 0x11, 0x24);
        public static readonly Color Signal = Color.FromArgb(0xFF, 0xC9, 0x09);
        public static readonly Color Background = Color.FromArgb(0xEE, 0xEE, 0xEA);
        public static readonly Color Surface = Color.FromArgb(0xF7, 0xF6, 0xF2);
        public static readonly Color Paper = Color.White;
        public static readonly Color Line = Color.FromArgb(0xDA, 0xD7, 0xDE);
        public static readonly Color InputLine = Color.FromArgb(0xBD, 0xB6, 0xC4);
        public static readonly Color Muted = Color.FromArgb(0x5F, 0x56, 0x68);
        public static readonly Color Pale = Color.FromArgb(0xFF, 0xF6, 0xD6);
        public static Font BodyFont() => new Font("Arial", 10.5F);

        public static void Apply(Control root, params Control[] excluded)
        {
            foreach (Control control in excluded) if (ReferenceEquals(root, control)) return;
            if (root is Form || root is TabPage) root.BackColor = Background;
            if (root.ForeColor != Color.White) root.ForeColor = Brand;
            if (root is TextBox text) { text.BackColor = Paper; text.BorderStyle = BorderStyle.FixedSingle; }
            if (root is NumericUpDown number) { number.BackColor = Paper; number.BorderStyle = BorderStyle.FixedSingle; }
            if (root is ComboBox || root is CheckedListBox) root.BackColor = Paper;
            if (root is HanlonButton button && button.Primary && !button.Font.Bold)
                button.Font = new Font(button.Font, button.Font.Style | FontStyle.Bold);
            if (root is DataGridView grid) StyleGrid(grid);
            foreach (Control child in root.Controls) Apply(child, excluded);
        }

        public static void StyleGrid(DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = Paper; grid.GridColor = Line;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Brand;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Paper;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Brand;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Paper;
            grid.DefaultCellStyle.ForeColor = Brand;
            grid.DefaultCellStyle.SelectionBackColor = Pale;
            grid.DefaultCellStyle.SelectionForeColor = Brand;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Surface;
        }

        public static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }

        public static void HeaderAccent(object? sender, PaintEventArgs e)
        {
            var header = (Control)sender!;
            int height = Math.Max(1, (int)Math.Round(4 * header.DeviceDpi / 96.0));
            using var brush = new SolidBrush(Signal);
            e.Graphics.FillRectangle(brush, 0, header.ClientSize.Height - height, header.ClientSize.Width, height);
        }
    }

    // Inherits native Button semantics: keyboard activation, Accept/Cancel,
    // accessibility and click handlers. Only the surface painting is replaced.
    internal sealed class HanlonButton : Button
    {
        private bool primary, hovered, pressed;
        public bool Primary
        {
            get => primary;
            set
            {
                primary = value;
                BackColor = value ? HanlonTheme.Brand : HanlonTheme.Paper;
                ForeColor = value ? HanlonTheme.Paper : HanlonTheme.Brand;
                Invalidate();
            }
        }
        public HanlonButton()
        {
            FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) pressed = true; Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); pressed = false; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) pressed = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? HanlonTheme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? HanlonTheme.Line : primary
                ? pressed && (hovered || Focused) ? Color.FromArgb(0x14, 0x0D, 0x1A)
                    : hovered ? Color.FromArgb(0x33, 0x26, 0x3F) : HanlonTheme.Brand
                : pressed && hovered ? HanlonTheme.Line : hovered ? Color.FromArgb(0xF4, 0xF2, 0xF6) : HanlonTheme.Paper;
            Color ink = !Enabled ? HanlonTheme.Muted : primary ? HanlonTheme.Paper : HanlonTheme.Brand;
            Color border = primary && Enabled ? HanlonTheme.Brand : HanlonTheme.InputLine;
            float scale = DeviceDpi / 96F;
            using var path = HanlonTheme.Rounded(new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), 7 * scale);
            using var brush = new SolidBrush(fill); using var pen = new Pen(border);
            g.FillPath(brush, path); g.DrawPath(pen, path);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Inflate(ClientRectangle, -Padding.Horizontal / 2, -Padding.Vertical / 2), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -(int)(6 * scale), -(int)(6 * scale)), ink, fill);
        }
    }

    // Outer application tabs only. Nest/Remnant tabs retain the original viewer.
    internal sealed class HanlonTabs : TabControl
    {
        public HanlonTabs() { DrawMode = TabDrawMode.OwnerDrawFixed; Padding = new Point(16, 6); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= TabCount) return;
            bool selected = e.Index == SelectedIndex;
            using var background = new SolidBrush(selected ? HanlonTheme.Paper : HanlonTheme.Background);
            e.Graphics.FillRectangle(background, e.Bounds);
            Color ink = selected ? HanlonTheme.Brand : HanlonTheme.Muted;
            TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, e.Bounds, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (selected)
            {
                int h = Math.Max(2, (int)Math.Round(3 * DeviceDpi / 96.0));
                using var line = new SolidBrush(HanlonTheme.Brand);
                e.Graphics.FillRectangle(line, e.Bounds.Left, e.Bounds.Bottom - h, e.Bounds.Width, h);
            }
            if ((e.State & DrawItemState.Focus) != 0)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -4, -4), ink, selected ? HanlonTheme.Paper : HanlonTheme.Background);
        }
    }
}

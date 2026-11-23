using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class LayoutConditionDialog : Form
    {
        private readonly NumericUpDown spacing = Number(0, 100000, 0.5m);
        private readonly NumericUpDown left = Number(0, 100000, 1);
        private readonly NumericUpDown right = Number(0, 100000, 1);
        private readonly NumericUpDown top = Number(0, 100000, 1);
        private readonly NumericUpDown bottom = Number(0, 100000, 1);
        private readonly NumericUpDown move = Number(0.01m, 100000, 0.5m);
        private readonly NumericUpDown rotate = Number(0.01m, 360, 1);
        private readonly NumericUpDown minimumBottomRemnant = Number(0, 100000, 5);

        public LayoutConditions Values => new LayoutConditions
        {
            Spacing = spacing.Value, Left = left.Value, Right = right.Value,
            Top = top.Value, Bottom = bottom.Value, MovePitch = move.Value, RotatePitch = rotate.Value,
            MinimumBottomRemnant = minimumBottomRemnant.Value
        };

        public LayoutConditionDialog(LayoutConditions current)
        {
            Text = "Layout Condition";
            ClientSize = new Size(620, 616);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = HanlonTheme.BodyFont();
            BackColor = HanlonTheme.Background;
            AutoScaleMode = AutoScaleMode.Dpi;
            spacing.Value = current.Spacing; left.Value = current.Left; right.Value = current.Right;
            top.Value = current.Top; bottom.Value = current.Bottom; move.Value = current.MovePitch; rotate.Value = current.RotatePitch;
            minimumBottomRemnant.Value = current.MinimumBottomRemnant;

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14)
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(main);
            main.Controls.Add(new Label
            {
                Text = "Layout Condition", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = HanlonTheme.Brand,
                Font = new Font("Arial", 15, FontStyle.Bold)
            }, 0, 0);

            var borders = new GroupBox { Text = "Part spacing / nesting border (mm)", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var borderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            borderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            borderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var sketch = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            sketch.Paint += DrawSketch;
            borderLayout.Controls.Add(sketch, 0, 0);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(8, 2, 0, 2) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            AddField(fields, 0, "Spacing (S)", spacing);
            AddField(fields, 1, "Left (L)", left);
            AddField(fields, 2, "Right (R)", right);
            AddField(fields, 3, "Top (T)", top);
            AddField(fields, 4, "Bottom (B)", bottom);
            borderLayout.Controls.Add(fields, 1, 0);
            borders.Controls.Add(borderLayout);
            main.Controls.Add(borders, 0, 1);

            main.Controls.Add(SingleField("Move Pitch", "Move pitch (mm)", move), 0, 2);
            main.Controls.Add(SingleField("Rotate Pitch", "Rotate pitch (degrees)", rotate), 0, 3);
            main.Controls.Add(SingleField("Remnant", "Minimum bottom remnant (mm)", minimumBottomRemnant), 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new HanlonButton { Text = "Cancel", Width = 120, Height = 40, DialogResult = DialogResult.Cancel };
            var ok = new HanlonButton { Text = "OK", Primary = true, Width = 120, Height = 40, DialogResult = DialogResult.OK };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            main.Controls.Add(buttons, 0, 5);
            AcceptButton = ok; CancelButton = cancel;
            HanlonTheme.Apply(this);
        }

        private static NumericUpDown Number(decimal minimum, decimal maximum, decimal increment)
            => new NumericUpDown
            {
                Minimum = minimum, Maximum = maximum, Increment = increment,
                DecimalPlaces = 2, TextAlign = HorizontalAlignment.Right, Dock = DockStyle.Fill,
                Margin = new Padding(4, 8, 4, 4)
            };

        private static void AddField(TableLayoutPanel panel, int row, string title, NumericUpDown input)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            panel.Controls.Add(input, 1, row);
        }

        private static Control SingleField(string title, string label, NumericUpDown input)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            fields.Controls.Add(input, 1, 0); box.Controls.Add(fields);
            return box;
        }

        private static void DrawSketch(object? sender, PaintEventArgs e)
        {
            var panel = (Panel)sender!;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var sheet = new Rectangle(18, 38, Math.Max(100, panel.Width - 36), Math.Max(80, panel.Height - 76));
            var inner = Rectangle.Inflate(sheet, -19, -19);
            using var edge = new Pen(HanlonTheme.Brand, 1.5f);
            using var dash = new Pen(Color.SlateGray) { DashStyle = DashStyle.Dash };
            using var fill = new SolidBrush(Color.FromArgb(238, 243, 249));
            using var part = new SolidBrush(Color.FromArgb(58, 126, 178));
            using var font = new Font("Arial", 9, FontStyle.Bold);
            g.FillRectangle(fill, sheet); g.DrawRectangle(edge, sheet); g.DrawRectangle(dash, inner);
            g.FillRectangle(part, inner.Left + 8, inner.Top + 16, 30, 22);
            g.FillRectangle(part, inner.Left + 65, inner.Top + 16, 30, 22);
            g.FillRectangle(part, inner.Left + 8, inner.Bottom - 34, 30, 22);
            g.DrawString("T", font, Brushes.Black, sheet.Left + sheet.Width / 2, sheet.Top - 22);
            g.DrawString("B", font, Brushes.Black, sheet.Left + sheet.Width / 2, sheet.Bottom + 3);
            g.DrawString("L", font, Brushes.Black, sheet.Left + 1, sheet.Top + sheet.Height / 2);
            g.DrawString("R", font, Brushes.Black, sheet.Right - 16, sheet.Top + sheet.Height / 2);
            g.DrawString("S", font, Brushes.Black, inner.Left + 45, inner.Top + 17);
        }
    }
}

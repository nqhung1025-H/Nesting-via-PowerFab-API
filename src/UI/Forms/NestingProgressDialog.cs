using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class NestingProgressDialog : Form
    {
        private readonly Dictionary<string, Label> values = new Dictionary<string, Label>();
        private readonly Label material = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label counts = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
        private readonly Button cancel = new HanlonButton { Text = "Cancel", AutoSize = false, Width = 140, Height = 40 };
        private readonly ToolTip tips = new ToolTip();
        private readonly bool importingAbe;
        private bool cancelling;
        public event Action? CancelRequested;

        public NestingProgressDialog(Font font, NestingProgress initial)
        {
            importingAbe = initial.IsAbeImport;
            Text = "Hanlon - Nesting progress"; Font = new Font("Arial", font.Size, font.Style);
            ClientSize = new Size(560, importingAbe ? 160 : 386); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; ControlBox = false;
            ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = importingAbe ? ImportLayout() : CalculationLayout();
            cancel.Click += (_, _) =>
            {
                if (cancelling) return;
                cancelling = true; cancel.Enabled = false; status.Text = "Cancelling…";
                CancelRequested?.Invoke();
            };
            Controls.Add(layout); HanlonTheme.Apply(this); UpdateProgress(initial);
        }

        private TableLayoutPanel ImportLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.Controls.Add(material, 0, 0);
            layout.Controls.Add(status, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0), Margin = Padding.Empty };
            buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 2);
            return layout;
        }

        private TableLayoutPanel CalculationLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 11, Padding = new Padding(16), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 11; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 10 ? 56 : i == 9 ? 24 : 28));
            layout.Controls.Add(material, 0, 0); layout.SetColumnSpan(material, 2);
            string[] fields = { "Shape", "Grade", "Size", "Optimisation", "Pass", "Max scrap %" };
            for (int i = 0; i < fields.Length; i++)
            {
                layout.Controls.Add(new Label { Text = fields[i] + ":", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i + 1);
                var value = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
                values.Add(fields[i], value); layout.Controls.Add(value, 1, i + 1);
            }
            tips.SetToolTip(values["Max scrap %"], "No maximum scrap percentage is configured for the Hanlon calculation.");
            layout.Controls.Add(counts, 0, 7); layout.SetColumnSpan(counts, 2);
            layout.Controls.Add(status, 0, 8); layout.SetColumnSpan(status, 2);
            layout.Controls.Add(bar, 0, 9); layout.SetColumnSpan(bar, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0), Margin = Padding.Empty };
            buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 10); layout.SetColumnSpan(buttons, 2);
            return layout;
        }

        public void UpdateProgress(NestingProgress progress)
        {
            if (cancelling || IsDisposed) return;
            if (importingAbe)
            {
                material.Text = "Sheet " + progress.MaterialNumber + " of " + progress.MaterialCount;
                status.Text = "Importing: " + progress.ImportSheetName;
                tips.SetToolTip(status, status.Text);
                return;
            }
            material.Text = "Material " + progress.MaterialNumber + " of " + progress.MaterialCount;
            values["Shape"].Text = progress.Shape; values["Grade"].Text = progress.Grade;
            values["Size"].Text = progress.Size; values["Optimisation"].Text = progress.Optimisation;
            values["Pass"].Text = progress.Pass; values["Max scrap %"].Text = progress.MaxScrap;
            counts.Text = "Processed: " + progress.Processed + " / " + progress.Total + "    Placed: " + progress.Placed + "    Unplaced: " + progress.Unplaced;
            status.Text = progress.Status; bar.Value = progress.Percent;
            tips.SetToolTip(status, progress.Status); tips.SetToolTip(values["Size"], progress.Size);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tips.Dispose();
                if (importingAbe) { counts.Dispose(); bar.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}

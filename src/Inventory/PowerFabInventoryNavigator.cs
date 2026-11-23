using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal static class PowerFabInventoryNavigator
    {
        // IExtensionManager exposes getMainForm(), but no open-item command.
        // Use only public UI controls, on PowerFab's own UI thread. Never invoke
        // private host methods, write database rows, type into fields or match a
        // sheet by PO/size alone. Unknown controls get a truthful lookup fallback.
        public static void Show(Func<Form> getMainForm, PowerFabInventoryTarget target,
            Action<PowerFabInventoryNavigationResult> completed)
        {
            try
            {
                Form main = getMainForm();
                if (main == null || main.IsDisposed || !main.IsHandleCreated)
                    throw new InvalidOperationException("The PowerFab main window is not available.");
                main.BeginInvoke(new Action(() => OpenOnHostThread(main, target, completed)));
            }
            catch (Exception error) { completed(new PowerFabInventoryNavigationResult { Detail = error.Message }); }
        }

        private static void OpenOnHostThread(Form main, PowerFabInventoryTarget target,
            Action<PowerFabInventoryNavigationResult> completed)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            bool done = false, opened = false;
            void Finish(bool selected, string detail)
            {
                if (done) return;
                done = true; timer.Stop(); timer.Dispose();
                completed(new PowerFabInventoryNavigationResult { Opened = opened, Selected = selected, Detail = detail });
            }
            void Locate()
            {
                try
                {
                    var forms = Application.OpenForms.Cast<Form>().Where(f => !f.IsDisposed && !f.InvokeRequired && f.Visible
                        && (Caption(f.Text) == "inventory" || Caption(f.Text).EndsWith(" - inventory", StringComparison.Ordinal))).ToList();
                    if (forms.Count == 1)
                    {
                        var inventory = forms[0];
                        if (!opened)
                        {
                            if (inventory.WindowState == FormWindowState.Minimized) inventory.WindowState = FormWindowState.Normal;
                            inventory.BringToFront(); inventory.Activate();
                        }
                        opened = true;
                        if (TrySelect(inventory, target.InventoryId)) { Finish(true, ""); return; }
                    }
                    if (DateTime.UtcNow >= deadline)
                        Finish(false, opened ? "PowerFab Inventory is open, but its current grid did not expose this stock ID for automatic selection."
                            : "PowerFab did not expose an Inventory window through its current navigation controls.");
                }
                catch (Exception error) { Finish(false, error.Message); }
            }
            timer.Tick += (_, _) => Locate();
            timer.Start();
            try
            {
                bool alreadyOpen = Application.OpenForms.Cast<Form>().Any(f => !f.IsDisposed && !f.InvokeRequired
                    && (Caption(f.Text) == "inventory" || Caption(f.Text).EndsWith(" - inventory", StringComparison.Ordinal)));
                if (alreadyOpen) { Locate(); return; }
                var items = Controls(main).OfType<ToolStrip>().SelectMany(s => MenuItems(s.Items))
                    .Where(i => i.Enabled && i.Available && Caption(i.Text) == "inventory"
                        && (i is not ToolStripDropDownItem drop || drop.DropDownItems.Count == 0)).ToList();
                if (items.Count > 0) { items[0].PerformClick(); return; }
                var button = Controls(main).OfType<Button>().FirstOrDefault(b => b.Enabled && b.Visible && Caption(b.Text) == "inventory");
                if (button != null) { button.PerformClick(); return; }
                Finish(false, "This PowerFab window does not expose an Inventory navigation control to the extension.");
            }
            catch (Exception error) { Finish(false, error.Message); }
        }

        private static bool TrySelect(Form inventory, string id)
        {
            foreach (var grid in Controls(inventory).OfType<DataGridView>().Where(g => g.Visible))
            {
                var names = grid.Columns.Cast<DataGridViewColumn>().SelectMany(c => new[] { c.Name, c.DataPropertyName, c.HeaderText }).ToList();
                if (!InventoryColumns(names)) continue;
                var matches = grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && r.Visible
                    && (Id(r.DataBoundItem) == id || grid.Columns.Cast<DataGridViewColumn>().Any(c => IdColumn(c.DataPropertyName, c.Name, c.HeaderText)
                        && Number(r.Cells[c.Index].Value) == id))).ToList();
                if (matches.Count != 1) continue;
                var row = matches[0]; var cell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);
                if (cell == null) continue;
                grid.ClearSelection(); grid.CurrentCell = cell; row.Selected = true;
                if (!row.Frozen) grid.FirstDisplayedScrollingRowIndex = row.Index;
                grid.Focus(); return true;
            }
            foreach (var list in Controls(inventory).OfType<ListView>().Where(v => v.Visible))
            {
                if (!InventoryColumns(list.Columns.Cast<ColumnHeader>().Select(c => c.Text))) continue;
                var matches = list.Items.Cast<ListViewItem>().Where(i => Id(i.Tag) == id).ToList();
                if (matches.Count != 1) continue;
                foreach (ListViewItem selected in list.SelectedItems.Cast<ListViewItem>().ToArray()) selected.Selected = false;
                matches[0].Selected = true; matches[0].Focused = true; matches[0].EnsureVisible(); list.Focus(); return true;
            }
            return false;
        }

        private static bool InventoryColumns(IEnumerable<string> names)
        {
            var fields = names.Select(Key).ToHashSet(StringComparer.Ordinal);
            return fields.Any(n => n is "ponumber" or "po" or "onorder" or "expectedremnant" or "stock")
                && fields.Any(n => n is "shape" or "dimensions" or "length");
        }
        private static bool IdColumn(params string[] names) => names.Select(Key).Any(n => n is "inventoryitemid" or "inventoryid" or "itemid" or "powerfabid");
        private static string Id(object? item)
        {
            if (item == null) return "";
            if (item is DataRowView view) item = view.Row;
            if (item is DataRow row)
            {
                foreach (DataColumn column in row.Table.Columns)
                    if (IdColumn(column.ColumnName) && Number(row[column]) is string value && value.Length > 0) return value;
                return "";
            }
            foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(item))
                if (IdColumn(property.Name) && Number(property.GetValue(item)) is string value && value.Length > 0) return value;
            return "";
        }
        private static string Number(object? value) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : "";
        private static string Key(string? value) => new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        private static string Caption(string? value) => (value ?? "").Replace("&", "").Trim().TrimEnd('.', '…').ToLowerInvariant();
        private static IEnumerable<Control> Controls(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Controls(child)) yield return nested; }
        }
        private static IEnumerable<ToolStripItem> MenuItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                yield return item;
                if (item is ToolStripDropDownItem drop) foreach (var child in MenuItems(drop.DropDownItems)) yield return child;
            }
        }
    }
}

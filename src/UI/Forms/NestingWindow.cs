using System;
using System.Data;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class NestingWindow : Form
    {
        private readonly string xmlPath;
        private readonly NestingSnapshot snapshot;
        private readonly PowerFabNestApi? api;
        private readonly bool useInventoryNesting;
        private readonly FlowLayoutPanel resultActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = true,
            Padding = new Padding(12, 2, 12, 2), Margin = Padding.Empty
        };
        private readonly TabControl pages = new HanlonTabs { Dock = DockStyle.Fill };
        private readonly TabPage resultPage = new TabPage("Nesting result");
        private readonly TabPage savedPage = new TabPage("Saved nests");
        private readonly TabControl previews = new TabControl { Dock = DockStyle.Fill };
        private readonly Label resultSummary = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(12, 6, 12, 6), AutoEllipsis = true, UseMnemonic = false, UseCompatibleTextRendering = false };
        private readonly Panel previewArea = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty,
            Font = new Font("Segoe UI", 10F), BackColor = SystemColors.Control, ForeColor = SystemColors.ControlText
        };
        private readonly DataTable savedResults = new DataTable();
        private readonly Label inventoryStatus = new Label { Dock = DockStyle.Bottom, Height = 34, AutoEllipsis = true, ForeColor = Color.DimGray, UseMnemonic = false };
        private DataGridView? inventoryGrid;
        private DataGridView? partsGrid;
        private readonly ComboBox resultPicker = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
        private readonly TableLayoutPanel previewNavigation = new TableLayoutPanel
        {
            Dock = DockStyle.None, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        private readonly Button previousSheetButton = SheetButton("‹", "Previous sheet");
        private readonly Button nextSheetButton = SheetButton("›", "Next sheet");
        private readonly List<ResultChoice> resultChoices = new List<ResultChoice>();
        private readonly List<AbeBatchItem> importedAbeLayouts = new List<AbeBatchItem>();
        private sealed class AbeLayoutChoice
        {
            public AbeBatchItem Item = null!;
            public int Number;
            public override string ToString() => "Sheet " + Number + " | " + Item.Name;
        }
        private bool changingResultPicker;
        private bool changingPlateSelection;
        private bool syncingPowerFab;
        private bool fittingPreviewFrame;
        private bool replacingPreviews;
        private bool generatingNesting;
        private bool syncQueued;
        private CancellationTokenSource? abeReadCancellation;
        private DateTime lastSyncUtc=DateTime.MinValue;
        private readonly System.Windows.Forms.Timer inventoryRefreshTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        private readonly ContextMenuStrip abeImportMenu = new HanlonContextMenu();
        private readonly ContextMenuStrip plateSelectionMenu = new ContextMenuStrip();
        private sealed class ResultChoice
        {
            public LoadedNestResult Loaded { get; set; } = null!;
            public string Details { get; set; } = string.Empty;
            public int Number;
            public override string ToString()
            {
                return "Sheet " + Number + " | "
                    + NestNaming.Display(Loaded.Result);
            }
        }
        private Button? saveButton;
        private Button? importButton;
        private readonly List<Button> actionButtons = new List<Button>();
        private LoadedNestResult? loadedResult;
        private LayoutConditions layoutConditions = new LayoutConditions();
        private static readonly Color Brand = HanlonTheme.Brand;

        private NestingWindow(NestingSnapshot snapshot, string xmlPath, Func<string, string>? execute, PowerFabReadModel? readModel)
        {
            this.xmlPath = xmlPath;
            this.snapshot = snapshot;
            api = execute == null ? null : new PowerFabNestApi(execute,readModel);
            abeImportMenu.Items.Add("Select files…",null,(_,_)=>ChooseAbeFiles());
            abeImportMenu.Items.Add("Select folder…",null,(_,_)=>ChooseAbeFolder());
            string settingsFolder = Path.GetDirectoryName(Path.GetDirectoryName(xmlPath)!)!;
            useInventoryNesting = NestingUiOptions.Load(settingsFolder).UseInventoryNesting;
            layoutConditions = LayoutConditions.Load(settingsFolder);
            Text = "Hanlon - Nesting (v1.12)";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 780);
            MinimumSize = new Size(720, 560);
            BackColor = HanlonTheme.Background;
            Font = HanlonTheme.BodyFont();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                Margin = Padding.Empty, Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, useInventoryNesting ? 44 : 24));
            Controls.Add(layout);

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Brand, RowCount = 2,
                ColumnCount = 1, Margin = Padding.Empty, Padding = new Padding(12, 4, 12, 4)
            };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            header.Paint += HanlonTheme.HeaderAccent;
            header.Controls.Add(new Label
            {
                Text = "HANLON", ForeColor = Color.White, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Arial", 18, FontStyle.Bold)
            }, 0, 0);
            header.Controls.Add(new Label
            {
                Text = "Nesting Request", ForeColor = Color.White, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Arial", 11)
            }, 0, 1);
            layout.Controls.Add(header, 0, 0);

            var summary = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Padding = new Padding(18, 4, 18, 4), Margin = Padding.Empty
            };
            summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            summary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            summary.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Arial", 11, FontStyle.Bold), ForeColor = Brand,
                Text = "Work order / combining run: " + Display(snapshot.WorkOrderID)
                    + "    |    Jobs: " + Display(snapshot.Jobs)
            }, 0, 0);
            summary.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Text = "Description: " + Display(snapshot.Description)
            }, 0, 1);
            layout.Controls.Add(summary, 0, 1);

            var tables = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Padding = new Padding(18, 0, 18, 0), Margin = Padding.Empty
            };
            tables.RowStyles.Add(new RowStyle(SizeType.Percent, useInventoryNesting ? 50 : 100));
            tables.RowStyles.Add(new RowStyle(useInventoryNesting ? SizeType.Percent : SizeType.Absolute, useInventoryNesting ? 50 : 0));
            var platePanel = (TableLayoutPanel)TablePanel("Plate parts to nest", snapshot.Parts,
                "No parts were included in this request.", out partsGrid, true);
            ConfigurePlateSelection();
            tables.Controls.Add(platePanel, 0, 0);
            var stockPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            stockPanel.Controls.Add(TablePanel("Inventory/Stocks", snapshot.Inventory,
                "No matching stock. Generate nesting to enter new sheet sizes.", out inventoryGrid, true));
            stockPanel.Controls.Add(inventoryStatus);
            stockPanel.Visible = useInventoryNesting;
            inventoryStatus.Text = "Loading PO numbers and quantities from PowerFab inventory…";
            tables.Controls.Add(stockPanel, 0, 1);
            var requestPage = new TabPage(useInventoryNesting ? "Request / stock" : "Request");
            requestPage.Controls.Add(tables);
            pages.TabPages.Add(requestPage);
            var previewLayout = new Panel { Dock = DockStyle.Fill };
            resultSummary.TextChanged += (_, _) => FitResultSummary();
            resultSummary.SizeChanged += (_, _) => FitResultSummary();
            resultSummary.FontChanged += (_, _) => FitResultSummary();
            resultSummary.Text = useInventoryNesting
                ? "Import ABE nest to view an existing NC/SY2 layout directly. Or tick plates and Generate nesting to calculate a new layout."
                : "Choose Generate nesting to open the ABE import workspace.";
            previewNavigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            previewNavigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            previewNavigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            previewNavigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewNavigation.Controls.Add(previousSheetButton, 0, 0);
            previewNavigation.Controls.Add(previews, 1, 0);
            previewNavigation.Controls.Add(nextSheetButton, 2, 0);
            previousSheetButton.Click += (_, _) => MoveSheet(-1);
            nextSheetButton.Click += (_, _) => MoveSheet(1);
            previewArea.Controls.Add(previewNavigation);
            previewArea.Layout += (_, _) => FitPreviewFrame();
            previews.SizeChanged += (_, _) => FitPreviewFrame();
            previews.SelectedIndexChanged += (_, _) => FitPreviewFrame();
            previewLayout.Controls.Add(previewArea);
            previewLayout.Controls.Add(resultSummary);
            previewLayout.Controls.Add(resultPicker);
            // Saving stays with the reviewed layouts, outside the main action row.
            saveButton = Button("Save nest", (_, _) => SaveNest());
            StylePrimaryAction(saveButton);
            saveButton.Enabled = false;
            importButton = Button("Import ABE nest", (_, _) => ImportAbeNest());
            var clearButton = Button("Clear nest", (_, _) => ClearNest());
            clearButton.TabIndex = 0; importButton.TabIndex = 1; saveButton.TabIndex = 2;
            void MatchImportButtonSize()
            {
                if (importButton != null && !clearButton.IsDisposed && clearButton.Size != importButton.Size)
                    clearButton.Size = importButton.Size;
            }
            importButton.SizeChanged += (_, _) => MatchImportButtonSize();
            clearButton.SizeChanged += (_, _) => MatchImportButtonSize();
            MatchImportButtonSize();
            resultActions.Controls.Add(saveButton);
            resultActions.Controls.Add(importButton);
            resultActions.Controls.Add(clearButton);
            resultActions.Visible = useInventoryNesting;
            previewLayout.Controls.Add(resultActions);
            resultPicker.SelectedIndexChanged += (_, _) =>
            {
                if (changingResultPicker) return;
                UpdateSheetNavigation();
                try
                {
                    if (resultPicker.SelectedItem is AbeLayoutChoice abeChoice) DisplayAbeLayout(abeChoice.Item);
                    else if (resultPicker.SelectedItem is ResultChoice choice) DisplayResult(choice.Loaded, choice.Details);
                }
                catch (Exception exception) { loadedResult = null; saveButton!.Enabled = false; ShowError(exception); }
            };
            resultPage.Controls.Add(previewLayout);
            pages.TabPages.Add(resultPage);
            foreach (string column in new[] { "Sheet name", "Process time", "Sheets", "Project requisition", "Saved requisition", "PO #", "Status", "Saved to", "Saved at" })
                savedResults.Columns.Add(column, typeof(string));
            savedPage.Controls.Add(TablePanel("Saved nests in PowerFab", savedResults,
                "No nests have been saved for this work order.", out DataGridView? savedGrid, true));
            pages.TabPages.Add(savedPage);
            RefreshSavedResults();
            layout.Controls.Add(pages, 0, 2);

            // Flow right-to-left so the buttons stay at the right edge. On very
            // narrow/high-DPI screens wrap complete buttons, never their labels.
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = true,
                Padding = new Padding(12, 4, 12, 4), Margin = Padding.Empty
            };
            var refresh = Button("Refresh Purchase & Inventory", (_, _) => RefreshPurchaseAndInventory());
            var conditions = Button("Layout Condition", (_, _) => EditLayoutConditions());
            var generate = Button("Generate nesting", (_, _) =>
            {
                if (useInventoryNesting) GenerateNesting();
                else OpenAbeWorkspace();
            });
            StylePrimaryAction(generate);
            Button close = Button("Close", (_, _) => Close());
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;
            refresh.TabIndex = 0; conditions.TabIndex = 1; generate.TabIndex = 2; close.TabIndex = 3;
            actions.Controls.Add(close);
            actions.Controls.Add(generate);
            if (useInventoryNesting) actions.Controls.Add(conditions);
            else conditions.Dispose();
            actions.Controls.Add(refresh);
            layout.Controls.Add(actions, 0, 3);

            var status = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 0),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Text = useInventoryNesting ? "Generate nesting calculates sheets required. Save nests links stock or purchasing and records future remnants.\n" + xmlPath
                    : "Import ABE nest, review the layouts, then Save nests.",
                Font = new Font("Arial", 9), ForeColor = HanlonTheme.Muted
            };
            var tip = new ToolTip();
            tip.SetToolTip(status, xmlPath);
            tip.SetToolTip(previousSheetButton, "Previous sheet");
            tip.SetToolTip(nextSheetButton, "Next sheet");
            FormClosed += (_, _) => { inventoryRefreshTimer.Dispose(); tip.Dispose(); ClearPreviews(); };
            layout.Controls.Add(status, 0, 4);
            Activated+=(_,_)=>{if(useInventoryNesting)QueuePowerFabRefresh(true);};
            inventoryRefreshTimer.Tick += (_, _) => QueuePowerFabRefresh();
            if (api != null && useInventoryNesting) inventoryRefreshTimer.Start();
            pages.SelectedIndexChanged+=(_,_)=>{if(pages.SelectedTab==savedPage && useInventoryNesting)QueuePowerFabRefresh();};
            HanlonTheme.Apply(this, previewArea);
            inventoryStatus.ForeColor = status.ForeColor = HanlonTheme.Muted;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            foreach (Button button in actionButtons.Where(b => !b.IsDisposed)) SizeActionButton(button);
            PerformLayout();
            double scale = DeviceDpi / 96.0;
            // Measure the real scaled controls instead of assuming a monitor
            // resolution or treating the complete window as a 2:1 rectangle.
            Size frame = new Size(Width - ClientSize.Width, Height - ClientSize.Height);
            int previewTabHeight = Math.Max(previews.ItemSize.Height, Font.Height + (int)Math.Round(8 * scale));
            // The result page starts hidden, so do not depend on its children's
            // current bounds/Visible values. Reserve their full workspace sizes
            // up front, including the picker and import/save row that appear later.
            Rectangle pageArea = pages.DisplayRectangle;
            int summaryHeight = Math.Max(resultSummary.Height, Font.Height * 3 + resultSummary.Padding.Vertical);
            int actionHeight = Math.Max((int)Math.Round(52 * scale),
                resultActions.GetPreferredSize(new Size(Math.Max(1, pageArea.Width), 0)).Height);
            Size surrounding = new Size(ClientSize.Width - pageArea.Width + (int)Math.Round(104 * scale),
                ClientSize.Height - pageArea.Height + summaryHeight + resultPicker.Height
                    + actionHeight + previewTabHeight + (int)Math.Round(16 * scale));
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size preferred = NestingWindowSizing.Fit(work.Size, frame, surrounding, DeviceDpi);
            MinimumSize = new Size(Math.Min((int)Math.Round(720 * scale), preferred.Width),
                Math.Min((int)Math.Round(560 * scale), preferred.Height));
            StartPosition = FormStartPosition.Manual;
            WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(work.Left + (work.Width - preferred.Width) / 2,
                work.Top + (work.Height - preferred.Height) / 2, preferred.Width, preferred.Height);
        }

        private void FitResultSummary()
        {
            int width = Math.Max(1, resultSummary.ClientSize.Width - resultSummary.Padding.Horizontal);
            int textHeight = TextRenderer.MeasureText(resultSummary.Text, resultSummary.Font,
                new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height;
            int explicitLines = Math.Min(4, resultSummary.Text.Count(c => c == '\n') + 1);
            textHeight = Math.Max(textHeight, resultSummary.Font.Height * explicitLines);
            int height = Math.Clamp(textHeight, resultSummary.Font.Height, resultSummary.Font.Height * 4)
                + resultSummary.Padding.Vertical;
            if (resultSummary.Height != height) resultSummary.Height = height;
        }

        private void FitPreviewFrame()
        {
            if (fittingPreviewFrame || replacingPreviews) return;
            fittingPreviewFrame = true;
            try
            {
                previewNavigation.Visible = previews.TabCount > 0;
                if (previews.TabCount == 0) return;
                double ratio = previews.SelectedTab?.Controls.OfType<ZoomableImageView>().FirstOrDefault()?.ImageAspectRatio ?? 2.0;
                int gutter = (int)Math.Round(previewNavigation.ColumnStyles[0].Width + previewNavigation.ColumnStyles[2].Width);
                // A second pass measures native tab borders after the first resize.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Rectangle display = previews.DisplayRectangle;
                    var chrome = new Size(Math.Max(0, previews.Width - display.Width) + gutter,
                        Math.Max(0, previews.Height - display.Height));
                    previewNavigation.Bounds = PreviewFrameSizing.Fit(previewArea.ClientSize, ratio, chrome,
                        (int)Math.Round(8 * DeviceDpi / 96.0));
                    previewNavigation.PerformLayout();
                    // Centre beside the drawing viewport, excluding its tab
                    // strip and native borders, rather than the complete frame.
                    Rectangle drawing = previews.DisplayRectangle;
                    int side = (int)Math.Round(4 * DeviceDpi / 96.0);
                    var navigationMargin = new Padding(side, Math.Max(0, previews.Top + drawing.Top), side,
                        Math.Max(0, previewNavigation.ClientSize.Height - previews.Top - drawing.Bottom));
                    previousSheetButton.Margin = nextSheetButton.Margin = navigationMargin;
                    previewNavigation.PerformLayout();
                }
            }
            finally { fittingPreviewFrame = false; }
        }

        public static bool Open(NestingSnapshot snapshot, string xmlPath, Func<string, string>? execute, PowerFabReadModel? readModel, out string errorMessage)
        {
            var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    using var form = new NestingWindow(snapshot, xmlPath, execute, readModel);
                    form.Shown += (_, _) =>
                    {
                        form.Activate();
                        started.TrySetResult(null);
                        if(form.useInventoryNesting)form.BeginInvoke(new Action(()=>form.SynchronizePowerFab(false,false)));
                    };
                    Application.Run(form);
                }
                catch (Exception exception)
                {
                    started.TrySetResult(exception.Message);
                    try { File.WriteAllText(xmlPath + ".window-error.txt", exception.ToString()); }
                    catch (Exception) { }
                }
            });
            // A dedicated STA thread is required for the window and its Save dialog.
            // It also keeps the window alive after PowerFab finishes the export call.
            thread.Name = "Hanlon nesting request window";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!started.Task.Wait(TimeSpan.FromSeconds(10)))
            {
                errorMessage = "The window did not finish opening within 10 seconds.";
                return false;
            }
            errorMessage = started.Task.Result ?? string.Empty;
            return string.IsNullOrEmpty(errorMessage);
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value;
        }

        private static Control TablePanel(string title, DataTable data, string emptyMessage, out DataGridView? tableGrid, bool showEmptyGrid = false)
        {
            tableGrid = null;
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = new Padding(0, 0, 0, 10), Padding = Padding.Empty
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var caption = new Label
            {
                Dock = DockStyle.Fill, Text = title + " (" + data.DefaultView.Count + " rows)",
                Font = new Font("Arial", 10.5F, FontStyle.Bold), ForeColor = Brand,
                TextAlign = ContentAlignment.MiddleLeft
            };
            data.DefaultView.ListChanged += (_, _) => caption.Text = title + " (" + data.DefaultView.Count + " rows)";
            panel.Controls.Add(caption, 0, 0);

            if (data.Rows.Count == 0 && !showEmptyGrid)
            {
                panel.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill, Text = emptyMessage, BackColor = Color.White,
                    TextAlign = ContentAlignment.MiddleCenter, BorderStyle = BorderStyle.FixedSingle,
                    Padding = new Padding(16)
                }, 0, 1);
                return panel;
            }

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, DataSource = data, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = true, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                Margin = Padding.Empty
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Brand, ForeColor = Color.White, Font = new Font("Arial", 10.5F, FontStyle.Bold),
                Padding = new Padding(6), SelectionBackColor = Brand, Alignment = DataGridViewContentAlignment.MiddleCenter
            };
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.DefaultCellStyle.Padding = new Padding(5, 2, 5, 2);
            HanlonTheme.StyleGrid(grid);
            grid.RowTemplate.Height = 30;
            void CenterColumn(DataGridViewColumn column)
            {
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
            void ConfigureTableColumns()
            {
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    CenterColumn(column);
                    if (data.Columns[column.DataPropertyName]?.ColumnMapping == MappingType.Hidden) column.Visible = false;
                }
            }
            // Apply to bound/generated columns as well as the overall grid.
            grid.ColumnAdded += (_, e) => CenterColumn(e.Column);
            grid.DataBindingComplete += (_, _) => ConfigureTableColumns();
            ConfigureTableColumns();
            tableGrid = grid;
            panel.Controls.Add(grid, 0, 1);
            return panel;
        }

        private string ExportFolder => Path.GetDirectoryName(xmlPath)!;

        private void SetAllPlateSelection(bool selected)
        {
            partsGrid?.EndEdit();
            changingPlateSelection = true;
            try { foreach (DataRow row in snapshot.Parts.Rows) row["Select"] = selected; }
            finally { changingPlateSelection = false; }
            snapshot.ApplyInventoryFilter();
            if (resultPicker.Items.Count > 0) InvalidateForInputs("Plate selection changed. Generate nesting again.");
        }

        private void ConfigurePlateSelection()
        {
            if (partsGrid == null) return;
            plateSelectionMenu.Font = Font;
            plateSelectionMenu.Items.Add("Select all", null, (_, _) => SetAllPlateSelection(true));
            plateSelectionMenu.Items.Add("Select none", null, (_, _) => SetAllPlateSelection(false));
            partsGrid.ContextMenuStrip = plateSelectionMenu;
            partsGrid.ReadOnly = false; partsGrid.MultiSelect = true;
            void ConfigureColumns()
            {
                if (!partsGrid.Columns.Contains("Select")) return;
                foreach (DataGridViewColumn column in partsGrid.Columns) column.ReadOnly = column.Name != "Select";
                partsGrid.Columns["Select"].Frozen = true;
            }
            partsGrid.DataBindingComplete += (_, _) => ConfigureColumns();
            ConfigureColumns();
            partsGrid.CurrentCellDirtyStateChanged += (_, _) =>
            { if (partsGrid.IsCurrentCellDirty) partsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            partsGrid.CellValueChanged += (_, e) =>
            {
                if (!changingPlateSelection && e.ColumnIndex >= 0 && partsGrid.Columns[e.ColumnIndex].Name == "Select")
                {
                    snapshot.ApplyInventoryFilter();
                    if (resultPicker.Items.Count > 0)
                        InvalidateForInputs("Plate selection changed. Generate nesting again.");
                }
            };
        }

        private void InvalidateResults(string message)
        {
            loadedResult = null;
            importedAbeLayouts.Clear();
            if (saveButton != null) saveButton.Enabled = false;
            changingResultPicker = true;
            try { resultChoices.Clear(); resultPicker.Items.Clear(); resultPicker.Visible = false; }
            finally { changingResultPicker = false; }
            UpdateSheetNavigation();
            resultSummary.Text = message;
        }

        private void ClearNest()
        {
            if (generatingNesting || syncingPowerFab || UseWaitCursor) return;
            ClearPreviews();
            InvalidateResults("Nest cleared. Import ABE nest to start again.");
            if (saveButton != null) saveButton.Text = "Save nest";
            importButton?.Focus();
        }

        private void InvalidateForInputs(string message)
        {
            // A saved layout is a read-only review, independent of current stock.
            if (resultChoices.Count > 0 && NestReview.Pending(resultChoices.Select(c => c.Loaded),
                PowerFabNestApi.ReadReceipts(ExportFolder)).Count == 0)
            {
                RefreshResultReview();
                return;
            }
            if (importedAbeLayouts.Count > 0) PresentAbeLayouts(importedAbeLayouts);
            else InvalidateResults(message);
        }

        private void PresentAbeLayouts(IEnumerable<AbeBatchItem> items)
        {
            resultActions.Visible = true;
            var next = items.Where(i => i.Document != null).ToList();
            importedAbeLayouts.Clear(); importedAbeLayouts.AddRange(next);
            loadedResult = null; resultChoices.Clear(); saveButton!.Enabled = false;
            changingResultPicker = true;
            try
            {
                resultPicker.Items.Clear();
                resultPicker.Items.AddRange(next.Select((item,index) => (object)new AbeLayoutChoice { Item = item, Number = index + 1 }).ToArray());
                resultPicker.Visible = next.Count > 0;
                if (next.Count > 0) resultPicker.SelectedIndex = 0;
            }
            finally { changingResultPicker = false; }
            UpdateSheetNavigation();
            if (next.Count > 0) DisplayAbeLayout(next[0]);
        }

        private void DisplayAbeLayout(AbeBatchItem item)
        {
            AbeNestDocument document = item.Document!;
            Image image = AbePreview(document);
            var page = new TabPage("ABE layout");
            page.Controls.Add(new ZoomableImageView(image,new AbeVectorPreview(document)));
            ReplacePreviewPages(new[] { page });
            loadedResult = null; saveButton!.Enabled = false;
            resultSummary.Text = "Sheet size unconfirmed | Thick: " + document.PowerFabThickness + " mm | "
                + document.ProductionCount + " parts | " + document.PackerCount + " packers | Process time: "
                + AbeProcessTime.DisplayOf(document.Reference);
            pages.SelectedTab = resultPage;
            FitPreviewFrame();
        }

        private void PresentResults(IEnumerable<ResultChoice> choices)
        {
            resultActions.Visible = true;
            var next = choices.ToList();
            for (int i = 0; i < next.Count; i++) next[i].Number = i + 1;
            changingResultPicker = true;
            try
            {
                resultChoices.Clear(); resultChoices.AddRange(next);
                resultPicker.Items.Clear(); resultPicker.Items.AddRange(next.Cast<object>().ToArray());
                resultPicker.Visible = next.Count > 0;
                if (next.Count > 0) resultPicker.SelectedIndex = 0;
            }
            finally { changingResultPicker = false; }
            UpdateSheetNavigation();
            if (next.Count > 0) DisplayResult(next[0].Loaded, next[0].Details);
        }

        private void MoveSheet(int direction)
        {
            if (changingResultPicker) return;
            int index = resultPicker.SelectedIndex + direction;
            if (index >= 0 && index < resultPicker.Items.Count)
                resultPicker.SelectedIndex = index;
        }

        private void UpdateSheetNavigation()
        {
            bool multiple = resultPicker.Items.Count > 1;
            previousSheetButton.Visible = nextSheetButton.Visible = multiple;
            previousSheetButton.Enabled = multiple && resultPicker.SelectedIndex > 0;
            nextSheetButton.Enabled = multiple && resultPicker.SelectedIndex >= 0
                && resultPicker.SelectedIndex < resultPicker.Items.Count - 1;
            // Scale with the form's current DPI when results arrive after startup.
            float width = multiple ? 48F * DeviceDpi / 96F : 0;
            previewNavigation.ColumnStyles[0].Width = width;
            previewNavigation.ColumnStyles[2].Width = width;
            FitPreviewFrame();
        }

        private List<MaterialPlan>? ChooseStock(List<PlateNestGroup> groups, IReadOnlyList<SavedNestReceipt> receipts, int preferredIndex)
        {
            var result = new List<MaterialPlan>();
            if(api==null)throw new InvalidOperationException("Open Hanlon through PowerFab to read the next requisition number.");
            string reference=api.NextRequisitionNumber(),batchId=Guid.NewGuid().ToString("N");
            bool automaticReference=true;
            foreach (var group in groups)
            {
                using var dialog = new MaterialPlanDialog(snapshot, group, receipts, preferredIndex, reference,null,automaticReference);
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Plan == null) return null;
                dialog.Plan.RequisitionBatchId=dialog.Plan.AutoRequisitionNumber?batchId:Guid.NewGuid().ToString("N");result.Add(dialog.Plan);
                reference=dialog.Plan.RequisitionNumber;automaticReference=dialog.Plan.AutoRequisitionNumber;
            }
            return result;
        }

        private string SelectedStockKey()
        {
            if (inventoryGrid?.CurrentRow?.DataBoundItem is not DataRowView row) return string.Empty;
            int index = snapshot.Inventory.Rows.IndexOf(row.Row);
            return index < 0 ? string.Empty : InventoryTracking.Key(LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ElementAt(index));
        }

        private void ReloadInventory(bool invalidateResult)
        {
            if (api == null) throw new InvalidOperationException("Start this window through the PowerFab extension to load inventory POs and quantities.");
            string selectedKey = SelectedStockKey();
            api.RefreshInventory(snapshot, ExportFolder);
            var stocks = LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList();
            int index = stocks.FindIndex(s => InventoryTracking.Key(s) == selectedKey);
            if (index >= 0 && inventoryGrid != null)
            {
                foreach (DataGridViewRow gridRow in inventoryGrid.Rows)
                    if (gridRow.DataBoundItem is DataRowView item && item.Row == snapshot.Inventory.Rows[index])
                    { inventoryGrid.CurrentCell = gridRow.Cells[0]; break; }
            }
            else if (selectedKey.Length > 2 && selectedKey != "I:0" && inventoryGrid != null)
            {
                inventoryGrid.ClearSelection(); inventoryGrid.CurrentCell = null;
            }
            inventoryStatus.Text = stocks.Count == 0
                ? "No matching stock. Generate nesting to enter new sheet sizes for purchasing."
                : "Updated " + DateTime.Now.ToString("T") + ". Available Qty = usable for this job. Reserved / allocated sheets remain visible."
                    + (stocks.Any(StockGeometry.IsFuture)?" Future remnants included: "+stocks.Count(StockGeometry.IsFuture)+".":"");
            if (snapshot.RemnantRefreshWarning.Length > 0)
                inventoryStatus.Text = "Sheets updated at " + DateTime.Now.ToString("T")
                    + ". Some PowerFab remnant details could not be loaded.";
            if (invalidateResult && (loadedResult != null || resultChoices.Count > 0))
                InvalidateForInputs("Inventory refreshed. Generate nesting again before saving.");
        }

        private void QueuePowerFabRefresh(bool returningToWindow=false)
        {
            if(api==null||syncingPowerFab||syncQueued||!IsHandleCreated||!returningToWindow&&DateTime.UtcNow-lastSyncUtc<TimeSpan.FromSeconds(2))return;
            syncQueued=true;
            BeginInvoke(new Action(()=>{
                syncQueued=false;
                if(IsDisposed||!Visible||!Enabled||UseWaitCursor||generatingNesting||OwnedForms.Any(f=>f.Visible))return;
                SynchronizePowerFab(false,false);
            }));
        }

        private bool SynchronizePowerFab(bool showErrors,bool invalidate)
        {
            if(api==null||syncingPowerFab)return api!=null;
            syncingPowerFab=true;UseWaitCursor=true;
            var errors=new List<string>();
            try
            {
                try {api.RefreshPurchaseLinks(snapshot,ExportFolder);}catch(Exception e){errors.Add(e.Message);}
                try {ReloadInventory(invalidate);}catch(Exception e){errors.Add(e.Message);}
                try {RefreshSavedResults();}catch(Exception e){errors.Add(e.Message);}
                if(errors.Count>0)
                {
                    inventoryStatus.Text="PowerFab refresh incomplete. Use Refresh Purchase & Inventory to retry.";
                    if(showErrors)ShowError(new InvalidOperationException(string.Join("\n",errors)));
                }
                return errors.Count==0;
            }
            finally {lastSyncUtc=DateTime.UtcNow;syncingPowerFab=false;UseWaitCursor=false;}
        }

        private void TestApi()
        {
            try
            {
                if (api == null) throw new InvalidOperationException("PowerFab did not supply an API manager. Start this window through the Hanlon export extension.");
                UseWaitCursor = true;
                string version = api.TestConnection();
                MessageBox.Show(this, "PowerFab API connection succeeded.\nVersion: " + version, "Hanlon - API", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { ShowError(exception); }
            finally { UseWaitCursor = false; }
        }

        private void CreateTemplate()
        {
            try
            {
                pages.SelectedIndex = 0;
                if (inventoryGrid?.CurrentRow?.DataBoundItem is not DataRowView selected)
                    throw new InvalidOperationException("Select an available stock row in the Request / stock tab first.");
                int index = snapshot.Inventory.Rows.IndexOf(selected.Row);
                NestMaterial material = LoadedNestResult.CreateTemplate(snapshot, index).Material;
                PlateNestGroup? group = PlateSelection.Groups(snapshot, PowerFabNestApi.ReadReceipts(ExportFolder)).SingleOrDefault(g => g.Matches(material));
                if (group == null) throw new InvalidOperationException("Tick matching plates for this stock before exporting a template.");
                NestResultPackage template = PlateSelection.Template(snapshot, group, index, layoutConditions);
                template.Material = MaterialRequirements.ForSave(template.Material);
                if(api==null)throw new InvalidOperationException("Open Hanlon through PowerFab to assign its REQ number.");
                template.RequisitionNumber = api.NextRequisitionNumber();
                template.AutoRequisitionNumber=true;template.RequisitionBatchId=Guid.NewGuid().ToString("N");
                using var dialog = new SaveFileDialog
                {
                    Title = "Save draft nesting-result template", Filter = "Nesting result (*.json)|*.json",
                    FileName = NestNaming.FileStem(NestNaming.Display(template)) + ".json", DefaultExt = "json", AddExtension = true
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(template, LoadedNestResult.JsonOptions));
                MessageBox.Show(this, "Draft template saved. It contains the selected sheet and matching requested parts. A completed layout, actual PNG images and verified quantities are required before Save nest.",
                    "Hanlon - Result template", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ImportResult()
        {
            using var dialog = new OpenFileDialog { Title = "Import completed nesting result", Filter = "Nesting result (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                LoadedNestResult candidate = LoadedNestResult.Load(dialog.FileName, snapshot);
                PresentResults(new[] { new ResultChoice { Loaded = candidate, Details = string.Empty } });
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ImportAbeNest()
        {
            if(generatingNesting||IsDisposed||Disposing||abeImportMenu.IsDisposed)return;
            // WinForms still uses the dropdown after its Closed notification.
            // Retain this menu across clicks and dispose it with its owner form.
            abeImportMenu.Show(this,PointToClient(Cursor.Position));
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing){abeReadCancellation?.Cancel();abeImportMenu.Dispose();plateSelectionMenu.Dispose();}
            base.Dispose(disposing);
        }

        private void ChooseAbeFiles()
        {
            using var picker=new OpenFileDialog{Title="Select ABE NC programs and/or SY2 files",Filter="All files (*.*)|*.*",CheckFileExists=true,Multiselect=true};
            if(picker.ShowDialog(this)==DialogResult.OK)
            {
                string[] files=picker.FileNames;
                ImportAbeBatch(token=>AbeImportBatch.ReadFiles(files,token));
            }
        }

        private void ChooseAbeFolder()
        {
            using var picker=new FolderBrowserDialog{Description="Select the ABE nesting folder (includes subfolders)",UseDescriptionForTitle=true,ShowNewFolderButton=false};
            if(picker.ShowDialog(this)==DialogResult.OK)
            {
                string folder=picker.SelectedPath;
                ImportAbeBatch(token=>AbeImportBatch.ReadFolder(folder,token));
            }
        }

        private string ArchiveAbeSources(AbeNestDocument document)
        {
            string folder=Path.Combine(ExportFolder,"ImportedABE",document.Reference.NcSha256.Substring(0,12)+document.Reference.Sy2Sha256.Substring(0,12));Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,document.Reference.NcFile),document.NcText);
            File.WriteAllText(Path.Combine(folder,document.Reference.Sy2File),document.Sy2Text);
            File.WriteAllText(Path.Combine(folder,"ABE-program.txt"),document.Reference.ProgramName);
            CopyAbeSheetReport(document,folder);
            return folder;
        }

        private static void CopyAbeSheetReport(AbeNestDocument document,string folder)
        {
            if(document.Reference.ProcessTime is not AbeProcessTime runtime)return;
            AbeProcessTime.Validate(document.Reference);
            byte[] bytes=File.ReadAllBytes(runtime.SourcePath);
            if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))!=runtime.ReportSha256)
                throw new InvalidDataException("The ABE sheet report changed while importing. Select the folder again.");
            File.WriteAllBytes(Path.Combine(folder,runtime.ReportFile),bytes);
            runtime.SourcePath=Path.Combine(folder,runtime.ReportFile);
        }

        private Image AbePreview(AbeNestDocument document)
        {
            string folder=ArchiveAbeSources(document);
            string path=Path.Combine(folder,"ABE-preview.png");
            AbeNcRenderer.Draw(document,path);
            File.WriteAllText(Path.Combine(folder,"ABE-preview.svg"),AbeNestImport.PreviewSvg(document));
            return DecodeImage(File.ReadAllBytes(path));
        }

        private async void ImportAbeBatch(Func<CancellationToken,AbeBatchRead> read)
        {
            if(generatingNesting||IsDisposed||Disposing)return;
            generatingNesting=true;
            using var cancellation=new CancellationTokenSource();
            abeReadCancellation=cancellation;
            bool linkImported=false,presented=false;
            string previousSummary=resultSummary.Text;
            const string readingSummary="Reading NC/SY2 layouts and sheet reports…";
            resultSummary.Text=readingSummary;
            resultActions.Enabled=false;UseWaitCursor=true;
            try
            {
                // Read in the background without opening a progress window.
                // Sheet verification still uses its compact import dialog.
                AbeBatchRead batch=await Task.Run(()=>read(cancellation.Token),cancellation.Token);
                if(IsDisposed||Disposing||cancellation.IsCancellationRequested)return;
                var valid=batch.Items.Where(i=>i.Document!=null).ToList();
                // Capture every pair before matching, even those the user does
                // not preview or whose later PowerFab linking fails.
                foreach(var item in valid)ArchiveAbeSources(item.Document!);
                if(batch.ReportWarnings.Count>0)
                {
                    string reportLog=Path.Combine(ExportFolder,"ImportedABE","sheet-report-reading.txt");
                    File.WriteAllLines(reportLog,batch.ReportWarnings);
                }
                if(valid.Count==0)throw new InvalidDataException("No usable NC/SY2 layouts were found. "+string.Join("\n",batch.Items.Select(i=>i.Name+": "+i.Status).Take(6)));
                PresentAbeLayouts(valid);
                presented=true;
                var issues=batch.Items.Where(i=>i.Document==null).ToList();
                if(issues.Count>0)MessageBox.Show(this,valid.Count+" layouts opened; "+issues.Count+" input issues.\n\n"+string.Join("\n",issues.Select(i=>i.Name+": "+i.Status).Take(6)),"Hanlon - ABE import",MessageBoxButtons.OK,MessageBoxIcon.Information);
                linkImported=api!=null;
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(!IsDisposed&&!Disposing)ShowError(ex);}
            finally
            {
                abeReadCancellation=null;generatingNesting=false;
                if(!IsDisposed&&!Disposing)
                {
                    UseWaitCursor=false;resultActions.Enabled=true;
                    if(!presented&&resultSummary.Text==readingSummary)resultSummary.Text=previousSummary;
                }
            }
            // Link only after the import guard has been released. This is the
            // same review/verification workflow, not an automatic SaveNest call.
            if(linkImported&&!IsDisposed&&!Disposing)LinkAbeLayouts();
        }

        private void LinkAbeLayouts()
        {
            if(generatingNesting||api==null||importedAbeLayouts.Count==0)return;
            var items=importedAbeLayouts.ToList();
            generatingNesting=true;
            var display=new List<ResultChoice>();var skipped=new List<string>();
            try
            {
                partsGrid?.EndEdit();if(!SynchronizePowerFab(true,false))return;
                loadedResult=null;saveButton!.Enabled=false;
                var receipts=PowerFabNestApi.ReadReceipts(ExportFolder);var pending=new List<SavedNestReceipt>();var budget=new AbeBatchBudget();
                string reference=api!.NextRequisitionNumber(),batchId=Guid.NewGuid().ToString("N");
                bool automaticReference=true;
                for(int sheetIndex=0;sheetIndex<items.Count;sheetIndex++)
                {
                    var item=items[sheetIndex];
                    AbeNestDocument document=item.Document!;
                    try
                    {
                        AbePartMatch match=budget.Match(document,snapshot,receipts);
                        var requiredParts=match.Bindings.Select(b=>b.Part).GroupBy(LoadedNestResult.PartKey).Select(g=>g.First()).ToList();
                        CalculatedNest? plan=null;
                        using var material=new MaterialPlanDialog(snapshot,match.Group,receipts.Concat(pending).ToList(),-1,reference,document,automaticReference,choice=>
                        {
                            choice.RequisitionBatchId=choice.AutoRequisitionNumber?batchId:Guid.NewGuid().ToString("N");
                            NestMaterial sheet=MaterialRequirements.ForSave(choice.NewSheet??LoadedNestResult.CreateTemplate(snapshot,choice.StockIndexes.Single()).Material);
                            NestResultPackage template=PlateSelection.ForMaterial(snapshot,match.Group,sheet,new LayoutConditions());
                            template.Parts=requiredParts;
                            template.RequisitionNumber=choice.RequisitionNumber;template.AutoRequisitionNumber=choice.AutoRequisitionNumber;
                            template.RequisitionBatchId=choice.RequisitionBatchId;template.TargetPurchaseOrderNumber=choice.TargetPurchaseOrderNumber;template.SheetNumber=display.Count+1;
                            var step=NestingProgress.ForAbeSheet(document.Reference.ProgramName,sheetIndex+1,items.Count);
                            plan=RunCalculation(step,(token,progress)=>
                            {
                                return AbeNestImport.CreateNcPlan(document,match,template,layoutConditions.MinimumBottomRemnant,token);
                            });
                        });
                        if(material.ShowDialog(this)!=DialogResult.OK||material.Plan==null||plan==null){skipped.Add(item.Name+": sheet selection cancelled.");continue;}
                        reference=material.Plan.RequisitionNumber;automaticReference=material.Plan.AutoRequisitionNumber;
                        string path=NestRenderer.Save(plan,ExportFolder),folder=Path.GetDirectoryName(path)!;
                        File.WriteAllText(Path.Combine(folder,document.Reference.NcFile),document.NcText);
                        File.WriteAllText(Path.Combine(folder,document.Reference.Sy2File),document.Sy2Text);
                        CopyAbeSheetReport(document,folder);
                        AbeNcRenderer.Draw(document,Path.Combine(folder,plan.Package.NestImageFile),plan.SheetLengthMm,plan.SheetWidthMm,true);
                        LoadedNestResult loaded=LoadedNestResult.Load(path,snapshot);
                        PowerFabNestApi.CheckPriorSaves(loaded.Result,receipts.Concat(pending),snapshot);
                        budget.Reserve(loaded.Result);
                        pending.Add(new SavedNestReceipt{WorkOrderId=snapshot.WorkOrderID,NestId=loaded.Result.NestId,MaterialKey=PowerFabNestApi.MaterialKey(loaded.Result.Material),Parts=loaded.Result.Parts,SheetQuantity=1,SavedAtUtc=DateTime.UtcNow});
                        item.Status="Ready to save";
                        display.Add(new ResultChoice{Loaded=loaded,Details="ABE: "+document.Reference.ProgramName+" | "+document.ProductionCount+" job parts | "+document.PackerCount+" packers | ABE cutting geometry, positions and rotations retained."});
                    }
                    catch(OperationCanceledException){skipped.Add(item.Name+": verification cancelled. Remaining files were not linked.");break;}
                    catch(Exception ex){item.Status=ex.Message;skipped.Add(item.Name+": "+ex.Message);}
                }
                SaveAbeDisplayNames(display);
                foreach(var choice in display)choice.Details+=" | "+display.Count+" sheets ready; "+skipped.Count+" skipped. Review, then Save all nests.";
                string report=Path.Combine(ExportFolder,"ImportedABE","batch-"+batchId+"-report.txt");Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllLines(report,display.Select(c=>"READY: "+c.Loaded.Result.AbeImport!.ProgramName+" | "+c.Loaded.Result.Material.Thickness+" "+c.Loaded.Result.Material.Unit).Concat(skipped.Select(s=>"SKIPPED: "+s)));
                if(display.Count>0)PresentResults(display);
                else PresentAbeLayouts(items);
                if(skipped.Count>0)MessageBox.Show(this,display.Count+" sheets ready to save; "+skipped.Count+" skipped.\n\n"+string.Join("\n",skipped.Take(6))+"\n\nFull report: "+report,"Hanlon - ABE import",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(OperationCanceledException){}
            catch(Exception ex){PresentAbeLayouts(items);ShowError(ex);}
            finally
            {
                try
                {
                    if(display.Count>0&&resultChoices.Count==0){SaveAbeDisplayNames(display);PresentResults(display);}
                }
                catch(Exception ex){loadedResult=null;saveButton!.Enabled=false;ShowError(ex);}
                finally{UseWaitCursor=false;generatingNesting=false;}
            }
        }

        private static void SaveAbeDisplayNames(IEnumerable<ResultChoice> choices)
        {
            var sheets = choices.ToList();
            NestNaming.AssignAbeNames(sheets.Select(c => c.Loaded.Result));
            foreach (var sheet in sheets)
            {
                string path = sheet.Loaded.SourceFile, temporary = path + ".names.tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(sheet.Loaded.Result, LoadedNestResult.JsonOptions));
                File.Move(temporary, path, true);
            }
        }

        private void DisplayResult(LoadedNestResult candidate, string details)
        {
                var receipts = PowerFabNestApi.ReadReceipts(ExportFolder);
                bool saved = NestReview.IsSaved(candidate, receipts);
                if (!saved) PowerFabNestApi.CheckPriorSaves(candidate.Result, receipts, snapshot);
                var images = new System.Collections.Generic.List<Image>();
                try
                {
                    images.Add(DecodeImage(candidate.NestImage));
                    foreach (byte[]? bytes in candidate.RemnantImages) images.Add(DecodeImage(bytes!));
                }
                catch { foreach (Image image in images) image.Dispose(); throw; }
                var nextPages = new List<TabPage>();
                for (int i = 0; i < images.Count; i++)
                {
                    string title = i == 0 ? "Nest" : "Remnant" + (string.IsNullOrWhiteSpace(candidate.Result.Remnants[i - 1].Edge) ? "" : " (" + candidate.Result.Remnants[i - 1].Edge + ")")
                        + ": " + candidate.Result.Remnants[i - 1].Length.ToString("0.###")
                        + " x " + candidate.Result.Remnants[i - 1].Width.ToString("0.###") + " " + candidate.Result.Remnants[i - 1].Unit;
                    var page = new TabPage(title);
                    IPreviewDrawing? drawing=null;
                    if(i==0&&candidate.AbeDocument!=null)
                    {
                        var material=candidate.Result.Material;
                        decimal factor=material.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4m:1m;
                        drawing=new AbeVectorPreview(candidate.AbeDocument,(double)(material.Length*factor),(double)(material.Width*factor));
                    }
                    page.Controls.Add(new ZoomableImageView(images[i],drawing));
                    nextPages.Add(page);
                }
                ReplacePreviewPages(nextPages);
                loadedResult = candidate;
                NestMaterial m = candidate.Result.Material;
                decimal mm = m.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m;
                string Measure(decimal value) => (value * mm).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                int packers = candidate.Result.AbeImport?.Packers.Count ?? 0;
                int parts = candidate.Result.Parts.Sum(p => p.Quantity);
                resultSummary.Text = "Requisition: " + Display(candidate.Result.RequisitionNumber)
                    + " | Parts: " + parts + " | Remnants: " + candidate.Result.Remnants.Count
                    + "\nSheet: " + Measure(m.Length) + " x " + Measure(m.Width) + " - Thick: " + Measure(m.Thickness) + " - Grade: " + m.Grade
                    + (candidate.Result.AbeImport == null ? (useInventoryNesting && !string.IsNullOrWhiteSpace(details) ? "\n" + details : "")
                        : "\nSheet name: " + NestNaming.Display(candidate.Result) + " | " + packers + " packers | Process time: "
                            + AbeProcessTime.DisplayOf(candidate.Result.AbeImport))
                    + (saved ? "\nSaved to PowerFab" : "");
                var pending = NestReview.Pending(resultChoices.Count > 0
                    ? resultChoices.Select(c => c.Loaded) : new[] { candidate }, receipts);
                saveButton!.Text = pending.Count == 0 ? "All nests saved"
                    : pending.Count < resultChoices.Count ? "Save remaining nests"
                    : pending.Count > 1 ? "Save all nests" : "Save nest";
                saveButton.Enabled = api != null && pending.Count > 0;
                pages.SelectedTab = resultPage;
                FitPreviewFrame();
        }

        private void RefreshResultReview()
        {
            int preview = previews.SelectedIndex;
            if (resultPicker.SelectedItem is ResultChoice choice) DisplayResult(choice.Loaded, choice.Details);
            else if (loadedResult != null) DisplayResult(loadedResult, string.Empty);
            if (preview >= 0 && preview < previews.TabCount) previews.SelectedIndex = preview;
            UpdateSheetNavigation();
        }

        private void OpenAbeWorkspace()
        {
            if (generatingNesting) return;
            partsGrid?.EndEdit();
            resultActions.Visible = true;
            pages.SelectedTab = resultPage;
            if (resultPicker.Items.Count == 0)
                resultSummary.Text = "Import ABE nest to prepare the layouts for PowerFab.";
            importButton?.Focus();
        }

        private void GenerateNesting()
        {
            if(generatingNesting)return;
            generatingNesting=true;
            try
            {
                partsGrid?.EndEdit();
                string preferredKey = SelectedStockKey();
                if (api != null&&!SynchronizePowerFab(true,false))return;
                var receipts = PowerFabNestApi.ReadReceipts(ExportFolder);
                var groups = PlateSelection.Groups(snapshot, receipts);
                int preferredIndex = LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList().FindIndex(s => InventoryTracking.Key(s) == preferredKey);
                var choices = ChooseStock(groups, receipts, preferredIndex);
                if (choices == null) return;
                var templates = choices.Select(c => PlateSelection.ForMaterial(snapshot,c.Group,
                    c.NewSheet ?? LoadedNestResult.CreateTemplate(snapshot,c.StockIndexes[0]).Material,layoutConditions)).ToList();
                InvalidateResults("Calculating sheets and purchasing requirements…");
                Dictionary<string,PowerFabDxf> source;
                UseWaitCursor=true;
                try { var parts=templates.SelectMany(t=>t.Parts).ToList(); source=api==null?PowerFabDxfSource.Read(snapshot,parts,null,ExportFolder):api.ReadPartDxfs(snapshot,parts,ExportFolder); }
                finally { UseWaitCursor=false; }
                var rules=layoutConditions.Copy();int total=groups.Sum(g=>g.Quantity);
                var initial=NestingProgress.ForMaterial(templates[0].Material,groups[0].Quantity,0,0,0,"Preparing placement…").ForBatch(1,groups.Count,0,total);
                var plans=RunCalculation(initial,(token,progress)=> {
                    var results=new List<CalculatedNest>();var shapes=new Dictionary<string,DxfShape>();var cache=new Dictionary<PowerFabDxf,DxfShape>();
                    foreach(var p in templates.SelectMany(t=>t.Parts)) {
                        token.ThrowIfCancellationRequested();var drawing=source[LoadedNestResult.PartKey(p)];
                        if(!cache.TryGetValue(drawing,out var shape)){shape=DxfGeometry.Read(drawing.Text,drawing.SourceName);cache.Add(drawing,shape);}
                        shapes[LoadedNestResult.PartKey(p)]=shape;
                    }
                    int before=0;
                    for(int i=0;i<choices.Count;i++) {
                        int number=i+1,processedBefore=before;
                        results.AddRange(NestingBatch.Calculate(snapshot,choices[i],receipts,rules,shapes,token,
                            update=>progress.Report(update.ForBatch(number,groups.Count,processedBefore,total))));
                        before+=groups[i].Quantity;
                    }
                    return results;
                });
                var display=new List<ResultChoice>();var reserved=new List<SavedNestReceipt>();
                int purchases=plans.Count(p=>p.Package.Material.Source.Equals("Purchase",StringComparison.OrdinalIgnoreCase));
                foreach(var plan in plans) {
                    string path=NestRenderer.Save(plan,ExportFolder);var loaded=LoadedNestResult.Load(path,snapshot);
                    PowerFabNestApi.CheckPriorSaves(loaded.Result,receipts.Concat(reserved),snapshot);
                    reserved.Add(new SavedNestReceipt{WorkOrderId=snapshot.WorkOrderID,NestId=loaded.Result.NestId,
                        MaterialKey=PowerFabNestApi.MaterialKey(loaded.Result.Material),Parts=loaded.Result.Parts,SheetQuantity=1,SavedAtUtc=DateTime.UtcNow});
                    string details="Sheets: "+plans.Count+" | Sheets beyond listed stock: "+purchases;
                    if(plan.Unplaced.Count>0)details+=" | Unplaced: "+string.Join("; ",plan.Unplaced);
                    display.Add(new ResultChoice{Loaded=loaded,Details=details});
                }
                PresentResults(display);
            }
            catch(OperationCanceledException) { }
            catch(Exception exception) { ShowError(exception); }
            finally { generatingNesting=false; }
        }

        private T RunCalculation<T>(NestingProgress initial, Func<CancellationToken, IProgress<NestingProgress>, T> calculate) where T : class
        {
            using var cancellation = new CancellationTokenSource();
            using var dialog = new NestingProgressDialog(Font, initial);
            dialog.CancelRequested += () => cancellation.Cancel();
            T? result = null; Exception? error = null;
            dialog.Shown += async (_, _) =>
            {
                try
                {
                    var progress = new Progress<NestingProgress>(update => { if (!dialog.IsDisposed) dialog.UpdateProgress(update); });
                    result = await Task.Run(() => calculate(cancellation.Token, progress), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                catch (Exception exception) { error = exception; }
                finally { dialog.Close(); }
            };
            dialog.ShowDialog(this);
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result ?? throw new OperationCanceledException();
        }

        private void EditLayoutConditions()
        {
            try
            {
                using var dialog = new LayoutConditionDialog(layoutConditions.Copy());
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                LayoutConditions edited = dialog.Values;
                edited.Save(Path.GetDirectoryName(ExportFolder)!);
                layoutConditions = edited;
                if (resultPicker.Items.Count > 0)
                    InvalidateForInputs("Layout conditions changed. Generate nesting again before saving.");
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void SaveNest()
        {
            if(api==null||loadedResult==null)return;
            var pending=NestReview.Pending(resultChoices.Count>0?resultChoices.Select(c=>c.Loaded):new[]{loadedResult},
                PowerFabNestApi.ReadReceipts(ExportFolder));
            if(pending.Count==0){RefreshResultReview();return;}
            saveButton!.Enabled=false;UseWaitCursor=true;
            var saved=new List<SavedNestReceipt>();string warning="";
            try {
                using(api.BeginRequisitionSave(pending.Any(p=>p.Result.AutoRequisitionNumber)))
                {
                    api.PrepareRequisitionBatch(pending,ExportFolder);
                    foreach(var item in pending) {
                        saved.Add(api.Save(item,snapshot,ExportFolder));
                    }
                }
                try { ReloadInventory(false);
                    if(snapshot.RemnantRefreshWarning.Length>0)warning="\nPowerFab inventory updated; some remnant details could not be loaded.";
                } catch(Exception ex) { warning="\nInventory refresh: "+ex.Message; }
                int remnants=saved.Sum(r=>r.RemnantPowerFabIds.Count);
                RefreshSavedResults();RefreshResultReview();
                if(remnants>0 && warning.Length==0)inventoryStatus.Text=remnants+" future remnant(s) saved. Inventory/Stocks refreshed; check Status and Available Qty.";
                string destinations=string.Join("\n",saved.Select(r=>r.NestName+" | "+r.StockStatus
                    +(r.RequisitionNumber.Length>0?" | Requisition: "+r.RequisitionNumber:"")
                    +(r.CurrentPurchaseOrders.Count>0?" | PO: "+string.Join(", ",r.CurrentPurchaseOrders):"")
                    +" | Nest ID: "+Display(r.PowerFabNestId)+" | Remnants: "+Display(string.Join(", ",r.RemnantPowerFabIds))
                    +(r.OutlineWarning.Length>0?"\n"+r.OutlineWarning:"")));
                MessageBox.Show(this,"PowerFab confirmed "+saved.Count+" nest save(s).\n"+destinations
                    +(remnants>0?"\nFuture remnants were saved with the nest and refreshed in Inventory/Stocks. They become physical stock after the parent nest is cut in PowerFab.":"")+warning,
                    "Hanlon - Nests saved",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception ex) {
                RefreshSavedResults();
                try { RefreshResultReview(); }
                catch { saveButton!.Enabled=false;UpdateSheetNavigation(); }
                ShowError(new InvalidOperationException(ex.Message+(saved.Count>0?"\nAlready confirmed: "+saved.Count+" nest(s). These were not submitted again.":""),ex));
            }
            finally { UseWaitCursor=false; }
        }

        private void RefreshPurchaseAndInventory()
        {
            TabPage? currentPage = pages.SelectedTab;
            SynchronizePowerFab(true,true);
            if (currentPage != null && pages.TabPages.Contains(currentPage)) pages.SelectedTab = currentPage;
        }

        private void RefreshSavedResults()
        {
            savedResults.Rows.Clear();var receipts=PowerFabNestApi.ReadReceipts(ExportFolder);snapshot.RefreshQuantities(receipts);
            var jobs=snapshot.PartNodes().Select(p=>NestingSnapshot.Field(p,"JobNumber","Job")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var r in receipts.Where(r=>!r.InactiveInPowerFab&&r.Parts.Any(p=>jobs.Contains(p.JobNumber))).OrderByDescending(r=>r.SavedAtUtc))
                savedResults.Rows.Add(r.NestName.Length>0?r.NestName:NestNaming.ForParts(r.Parts),r.ProcessTime?.Display??"Not available",r.SheetQuantity,
                    r.ProjectRequisitionReference,r.RequisitionNumber,string.Join(", ",r.CurrentPurchaseOrders.DefaultIfEmpty(r.PurchaseOrderNumber)),
                    r.StockStatus,r.SavedTo,r.SavedAtUtc.ToLocalTime().ToString("g"));
        }

        private static Image DecodeImage(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using Image image = Image.FromStream(stream, true, true);
            return new Bitmap(image);
        }

        private void ReplacePreviewPages(IReadOnlyList<TabPage> nextPages)
        {
            var previousPages = previews.TabPages.Cast<TabPage>().ToArray();
            replacingPreviews = true;
            previewNavigation.SuspendLayout(); previews.SuspendLayout();
            try
            {
                // Removing the selected tab can fire SelectedIndexChanged with
                // zero tabs. Adding the first replacement need not fire it again,
                // especially after that intermediate event hid the parent panel.
                previews.TabPages.Clear();
                previews.TabPages.AddRange(nextPages.ToArray());
                if (previews.TabCount > 0) previews.SelectedIndex = 0;
            }
            finally
            {
                replacingPreviews = false;
                previews.ResumeLayout(false); previewNavigation.ResumeLayout(false);
                foreach (TabPage page in previousPages)
                {
                    foreach (PictureBox picture in page.Controls.OfType<PictureBox>()) picture.Image?.Dispose();
                    page.Dispose();
                }
                // Always restore visibility and layout after the full update.
                // Do not rely on another tab event, changed label height or resize.
                FitPreviewFrame();
                previews.Invalidate(true);
            }
        }

        private void ClearPreviews()
        {
            ReplacePreviewPages(Array.Empty<TabPage>());
        }

        private void ShowError(Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Hanlon - Nesting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private Button Button(string text, EventHandler action)
        {
            var button = new HanlonButton
            {
                Text = text, AutoSize = false, Dock = DockStyle.None, UseMnemonic = false,
                Padding = new Padding(12, 2, 12, 2), Margin = new Padding(4),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter
            };
            button.FlatAppearance.BorderColor = HanlonTheme.InputLine;
            void Measure() => SizeActionButton(button);
            button.TextChanged += (_, _) => Measure();
            button.FontChanged += (_, _) => Measure();
            button.DpiChangedAfterParent += (_, _) => Measure();
            button.PaddingChanged += (_, _) => Measure();
            Measure();
            actionButtons.Add(button);
            button.Click += action;
            return button;
        }

        private static void SizeActionButton(Button button)
        {
            double scale = button.DeviceDpi / 96.0;
            int minimum = button.Text == "Close" ? 96 : 156;
            int textWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
            button.Size = new Size(Math.Max((int)Math.Round(minimum * scale), textWidth + button.Padding.Horizontal + (int)Math.Round(8 * scale)),
                Math.Max((int)Math.Round(40 * scale), button.Font.Height + button.Padding.Vertical + (int)Math.Round(12 * scale)));
        }

        private static void StylePrimaryAction(Button button)
        {
            if (button is HanlonButton branded) branded.Primary = true;
            button.BackColor = Brand; button.ForeColor = HanlonTheme.Paper;
        }

        private static Button SheetButton(string text, string accessibleName)
        {
            return new SheetNavigationButton
            {
                Forward = text == "›", AccessibleName = accessibleName, UseMnemonic = false,
                Font = new Font("Segoe UI", 24F, FontStyle.Bold), Size = new Size(40, 56),
                Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(4),
                Enabled = false, Visible = false, BackColor = Color.White,
                FlatStyle = FlatStyle.Standard, UseVisualStyleBackColor = false
            };
        }

        private void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not open", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveXmlAs()
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save nesting request XML", Filter = "XML files (*.xml)|*.xml",
                FileName = NestNaming.FileStem(NestNaming.ForRequest(snapshot)) + ".xml", DefaultExt = "xml", AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(xmlPath), StringComparison.OrdinalIgnoreCase))
                    File.Copy(xmlPath, dialog.FileName, true);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class NestingSnapshot
    {
        private const string InventoryMatchColumn = "__MatchesSelectedMaterial";
        public XElement RequestRoot { get; private set; } = new XElement("WorkOrder");
        public string OriginalRequestXml { get; private set; } = string.Empty;
        public DateTime ExportTimeUtc {get;private set;}=DateTime.UtcNow;
        public string WorkOrderID { get; private set; } = string.Empty;
        public string Description { get; private set; } = string.Empty;
        public string Jobs { get; private set; } = string.Empty;
        public DataTable Parts { get; private set; } = new DataTable();
        public DataTable Inventory { get; private set; } = new DataTable();
        public IReadOnlyList<XElement> OriginalStock { get; private set; } = Array.Empty<XElement>();
        public HashSet<string> InventoryReceiptBaseline { get; } = new HashSet<string>(StringComparer.Ordinal);
        public bool LiveInventoryLoaded { get; private set; }
        public string RemnantRefreshWarning { get; internal set; } = string.Empty;

        public static NestingSnapshot FromDocument(XDocument document)
        {
            XElement root = document.Root ?? throw new ArgumentException("The request has no XML root.");
            var snapshot = new NestingSnapshot
            {
                RequestRoot = new XElement(root),
                OriginalRequestXml = root.ToString(),
                WorkOrderID = Field(root, "WorkOrderID", "CombinationRunID", "CombiningRunID"),
                Description = Field(root, "WorkOrderDescription", "Description"),
                Parts = NewTable("Job #", "Main mark", "Piece mark", "Qty", "Shape", "Dimensions", "Length", "Grade", "Width", "Thick", "Sequence", "Lot"),
                Inventory = NewTable("Type", "Status", "Job #", "Lot", "On order", "Cut", "PO # / Stock ID", "Qty", "Available Qty", "Location", "Stock", "Expected remnant", "Shape", "Dimensions", "Length", "Grade", "Width", "Thick", "PowerFab ID", "Remnant ID")
            };
            snapshot.OriginalStock = LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Select(e => new XElement(e)).ToList();
            snapshot.Inventory.Columns.Add(new DataColumn(InventoryMatchColumn, typeof(bool))
                { DefaultValue = false, ColumnMapping = MappingType.Hidden });
            snapshot.Inventory.DefaultView.RowFilter = "[" + InventoryMatchColumn + "] = true";
            if (DateTime.TryParse(Field(root,"CreationDateTime"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out DateTime exported)) snapshot.ExportTimeUtc=exported.ToUniversalTime();
            foreach (XElement part in snapshot.PartNodes())
                snapshot.Parts.Rows.Add(Field(part, "JobNumber", "Job"), Field(part, "MainMark"), Field(part, "PieceMark"),
                    Field(part, "Quantity"), Field(part, "Shape"), PartDimensions(part), Measurement(part, "Length"),
                    Field(part, "Grade"), Measurement(part, "Width"), Measurement(part, "PlateThickness", "Thickness"),
                    Field(part, "Sequence"), Field(part, "LotNumber", "Lot"));
            var selected = new DataColumn("Select", typeof(bool)) { DefaultValue = true };
            snapshot.Parts.Columns.Add(selected);
            selected.SetOrdinal(0);
            snapshot.Jobs = string.Join(", ", snapshot.PartNodes().Select(p => Field(p, "JobNumber", "Job"))
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            snapshot.RefreshQuantities(Array.Empty<SavedNestReceipt>());
            return snapshot;
        }

        public void ReplaceInventory(IEnumerable<XElement> records, IEnumerable<SavedNestReceipt> baseline)
        {
            var container = Child(RequestRoot, "AvailableInventory", "Inventory");
            if (container == null) { container = new XElement(RequestRoot.Name.Namespace + "AvailableInventory"); RequestRoot.Add(container); }
            container.ReplaceNodes(records.Select(e => new XElement(e)));
            InventoryReceiptBaseline.Clear();
            foreach (var receipt in baseline) InventoryReceiptBaseline.Add(receipt.NestId);
            LiveInventoryLoaded = true;
            RefreshQuantities(baseline);
        }

        public void RefreshQuantities(IEnumerable<SavedNestReceipt> receipts)
        {
            var saved = receipts.Where(r=>!r.PartsReleased).ToList();
            Inventory.Rows.Clear();
            foreach (XElement stock in LoadedNestResult.InventoryNodes(RequestRoot))
            {
                string po = Field(stock, "PONumber", "PurchaseOrderNumber", "PO");
                string quantity = Field(stock, "Quantity");
                if (decimal.TryParse(quantity, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal count))
                    quantity = Math.Max(0, count - InventoryTracking.UnreflectedSaves(this, stock, saved)).ToString("0.####", CultureInfo.InvariantCulture);
                string available = InventoryTracking.UnavailableReason(stock, this).Length == 0 ? quantity : "0";
                Inventory.Rows.Add(StockGeometry.IsFuture(stock) ? "Future remnant" : StockGeometry.IsRemnant(stock) ? "Remnant" : "Sheet",
                    InventoryTracking.Status(stock, this), Field(stock, "Job", "JobNumber"), Field(stock, "LotNumber", "Lot"),
                    YesNo(stock, "OnOrder"), YesNo(stock, "Cut", "TakenFromStock"), po.Length == 0 ? "(no PO)" : po,
                    quantity, available, Field(stock, "Location"), YesNo(stock, "Stock"),
                    StockGeometry.IsFuture(stock) ? "Yes" : YesNo(stock, "ExpectedRemnant", "ExpectedDrop"),
                    Field(stock, "Shape"), Field(stock, "Dimensions"), Measurement(stock, "Length"), Field(stock, "Grade"),
                    Measurement(stock, "Width"), Measurement(stock, "PlateThickness", "Thickness"),
                    Field(stock, "ID", "PowerFabID", "InventoryID", "InventoryItemID"),
                    Field(stock, "RemnantPowerFabID", "ParentRemnantPowerFabID", "RemnantID", "ExternalRemnantID", "ParentRemnantID"));
            }
            var nodes = PartNodes().ToList();
            for (int i = 0; i < nodes.Count; i++)
            {
                string key = LoadedNestResult.PartKey(nodes[i]);
                int used = saved.Where(r => r.WorkOrderId == WorkOrderID).SelectMany(r => r.Parts)
                    .Where(p => LoadedNestResult.PartKey(p) == key).Sum(p => p.Quantity);
                int earlier = nodes.Take(i).Where(p => LoadedNestResult.PartKey(p) == key).Sum(p => int.TryParse(Field(p, "Quantity"), out int n) ? n : 0);
                int total = int.TryParse(Field(nodes[i], "Quantity"), out int q) ? q : 0;
                Parts.Rows[i]["Qty"] = Math.Max(0, total - Math.Max(0, used - earlier)).ToString(CultureInfo.InvariantCulture);
            }
            ApplyInventoryFilter();
        }

        internal void ApplyInventoryFilter()
        {
            var parts = PartNodes().ToList();
            var selected = parts.Where((part, index) => Parts.Rows[index]["Select"] is bool active && active
                && int.TryParse(Convert.ToString(Parts.Rows[index]["Qty"], CultureInfo.InvariantCulture), out int quantity) && quantity > 0)
                .Select(part => (Shape: Field(part, "Shape"), Grade: Field(part, "Grade"),
                    Thickness: StockGeometry.Mm(part, "PlateThickness", "Thickness")))
                .Where(part => part.Thickness > 0).Distinct().ToList();
            var stocks = LoadedNestResult.InventoryNodes(RequestRoot).ToList();
            for (int index = 0; index < Inventory.Rows.Count; index++)
            {
                XElement stock = stocks[index];
                decimal thickness = StockGeometry.Mm(stock, "PlateThickness", "Thickness");
                Inventory.Rows[index][InventoryMatchColumn] = LiveInventoryLoaded && thickness > 0 && selected.Any(part =>
                    part.Shape.Equals(Field(stock, "Shape"), StringComparison.OrdinalIgnoreCase)
                    && part.Grade.Equals(Field(stock, "Grade"), StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(part.Thickness - thickness) <= 0.01m);
            }
        }

        internal IEnumerable<XElement> PartNodes() => Child(RequestRoot, "WorkOrderParts", "Parts")?.Elements()
            .Where(e => Named(e, "WorkOrderPart", "Part")) ?? Enumerable.Empty<XElement>();
        private static DataTable NewTable(params string[] names)
        {
            var table = new DataTable(); foreach (string name in names) table.Columns.Add(name, typeof(string)); return table;
        }
        internal static bool Named(XElement element, params string[] names) => names.Any(n => string.Equals(element.Name.LocalName, n, StringComparison.OrdinalIgnoreCase));
        internal static XElement? Child(XElement element, params string[] names) => names.Select(n => element.Elements().FirstOrDefault(e => Named(e, n))).FirstOrDefault(e => e != null);
        internal static string Field(XElement element, params string[] names)
        {
            XElement? value = Child(element, names); return value == null ? string.Empty : (Child(value, "Value")?.Value ?? value.Value).Trim();
        }
        private static string YesNo(XElement element, params string[] names)
        {
            string value = Field(element, names); return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase) ? "Yes" : "";
        }
        private static string PartDimensions(XElement part)
        {
            string value = Field(part, "Dimensions"); if (value.Length > 0) return value;
            return Field(part, "PlateThickness", "Thickness") + " x " + Field(part, "Width");
        }
        private static string Measurement(XElement element, params string[] names)
        {
            XElement? value = Child(element, names); if (value == null) return string.Empty;
            string unit = value.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("UOM", StringComparison.OrdinalIgnoreCase))?.Value ?? Field(value, "UOM", "Unit");
            return (Field(element, names) + (unit.Length == 0 ? "" : " " + unit)).Trim();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class PlateNestGroup
    {
        public string Shape { get; set; } = string.Empty;
        public string Grade { get; set; } = string.Empty;
        public decimal ThicknessMm { get; set; }
        public Dictionary<string, int> Quantities { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Quantity => Quantities.Values.Sum();
        public string Label => Shape + " | " + Grade + " | " + ThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm | " + Quantity + " parts";
        public bool Matches(NestMaterial m) => string.Equals(Shape, m.Shape.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Grade, m.Grade.Trim(), StringComparison.OrdinalIgnoreCase)
            && Math.Abs(ThicknessMm - m.Thickness * (m.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m)) <= 0.01m;
    }

    internal sealed class PlateStockOption
    {
        public int InventoryIndex { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    internal static class PlateSelection
    {
        // Checkbox state belongs to the source DataRow, so sorting the grid cannot
        // change the selected part identities or quantities.
        public static List<PlateNestGroup> Groups(NestingSnapshot snapshot, IEnumerable<SavedNestReceipt> receipts)
        {
            snapshot.RefreshQuantities(receipts);
            var nodes = snapshot.PartNodes().ToList();
            var groups = new Dictionary<string, PlateNestGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++)
            {
                DataRow row = snapshot.Parts.Rows[i];
                if (row["Select"] is not bool selected || !selected) continue;
                if (!int.TryParse((string)row["Qty"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity) || quantity <= 0) continue;
                XElement part = nodes[i];
                string shape = F(part, "Shape"), grade = F(part, "Grade");
                XElement? thickness = NestingSnapshot.Child(part, "PlateThickness", "Thickness");
                string unit = thickness?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("UOM", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                if (!decimal.TryParse(F(part, "PlateThickness", "Thickness"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal mm)
                    || mm <= 0 || !(unit.Equals("MM", StringComparison.OrdinalIgnoreCase) || unit.Equals("IN", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The selected plate is missing its thickness or unit: " + F(part, "PieceMark"));
                if (unit.Equals("IN", StringComparison.OrdinalIgnoreCase)) mm *= 25.4m;
                mm = decimal.Round(mm, 4, MidpointRounding.AwayFromZero);
                string key = shape.Trim().ToUpperInvariant() + "\u001f" + grade.Trim().ToUpperInvariant() + "\u001f" + mm.ToString("G29", CultureInfo.InvariantCulture);
                if (!groups.TryGetValue(key, out PlateNestGroup? group))
                { group = new PlateNestGroup { Shape = shape, Grade = grade, ThicknessMm = mm }; groups.Add(key, group); }
                string identity = LoadedNestResult.PartKey(part);
                group.Quantities[identity] = checked((group.Quantities.TryGetValue(identity, out int n) ? n : 0) + quantity);
            }
            if (groups.Count == 0) throw new InvalidOperationException("Tick at least one plate with remaining quantity, or click Select all.");
            return groups.Values.OrderBy(g => g.Shape, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Grade, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.ThicknessMm).ToList();
        }

        public static NestResultPackage Template(NestingSnapshot snapshot, PlateNestGroup group, int stockIndex, LayoutConditions conditions)
        {
            return ForMaterial(snapshot, group, LoadedNestResult.CreateTemplate(snapshot, stockIndex).Material, conditions);
        }

        public static NestResultPackage ForMaterial(NestingSnapshot snapshot, PlateNestGroup group, NestMaterial material, LayoutConditions conditions)
        {
            NestResultPackage template = LoadedNestResult.CreateForMaterial(snapshot, material);
            if (!group.Matches(template.Material)) throw new InvalidOperationException("Choose matching stock for " + group.Label + ".");
            template.LayoutConditions = conditions.Copy();
            template.Parts = template.Parts.GroupBy(LoadedNestResult.PartKey).Where(g => group.Quantities.ContainsKey(g.Key)).Select(g =>
            {
                NestPart p = g.First();
                return new NestPart { JobNumber = p.JobNumber, MainMark = p.MainMark, PieceMark = p.PieceMark,
                    Sequence = p.Sequence, Phase = p.Phase, LotNumber = p.LotNumber, Quantity = group.Quantities[g.Key], Length = p.Length, Width = p.Width, Unit = p.Unit };
            }).ToList();
            if (template.Parts.Count != group.Quantities.Count) throw new InvalidDataException("Selected plate identities do not match this material's stock request.");
            template.NestName = NestNaming.ForParts(template.Parts);
            return template;
        }

        public static List<PlateStockOption> StockOptions(NestingSnapshot snapshot, PlateNestGroup group, IReadOnlyList<SavedNestReceipt> receipts)
        {
            var options = new List<PlateStockOption>();
            var stocks = LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList();
            for (int i = 0; i < stocks.Count; i++)
            {
                try
                {
                    NestResultPackage template = LoadedNestResult.CreateTemplate(snapshot, i);
                    if (!group.Matches(template.Material)) continue;
                    PowerFabNestApi.CheckStockQuantity(template, receipts, snapshot);
                    NestMaterial m = template.Material;
                    StockGeometry.Validate(m);
                    options.Add(new PlateStockOption { InventoryIndex = i,
                        Label = (StockGeometry.IsFuture(stocks[i]) ? "Future remnant" : StockGeometry.IsRemnant(stocks[i]) ? "Remnant" : "Sheet") + " | PO " + (m.PurchaseOrderNumber.Length == 0 ? "(no PO)" : m.PurchaseOrderNumber)
                            + " | " + m.Length + " × " + m.Width + " " + m.Unit + " | Qty " + snapshot.Inventory.Rows[i]["Qty"]
                            + " | ID " + (m.ParentRemnantPowerFabId.Length>0?m.ParentRemnantPowerFabId:m.InventoryId)
                            + (m.RequiresOutline&&m.StockContour.Count==0?" | Outline unavailable":"") + (m.StockLocation.Length == 0 ? "" : " | " + m.StockLocation) });
                }
                catch (InvalidDataException) { }
                catch (InvalidOperationException) { }
            }
            return options;
        }
        private static string F(XElement element, params string[] names) => NestingSnapshot.Field(element, names);
    }
}

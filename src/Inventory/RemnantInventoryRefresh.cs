using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Tekla.PowerFab.reqPowerFabAPI;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed partial class PowerFabNestApi
    {
        // Only the current GetInventory response may supply rows and quantities.
        // Saved files supply geometry for those exact live remnant identities.
        private List<XElement> RefreshRemnantInventory(List<XElement> inventory, NestingSnapshot snapshot,
            string folder, IReadOnlyList<RemnantOutlineRecord> outlines, out string warning)
        {
            warning = string.Empty;
            var candidates = inventory.Where(StockGeometry.IsFuture).ToDictionary(
                StockGeometry.PowerFabRemnantId, stock => new XElement(stock), StringComparer.Ordinal);
            // A live future row supplies identity/material, not a second full
            // parent sheet. Rebuild it only after checking current lifecycle.
            inventory.RemoveAll(StockGeometry.IsFuture);
            if (candidates.Count == 0) return inventory;
            if (readModel != null)
            {
                try
                {
                    var registered = RefreshRegisteredRemnants(inventory, snapshot, folder, outlines, candidates);
                    foreach (string id in registered) candidates.Remove(id);
                }
                catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                {
                    warning = "Native remnant verification was unavailable. " + error.Message;
                }
            }
            // Read details only for IDs returned by this inventory response.
            foreach (var batch in candidates.Keys.Chunk(100))
            {
                // Independent commands allow PowerFab to return one result per
                // remnant rather than relying on a single aggregate result.
                var request = new fsreqFabSuiteXMLRequest { Remnant_Get = batch.Select(id => new fsreqRemnant_Get {
                    RemnantID = new[] { id }, APILog = fsreqCommandAPILog.all, APILogSpecified = true }).ToArray() };
                string xml = Serialize(request), log = Path.Combine(folder, "RemnantInventoryLog");
                Directory.CreateDirectory(log);
                string stem = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(log, stem + ".request.xml"), xml, new UTF8Encoding(false));
                string text = execute(xml);
                File.WriteAllText(Path.Combine(log, stem + ".response.xml"), text, new UTF8Encoding(false));
                var response = ParseResponse(text); ThrowErrors(response);
                var live = ReadRemnantResults(response, out string batchWarning);
                if (batchWarning.Length > 0) warning += (warning.Length > 0 ? " " : "") + batchWarning;
                foreach (var remnant in live)
                {
                    string id = Value(remnant, "RemnantID");
                    if (!batch.Contains(id)) continue;
                    string physicalId = Value(remnant, "InventoryItemID");
                    if (InventoryTracking.PositiveId(physicalId))
                    {
                        // A physical remnant must exist in the fresh stock read.
                        // Never leave its old future row alongside it or invent
                        // a physical quantity from the bounding rectangle.
                        foreach (var physical in inventory.Where(s => !StockGeometry.IsFuture(s) && Value(s, "ID") == physicalId))
                        {
                            AssignRemnantIdentity(physical, remnant);
                            RemnantCatalog.LinkPhysical(folder, id, physicalId, Value(remnant, "TrueShapeID"));
                            RemnantCatalog.Apply(physical, outlines);
                        }
                        continue;
                    }
                    if (inventory.Any(s => StockGeometry.PowerFabRemnantId(s) == id)) continue;
                    XElement? parent = LiveRemnantParent(remnant);
                    if (parent == null || !decimal.TryParse(Value(parent, "Quantity"), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out decimal quantity) || quantity < 1 || quantity != decimal.Truncate(quantity)) continue;
                    XElement seed = candidates[id];
                    decimal length = DecimalField(remnant, "Length"), width = DecimalField(remnant, "Width");
                    decimal thick = StockGeometry.Mm(seed, "PlateThickness", "Thickness");
                    if (length <= 0 || width <= 0 || thick <= 0 || !SameRemnantBounds(seed, length, width)) continue;
                    // Keep the inventory response's quantity, PO, location and
                    // material. Parent details must not replace current stock.
                    var stock = new XElement(seed);
                    NestingSnapshot.Child(stock, "HanlonUnavailableReason")?.Remove();
                    StockGeometry.Set(stock, "HanlonParentJob", Value(parent, "Job", "JobNumber"));
                    StockGeometry.Set(stock, "ExternalNestID", Value(remnant, "ExternalNestID"));
                    AssignRemnantIdentity(stock, remnant);
                    RemnantCatalog.Apply(stock, outlines);
                    // Exported geometry is a fallback only when this exact live
                    // remnant still matches its recorded bounds and area.
                    if (Value(stock, "HanlonContour").Length == 0 && SameRemnantBounds(seed, length, width))
                        foreach (string field in new[] { "HanlonContour", "HanlonHoles", "HanlonCurveTolerance" })
                            if (Value(seed, field).Length > 0) StockGeometry.Set(stock, field, Value(seed, field));
                    VerifyRemnantOutline(stock, DecimalField(remnant, "Area"), folder);
                    StockGeometry.Set(stock, "HanlonRemnantVerified", "Remnant_Get");
                    inventory.Add(stock);
                }
            }
            return inventory;
        }

        private static void VerifyRemnantOutline(XElement stock, decimal areaSqm, string folder)
        {
            decimal length=StockGeometry.Mm(stock,"Length"), width=StockGeometry.Mm(stock,"Width");
            // An exact area/bounds match proves a solid rectangle. An irregular
            // remnant must retain its saved polygon; never use its bounding box.
            if(Value(stock,"HanlonContour").Length==0 && areaSqm>0
                && Math.Abs(areaSqm*1000000m-length*width)<=1m)
            {
                var points=new[]{(0d,0d),((double)length,0d),((double)length,(double)width),(0d,(double)width)};
                StockGeometry.Set(stock,"HanlonContour",JsonSerializer.Serialize(points.Select(p=>new LayoutPoint{X=p.Item1,Y=p.Item2}),LoadedNestResult.JsonOptions));
                StockGeometry.Set(stock,"HanlonHoles","[]");
            }
            if(Value(stock,"HanlonContour").Length==0)return;
            try
            {
                var material=new NestMaterial{Length=length,Width=width,Unit="MM"};
                StockGeometry.Apply(stock,material);StockGeometry.Validate(material);
                if(areaSqm<=0 || Math.Abs((double)(areaSqm*1000000m)-Math.Abs(Clipper2Lib.Clipper.Area(StockGeometry.Region(material))))>1)
                    ClearRemnantOutline(stock);
                else RemnantCatalog.RememberStock(stock,folder);
            }
            catch(InvalidDataException){ClearRemnantOutline(stock);}
        }

        private static List<XElement> ReadRemnantResults(XDocument response, out string warning)
        {
            warning = string.Empty;
            var commands = response.Root?.Elements().Where(e => Named(e, "Remnant_Get")).ToList() ?? new List<XElement>();
            if (commands.Count == 0) throw new InvalidDataException("PowerFab returned no remnant query result.");
            var failed = commands.Where(e => !True(Value(e, "Successful"))).ToList();
            if (failed.Count > 0) warning = "Some remnant queries were not confirmed by PowerFab. "
                + string.Join("; ", failed.Select(e => Value(e, "ErrorMessage")).Where(s => s.Length > 0));
            var records = commands.Where(e => True(Value(e, "Successful")))
                .SelectMany(e => e.Elements().Where(r => Named(r, "Remnant"))).ToList();
            if (records.GroupBy(e => Value(e, "RemnantID")).Any(g => g.Count() != 1))
                throw new InvalidDataException("PowerFab returned duplicate remnant identities.");
            return records;
        }

        private static XElement? LiveRemnantParent(XElement remnant)
        {
            var parent = NestingSnapshot.Child(remnant, "ParentInventoryItem", "ParentRequisitionItem");
            if (parent != null) return True(Value(parent, "ExternalNestBroken")) ? null : parent;
            var prior = NestingSnapshot.Child(remnant, "ParentRemnant");
            return prior == null ? null : LiveRemnantParent(prior);
        }
        private static bool SameRemnantBounds(XElement seed, decimal length, decimal width) =>
            Math.Abs(StockGeometry.Mm(seed, "Length") - length) <= 0.05m && Math.Abs(StockGeometry.Mm(seed, "Width") - width) <= 0.05m;
        private static decimal DecimalField(XElement e, string name) => decimal.TryParse(Value(e, name), NumberStyles.Number,
            CultureInfo.InvariantCulture, out decimal value) ? value : 0;
        private static void AssignRemnantIdentity(XElement stock, XElement remnant)
        {
            StockGeometry.Set(stock, "RemnantPowerFabID", Value(remnant, "RemnantID"));
            StockGeometry.Set(stock, "RemnantName", Value(remnant, "RemnantName"));
            StockGeometry.Set(stock, "TrueShapeID", Value(remnant, "TrueShapeID"));
            StockGeometry.Set(stock, "HanlonVerifiedRemnantAreaSqm", Value(remnant, "Area"));
        }
        private static void ClearRemnantOutline(XElement stock)
        {
            foreach (string field in new[] { "HanlonContour", "HanlonHoles", "HanlonCurveTolerance" })
                NestingSnapshot.Child(stock, field)?.Remove();
        }
        private static void SetMm(XElement e, string field, decimal value)
        {
            StockGeometry.Set(e, field, value.ToString(CultureInfo.InvariantCulture));
            NestingSnapshot.Child(e, field)!.SetAttributeValue("UOM", "MM");
        }
    }
}

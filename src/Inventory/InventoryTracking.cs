using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Tekla.PowerFab.reqPowerFabAPI;

namespace Hanlon.PowerFab.Nesting
{
    internal static class InventoryTracking
    {
        public static fsreqFabSuiteXMLRequest BuildRequest(NestingSnapshot snapshot)
        {
            var materials = snapshot.OriginalStock.Concat(snapshot.PartNodes())
                .Select(e => (Shape: F(e, "Shape"), Grade: F(e, "Grade"))).Where(p => p.Shape.Length > 0 && p.Grade.Length > 0)
                .Distinct().ToList();
            if (materials.Count == 0) throw new InvalidDataException("The nesting request has no material shape and grade to query.");
            return new fsreqFabSuiteXMLRequest
            {
                GetInventory = materials.Select(p => new fsreqGetInventory
                {
                    APILog = fsreqCommandAPILog.all, APILogSpecified = true,
                    IncludeInventory = true, IncludeInventorySpecified = true,
                    IncludeHistory = false, IncludeHistorySpecified = true,
                    LinkSoftware = PowerFabNestApi.SoftwareName,
                    UseGradeSubstitutions = false, UseGradeSubstitutionsSpecified = true,
                    Filters = new[] { new fsreqFilters { Filter = new[] { Filter("Shape", p.Shape), Filter("Grade", p.Grade) } } }
                }).ToArray()
            };
        }

        public static List<XElement> ReadRecords(XDocument response, NestingSnapshot snapshot, int expectedCommands)
        {
            var commands = response.Descendants().Where(e => N(e, "GetInventory")).ToList();
            if (commands.Count != expectedCommands || commands.Any(e => !IsTrue(F(e, "Successful"))))
                throw new InvalidDataException("PowerFab did not return a successful inventory result for every requested material. "
                    + string.Join("; ", commands.Select(e => F(e, "ErrorMessage")).Where(s => s.Length > 0)));
            var found = new List<XElement>();
            foreach (XElement record in commands.SelectMany(c => c.Elements().Where(e => N(e, "InventoryRecord", "InventoryItem"))))
            {
                if (F(record, "Type").Contains("HIST", StringComparison.OrdinalIgnoreCase)) continue;
                bool future=StockGeometry.IsFuture(record);
                if (!future && IsTrue(F(record, "Cut", "TakenFromStock"))) continue;
                string id=future?StockGeometry.PowerFabRemnantId(record):F(record,"ID","InventoryItemID","InventoryID","PowerFabID");
                if (!PositiveId(id)) continue;
                if (!decimal.TryParse(F(record, "Quantity"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal qty) || qty < 1 || qty != decimal.Truncate(qty)) continue;
                // Keep reserved and combined parent sheets visible. Availability is
                // checked separately, so visibility cannot allocate them twice.
                XElement item;
                try { item = Normalize(record); } catch (InvalidDataException) { continue; }
                if(future)
                {
                    Set(item,"ID",id);Set(item,"RemnantPowerFabID",id);Set(item,"ExpectedRemnant","true");
                    Set(item,"HanlonLiveFutureInventory","true");
                    StockGeometry.SeparateFutureFromParent(item);
                    Set(item,"HanlonUnavailableReason","Remnant details unavailable");
                }
                if (!MatchesRequest(item, snapshot)) continue;
                // PO and external links can cover many sheets: keep each inventory ID separate.
                if (!found.Any(e => Key(e) == Key(item))) found.Add(item);
            }
            return found.OrderBy(e => F(e, "PONumber"), StringComparer.OrdinalIgnoreCase).ThenBy(e => F(e, "ID"), StringComparer.Ordinal).ToList();
        }

        internal static bool MatchesRequest(XElement item, NestingSnapshot snapshot)
        {
            // The export is a starting list, not a permanent size filter. New
            // lengths and changed thicknesses must be visible after refresh.
            // PlateSelection still requires the exact shape/grade/thickness
            // for each material group before offering stock for calculation.
            return snapshot.OriginalStock.Concat(snapshot.PartNodes()).Any(e =>
                F(e, "Shape").Equals(F(item, "Shape"), StringComparison.OrdinalIgnoreCase)
                && F(e, "Grade").Equals(F(item, "Grade"), StringComparison.OrdinalIgnoreCase));
        }

        internal static string UnavailableReason(XElement stock, NestingSnapshot snapshot)
        {
            if (IsTrue(F(stock, "Cut", "TakenFromStock"))) return "Taken from stock";
            string job = ReservationJob(stock);
            if (job.Length > 0 && !snapshot.PartNodes().Any(p => F(p, "JobNumber", "Job").Equals(job, StringComparison.OrdinalIgnoreCase)))
                return "Reserved for " + job;
            if (F(stock, "NestID").Length > 0 || PositiveId(F(stock, "ExternalNestID"))
                || decimal.TryParse(F(stock, "PercentCombined"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal combined) && combined > 0)
                return "Allocated to nest";
            if (F(stock, "HanlonUnavailableReason").Length > 0) return F(stock, "HanlonUnavailableReason");
            if (StockGeometry.IsRemnant(stock) && F(stock, "HanlonContour").Length == 0) return "Remnant outline unavailable";
            if (!decimal.TryParse(F(stock, "Quantity"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quantity)
                || quantity < 1 || quantity != decimal.Truncate(quantity)) return "Quantity not confirmed";
            return "";
        }

        internal static string ReservationJob(XElement stock)
        {
            string flag = F(stock, "JobReserve");
            // JobReserve is a boolean in native inventory-item responses, not
            // the job's name. Some older exports use a textual reserve job.
            if (flag == "0" || flag.Equals("false", StringComparison.OrdinalIgnoreCase)) return "";
            string job = new[] { "Job", "JobNumber" }.Select(f => F(stock, f)).FirstOrDefault(v => v.Length > 0) ?? "";
            return job.Length > 0 ? job : flag.Length > 0 && !IsTrue(flag) ? flag : "";
        }

        internal static string Status(XElement stock, NestingSnapshot snapshot)
        {
            string reason = UnavailableReason(stock, snapshot);
            return reason.Length > 0 ? reason : StockGeometry.IsFuture(stock) ? "Future remnant"
                : IsTrue(F(stock, "OnOrder")) ? "On order" : "Available";
        }

        private static XElement Normalize(XElement record)
        {
            var item = new XElement("InventoryItem", record.Elements().Select(e => new XElement(e)));
            Set(item, "ID", F(record, "ID", "InventoryItemID", "InventoryID", "PowerFabID"));
            if (F(item, "PONumber").Length == 0) Set(item, "PONumber", F(record, "PurchaseOrderNumber", "PO"));
            foreach (string field in new[] { "PlateThickness", "Length", "Width" })
            {
                decimal mm = Mm(record, field, field == "PlateThickness" ? "Thickness" : field);
                if (mm <= 0) throw new InvalidDataException("Inventory dimensions are incomplete.");
                Set(item, field, mm.ToString(CultureInfo.InvariantCulture));
                NestingSnapshot.Child(item, field)!.SetAttributeValue("UOM", "MM");
            }
            return item;
        }

        internal static decimal UnreflectedSaves(NestingSnapshot snapshot, XElement stock, IEnumerable<SavedNestReceipt> receipts)
        {
            string key = Key(stock);
            receipts=receipts.Where(r=>!r.PartsReleased);
            if (StockGeometry.IsFuture(stock))
                return receipts.Where(r=>r.MaterialKey==key && (snapshot.LiveInventoryLoaded
                    ? !snapshot.InventoryReceiptBaseline.Contains(r.NestId) : r.SavedAtUtc>snapshot.ExportTimeUtc)).Sum(r=>r.SheetQuantity);
            return receipts.Count(r => r.MaterialKey == key && (snapshot.LiveInventoryLoaded
                ? !snapshot.InventoryReceiptBaseline.Contains(r.NestId) : r.WorkOrderId == snapshot.WorkOrderID));
        }
        internal static string Key(XElement stock) => StockGeometry.PowerFabRemnantId(stock).Length>0
            && F(stock, "Type").Equals("FutureRemnant", StringComparison.OrdinalIgnoreCase)
            ? "R:" + StockGeometry.PowerFabRemnantId(stock)
            : PositiveId(F(stock, "ID", "PowerFabID", "InventoryID", "InventoryItemID")) ? "I:" + F(stock, "ID", "PowerFabID", "InventoryID", "InventoryItemID")
            : "E:" + F(stock, "ExternalInventoryLinkID");
        internal static bool PositiveId(string value) => long.TryParse(value, out long id) && id > 0;
        private static decimal Mm(XElement e, params string[] fields)
        {
            XElement? value = NestingSnapshot.Child(e, fields);
            if (value == null || !decimal.TryParse(F(e, fields), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number)) throw new InvalidDataException("Missing inventory size.");
            string unit = value.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("UOM", StringComparison.OrdinalIgnoreCase))?.Value ?? F(value, "UOM", "Unit");
            return unit.ToUpperInvariant() switch { "MM" => number, "IN" => number * 25.4m, _ => throw new InvalidDataException("Inventory dimensions need explicit MM or IN units.") };
        }
        private static fsreqFilter Filter(string type, string value) => new fsreqFilter { FilterType = type, FilterValue = new fsreqFilterFilterValue { Value = value } };
        private static void Set(XElement item, string field, string value)
        {
            XElement? node = NestingSnapshot.Child(item, field);
            if (node == null) item.Add(new XElement(field, value)); else node.Value = value;
        }
        private static string F(XElement e, params string[] names) => NestingSnapshot.Field(e, names);
        private static bool N(XElement e, params string[] names) => NestingSnapshot.Named(e, names);
        private static bool IsTrue(string value) => value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}

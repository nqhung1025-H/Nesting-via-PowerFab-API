using System.Globalization;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class PowerFabInventoryTarget
    {
        public string InventoryId { get; set; } = "";
        public string Reference { get; set; } = "";

        public static PowerFabInventoryTarget? FromStock(XElement stock)
        {
            // A future-remnant ID and an external link ID are different from
            // InventoryItemID. Planning options (Quantity=any) have no stock row.
            string id = NestingSnapshot.Field(stock, "ID", "InventoryItemID", "InventoryID", "PowerFabID");
            if (StockGeometry.IsFuture(stock) || !InventoryTracking.PositiveId(id)
                || !decimal.TryParse(NestingSnapshot.Field(stock, "Quantity"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quantity)
                || quantity <= 0 || quantity != decimal.Truncate(quantity)) return null;
            string F(params string[] names) => NestingSnapshot.Field(stock, names);
            return new PowerFabInventoryTarget { InventoryId = id,
                Reference = "Inventory ID: " + id + "\nPO: " + (F("PONumber", "PurchaseOrderNumber", "PO") is string po && po.Length > 0 ? po : "(no PO)")
                    + "\n" + F("Shape") + " | " + F("Dimensions") + " | Length: " + F("Length")
                    + " | Grade: " + F("Grade") + "\nJob: " + F("Job", "JobNumber") };
        }
    }
    internal sealed class PowerFabInventoryNavigationResult
    {
        public bool Opened { get; set; }
        public bool Selected { get; set; }
        public string Detail { get; set; } = "";
    }
}

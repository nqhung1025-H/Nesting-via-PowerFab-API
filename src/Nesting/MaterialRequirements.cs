using System;
using System.IO;

namespace Hanlon.PowerFab.Nesting
{
    internal static class MaterialRequirements
    {
        public static bool UsesPowerFabAllocation(NestMaterial material) =>
            material.Source.Equals("Purchase", StringComparison.OrdinalIgnoreCase)
            || material.Source.Equals("Requirement", StringComparison.OrdinalIgnoreCase);

        // A chosen full sheet supplies a calculation size. It is not a promise
        // that PowerFab will allocate that inventory record when this nest is saved.
        // Remnants must retain their real stock/parent identity and actual outline.
        public static NestMaterial ForSave(NestMaterial material)
        {
            var planned = NestingBatch.Clone(material);
            if (!planned.Source.Equals("Inventory", StringComparison.OrdinalIgnoreCase)
                || planned.RequiresOutline || planned.StockContour.Count > 0
                || planned.ParentRemnantPowerFabId.Length > 0) return planned;
            planned.Source = "Requirement";
            planned.ReferenceInventoryId = planned.InventoryId;
            planned.ReferencePurchaseOrderNumber = planned.PurchaseOrderNumber;
            planned.InventoryId = "";
            planned.ExternalInventoryLinkId = "";
            planned.PurchaseOrderNumber = "";
            planned.PurchaseOrderItemNumber = "";
            planned.StockJob = "";
            planned.StockLocation = "";
            planned.StockSecondaryLocation = "";
            planned.OnOrder = false;
            return planned;
        }

        public static void ValidateUnallocated(NestMaterial material)
        {
            if (!UsesPowerFabAllocation(material)) return;
            if (material.InventoryId.Length > 0 || material.ExternalInventoryLinkId.Length > 0
                || material.ParentRemnantPowerFabId.Length > 0 || material.RequiresOutline
                || material.StockContour.Count > 0 || material.StockHoles.Count > 0)
                throw new InvalidDataException("A sheet-size requirement cannot contain allocated stock IDs or a remnant outline. Use the selected remnant's real identity to nest on leftover material.");
        }
    }
}

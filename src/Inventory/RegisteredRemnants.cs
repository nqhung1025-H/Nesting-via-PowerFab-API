using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class RegisteredRemnant
    {
        public string RemnantId { get; set; } = "";
        public string Name { get; set; } = "";
        public string CreatorNestId { get; set; } = "";
        public string CreatorGuid { get; set; } = "";
        public string CreatorJobs { get; set; } = "";
        public string ParentRemnantId { get; set; } = "";
        public string Shape { get; set; } = "";
        public string Grade { get; set; } = "";
        public decimal ThicknessMm { get; set; }
        public decimal LengthMm { get; set; }
        public decimal WidthMm { get; set; }
        public decimal AreaSqm { get; set; }
        public decimal Quantity { get; set; }
        public bool Skeleton { get; set; }
        public bool CreatorInUse { get; set; }
        public decimal CreatorCutQuantity { get; set; }
        public bool HasInventory { get; set; }
        public bool HasRequisition { get; set; }
        public decimal ChildNests { get; set; }
        public string PurchaseOrder { get; set; } = "";
        public bool OnOrder { get; set; }
    }

    internal sealed partial class PowerFabReadModel
    {
        // Published table relationships distinguish a remnant's CREATING nest
        // from a later nest USING that remnant. All statements are read-only.
        internal List<RegisteredRemnant> RegisteredRemnants(IEnumerable<string> creatorIds, IEnumerable<string> remnantIds)
        {
            const string inv = "i.Quantity>0 AND COALESCE(i.ExternalNestBroken,0)=0 AND (i.ExternalNestID=n.ExternalNestID "
                + "OR EXISTS (SELECT 1 FROM inventoryitemcombinationitems ic WHERE ic.ItemID=i.ItemID AND ic.ExternalNestID=n.ExternalNestID))";
            const string req = "qi.Quantity>0 AND COALESCE(qi.ExternalNestBroken,0)=0 AND qi.ExternalNestID=n.ExternalNestID";
            string sql = "SELECT r.RemnantID, r.RemnantName, r.BoundingLength, r.BoundingWidth, r.SquareMeters, r.IsSkeleton, "
                + "rr.RemnantQuantity, n.ExternalNestID AS CreatorNestID, n.ExternalGUID AS CreatorGUID, "
                + "n.ParentRemnantID, n.InUse, n.QuantityCut, sh.Shape, n.DimThickness, "
                + "EXISTS(SELECT 1 FROM inventoryitems i WHERE " + inv + ") AS HasInventory, "
                + "EXISTS(SELECT 1 FROM requisitionitems qi JOIN requisitions q ON q.RequisitionID=qi.RequisitionID WHERE " + req + ") AS HasRequisition, "
                + "COALESCE((SELECT gr.Grade FROM inventoryitems i JOIN grades gr ON gr.GradeID=i.GradeID WHERE " + inv + " LIMIT 1), "
                + "(SELECT gr.Grade FROM requisitionitems qi JOIN requisitions q ON q.RequisitionID=qi.RequisitionID "
                + "JOIN grades gr ON gr.GradeID=qi.GradeID WHERE " + req + " LIMIT 1),'') AS Grade, "
                + "(SELECT COUNT(*) FROM externalnests c WHERE c.ParentRemnantID=r.RemnantID AND COALESCE(c.InUse,0)<>0 "
                + "AND COALESCE(c.QuantityCut,0)=0) AS ChildNests, "
                + "COALESCE((SELECT GROUP_CONCAT(DISTINCT p.JobNumber ORDER BY p.JobNumber SEPARATOR ', ') "
                + "FROM externalnestparts p WHERE p.ExternalNestID=n.ExternalNestID),'') AS CreatorJobs, "
                + "COALESCE((SELECT i.PONumber FROM inventoryitems i WHERE " + inv + " LIMIT 1),'') AS PONumber, "
                + "(EXISTS(SELECT 1 FROM inventoryitems i WHERE " + inv + ") AND NOT EXISTS(SELECT 1 FROM inventoryitems i "
                + "WHERE " + inv + " AND COALESCE(i.OnOrder,0)=0)) AS OnOrder "
                + "FROM externalnestresultremnants rr JOIN externalnestresults nr ON nr.ExternalNestResultID=rr.ExternalNestResultID "
                + "JOIN externalnests n ON n.ExternalNestID=nr.ExternalNestID JOIN remnants r ON r.RemnantID=rr.RemnantID "
                + "LEFT JOIN shapes sh ON sh.ShapeID=n.ShapeID WHERE ";
            var result = new Dictionary<string, RegisteredRemnant>(StringComparer.Ordinal);
            void Read(string condition, IEnumerable<string> values)
            {
                foreach (var ids in values.Where(InventoryTracking.PositiveId).Distinct().Chunk(100))
                {
                    using var command = SelectIds(sql + condition, ids);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var r = new RegisteredRemnant { RemnantId=Text(reader,"RemnantID"), Name=Text(reader,"RemnantName"),
                            CreatorNestId=Text(reader,"CreatorNestID"), CreatorGuid=Text(reader,"CreatorGUID"), CreatorJobs=Text(reader,"CreatorJobs"),
                            ParentRemnantId=Text(reader,"ParentRemnantID"), Shape=Text(reader,"Shape"), Grade=Text(reader,"Grade"),
                            ThicknessMm=Number(reader,"DimThickness"), LengthMm=Number(reader,"BoundingLength"), WidthMm=Number(reader,"BoundingWidth"),
                            AreaSqm=Number(reader,"SquareMeters"), Quantity=Number(reader,"RemnantQuantity"), Skeleton=Number(reader,"IsSkeleton")!=0,
                            CreatorInUse=Number(reader,"InUse")!=0, CreatorCutQuantity=Number(reader,"QuantityCut"),
                            HasInventory=Number(reader,"HasInventory")!=0, HasRequisition=Number(reader,"HasRequisition")!=0,
                            ChildNests=Number(reader,"ChildNests"), PurchaseOrder=Text(reader,"PONumber"), OnOrder=Number(reader,"OnOrder")!=0 };
                        if (!InventoryTracking.PositiveId(r.RemnantId)) continue;
                        if (result.TryGetValue(r.RemnantId,out var previous) && previous.CreatorNestId!=r.CreatorNestId)
                            throw new InvalidOperationException("PowerFab links the same remnant to different creating nests. Its availability could not be verified.");
                        result[r.RemnantId]=r;
                    }
                }
            }
            Read("n.ExternalNestID IN ", creatorIds);
            Read("r.RemnantID IN ", remnantIds);
            return result.Values.ToList();
        }

    }
}

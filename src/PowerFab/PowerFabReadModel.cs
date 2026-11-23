using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class NativeNestState
    {
        public bool Exists { get; set; }
        public bool? InUse { get; set; }
        public decimal QuantityCut { get; set; }
        public string ParentRemnantId { get; set; } = "";
        public bool MaterialLinksChecked { get; set; }
        public bool HasLiveRequisition { get; set; }
        public bool HasLiveInventory { get; set; }
    }
    internal sealed class RemnantStockLink
    {
        public string InventoryId { get; set; } = "";
        public string TrueShapeId { get; set; } = "";
        public string RemnantId { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal AreaSqm { get; set; }
    }

    // Read-only queries through the command factory supplied by the PowerFab host.
    // Table/column names come from Trimble's published PowerFab table/field listing.
    // Business writes continue to use SaveNest; no database rows are edited here.
    internal sealed partial class PowerFabReadModel
    {
        private readonly Func<string, IDbCommand> createCommand;
        public PowerFabReadModel(Func<string, IDbCommand> createCommand) { this.createCommand = createCommand; }

        public IDisposable RequisitionSequenceLock()
        {
            var command=createCommand("SELECT GET_LOCK(CONCAT('Hanlon.Nesting.REQ.',DATABASE()),30)");
            try
            {
                if(Convert.ToString(command.ExecuteScalar(),CultureInfo.InvariantCulture)!="1")
                    throw new InvalidOperationException("Another Hanlon save is assigning its requisition number. Retry Save nest after it finishes. No nest was sent.");
                return new SequenceLock(command);
            }
            catch{command.Dispose();throw;}
        }
        private sealed class SequenceLock:IDisposable
        {
            private IDbCommand? command;
            public SequenceLock(IDbCommand command){this.command=command;}
            public void Dispose()
            {
                if(command==null)return;
                try{command.CommandText="SELECT RELEASE_LOCK(CONCAT('Hanlon.Nesting.REQ.',DATABASE()))";command.ExecuteScalar();}
                finally{command.Dispose();command=null;}
            }
        }

        public IReadOnlyList<string> RequisitionNumbers()
        {
            using var command = createCommand("SELECT RequisitionNumber FROM requisitions");
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while(reader.Read()) if(!reader.IsDBNull(0)) names.Add(Convert.ToString(reader.GetValue(0),CultureInfo.InvariantCulture) ?? "");
            return names;
        }

        public Dictionary<string, NativeNestState> NestStates(IEnumerable<SavedNestReceipt> receipts)
        {
            var states = new Dictionary<string, NativeNestState>(StringComparer.Ordinal);
            foreach(var batch in receipts.Where(r=>InventoryTracking.PositiveId(r.PowerFabNestId)).Chunk(100))
            {
                var byId = batch.GroupBy(r=>r.PowerFabNestId).ToDictionary(g=>g.Key,g=>g.ToList());
                var ids = byId.Keys.ToArray();
                using var command = SelectIds("SELECT ExternalNestID, ExternalGUID, InUse, QuantityCut, ParentRemnantID FROM externalnests WHERE ExternalNestID IN ",ids);
                foreach(var receipt in batch) states[receipt.NestId] = new NativeNestState();
                using(var reader = command.ExecuteReader())
                {
                    while(reader.Read())
                    {
                        string id = Text(reader,"ExternalNestID"), guid = Text(reader,"ExternalGUID");
                        if(!byId.TryGetValue(id,out var matching)) continue;
                        foreach(var receipt in matching)
                        {
                            bool same = guid.Equals(receipt.NestId,StringComparison.OrdinalIgnoreCase)
                                || Guid.TryParse(guid,out var a)&&Guid.TryParse(receipt.NestId,out var b)&&a==b;
                            if(!same){states.Remove(receipt.NestId);continue;}
                            states[receipt.NestId] = new NativeNestState {Exists=true,
                                InUse=reader.IsDBNull(reader.GetOrdinal("InUse"))?null:Number(reader,"InUse")!=0,
                                QuantityCut=Number(reader,"QuantityCut"),ParentRemnantId=Text(reader,"ParentRemnantID")};
                        }
                    }
                }
                // PowerFab may retain an externalnests row (and its InUse flag)
                // after deleting a requisition. Verify current material relations.
                // An orphan item whose requisition header was deleted is not live.
                var requisitions=LinkedNestIds("SELECT DISTINCT ri.ExternalNestID FROM requisitionitems ri "
                    +"JOIN requisitions r ON r.RequisitionID=ri.RequisitionID "
                    +"WHERE ri.Quantity>0 AND COALESCE(ri.ExternalNestBroken,0)=0 AND ri.ExternalNestID IN ",ids);
                var inventory=LinkedNestIds("SELECT DISTINCT i.ExternalNestID FROM inventoryitems i "
                    +"WHERE i.Quantity>0 AND COALESCE(i.ExternalNestBroken,0)=0 AND i.ExternalNestID IN ",ids);
                inventory.UnionWith(LinkedNestIds("SELECT DISTINCT c.ExternalNestID FROM inventoryitemcombinationitems c "
                    +"JOIN inventoryitems i ON i.ItemID=c.ItemID "
                    +"WHERE i.Quantity>0 AND COALESCE(i.ExternalNestBroken,0)=0 AND c.ExternalNestID IN ",ids));
                foreach(var receipt in batch)
                {
                    if(!states.TryGetValue(receipt.NestId,out var state))continue;
                    state.MaterialLinksChecked=true;
                    state.HasLiveRequisition=requisitions.Contains(receipt.PowerFabNestId);
                    state.HasLiveInventory=inventory.Contains(receipt.PowerFabNestId);
                }
            }
            return states;
        }

        private HashSet<string> LinkedNestIds(string sql,IReadOnlyList<string> ids)
        {
            using var command=SelectIds(sql,ids);using var reader=command.ExecuteReader();
            var result=new HashSet<string>(StringComparer.Ordinal);
            while(reader.Read())result.Add(Text(reader,"ExternalNestID"));
            return result;
        }

        public List<RemnantStockLink> RemnantStockLinks(IEnumerable<string> inventoryIds)
        {
            var result = new List<RemnantStockLink>();
            foreach(var ids in inventoryIds.Where(InventoryTracking.PositiveId).Distinct().Chunk(100))
            {
                using var command = SelectIds("SELECT i.ItemID AS InventoryID, t.TrueShapeID, r.RemnantID, r.RemnantName, r.SquareMeters FROM inventoryitems i "
                    +"JOIN trueshapes t ON t.TrueShapeID=i.TrueShapeID JOIN remnants r ON r.RemnantID=t.ReferenceID "
                    +"WHERE t.ReferenceType='Remnant' AND i.ItemID IN ",ids);
                using var reader = command.ExecuteReader();
                while(reader.Read()) result.Add(new RemnantStockLink {InventoryId=Text(reader,"InventoryID"),TrueShapeId=Text(reader,"TrueShapeID"),
                    RemnantId=Text(reader,"RemnantID"),Name=Text(reader,"RemnantName"),AreaSqm=Number(reader,"SquareMeters")});
            }
            return result;
        }

        private IDbCommand SelectIds(string sql,IReadOnlyList<string> ids)
        {
            var command=createCommand(sql+"("+string.Join(",",Enumerable.Range(0,ids.Count).Select(i=>"@hanlonId"+i))+")");
            try
            {
                for(int i=0;i<ids.Count;i++)
                {
                    var p=command.CreateParameter();p.ParameterName="@hanlonId"+i;p.DbType=DbType.Int64;
                    p.Value=long.Parse(ids[i],CultureInfo.InvariantCulture);command.Parameters.Add(p);
                }
                return command;
            }
            catch {command.Dispose();throw;}
        }
        private static string Text(IDataRecord r,string column) => r.IsDBNull(r.GetOrdinal(column))?"":Convert.ToString(r[column],CultureInfo.InvariantCulture)??"";
        private static decimal Number(IDataRecord r,string column) => r[column] is bool b?(b?1:0):decimal.TryParse(Text(r,column),NumberStyles.Number,CultureInfo.InvariantCulture,out var n)?n:0;
    }
}

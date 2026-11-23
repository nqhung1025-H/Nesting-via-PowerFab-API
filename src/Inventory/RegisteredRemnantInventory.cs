using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed partial class PowerFabNestApi
    {
        private HashSet<string> RefreshRegisteredRemnants(List<XElement> inventory, NestingSnapshot snapshot, string folder,
            IReadOnlyList<RemnantOutlineRecord> outlines, Dictionary<string,XElement> candidates)
        {
            // Validate only remnants in the current PowerFab inventory read.
            // Saved receipts must never add rows from earlier jobs or tests.
            var rows=readModel!.RegisteredRemnants(Array.Empty<string>(),candidates.Keys);
            var byId=rows.ToDictionary(r=>r.RemnantId,StringComparer.Ordinal);
            for(int depth=0;depth<32;depth++)
            {
                var missing=byId.Values.Select(r=>r.ParentRemnantId).Where(id=>InventoryTracking.PositiveId(id)&&!byId.ContainsKey(id)).Distinct().ToArray();
                if(missing.Length==0)break;
                var ancestors=readModel.RegisteredRemnants(Array.Empty<string>(),missing);
                if(ancestors.Count==0)break;
                foreach(var ancestor in ancestors)byId[ancestor.RemnantId]=ancestor;
            }
            string log=Path.Combine(folder,"RemnantInventoryLog");Directory.CreateDirectory(log);
            File.WriteAllText(Path.Combine(log,"latest-native-registration.json"),JsonSerializer.Serialize(byId.Values,LoadedNestResult.JsonOptions));

            bool Live(RegisteredRemnant row,HashSet<string> visited)
            {
                if(!visited.Add(row.RemnantId)||!row.CreatorInUse||row.CreatorCutQuantity>0)return false;
                if(row.HasInventory||row.HasRequisition)return true;
                return byId.TryGetValue(row.ParentRemnantId,out var parent)&&Live(parent,visited);
            }
            RegisteredRemnant Root(RegisteredRemnant row)
            {
                var visited=new HashSet<string>();
                while(row.Grade.Length==0&&visited.Add(row.RemnantId)&&byId.TryGetValue(row.ParentRemnantId,out var parent))row=parent;
                return row;
            }
            var handled=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in rows)
            {
                if(!candidates.ContainsKey(row.RemnantId))continue;
                handled.Add(row.RemnantId);
                // A cut parent is replaced only by the live physical inventory
                // remnant. A deleted/broken parent cannot supply future stock.
                if(!Live(row,new HashSet<string>())||row.Skeleton||row.Quantity<1||row.Quantity!=decimal.Truncate(row.Quantity))continue;
                if(inventory.Any(s=>!StockGeometry.IsFuture(s)&&StockGeometry.PowerFabRemnantId(s)==row.RemnantId))continue;
                var root=Root(row);
                candidates.TryGetValue(row.RemnantId,out var seed);
                string grade=row.Grade.Length>0?row.Grade:root.Grade;
                if(grade.Length==0&&seed!=null)grade=Value(seed,"Grade");
                if(row.Shape.Length==0||grade.Length==0||row.ThicknessMm<=0||row.LengthMm<=0||row.WidthMm<=0)
                {handled.Remove(row.RemnantId);continue;}
                // SQL columns have no UOM attribute. Verify their linear scale
                // against a saved/exported metric seed rather than assuming
                // that native database dimensions are already millimetres.
                if(seed!=null)
                {
                    decimal seedLength=StockGeometry.Mm(seed,"Length"),seedWidth=StockGeometry.Mm(seed,"Width"),
                        seedThickness=StockGeometry.Mm(seed,"PlateThickness","Thickness");
                    decimal scale=new[]{1m,25.4m}.FirstOrDefault(f=>Math.Abs(row.LengthMm*f-seedLength)<=0.05m
                        &&Math.Abs(row.WidthMm*f-seedWidth)<=0.05m);
                    decimal thicknessScale=new[]{1m,25.4m}.FirstOrDefault(f=>Math.Abs(row.ThicknessMm*f-seedThickness)<=0.01m);
                    if(scale==0||thicknessScale==0){handled.Remove(row.RemnantId);continue;}
                    row.LengthMm*=scale;row.WidthMm*=scale;row.ThicknessMm*=thicknessScale;
                }
                else {handled.Remove(row.RemnantId);continue;}
                if(!Value(seed!,"Shape").Equals(row.Shape,StringComparison.OrdinalIgnoreCase)
                    ||!Value(seed!,"Grade").Equals(grade,StringComparison.OrdinalIgnoreCase))
                {handled.Remove(row.RemnantId);continue;}
                var stock=new XElement(seed!);
                NestingSnapshot.Child(stock,"HanlonUnavailableReason")?.Remove();
                StockGeometry.Set(stock,"RemnantName",row.Name);
                StockGeometry.Set(stock,"HanlonParentJob",row.CreatorJobs);
                StockGeometry.Set(stock,"HanlonVerifiedRemnantAreaSqm",row.AreaSqm.ToString(CultureInfo.InvariantCulture));
                StockGeometry.Set(stock,"HanlonRemnantVerified","NativeRegistration");
                if(!InventoryTracking.MatchesRequest(stock,snapshot))continue;
                RemnantCatalog.Apply(stock,outlines);
                if(Value(stock,"HanlonContour").Length==0&&seed!=null&&SameRemnantBounds(seed,row.LengthMm,row.WidthMm)
                    &&Value(seed,"Shape").Equals(row.Shape,StringComparison.OrdinalIgnoreCase)
                    &&Value(seed,"Grade").Equals(grade,StringComparison.OrdinalIgnoreCase)
                    &&Math.Abs(StockGeometry.Mm(seed,"PlateThickness","Thickness")-row.ThicknessMm)<=0.01m)
                    foreach(string field in new[]{"HanlonContour","HanlonHoles","HanlonCurveTolerance"})
                        if(Value(seed,field).Length>0)StockGeometry.Set(stock,field,Value(seed,field));
                VerifyRemnantOutline(stock,row.AreaSqm,folder);
                if(row.ChildNests>0)StockGeometry.Set(stock,"HanlonUnavailableReason","Allocated to nest");
                inventory.Add(stock);
            }
            return handled;
        }
    }
}

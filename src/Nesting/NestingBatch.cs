using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class MaterialPlan
    {
        public PlateNestGroup Group {get;set;}=null!;
        public List<int> StockIndexes {get;}=new List<int>();
        public NestMaterial? NewSheet {get;set;}
        public string RequisitionNumber {get;set;}="";
        public bool AutoRequisitionNumber {get;set;}
        public string RequisitionBatchId {get;set;}="";
        public string TargetPurchaseOrderNumber {get;set;}="";
    }
    internal static class NestingBatch
    {
        public static List<CalculatedNest> Calculate(NestingSnapshot snapshot,MaterialPlan choice,
            IReadOnlyList<SavedNestReceipt> receipts,LayoutConditions conditions,Dictionary<string,DxfShape> shapes,
            CancellationToken token,Action<NestingProgress>? report=null)
        {
            var remaining=new Dictionary<string,int>(choice.Group.Quantities,StringComparer.OrdinalIgnoreCase);
            var result=new List<CalculatedNest>();var reserved=receipts.ToList();
            bool HasRemaining()=>remaining.Values.Any(q=>q>0);
            NestResultPackage Template(NestMaterial material)
            {
                var template=PlateSelection.ForMaterial(snapshot,choice.Group,MaterialRequirements.ForSave(material),conditions);
                template.Parts=template.Parts.Where(p=>remaining.TryGetValue(LoadedNestResult.PartKey(p),out int q)&&q>0)
                    .Select(p=>{p.Quantity=remaining[LoadedNestResult.PartKey(p)];return p;}).ToList();
                template.RequisitionNumber=choice.RequisitionNumber;template.TargetPurchaseOrderNumber=choice.TargetPurchaseOrderNumber;
                template.AutoRequisitionNumber=choice.AutoRequisitionNumber;template.RequisitionBatchId=choice.RequisitionBatchId;
                template.SheetNumber=result.Count+1;return template;
            }
            bool Fit(NestMaterial material)
            {
                token.ThrowIfCancellationRequested();var template=Template(material);
                try
                {
                    int before=choice.Group.Quantity-remaining.Values.Sum();
                    var plan=DxfNestingEngine.Calculate(template,template.Parts.Select(p=>new NestingInput{Part=p,Shape=shapes[LoadedNestResult.PartKey(p)]}),conditions,token,
                        report:u=>report?.Invoke(NestingProgress.ForMaterial(material,choice.Group.Quantity,before+u.Placed,before+u.Placed,0,u.Status)));
                    foreach(var p in plan.Package.Parts)remaining[LoadedNestResult.PartKey(p)]-=p.Quantity;
                    result.Add(plan);reserved.Add(new SavedNestReceipt{WorkOrderId=snapshot.WorkOrderID,NestId=plan.Package.NestId,
                        MaterialKey=PowerFabNestApi.MaterialKey(material),Parts=plan.Package.Parts,SheetQuantity=1,SavedAtUtc=DateTime.UtcNow});return true;
                }
                catch(InvalidDataException e) when(e.Message.StartsWith("No requested plate fits",StringComparison.Ordinal)){return false;}
            }
            foreach(int index in choice.StockIndexes)
            {
                if(!HasRemaining())break;
                var stock=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ElementAt(index);
                var material=LoadedNestResult.CreateTemplate(snapshot,index).Material;
                StockGeometry.Validate(material);
                int quantity=(int)Math.Min(int.MaxValue,decimal.Parse(NestingSnapshot.Field(stock,"Quantity"),System.Globalization.CultureInfo.InvariantCulture));
                int used=(int)InventoryTracking.UnreflectedSaves(snapshot,stock,reserved);
                for(int n=used;n<quantity&&HasRemaining();n++)if(!Fit(material))break;
            }
            if(choice.NewSheet!=null)
            {
                int guard=0;
                while(HasRemaining())
                {
                    if(++guard>2000)throw new InvalidDataException("This run would need more than 2000 new sheets. Check the entered sheet size and quantities.");
                    if(!Fit(choice.NewSheet))throw new InvalidDataException("The remaining parts do not fit the new sheet size with the current layout conditions. Increase the sheet size or adjust the conditions.");
                }
            }
            if(result.Count==0)throw new InvalidDataException("No selected plate fits the chosen material. Enter a new sheet size or choose different stock.");
            var missing=remaining.Where(p=>p.Value>0).Select(p=>p.Value+" x "+p.Key.Split('\u001f').ElementAtOrDefault(2)).ToList();
            foreach(var plan in result){plan.Unplaced.Clear();if(missing.Count>0)plan.Unplaced.AddRange(missing);}
            return result;
        }
        internal static NestMaterial Clone(NestMaterial material)=>JsonSerializer.Deserialize<NestMaterial>(JsonSerializer.Serialize(material,LoadedNestResult.JsonOptions),LoadedNestResult.JsonOptions)!;
    }
}

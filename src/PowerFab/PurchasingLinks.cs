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
        private List<XElement> AddRemnants(List<XElement> records,NestingSnapshot snapshot,string folder,out string warning)
        {
            warning = string.Empty;
            RemnantCatalog.RecoverSavedOutlines(folder);
            if(readModel!=null)
            {
                var links=readModel.RemnantStockLinks(records.Where(r=>!StockGeometry.IsFuture(r)).Select(r=>Value(r,"ID")));
                foreach(var stock in records.Where(s=>!StockGeometry.IsFuture(s)))
                {
                    var matches=links.Where(l=>l.InventoryId==Value(stock,"ID")).ToList();
                    if(matches.Count>1)throw new InvalidDataException("PowerFab returned more than one remnant identity for an inventory sheet. Refresh its stock record before nesting.");
                    if(matches.Count==0)continue;var link=matches[0];
                    StockGeometry.Set(stock,"RemnantPowerFabID",link.RemnantId);StockGeometry.Set(stock,"TrueShapeID",link.TrueShapeId);
                    StockGeometry.Set(stock,"RemnantName",link.Name);
                    StockGeometry.Set(stock,"HanlonVerifiedRemnantAreaSqm",link.AreaSqm.ToString(CultureInfo.InvariantCulture));
                    RemnantCatalog.LinkPhysical(folder,link.RemnantId,link.InventoryId,link.TrueShapeId);
                }
            }
            var outlines=RemnantCatalog.Read(folder);
            foreach(var record in records)
            {
                if(!StockGeometry.IsFuture(record)&&InventoryTracking.PositiveId(StockGeometry.PowerFabRemnantId(record)))
                    RemnantCatalog.LinkPhysical(folder,StockGeometry.PowerFabRemnantId(record),Value(record,"ID"),Value(record,"TrueShapeID"));
                RemnantCatalog.Apply(record,outlines);
            }
            // Saved files enrich the outlines of current inventory records only.
            try
            {
                var refreshed=RefreshRemnantInventory(records,snapshot,folder,outlines,out string futureWarning);
                warning=string.Join(" ",new[]{warning,futureWarning}.Where(s=>s.Length>0));return refreshed;
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
            {
                // GetInventory and current native physical-remnant links already
                // succeeded. An optional remnant read must not discard those
                // live sheets or stop new-sheet calculations. No cached rows or
                // zero-quantity placeholders are added after a detail failure.
                warning = "PowerFab inventory updated. Some remnant details could not be loaded. " + error.Message;
                return records;
            }
        }

        private void ValidateFutureRemnant(NestMaterial material,NestingSnapshot snapshot)
        {
            // SaveCore has just refreshed inventory. Use that verified native
            // registration/API result; do not require an optional Remnant_Get
            // command on hosts where the native read already confirmed it.
            var matches=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Where(s=>StockGeometry.IsFuture(s)
                &&StockGeometry.PowerFabRemnantId(s)==material.ParentRemnantPowerFabId).ToList();
            if(!snapshot.LiveInventoryLoaded||matches.Count!=1||Value(matches[0],"HanlonRemnantVerified").Length==0
                ||InventoryTracking.UnavailableReason(matches[0],snapshot).Length>0)
                throw new InvalidDataException("This future remnant is no longer verified and available. Refresh inventory before saving it.");
            var record=matches[0];
            decimal f=material.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4m:1m;
            decimal length=StockGeometry.Mm(record,"Length"),width=StockGeometry.Mm(record,"Width");
            if(length<=0||width<=0
                ||Math.Abs(length-material.Length*f)>0.05m||Math.Abs(width-material.Width*f)>0.05m)
                throw new InvalidDataException("The future remnant's PowerFab bounds changed. Generate a new layout before saving.");
            if(!Same(record,material.Shape,"Shape")||!Same(record,material.Grade,"Grade")
                ||Math.Abs(StockGeometry.Mm(record,"PlateThickness","Thickness")-material.Thickness*f)>0.01m)
                throw new InvalidDataException("The future remnant's material changed. Generate a new layout before saving.");
            if(!decimal.TryParse(Value(record,"HanlonVerifiedRemnantAreaSqm"),NumberStyles.Number,CultureInfo.InvariantCulture,out decimal area)
                ||area<=0||Math.Abs((double)(area*1000000m)-Math.Abs(Clipper2Lib.Clipper.Area(StockGeometry.Region(material))))>1)
                throw new InvalidDataException("The future remnant's PowerFab area differs from the saved outline. Refresh its PowerFab record before nesting.");
        }

        private void ValidatePurchaseOrder(NestResultPackage result,string folder)
        {
            if(string.IsNullOrWhiteSpace(result.TargetPurchaseOrderNumber))return;
            var request=new fsreqFabSuiteXMLRequest{GetPurchaseOrderInformation=new[]{new fsreqGetPurchaseOrderInformation{
                PONumber=new[]{result.TargetPurchaseOrderNumber},APILog=fsreqCommandAPILog.all,APILogSpecified=true}}};
            var response=ParseResponse(execute(Serialize(request)));ThrowErrors(response);
            var command=response.Descendants().FirstOrDefault(e=>Named(e,"GetPurchaseOrderInformation"));
            if(command==null||!True(Value(command,"Successful"))||!command.Elements().Any(e=>Named(e,"PurchaseOrderInformation")
                &&Value(e,"PONumber").Equals(result.TargetPurchaseOrderNumber,StringComparison.OrdinalIgnoreCase)
                &&(Value(e,"Successful").Length==0||True(Value(e,"Successful")))))
                throw new InvalidDataException("The selected purchase order was not found or could not be read. Check its number or save the new sheets to the project requisition. No nest was sent.");
        }

        private static void ReadSavedDestination(SavedNestReceipt receipt,XElement saved)
        {
            if(receipt.SavedTo.Equals("Requisition",StringComparison.OrdinalIgnoreCase))
            {
                var item=saved.Elements().FirstOrDefault(e=>Named(e,"RequisitionItem"));
                if(item==null)throw new InvalidDataException("The nest was saved, but PowerFab returned no requisition item. Review the API response before saving again.");
                receipt.RequisitionNumber=Value(item,"RequisitionNumber");receipt.RequisitionItemId=Value(item,"RequisitionItemID");
                if(receipt.RequisitionNumber.Length==0||!InventoryTracking.PositiveId(receipt.RequisitionItemId))
                    throw new InvalidDataException("PowerFab saved the nest without confirming the requisition number and item ID. Review the response before saving again.");
                receipt.StockStatus="Awaiting purchase";
            }
            else if(receipt.SavedTo.Equals("Inventory",StringComparison.OrdinalIgnoreCase))
            {
                var item=saved.Elements().FirstOrDefault(e=>Named(e,"InventoryItem"));
                if(item!=null)
                {
                    string id=Value(item,"ID","InventoryItemID"),po=Value(item,"PONumber");
                    if(InventoryTracking.PositiveId(id))receipt.CurrentInventoryIds.Add(id);
                    if(po.Length>0){receipt.CurrentPurchaseOrders.Add(po);receipt.PurchaseOrderNumber=po;}
                    receipt.StockStatus=True(Value(item,"OnOrder"))?"On order":"In stock";
                }
                else receipt.StockStatus="Inventory linked";
            }
            else receipt.StockStatus="Future remnant";
        }

        public int RefreshPurchaseLinks(NestingSnapshot snapshot,string folder)
        {
            var jobs=snapshot.PartNodes().Select(p=>NestingSnapshot.Field(p,"JobNumber","Job")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var receipts=ReadReceipts(folder).Where(r=>r.Parts.Any(p=>jobs.Contains(p.JobNumber))).ToList();
            if(receipts.Count==0)return 0;
            var native=readModel?.NestStates(receipts)??new Dictionary<string,NativeNestState>();
            int updated=0;
            foreach(var batch in receipts.Chunk(100))
            {
                var request=new fsreqFabSuiteXMLRequest{GetNestStatus=new[]{new fsreqGetNestStatus{NestID=batch.Select(r=>r.NestId).ToArray(),APILog=fsreqCommandAPILog.all,APILogSpecified=true}}};
                string xml=Serialize(request);var response=ParseResponse(execute(xml));ThrowErrors(response);
                string log=Path.Combine(folder,"PurchasingLinkLog");Directory.CreateDirectory(log);
                string stem=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(log,stem+".request.xml"),xml);File.WriteAllText(Path.Combine(log,stem+".response.xml"),response.ToString());
                File.WriteAllText(Path.Combine(log,stem+".native.json"),JsonSerializer.Serialize(batch.Select(r=>new {
                    r.NestId,r.PowerFabNestId,State=native.TryGetValue(r.NestId,out var state)?state:null
                }),LoadedNestResult.JsonOptions));
                var command=response.Descendants().FirstOrDefault(e=>Named(e,"GetNestStatus"));
                if(command==null||!True(Value(command,"Successful")))throw new InvalidDataException("PowerFab did not confirm the saved nest statuses. Existing project history was kept.");
                foreach(var receipt in batch)
                {
                    var status=command.Elements().FirstOrDefault(e=>Named(e,"NestStatus")&&Value(e,"NestID")==receipt.NestId);
                    native.TryGetValue(receipt.NestId,out var nativeState);
                    if(nativeState?.Exists==true)receipt.ConfirmedCutQuantity=nativeState.QuantityCut;
                    if(status==null)
                    {
                        // An omitted API row alone is not deletion evidence. The host's
                        // current nest record can positively confirm removal or cutting.
                        if(nativeState==null)continue;
                        if(HasNoCurrentMaterial(receipt,nativeState))
                        {
                            UpdateInactive(receipt);receipt.LinksRefreshedAtUtc=DateTime.UtcNow;WriteReceipt(folder,receipt);updated++;continue;
                        }
                        if(nativeState.Exists&&nativeState.InUse!=false&&nativeState.QuantityCut<=0)
                        {
                            if(nativeState.InUse==true)
                            {
                                receipt.InactiveInPowerFab=false;receipt.PartsReleased=false;receipt.RemovedAtUtc=null;
                                receipt.StockStatus=receipt.SavedTo.Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase)?"Future remnant":"Link refresh pending";
                                receipt.LinksRefreshedAtUtc=DateTime.UtcNow;WriteReceipt(folder,receipt);updated++;
                            }
                            continue;
                        }
                        UpdateInactive(receipt);receipt.LinksRefreshedAtUtc=DateTime.UtcNow;WriteReceipt(folder,receipt);updated++;continue;
                    }
                    bool HasQuantity(XElement item)=>!decimal.TryParse(Value(item,"Quantity"),NumberStyles.Number,CultureInfo.InvariantCulture,out var quantity)||quantity>0;
                    var inventory=status.Elements().Where(e=>Named(e,"NestedInventory")&&HasQuantity(e)).ToList();
                    var requisitions=status.Elements().Where(e=>Named(e,"NestedRequisition")&&HasQuantity(e)).ToList();
                    receipt.CurrentInventoryIds=inventory.Select(e=>Value(e,"InventoryID")).Where(InventoryTracking.PositiveId).Distinct().ToList();
                    receipt.CurrentPurchaseOrders=inventory.Select(e=>Value(e,"PONumber")).Where(v=>v.Length>0).Distinct().ToList();
                    if(requisitions.Count>0)
                    {
                        receipt.RequisitionNumber=Value(requisitions[0],"RequisitionNumber");receipt.RequisitionItemId=Value(requisitions[0],"RequisitionItemID");
                    }
                    receipt.StockStatus=inventory.Count>0?(inventory.All(e=>True(Value(e,"OnOrder")))?"On order":inventory.Any(e=>True(Value(e,"OnOrder")))?"Part received":"In stock")
                        :requisitions.Count>0?"Awaiting purchase":receipt.SavedTo.Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase)?"Future remnant":"No active material link";
                    if(inventory.Count>0||requisitions.Count>0)
                    {receipt.InactiveInPowerFab=false;receipt.PartsReleased=false;receipt.RemovedAtUtc=null;}
                    else if(nativeState!=null&&(!nativeState.Exists||nativeState.InUse==false||nativeState.QuantityCut>0||HasNoCurrentMaterial(receipt,nativeState)))UpdateInactive(receipt);
                    else
                    {
                        receipt.InactiveInPowerFab=!receipt.SavedTo.Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase);
                        if(nativeState?.Exists==true&&nativeState.InUse==true)
                        {
                            receipt.InactiveInPowerFab=false;receipt.PartsReleased=false;receipt.RemovedAtUtc=null;
                            receipt.StockStatus=receipt.SavedTo.Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase)?"Future remnant":"Link refresh pending";
                        }
                    }
                    receipt.LinksRefreshedAtUtc=DateTime.UtcNow;WriteReceipt(folder,receipt);updated++;
                }
            }
            return updated;
        }
        private static bool HasNoCurrentMaterial(SavedNestReceipt receipt,NativeNestState native)
        {
            bool future=receipt.SavedTo.Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase)
                ||receipt.MaterialSource.Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase)
                ||InventoryTracking.PositiveId(native.ParentRemnantId);
            return native.MaterialLinksChecked&&!native.HasLiveRequisition&&!native.HasLiveInventory&&!future;
        }
        private static void UpdateInactive(SavedNestReceipt receipt)
        {
            receipt.InactiveInPowerFab=true;
            bool cut=receipt.ConfirmedCutQuantity>0;
            receipt.PartsReleased=!cut;
            receipt.StockStatus=cut?"Cut in PowerFab":"Removed in PowerFab";
            receipt.CurrentInventoryIds.Clear();receipt.CurrentPurchaseOrders.Clear();
            receipt.RemovedAtUtc??=DateTime.UtcNow;
        }
        private static void WriteReceipt(string folder,SavedNestReceipt receipt)
        {
            string directory=Path.Combine(folder,"SavedNestReceipts");Directory.CreateDirectory(directory);
            string key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(receipt.NestId))).ToLowerInvariant();
            string path=Path.Combine(directory,key+".json"),temp=path+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(receipt,LoadedNestResult.JsonOptions));File.Move(temp,path,true);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static partial class RemnantRefreshChecks
    {
        private static void RegisteredRemnantChecks()
        {
            CrossJobRegistryAndSave();
            LiveFutureRowsAndFailures();
            NativeLifecycleAndUnits();
            NestedRemnantAndRectangle();
            PowerFabInventoryIsOnlyRowSource();
        }
        private static NestingSnapshot ForJob(string job)
        {
            var document=new XDocument(new XElement(Snapshot().RequestRoot));
            foreach(var part in document.Descendants().Where(e=>e.Name.LocalName=="WorkOrderPart"))StockGeometry.Set(part,"JobNumber",job);
            return NestingSnapshot.FromDocument(document);
        }
        private static RegisteredRemnant Native(string id="42")=>new RegisteredRemnant{RemnantId=id,Name="test nnr_S1_R1",CreatorNestId="1001",
            CreatorGuid="parent-nest",CreatorJobs="test nnr",CreatorInUse=true,HasInventory=true,Shape="PLT",Grade="250",ThicknessMm=10,
            LengthMm=1000,WidthMm=500,AreaSqm=0.34m,Quantity=1,PurchaseOrder="011126"};
        private static void CrossJobRegistryAndSave()
        {
            string folder=Folder("all-jobs");var native=Native();var store=new NativeStore(native);
            var parent=Sheet("46988","test nnr");parent.Add(new XElement("NestID","parent-nest"));
            var server=new Server{Inventory=new List<XElement>{parent,Future()},MissingRemnantResult=true};
            var api=new PowerFabNestApi(server.Execute,store.ReadModel());
            foreach(string job in new[]{"H0306","H0412","another new job"})
            {
                var snapshot=ForJob(job);api.RefreshInventory(snapshot,folder);
                var records=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList();
                int future=records.FindIndex(StockGeometry.IsFuture);
                Check(future>=0&&(string)snapshot.Inventory.Rows[future]["Available Qty"]=="1",job+" can use test nnr's verified remnant");
                Check((string)snapshot.Inventory.Rows[0]["Available Qty"]=="0","Reserved parent stays unavailable to "+job);
                var material=LoadedNestResult.CreateTemplate(snapshot,future).Material;
                Check(material.ParentRemnantPowerFabId=="42"&&material.StockJob==""&&material.StockContour.Count==6,
                    "Cross-job template keeps native remnant identity and actual polygon");
            }
            Check(server.RemnantReads==0,"Verified native registration works even when optional Remnant_Get is unavailable");
            var target=ForJob("H0412");api.RefreshInventory(target,folder);
            var options=PlateSelection.StockOptions(target,PlateSelection.Groups(target,Array.Empty<SavedNestReceipt>()).Single(),Array.Empty<SavedNestReceipt>());
            Check(options.Count==1,"Only the remnant is offered alongside the reserved parent");
            var choice=new MaterialPlan{Group=PlateSelection.Groups(target,Array.Empty<SavedNestReceipt>()).Single(),
                NewSheet=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=10,Length=3000,Width=1500,Unit="MM",Dimensions="10X1500"}};
            choice.StockIndexes.Add(options[0].InventoryIndex);
            var part=LoadedNestResult.CreateTemplate(target,options[0].InventoryIndex).Parts.Single();
            var shapes=new Dictionary<string,DxfShape>{[LoadedNestResult.PartKey(part)]=new DxfShape{Source="fixture",Outer=new PathD{
                new PointD(0,0),new PointD(100,0),new PointD(100,50),new PointD(0,50)}}};
            var plans=NestingBatch.Calculate(target,choice,Array.Empty<SavedNestReceipt>(),new LayoutConditions(),shapes,CancellationToken.None);
            Check(plans.Count==1&&plans[0].Package.Material.Source=="FutureRemnant"&&plans[0].Unplaced.Count==0,
                "Another job nests on the real leftover without generating a new purchasing sheet");
            var result=plans[0].Package;result.NestImageFile="sheet.png";
            foreach(var remnant in result.Remnants)remnant.ImageFile="sheet.png";
            File.WriteAllBytes(Path.Combine(folder,"sheet.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
            string path=Path.Combine(folder,"child.json");File.WriteAllText(path,JsonSerializer.Serialize(result,LoadedNestResult.JsonOptions));
            server.AfterSave=()=>native.ChildNests=1;
            var receipt=api.Save(LoadedNestResult.Load(path,target),target,folder);
            Check(receipt.SavedTo=="FutureRemnant"&&receipt.MaterialKey=="R:42","Save confirms native future-remnant allocation for the other job");
            var used=server.LastSave!.Descendants().Single(e=>e.Name.LocalName=="MaterialUsed");
            Check(NestingSnapshot.Field(used,"ParentRemnantPowerFabID")=="42"&&!used.Elements().Any(e=>e.Name.LocalName=="ExternalInventoryLinkID"),
                "SaveNest links only the leftover, never the complete reserved parent");
            var third=ForJob("third job");api.RefreshInventory(third,folder);
            var original=LoadedNestResult.InventoryNodes(third.RequestRoot).Single(e=>StockGeometry.PowerFabRemnantId(e)=="42");
            Check(InventoryTracking.UnavailableReason(original,third)=="Allocated to nest","Claimed leftover cannot be allocated again by a third job");
            Check(File.Exists(Path.Combine(folder,"RemnantInventoryLog","latest-native-registration.json")),"Native availability evidence is retained");
        }
        private static void LiveFutureRowsAndFailures()
        {
            string folder=Folder("live-future");var future=Future();StockGeometry.Set(future,"Job","test nnr");
            StockGeometry.Set(future,"PercentCombined","90");StockGeometry.Set(future,"NestID","parent-nest");StockGeometry.Set(future,"RemnantFabSuiteID","42");
            NestingSnapshot.Child(future,"RemnantID")!.Remove();
            var server=new Server{Inventory=new List<XElement>{future},Remnants=new List<XElement>{Remnant(Parent("test nnr"))}};
            var snapshot=ForJob("different job");server.Api().RefreshInventory(snapshot,folder);
            Check(snapshot.Inventory.Rows.Count==1&&(string)snapshot.Inventory.Rows[0]["Available Qty"]=="1",
                "Live GetInventory future row survives parent combined flags and historical ID spelling");
            Check(server.RemnantReads==1,"Live future IDs become discovery candidates even with no original export row");
            // A detail failure must not manufacture a pending inventory row.
            future=Future();StockGeometry.Set(future,"RemnantID","99");server.Inventory=new List<XElement>{Sheet("104"),future};
            server.MissingRemnantResult=true;server.Api().RefreshInventory(snapshot,folder);
            Check(!LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Any(StockGeometry.IsFuture),"Failed remnant details do not leave a cached pending row");
            Check((string)snapshot.Inventory.Rows[0]["Available Qty"]=="1","Ordinary sheet remains usable during optional remnant failure");
        }
        private static void NativeLifecycleAndUnits()
        {
            string folder=Folder("native-lifecycle");var row=Native();var store=new NativeStore(row);var server=new Server{Inventory=new List<XElement>{Future()},MissingRemnantResult=true};
            var api=new PowerFabNestApi(server.Execute,store.ReadModel());var snapshot=ForJob("any job");
            row.LengthMm/=25.4m;row.WidthMm/=25.4m;row.ThicknessMm/=25.4m;api.RefreshInventory(snapshot,folder);
            var stock=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Single();
            Check(StockGeometry.Mm(stock,"Length")==1000&&StockGeometry.Mm(stock,"PlateThickness")==10,
                "Native linear units are checked against the saved metric outline");
            row.LengthMm=1000;row.WidthMm=500;row.ThicknessMm=10;row.HasInventory=false;row.HasRequisition=true;
            api.RefreshInventory(snapshot,folder);Check((string)snapshot.Inventory.Rows[0]["Available Qty"]=="1","Future can be planned while parent material is on a live PR");
            row.HasRequisition=false;api.RefreshInventory(snapshot,folder);Check(snapshot.Inventory.Rows.Count==0,"Deleted parent purchasing link cannot resurrect old leftover");
            row.HasInventory=true;row.CreatorCutQuantity=1;api.RefreshInventory(snapshot,folder);Check(snapshot.Inventory.Rows.Count==0,"Cut creator no longer exposes an uncut future row");
            row.CreatorCutQuantity=0;row.CreatorInUse=false;api.RefreshInventory(snapshot,folder);Check(snapshot.Inventory.Rows.Count==0,"Inactive creator cannot supply future stock");
            row.CreatorInUse=true;row.AreaSqm=0.33m;api.RefreshInventory(snapshot,folder);
            Check((string)snapshot.Inventory.Rows[0]["Available Qty"]=="0","Native area mismatch rejects a stale polygon");
            row.AreaSqm=0.34m;row.Skeleton=true;api.RefreshInventory(snapshot,folder);Check(snapshot.Inventory.Rows.Count==0,"Skeleton cannot be offered as usable remnant");
            row.Skeleton=false;var physical=Sheet("777");StockGeometry.Set(physical,"RemnantID","42");server.Inventory=new List<XElement>{physical};
            api.RefreshInventory(snapshot,folder);Check(snapshot.Inventory.Rows.Count==1&&!StockGeometry.IsFuture(LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Single()),
                "Physical remnant replaces the future without a duplicate native ID");
        }
        private static void NestedRemnantAndRectangle()
        {
            string folder=Folder("remnant-chain");var root=Native();root.ChildNests=1;
            var child=Native("43");child.CreatorNestId="2002";child.ParentRemnantId="42";child.CreatorJobs="second job";
            child.HasInventory=false;child.Grade="";
            var outline=RemnantCatalog.Read(folder).Single();outline.PowerFabRemnantId="43";
            File.WriteAllText(Path.Combine(folder,"RemnantOutlines","R-43.json"),JsonSerializer.Serialize(outline,LoadedNestResult.JsonOptions));
            var server=new Server{Inventory=new List<XElement>{Future(),Future("43")},MissingRemnantResult=true};var api=new PowerFabNestApi(server.Execute,new NativeStore(root,child).ReadModel());
            var snapshot=ForJob("fourth job");api.RefreshInventory(snapshot,folder);
            var records=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList();
            Check(records.Count==2&&InventoryTracking.UnavailableReason(records.Single(s=>StockGeometry.PowerFabRemnantId(s)=="42"),snapshot)=="Allocated to nest",
                "A parent remnant stays claimed while its child's leftover is tracked separately");
            var free=records.Single(s=>StockGeometry.PowerFabRemnantId(s)=="43");
            Check(InventoryTracking.UnavailableReason(free,snapshot)==""&&F(free,"Grade")=="250",
                "Remnant of a remnant inherits verified material through its live parent chain and works across jobs");
            root.HasInventory=false;api.RefreshInventory(snapshot,folder);
            Check(snapshot.Inventory.Rows.Count==0,"Deleting the root material invalidates the whole future-remnant chain");
            folder=Folder("native-rectangle",false);var rectangle=Native();rectangle.AreaSqm=0.5m;
            server.Inventory=new List<XElement>{Future()};api=new PowerFabNestApi(server.Execute,new NativeStore(rectangle).ReadModel());
            api.RefreshInventory(snapshot,folder);var material=LoadedNestResult.CreateTemplate(snapshot,0).Material;
            Check(material.StockContour.Count==4&&Math.Abs(Clipper.Area(StockGeometry.Region(material))-500000)<1,
                "An exact native area/bounds match recovers a solid rectangular leftover without a DXF upload");
            Check(RemnantCatalog.Read(folder).Single().PowerFabRemnantId=="42","Verified rectangle is retained for future job requests");
        }
        private static void PowerFabInventoryIsOnlyRowSource()
        {
            string folder=Folder("powerfab-only");
            var outline=RemnantCatalog.Read(folder).Single();
            foreach(string id in new[]{"176","177","178","179","180","181"})
            {
                outline.PowerFabRemnantId=id;
                File.WriteAllText(Path.Combine(folder,"RemnantOutlines","R-"+id+".json"),JsonSerializer.Serialize(outline,LoadedNestResult.JsonOptions));
            }
            Directory.CreateDirectory(Path.Combine(folder,"SavedNestReceipts"));
            File.WriteAllText(Path.Combine(folder,"SavedNestReceipts","old-test.json"),JsonSerializer.Serialize(new SavedNestReceipt{
                NestId="parent-nest",PowerFabNestId="1001",RemnantPowerFabIds=new List<string>{"42","176","177","178","179","180","181"}
            },LoadedNestResult.JsonOptions));
            var snapshot=Snapshot(Future(),Future("176"));
            Check(snapshot.Inventory.DefaultView.Count==0,"Old export stock is hidden until PowerFab inventory has been read");
            var sheet=Sheet("104");StockGeometry.Set(sheet,"Quantity","3");StockGeometry.Set(sheet,"PONumber","LIVE-PO");
            var server=new Server{Inventory=new List<XElement>{sheet},MissingRemnantResult=true};
            var row=Native();var api=new PowerFabNestApi(server.Execute,new NativeStore(row,Native("176")).ReadModel());
            api.RefreshInventory(snapshot,folder);
            Check(snapshot.Inventory.Rows.Count==1&&(string)snapshot.Inventory.Rows[0]["PowerFab ID"]=="104"
                &&(string)snapshot.Inventory.Rows[0]["Qty"]=="3","Only current PowerFab stock is listed despite cached remnants, receipts and old XML");
            Check(server.RemnantReads==0&&snapshot.RemnantRefreshWarning.Length==0,"Old cached IDs do not trigger remnant queries or pending warnings");
            server.Inventory.Clear();api.RefreshInventory(snapshot,folder);
            Check(snapshot.Inventory.Rows.Count==0&&snapshot.Inventory.DefaultView.Count==0,"An empty PowerFab inventory response stays empty");
            var current=Future();StockGeometry.Set(current,"Quantity","2");StockGeometry.Set(current,"PONumber","CURRENT-PO");
            StockGeometry.Set(current,"Location","CURRENT-LOCATION");server.Inventory.Add(current);
            row.Quantity=7;row.PurchaseOrder="OTHER-PARENT-PO";
            api.RefreshInventory(snapshot,folder);
            var live=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Single();
            Check(StockGeometry.PowerFabRemnantId(live)=="42"&&F(live,"Quantity")=="2"&&F(live,"PONumber")=="CURRENT-PO"
                &&F(live,"Location")=="CURRENT-LOCATION","Remnant details cannot replace the current inventory quantity, PO or location");
            Check(snapshot.Inventory.Rows.Count==1&&server.RemnantReads==0,"Only the remnant returned by PowerFab is enriched, without replaying other cached IDs");
            server.Inventory.Clear();api.RefreshInventory(snapshot,folder);
            Check(snapshot.Inventory.Rows.Count==0&&!LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Any(),"A remnant removed from current inventory disappears without a placeholder");
        }

        private sealed class NativeStore
        {
            private readonly RegisteredRemnant[] rows;
            public NativeStore(params RegisteredRemnant[] rows){this.rows=rows;}
            public PowerFabReadModel ReadModel()=>new PowerFabReadModel(sql=>new NativeCommand(this,sql));
            private DataTable Read(string sql,IDataParameterCollection parameters)
            {
                if(!sql.StartsWith("SELECT ",StringComparison.Ordinal))throw new Exception("Only read-only native commands are allowed");
                if(sql.Contains("AS InventoryID"))
                {
                    var physical=new DataTable();foreach(string name in new[]{"InventoryID","TrueShapeID","RemnantID","RemnantName","SquareMeters"})physical.Columns.Add(name);
                    return physical;
                }
                if(!sql.Contains("FROM externalnestresultremnants rr"))throw new Exception("Unexpected native SQL: "+sql);
                Check(parameters.Count>0&&parameters.Cast<IDataParameter>().All(p=>p.DbType==DbType.Int64&&p.Value is long),"Native ID lookups use typed bound parameters");
                var ids=parameters.Cast<IDataParameter>().Select(p=>Convert.ToString(p.Value,CultureInfo.InvariantCulture)).ToHashSet();
                bool creators=sql.Contains("WHERE n.ExternalNestID IN (");
                var table=new DataTable();
                foreach(string name in new[]{"RemnantID","RemnantName","BoundingLength","BoundingWidth","SquareMeters","IsSkeleton","RemnantQuantity",
                    "CreatorNestID","CreatorGUID","ParentRemnantID","InUse","QuantityCut","Shape","DimThickness","HasInventory","HasRequisition","Grade","ChildNests","CreatorJobs","PONumber","OnOrder"})table.Columns.Add(name,typeof(object));
                foreach(var r in rows.Where(r=>ids.Contains(creators?r.CreatorNestId:r.RemnantId)))table.Rows.Add(r.RemnantId,r.Name,r.LengthMm,r.WidthMm,r.AreaSqm,r.Skeleton,r.Quantity,
                    r.CreatorNestId,r.CreatorGuid,r.ParentRemnantId,r.CreatorInUse,r.CreatorCutQuantity,r.Shape,r.ThicknessMm,r.HasInventory,r.HasRequisition,r.Grade,r.ChildNests,r.CreatorJobs,r.PurchaseOrder,r.OnOrder);
                return table;
            }
            private sealed class NativeCommand:IDbCommand
            {
                private readonly NativeStore owner;
                public NativeCommand(NativeStore owner,string sql){this.owner=owner;CommandText=sql;}
                [AllowNull]public string CommandText{get;set;}
                public int CommandTimeout{get;set;}public CommandType CommandType{get;set;}
                public IDbConnection? Connection{get;set;}public IDbTransaction? Transaction{get;set;}
                public UpdateRowSource UpdatedRowSource{get;set;}public IDataParameterCollection Parameters{get;}=new NativeParameters();
                public void Dispose(){}public void Cancel()=>throw new NotSupportedException();public void Prepare()=>throw new NotSupportedException();
                public IDbDataParameter CreateParameter()=>new NativeParameter();
                public int ExecuteNonQuery()=>throw new NotSupportedException();public object ExecuteScalar()=>throw new NotSupportedException();
                public IDataReader ExecuteReader()=>ExecuteReader(CommandBehavior.Default);
                public IDataReader ExecuteReader(CommandBehavior behavior)=>owner.Read(CommandText,Parameters).CreateDataReader();
            }
            private sealed class NativeParameters:ArrayList,IDataParameterCollection
            {
                public bool Contains(string name)=>this.Cast<IDataParameter>().Any(p=>p.ParameterName==name);
                public int IndexOf(string name)=>this.Cast<IDataParameter>().ToList().FindIndex(p=>p.ParameterName==name);
                public void RemoveAt(string name)=>RemoveAt(IndexOf(name));
                public object this[string name]{get=>this[IndexOf(name)]!;set=>this[IndexOf(name)]=value;}
            }
            private sealed class NativeParameter:IDbDataParameter
            {
                public DbType DbType{get;set;}public ParameterDirection Direction{get;set;}public bool IsNullable=>false;
                [AllowNull]public string ParameterName{get;set;}="";[AllowNull]public string SourceColumn{get;set;}="";
                public DataRowVersion SourceVersion{get;set;}public object? Value{get;set;}
                public byte Precision{get;set;}public byte Scale{get;set;}public int Size{get;set;}
            }
        }
    }
}

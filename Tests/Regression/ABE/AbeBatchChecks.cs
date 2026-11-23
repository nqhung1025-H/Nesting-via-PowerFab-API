using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeBatchChecks
    {
        private static int passed;
        private static void Check(bool value,string name){if(!value)throw new Exception("ABE batch: "+name);passed++;}
        private static void Reject(Action act,string name){try{act();}catch(InvalidDataException){passed++;return;}throw new Exception("ABE batch accepted: "+name);}
        private static (string Nc,string Sy2) Pair(string folder,string name,int thick,string mark="P1",string ext="")
        {
            Directory.CreateDirectory(folder);string nc=Path.Combine(folder,name+ext),sy2=Path.Combine(folder,name+".SY2");
            File.WriteAllText(nc,$"M102(SS400-{thick}.0E-L2)\nG92X3070Y1550\nG93X0Y0\nM100\nG00X20Y30\nM103\nG01X40\nG01Y40\nG01X20\nG01Y30\nM104\nM101\nG10\n");
            File.WriteAllText(sy2,$"[HEADER_INFO]\nVER=1.00\nSTYPE=2\nPDNUM=1\nMACHINENAME=TEST\n[PD_0001]\nHNO=1\nHNAME={mark}\nHKIND=1\nHMTT=SS400,{thick}.0\nP98DT=0,1,1,0,0\nPBX=20\nPBY=30\nPBAX=20\nPBAY=30\nPANG=0\nHSX=20\nHSY=10\nHSZX=20\nHSZY=10\n");return(nc,sy2);
        }
        private static NestingSnapshot Snapshot(int quantity=1)
        {
            XElement Part(int t)=>new XElement("WorkOrderPart",new XElement("JobNumber","BATCH-JOB"),new XElement("MainMark","A"+t),new XElement("PieceMark","P1"),
                new XElement("Quantity",quantity),new XElement("Shape","PLT"),new XElement("Grade","250"),new XElement("Dimensions",t+"X10"),
                new XElement("Length",new XAttribute("UOM","MM"),20),new XElement("Width",new XAttribute("UOM","MM"),10),new XElement("PlateThickness",new XAttribute("UOM","MM"),t));
            return NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",new XElement("WorkOrderID","BATCH-RUN"),new XElement("WorkOrderParts",Part(10),Part(12),Part(16)),new XElement("AvailableInventory"))));
        }
        public static int Run()
        {
            string root=Path.Combine(Path.GetTempPath(),"HanlonAbeBatchChecks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var a=Pair(Path.Combine(root,"10mm"),"same-program",10);
                var b=Pair(Path.Combine(root,"12mm"),"same-program",12,ext:".nc");
                var c=Pair(Path.Combine(root,"nested","16mm"),"third",16);
                File.WriteAllText(Path.Combine(root,"README.txt"),"Unrelated report");File.WriteAllText(Path.Combine(root,"drawing.dxf"),"Unrelated DXF");
                var folder=AbeImportBatch.ReadFolder(root,CancellationToken.None);
                Check(folder.Items.Count==3&&folder.Items.All(i=>i.Document!=null),"Folder recursively imports only the three valid pairs");
                Check(folder.Items.Select(i=>i.Document!.Thickness).OrderBy(t=>t).SequenceEqual(new decimal[]{10,12,16}),"All requested thicknesses read from source data");
                Check(folder.Items.Count(i=>i.Name=="same-program")==2,"Same names in separate thickness folders are not mixed");
                var selected=AbeImportBatch.ReadFiles(new[]{a.Nc,a.Sy2,b.Sy2,c.Nc,a.Nc},CancellationToken.None);
                Check(selected.Items.Count==3&&selected.Items.All(i=>i.Document!=null),"NC-only, SY2-only and both-file selections resolve counterparts");
                Check(selected.DuplicateSelections==2,"Selecting the same path or both members does not import a sheet twice");
                var missing=Pair(Path.Combine(root,"broken"),"missing",10);File.Delete(missing.Sy2);
                var mixed=AbeImportBatch.ReadFiles(new[]{missing.Nc,a.Nc},CancellationToken.None);
                Check(mixed.Items.Count(i=>i.Document!=null)==1&&mixed.Items.Count(i=>i.Document==null)==1,"Missing SY2 does not suppress valid neighboring files");
                var orphan=Pair(Path.Combine(root,"orphan"),"orphan",12);File.Delete(orphan.Nc);
                Check(AbeImportBatch.ReadFiles(new[]{orphan.Sy2},CancellationToken.None).Items.Single().Status.Contains("missing"),"Missing NC is reported");
                var ambiguous=Pair(Path.Combine(root,"ambiguous"),"variant",10);File.Copy(ambiguous.Nc,ambiguous.Nc+".nc");
                Check(AbeImportBatch.ReadFiles(new[]{ambiguous.Sy2},CancellationToken.None).Items.Single().Document==null,"Ambiguous NC variants are not guessed");
                Check(AbeImportBatch.ReadFiles(new[]{ambiguous.Nc,ambiguous.Sy2},CancellationToken.None).Items.Single().Document!=null,"Explicit NC selection resolves an ambiguous folder");
                Check(AbeImportBatch.ReadFiles(new[]{ambiguous.Nc,ambiguous.Nc+".nc"},CancellationToken.None).Items.All(i=>i.Document==null),"Two variants cannot share one selected SY2");
                var invalid=Pair(Path.Combine(root,"invalid"),"bad",10);File.WriteAllText(invalid.Nc,"M100\nM98P10\nM101\n");
                var partial=AbeImportBatch.ReadFiles(new[]{invalid.Nc,b.Nc},CancellationToken.None);
                Check(partial.Items.Count(i=>i.Document!=null)==1&&partial.Items.Single(i=>i.Document==null).Status.Contains("Unsupported"),"Invalid NC is identified while another thickness stays available");
                var mismatch=Pair(Path.Combine(root,"mismatch"),"mismatch",10);File.WriteAllText(mismatch.Sy2,File.ReadAllText(mismatch.Sy2).Replace("SS400,10.0","SS400,12.0"));
                Check(AbeImportBatch.ReadFiles(new[]{mismatch.Nc},CancellationToken.None).Items.Single().Document==null,"NC/SY2 thickness mismatch is rejected per pair");
                var snapshot=Snapshot();var budget=new AbeBatchBudget();var plans=new List<CalculatedNest>();string batchId=Guid.NewGuid().ToString("N");
                foreach(var item in folder.Items.OrderBy(i=>i.Document!.Thickness))
                {
                    var doc=item.Document!;doc.Reference.MaterialConfirmed=true;var match=budget.Match(doc,snapshot,Array.Empty<SavedNestReceipt>());
                    var sheet=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=doc.Thickness,Length=200,Width=100,Dimensions=doc.Thickness.ToString("0.########",CultureInfo.InvariantCulture)+"X100",Unit="MM"};
                    var template=PlateSelection.ForMaterial(snapshot,match.Group,sheet,new LayoutConditions());template.RequisitionNumber="REQ500";template.RequisitionBatchId=batchId;template.AutoRequisitionNumber=true;template.SheetNumber=plans.Count+1;
                    var shapes=match.Bindings.Select(x=>x.Part).Distinct().ToDictionary(LoadedNestResult.PartKey,p=>new DxfShape{Source="fixture.dxf",Outer=new PathD{new PointD(0,0),new PointD(20,0),new PointD(20,10),new PointD(0,10)}});
                    var plan=AbeNestImport.CreatePlan(doc,match,template,shapes,100,CancellationToken.None);budget.Reserve(plan.Package);plans.Add(plan);
                    Check(plan.Package.Material.Thickness==match.Group.ThicknessMm&&plan.Placed.Single().Part.MainMark=="A"+doc.Thickness.ToString("0.########",CultureInfo.InvariantCulture),"Identical piece mark is matched within thickness "+doc.Thickness);
                }
                Check(plans.Select(p=>p.Package.NestId).Distinct().Count()==3,"Each imported sheet has a unique nest ID");
                Check(plans.All(p=>p.Placed.Single().X==20&&p.Placed.Single().Y==30),"All three original placements are preserved");
                var requests=new List<XDocument>();
                foreach(var plan in plans)
                {
                    string folderOut=Path.Combine(root,"results",plan.Package.NestId);Directory.CreateDirectory(folderOut);
                    var pair=folder.Items.Single(i=>i.Document!.Thickness==plan.Package.Material.Thickness).Document!;
                    File.WriteAllText(Path.Combine(folderOut,pair.Reference.NcFile),pair.NcText);File.WriteAllText(Path.Combine(folderOut,pair.Reference.Sy2File),pair.Sy2Text);
                    plan.Package.NestImageFile="sheet.png";foreach(var remnant in plan.Package.Remnants)remnant.ImageFile="sheet.png";
                    File.WriteAllBytes(Path.Combine(folderOut,"sheet.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
                    plan.Package.Placements=plan.Placed.Select(p=>new NestPlacement{JobNumber=p.Part.JobNumber,MainMark=p.Part.MainMark,PieceMark=p.Part.PieceMark,Sequence=p.Part.Sequence,LotNumber=p.Part.LotNumber,X=p.X,Y=p.Y,Rotation=p.Angle}).ToList();
                    string resultPath=Path.Combine(folderOut,"result.json");File.WriteAllText(resultPath,JsonSerializer.Serialize(plan.Package,LoadedNestResult.JsonOptions));
                    var loaded=LoadedNestResult.Load(resultPath,snapshot);requests.Add(XDocument.Parse(PowerFabNestApi.Serialize(PowerFabNestApi.BuildSaveRequest(loaded,snapshot))));
                }
                string Value(XDocument d,string name)=>d.Descendants().First(e=>e.Name.LocalName==name).Value;
                Check(requests.All(r=>Value(r,"RequisitionNumber")=="REQ500"),"All three real SDK SaveNest requests carry the shared project requisition");
                Check(requests.Select(r=>Value(r,"Dimensions")).SequenceEqual(new[]{"10X100","12X100","16X100"}),"SaveNest keeps each sheet thickness separate");
                Check(requests.Select(r=>Value(r,"MainMark")).SequenceEqual(new[]{"A10","A12","A16"}),"SaveNest preserves the correct part identity for each thickness");
                Reject(()=>budget.Match(folder.Items.First(i=>i.Document!.Thickness==10).Document!,snapshot,Array.Empty<SavedNestReceipt>()),"Pending sheet cannot allocate the same part quantity again");
                var remaining=new AbeBatchBudget();var snap2=Snapshot(2);var doc10=folder.Items.First(i=>i.Document!.Thickness==10).Document!;
                var first=remaining.Match(doc10,snap2,Array.Empty<SavedNestReceipt>());
                remaining.Reserve(new NestResultPackage{Parts=new List<NestPart>{new NestPart{JobNumber=first.Bindings[0].Part.JobNumber,MainMark=first.Bindings[0].Part.MainMark,PieceMark="P1",Quantity=1}}});
                Check(remaining.Match(doc10,snap2,Array.Empty<SavedNestReceipt>()).Bindings.Count==1,"A second sheet can use genuinely remaining copies");
                Check(snap2.Parts.Rows.Count==3&&snap2.Parts.Rows.Cast<System.Data.DataRow>().All(r=>(string)r["Qty"]=="2"),"Pending budget does not rewrite PowerFab source quantities");
                using var cancellation=new CancellationTokenSource();cancellation.Cancel();bool stopped=false;
                try{AbeImportBatch.ReadFolder(root,cancellation.Token);}catch(OperationCanceledException){stopped=true;}
                Check(stopped,"Folder import honors cancellation");
                Console.WriteLine($"PASS: {passed} ABE multi-file/folder and 10/12/16 mm batch checks.");return passed;
            }
            finally{Directory.Delete(root,true);}
        }
    }
}

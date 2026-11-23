using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class Phase10And11Checks
    {
        private static int passed;
        private static void Check(bool ok,string name){if(!ok)throw new Exception("Phase10/11: "+name);passed++;}
        private static void Reject(Action action,string name)
        {try{action();}catch(InvalidDataException){passed++;return;}throw new Exception("Phase10/11 accepted: "+name);}
        public static int Run()
        {
            string root=Path.Combine(Path.GetTempPath(),"HanlonPhase10",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                string fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","Phase10and11");
                var xml=XDocument.Load(Path.Combine(fixture,"PowerFab-request.xml"));
                var snapshot=NestingSnapshot.FromDocument(xml);
                var doc=AbeNestImport.Read(Path.Combine(fixture,"H0306-PHASE10+11-10MM.nc"),Path.Combine(fixture,"H0306-PHASE10+11-10MM.sy2"));
                Check(doc.ProductionCount==16&&doc.PackerCount==12,"Actual 10mm source counts");
                var match=AbeNestImport.MatchParts(doc,snapshot,Array.Empty<SavedNestReceipt>());
                Check(match.Bindings.Count==16&&match.Bindings.Select(b=>b.Part.LotNumber).Distinct().Count()==2,"All actual lot18/lot20 parts match");
                var source=PowerFabDxfSource.Read(snapshot,match.Bindings.Select(b=>b.Part),null,root);
                Check(source.Count==8,"Every actual PowerFab assembly receives embedded NC1 geometry without API fallback");
                var shapes=source.ToDictionary(p=>p.Key,p=>DxfGeometry.Read(p.Value.Text,p.Value.SourceName));
                Check(shapes.Values.Count(s=>s.CurveTolerance>0)==6,"Only curved external outlines retain sampling clearance");
                var material=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=10,Length=3000,Width=1500,Dimensions="10X1500",Unit="MM"};
                var template=PlateSelection.ForMaterial(snapshot,match.Group,material,new LayoutConditions());
                var plan=AbeNestImport.CreatePlan(doc,match,template,shapes,100,CancellationToken.None);
                const string nestName="H0306-PHASE10+11-10MM";
                Check(plan.Package.NestName==nestName,"Actual ABE filename supplies the clean nest name");
                Check(plan.Package.Remnants.All(r=>r.Name==nestName),"Actual future remnants use the clean parent nest name");
                var actualRight=plan.Package.Remnants.Single();
                Check(actualRight.Edge=="Right"&&actualRight.Contour.Count==4&&actualRight.Holes.Count==0,"Actual 10mm ABE layout produces a rectangular right remnant");
                Check(actualRight.Width==1500&&actualRight.Length<3000&&actualRight.Area==actualRight.Length*1500,
                    "Actual ABE remnant dimensions and area exclude the bottom-left strip");
                Check(plan.Placed.Count==16&&plan.Package.Parts.Sum(p=>p.Quantity)==16,"Actual 10mm full layout verifies, retaining all quantities");
                Check(plan.Package.AbeImport!.Packers.Count==12,"All actual packers remain separate from job quantity");
                Check(plan.Package.Material.Grade=="250"&&doc.Reference.MaterialConfirmed,"SS400 automatically uses matched PowerFab grade250");
                plan.Package.RequisitionNumber="REQCHECK";
                plan.Package.Placements=plan.Placed.Select(p=>new NestPlacement{JobNumber=p.Part.JobNumber,MainMark=p.Part.MainMark,PieceMark=p.Part.PieceMark,
                    LotNumber=p.Part.LotNumber,Sequence=p.Part.Sequence,X=p.X,Y=p.Y,Rotation=p.Angle}).ToList();
                plan.Package.NestImageFile="sheet.png";
                foreach(var remnant in plan.Package.Remnants)remnant.ImageFile="sheet.png";
                File.WriteAllBytes(Path.Combine(root,"sheet.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
                File.WriteAllText(Path.Combine(root,doc.Reference.NcFile),doc.NcText);File.WriteAllText(Path.Combine(root,doc.Reference.Sy2File),doc.Sy2Text);
                string json=Path.Combine(root,"result.json");File.WriteAllText(json,JsonSerializer.Serialize(plan.Package,LoadedNestResult.JsonOptions));
                var loaded=LoadedNestResult.Load(json,snapshot);
                Check(loaded.Result.Parts.Sum(p=>p.Quantity)==16&&loaded.Result.AbeImport!.Packers.Count==12,"Actual 10mm source hashes, lot matches and geometry metadata survive result reload");
                var save=XDocument.Parse(PowerFabNestApi.Serialize(PowerFabNestApi.BuildSaveRequest(loaded,snapshot)));
                Check(save.Descendants().Single(e=>e.Name.LocalName=="SourceApplication").Value=="Hanlon - Nesting","SaveNest submits the friendly source application name");
                Check(save.Descendants().Single(e=>e.Name.LocalName=="NestExtra1").Value==nestName,"SaveNest submits the same clean nest name shown in Hanlon");
                Check(save.Descendants().Where(e=>e.Name.LocalName=="RemnantName").All(e=>e.Value==nestName),"SaveNest submits clean remnant display names");
                Check(decimal.Parse(save.Descendants().Single(e=>e.Name.LocalName=="Remnant").Elements().Single(e=>e.Name.LocalName=="Area").Value,CultureInfo.InvariantCulture)==actualRight.Area,
                    "Actual SaveNest XML carries the reduced rectangular remnant area");
                Check(save.Descendants().Where(e=>e.Name.LocalName=="RemnantID").Select(e=>e.Value)
                    .SequenceEqual(loaded.Result.Remnants.Select(r=>r.RemnantId)),"SaveNest retains the required external remnant identities");
                var inventory=XDocument.Parse(PowerFabNestApi.Serialize(InventoryTracking.BuildRequest(snapshot)));
                Check(inventory.Descendants().Where(e=>e.Name.LocalName=="LinkSoftware").All(e=>e.Value=="Hanlon.PowerFab.Nesting"),"Friendly source metadata does not change the existing inventory-link key");
                Check(save.Descendants().Where(e=>e.Name.LocalName=="NestPart").Sum(e=>int.Parse(e.Elements().Single(x=>x.Name.LocalName=="Quantity").Value))==16,"Actual 10mm SaveNest XML links all 16 job parts and excludes packers");
                foreach(string code in new[]{"SPC3.2","SPH4.5","SUS304","AL5083","OTHER"})
                {
                    doc.Reference.Material=code;doc.Reference.MaterialConfirmed=false;
                    var next=AbeNestImport.CreatePlan(doc,match,template,shapes,100,CancellationToken.None);
                    Check(next.Package.Material.Grade=="250"&&doc.Reference.MaterialConfirmed,"Automatic PowerFab grade for ABE code "+code);
                }
                var nc=xml.Descendants().Where(e=>e.Name.LocalName=="PartCNCData").Select(e=>e.Elements().ToDictionary(x=>x.Name.LocalName,x=>x.Value)).Single(f=>f["PieceMark"]=="10-p-1")["CNCData"];
                var curved=DxfGeometry.Read(Nc1Geometry.Convert(nc,"actual10p1").DxfText,"actual10p1");
                Check(curved.Outer.Count>14&&curved.Holes.Count==2,"Tangential markers do not become contour vertices; holes retained");
                Reject(()=>Nc1Geometry.Convert(nc.Replace("4.50      16.50t","4.50      30.00t"),"bad hint"),"Inconsistent notch tool hint");
                Reject(()=>Nc1Geometry.Convert(nc.Replace("18.36      -6.36","18.36      -0.10"),"bad radius"),"Impossible arc radius");
                Reject(()=>Nc1Geometry.Convert(nc.Replace("-6.36       0.00","-6.36      20.00"),"bevel"),"Bevels are not silently discarded");
                var otherJob=new XDocument(xml);
                foreach(var e in otherJob.Descendants().Where(e=>e.Name.LocalName=="JobNumber"))e.Value="JFF42";
                foreach(var e in otherJob.Descendants().Where(e=>e.Name.LocalName=="Grade"))e.Value="350";
                var otherDoc=AbeNestImport.Parse(doc.NcText,doc.Sy2Text.Replace("NE18-","XYZ18-").Replace("NE20-","ABC20-"));
                otherDoc.Reference.ProgramName=doc.Reference.ProgramName;
                var otherSnapshot=NestingSnapshot.FromDocument(otherJob);
                var otherMatch=AbeNestImport.MatchParts(otherDoc,otherSnapshot,Array.Empty<SavedNestReceipt>());
                Check(otherMatch.Bindings.Count==16&&otherMatch.Bindings.All(b=>b.Part.JobNumber=="JFF42")&&otherMatch.Group.Grade=="350","Other jobs, prefixes and PowerFab grades are not hard-coded");
                var preview=AbeNestImport.PreviewSvg(doc);
                Check(preview.Contains("<svg")&&doc.Placements.Count==28,"Direct NC/SY2 layout preview is independent of PowerFab linking");
                Adjacency(snapshot,root);
                Console.WriteLine($"PASS: {passed} actual Phase10/11 NC1, automatic grade and clearance regression checks.");
                return passed;
            }
            finally{Directory.Delete(root,true);}
        }

        private static void Adjacency(NestingSnapshot snapshot,string root)
        {
            var part=new NestPart{JobNumber="H0306",PieceMark="10-p-2",MainMark="10-B-1"};
            var records=PowerFabDxfSource.Read(snapshot,new[]{part},null,root);
            var shape=DxfGeometry.Read(records.Single().Value.Text,"actual20mmRectangleWithRoundHoles");
            Check(shape.Holes.Count==2&&shape.CurveTolerance==0,"Round holes do not add an exterior clearance allowance");
            PlacedOutline At(double x)=>new PlacedOutline{Part=part,X=x,Y=10,Outer=new PathD(shape.Outer.Select(p=>new PointD(p.x+x,p.y+10))),
                Clearance=new PathsD{new PathD(shape.Outer.Select(p=>new PointD(p.x+x,p.y+10)))},CurveTolerance=shape.CurveTolerance};
            var plan=new CalculatedNest{Package=new NestResultPackage{Material=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=20,Length=500,Width=200,Unit="MM"}},SheetLengthMm=500,SheetWidthMm=200};
            plan.Placed.Add(At(10));plan.Placed.Add(At(190));
            var rules=new LayoutConditions{Spacing=0,Left=0,Right=0,Top=0,Bottom=0};
            DxfNestingEngine.VerifyPlan(plan,rules);Check(true,"Two actual drilled rectangles can share a straight boundary at zero spacing");
            rules.Spacing=1;Reject(()=>DxfNestingEngine.VerifyPlan(plan,rules),"Required positive spacing still enforced");rules.Spacing=0;
            plan.Placed[1]=At(189.9);Reject(()=>DxfNestingEngine.VerifyPlan(plan,rules),"True material overlap still rejected");
        }
    }
}

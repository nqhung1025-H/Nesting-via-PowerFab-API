using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeNcGeometryChecks
    {
        private static int passed;
        private static void Check(bool value,string name){if(!value)throw new Exception("ABE NC geometry: "+name);passed++;}
        private static void Reject(Action action,string name)
        {try{action();}catch(InvalidDataException){passed++;return;}throw new Exception("ABE NC geometry accepted: "+name);}
        private static readonly string Fixtures=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE");

        public static int Run()
        {
            string temporary=Path.Combine(Path.GetTempPath(),"HanlonAbeNcGeometry",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                string folder=Path.Combine(Fixtures,"240175");
                var snapshot=NestingSnapshot.FromDocument(XDocument.Load(Path.Combine(folder,"PowerFab-request.xml")));
                string requestBefore=snapshot.RequestRoot.ToString();
                foreach(var (mm,quantity,packers) in new[]{(5,328,18),(6,43,6),(12,43,26)})
                {
                    string name="240175-02-PHASE90C-"+mm+"MM";
                    var doc=AbeNestImport.Read(Path.Combine(folder,name+".nc"),Path.Combine(folder,name+".sy2"));
                    AbeSheetReportChecks.AttachFixtureRuntime(doc,mm);
                    var match=AbeNestImport.MatchParts(doc,snapshot,Array.Empty<SavedNestReceipt>());
                    var material=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=mm,Length=3000,Width=1500,Dimensions=mm+"X1500",Unit="MM"};
                    NestResultPackage Template()=>PlateSelection.ForMaterial(snapshot,match.Group,material,new LayoutConditions());
                    var plan=AbeNestImport.CreateNcPlan(doc,match,Template(),100,CancellationToken.None);
                    Check(plan.Package.NestName==name&&plan.Package.Remnants.All(r=>r.Name==name),
                        "Actual sheet/remnant names exactly match the imported ABE filename without its extension");
                    Check(plan.Placed.Count==quantity&&plan.Package.Parts.Sum(p=>p.Quantity)==quantity,"Actual "+mm+" mm job quantities");
                    Check(plan.Package.AbeImport!.Packers.Count==packers,"Actual "+mm+" mm packers remain separate");
                    var sources=match.Bindings.OrderBy(b=>b.Placement.Number).Select(b=>b.Placement).ToArray();
                    Check(plan.Placed.Select((p,i)=>p.X==sources[i].X&&p.Y==sources[i].Y&&p.Angle==sources[i].Angle).All(v=>v),"Actual "+mm+" mm original placement and rotation");
                    Check(plan.Placed.All(p=>p.SourceDxf.StartsWith("ABE NC/SY2:",StringComparison.Ordinal)),"Actual "+mm+" mm uses imported cutting geometry");
                    Check(plan.Package.AbeImport.GeometrySource=="ABE NC/SY2 cutting paths","Actual "+mm+" mm geometry provenance");
                    Check(plan.Package.Remnants.Count==1&&plan.Package.Remnants.All(RemnantGeometry.IsRectangle),"Actual "+mm+" mm rectangular remnant");
                    if(mm==5)Check(plan.Placed.All(p=>p.Holes.Count==1&&Math.Abs(ShapeBounds.Of(p.Holes[0]).Width-8)<0.02),"All 328 actual 5 mm parts retain the NC's 8 mm holes; duplicate NC1 features do not duplicate laser cuts");
                    if(mm==12)
                    {
                        var changed=plan.Placed.Where(p=>p.Part.PieceMark=="90_PL-2514").ToArray();
                        Check(changed.Length==2&&changed.All(p=>Math.Abs(Math.Abs(Clipper.Area(p.Outer))-48000)<0.1&&p.Holes.Count==4),"Actual 2514 uses ABE 240 x 200 outlines and all four holes");
                        string oldNc=snapshot.RequestRoot.Descendants().Single(e=>e.Name.LocalName=="PartCNCData"
                            &&NestingSnapshot.Field(e,"PieceMark")=="90_PL-2514").Elements().Single(e=>e.Name.LocalName=="CNCData").Value;
                        Check(Math.Abs(DxfGeometry.Read(Nc1Geometry.Convert(oldNc,"old source").DxfText,"old source").Bounds.Height-210)<0.001,"Conflicting 210 mm PowerFab NC1 is preserved rather than resized");
                    }
                    RoundTrip(plan,doc,snapshot,Path.Combine(temporary,mm+"mm"),quantity);
                    decimal originalLength=material.Length;material.Length=100;
                    Reject(()=>AbeNestImport.CreateNcPlan(doc,match,Template(),100,CancellationToken.None),"Actual "+mm+" mm still rejects an undersized physical sheet");
                    material.Length=originalLength;
                    sources[0].OriginalX+=5;
                    Reject(()=>AbeNestImport.CreateNcPlan(doc,match,Template(),100,CancellationToken.None),"Actual "+mm+" mm still rejects a PowerFab part-size mismatch");
                }
                Check(snapshot.RequestRoot.ToString()==requestBefore,"The original PowerFab NC1 and fabrication records remain unchanged");
                PreviousImports();
                SharedProfileThickness(temporary);
                UnverifiedGeometry();
                Console.WriteLine($"PASS: {passed} ABE cutting-geometry/profile checks, including actual 240175 sheets, shared 4.5 settings, source retention and SaveNest XML.");
                return passed;
            }
            finally{Directory.Delete(temporary,true);}
        }

        private static void RoundTrip(CalculatedNest plan,AbeNestDocument doc,NestingSnapshot snapshot,string folder,int quantity)
        {
            Directory.CreateDirectory(folder);var result=plan.Package;
            result.RequisitionNumber="REQ-REPLAY";result.NestImageFile="preview.png";
            foreach(var remnant in result.Remnants)remnant.ImageFile="preview.png";
            // Image bytes are a test stub; geometry is the actual imported NC.
            File.WriteAllBytes(Path.Combine(folder,"preview.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
            File.WriteAllText(Path.Combine(folder,doc.Reference.NcFile),doc.NcText);
            File.WriteAllText(Path.Combine(folder,doc.Reference.Sy2File),doc.Sy2Text);
            if(result.AbeImport?.ProcessTime is AbeProcessTime runtime)File.Copy(runtime.SourcePath,Path.Combine(folder,runtime.ReportFile));
            result.Placements=plan.Placed.Select(p=>new NestPlacement{JobNumber=p.Part.JobNumber,MainMark=p.Part.MainMark,PieceMark=p.Part.PieceMark,
                Sequence=p.Part.Sequence,LotNumber=p.Part.LotNumber,X=p.X,Y=p.Y,Rotation=p.Angle,SourceDxf=p.SourceDxf,
                Outer=p.Outer.Select(v=>new LayoutPoint{X=v.x,Y=v.y}).ToList(),Holes=p.Holes.Select(h=>h.Select(v=>new LayoutPoint{X=v.x,Y=v.y}).ToList()).ToList()}).ToList();
            string path=Path.Combine(folder,"result.json");File.WriteAllText(path,JsonSerializer.Serialize(result,LoadedNestResult.JsonOptions));
            var loaded=LoadedNestResult.Load(path,snapshot);
            Check(loaded.AbeDocument!=null&&loaded.AbeDocument.NcText==doc.NcText&&loaded.AbeDocument.Marks.Count==doc.Marks.Count,
                "Verified source strokes remain available for sharp saved-nest zoom previews");
            Check(loaded.Result.AbeImport!.GeometrySource=="ABE NC/SY2 cutting paths"&&loaded.Result.Placements.Count==quantity,"Actual source provenance and placements survive reload");
            var save=XDocument.Parse(PowerFabNestApi.Serialize(PowerFabNestApi.BuildSaveRequest(loaded,snapshot)));
            if(result.AbeImport?.ProcessTime is AbeProcessTime expectedRuntime)
            {
                Check(loaded.Result.AbeImport!.ProcessTime!.Seconds==expectedRuntime.Seconds,"Report time and source provenance survive saved-result reload");
                Check(save.Descendants().Single(e=>e.Name.LocalName=="NestExtra2").Value=="Process time: "+expectedRuntime.Display,"SaveNest XML includes the exact report runtime");
                string report=Path.Combine(folder,expectedRuntime.ReportFile);byte[] original=File.ReadAllBytes(report);
                File.AppendAllText(report,"changed");Reject(()=>LoadedNestResult.Load(path,snapshot),"Changed archived PDF must be re-imported");File.WriteAllBytes(report,original);
            }
            Check(save.Descendants().Single(e=>e.Name.LocalName=="NestExtra1").Value==result.NestName
                &&save.Descendants().Where(e=>e.Name.LocalName=="RemnantName").All(e=>e.Value==result.NestName),
                "SaveNest transmits the same sheet name for the nest and remnant");
            Check(save.Descendants().Where(e=>e.Name.LocalName=="NestPart").Sum(e=>int.Parse(e.Elements().Single(c=>c.Name.LocalName=="Quantity").Value))==quantity,"Actual SaveNest XML retains every job part and excludes packers");
            loaded.Result.NestName="Old generated Phase 90C name";
            foreach(var remnant in loaded.Result.Remnants)remnant.Name="Old remnant name";
            var resumed=XDocument.Parse(PowerFabNestApi.Serialize(PowerFabNestApi.BuildSaveRequest(loaded,snapshot)));
            Check(resumed.Descendants().Single(e=>e.Name.LocalName=="NestExtra1").Value==doc.Reference.ProgramName
                &&resumed.Descendants().Where(e=>e.Name.LocalName=="RemnantName").All(e=>e.Value==doc.Reference.ProgramName),
                "SaveNest uses the exact ABE source name even when an unsent draft contains older generated names");
        }

        private static void PreviousImports()
        {
            foreach(string subfolder in new[]{"H0373","Phase10and11","Received"})
            {
                var batch=AbeImportBatch.ReadFolder(Path.Combine(Fixtures,subfolder),CancellationToken.None,null);
                foreach(var item in batch.Items.Where(i=>i.Document!=null))
                {
                    var doc=item.Document!;
                    foreach(var part in doc.Placements.Where(p=>!AbePartNames.IsPacker(p.Name)))AbeNcGeometry.Read(doc,part,CancellationToken.None);
                    Check(true,"Earlier actual NC contours: "+item.Name);
                }
            }
            string folder=Path.Combine(Fixtures,"Phase10and11");
            var snapshot=NestingSnapshot.FromDocument(XDocument.Load(Path.Combine(folder,"PowerFab-request.xml")));
            var original=AbeNestImport.Read(Path.Combine(folder,"H0306-PHASE10+11-10MM.nc"),Path.Combine(folder,"H0306-PHASE10+11-10MM.sy2"));
            var match=AbeNestImport.MatchParts(original,snapshot,Array.Empty<SavedNestReceipt>());
            var material=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=10,Length=3000,Width=1500,Dimensions="10X1500",Unit="MM"};
            var plan=AbeNestImport.CreateNcPlan(original,match,PlateSelection.ForMaterial(snapshot,match.Group,material,new LayoutConditions()),100,CancellationToken.None);
            Check(plan.Placed.Count==16&&plan.Placed.All(p=>p.Holes.Count==2),"Earlier Phase 10/11 curved and chamfered outlines retain both holes");
        }

        private static void SharedProfileThickness(string temporary)
        {
            NestingSnapshot Snapshot(AbeNestDocument doc,params int[] nominals)
            {
                var parts=new List<XElement>();
                foreach(int nominal in nominals)
                    foreach(var g in doc.Placements.Where(p=>!AbePartNames.IsPacker(p.Name)).GroupBy(p=>p.Name))
                    {
                        var p=g.First();
                        parts.Add(new XElement("WorkOrderPart",new XElement("JobNumber","PROFILE-JOB"),new XElement("MainMark",nominal+"-"+p.Name),
                            new XElement("PieceMark",p.Name),new XElement("Quantity",g.Count()),new XElement("Shape","PLT"),new XElement("Grade","250"),
                            new XElement("Dimensions",nominal+"X"+p.OriginalY),new XElement("Length",new XAttribute("UOM","MM"),p.OriginalX),
                            new XElement("Width",new XAttribute("UOM","MM"),p.OriginalY),new XElement("PlateThickness",new XAttribute("UOM","MM"),nominal)));
                    }
                return NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",new XElement("WorkOrderID","PROFILE-CHECK"),new XElement("WorkOrderParts",parts),new XElement("AvailableInventory"))));
            }
            AbeNestDocument Read(int nominal)
            {
                string stem=Path.Combine(Fixtures,"H0373","H0373-PHASE1-"+nominal+"MM");
                return AbeNestImport.Read(stem,stem+".SY2");
            }
            foreach(int nominal in new[]{4,5})foreach(bool filename in new[]{true,false})
            {
                var doc=Read(nominal);
                if(!filename)doc.Reference.ProgramName="PROFILE-NEST";
                var snapshot=Snapshot(doc,nominal);
                var match=AbeNestImport.MatchParts(doc,snapshot,Array.Empty<SavedNestReceipt>());
                Check(doc.Thickness==4.5m&&doc.PowerFabThickness==nominal&&match.Group.ThicknessMm==nominal,"Actual SPH4.5 maps to PowerFab "+nominal+" using "+(filename?"filename":"matched parts"));
                var sheet=new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=nominal,Length=3000,Width=1500,Dimensions=nominal+"X1500",Unit="MM"};
                var plan=AbeNestImport.CreateNcPlan(doc,match,PlateSelection.ForMaterial(snapshot,match.Group,sheet,new LayoutConditions()),100,CancellationToken.None);
                Check(plan.Package.Material.Thickness==nominal&&plan.Package.Material.Grade=="250"&&plan.Package.AbeImport!.PowerFabThicknessMm==nominal,"PowerFab nominal thickness/grade are retained in the completed plan");
                RoundTrip(plan,doc,snapshot,Path.Combine(temporary,"profile-"+nominal+"-"+filename),doc.ProductionCount);
            }
            var original=Read(4);
            var renamed=AbeNestImport.Parse(original.NcText,original.Sy2Text.Replace("HMTT=SPH,","HMTT=OTHER,"));
            Check(renamed.Thickness==4.5m&&renamed.Placements.Count==original.Placements.Count,"Different NC/SY2 profile letters do not reject the same numeric 4.5 setting");
            var mixed=AbeNestImport.Parse(original.NcText,new System.Text.RegularExpressions.Regex("HMTT=SPH,").Replace(original.Sy2Text,"HMTT=OTHER,",1));
            Check(mixed.Placements.Count==original.Placements.Count,"Profile-letter differences within SY2 do not change thickness verification");
            Reject(()=>AbeNestImport.Parse(original.NcText.Replace("SPH4.5E","SPC6.0E"),original.Sy2Text),"A true NC/SY2 numeric-setting mismatch is still rejected");
            var ambiguous=Read(5);ambiguous.Reference.ProgramName="PROFILE-NEST";
            Reject(()=>AbeNestImport.MatchParts(ambiguous,Snapshot(ambiguous,4,5),Array.Empty<SavedNestReceipt>()),"Both 4 and 5 mm checked for the same part without a filename are not guessed");
            ambiguous.Reference.ProgramName="PROFILE-4MM";
            Check(AbeNestImport.MatchParts(ambiguous,Snapshot(ambiguous,4,5),Array.Empty<SavedNestReceipt>()).Group.ThicknessMm==4,"An explicit 4MM filename resolves the ambiguous shared profile");
        }

        private static void UnverifiedGeometry()
        {
            var p=new AbePlacement{Number=1,Name="CONTROL",X=10,Y=10,SizeX=20,SizeY=10,OriginalX=20,OriginalY=10};
            PathD Path(params double[] xy)=>new PathD(Enumerable.Range(0,xy.Length/2).Select(i=>new PointD(xy[2*i],xy[2*i+1])));
            var doc=new AbeNestDocument();
            doc.Cuts.Add(Path(10,10,30,10,30,20,10,20,10,10));
            var shape=AbeNcGeometry.Read(doc,p,CancellationToken.None);
            Check(Math.Abs(shape.Area-200)<0.001,"Closed actual NC rectangle");
            doc.Cuts[0]=Path(12,10,30,10,30,20,10,20,10,10);
            Check(Math.Abs(AbeNcGeometry.Read(doc,p,CancellationToken.None).Area-200)<0.001,"Explicit 2 mm holding micro-joint closes without changing the NC");
            doc.Cuts[0]=Path(12.5,10,30,10,30,20,10,20,10,10);
            Reject(()=>AbeNcGeometry.Read(doc,p,CancellationToken.None),"A larger unexplained gap is not guessed closed");
            doc.Cuts[0]=Path(10,10,30,10,30,20,10,20);
            Reject(()=>AbeNcGeometry.Read(doc,p,CancellationToken.None),"A missing side is not invented from the SY2 rectangle");
            doc.Marks.Add(Path(10,10,30,10,30,20,10,20,10,10));doc.Cuts.Clear();doc.Cuts.Add(Path(15,12,17,12,17,14,15,14,15,12));
            Reject(()=>AbeNcGeometry.Read(doc,p,CancellationToken.None),"Marking is not treated as an external cut contour");
            doc.Cuts.Clear();doc.Cuts.Add(Path(10,10,30,10,30,15,25,15,25,20,10,20,10,10));
            Check(Math.Abs(AbeNcGeometry.Read(doc,p,CancellationToken.None).Area-175)<0.001,"A concave NC outline is retained rather than replaced by its box");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{AbeNcGeometry.Read(doc,p,cancel.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){passed++;}
        }
    }
}

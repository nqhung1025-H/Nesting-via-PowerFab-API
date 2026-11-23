using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeNamesAndPackersChecks
    {
        private static int passed;
        private static void Check(bool value,string name){if(!value)throw new Exception("ABE names/packers: "+name);passed++;}
        private static void Reject(Action act,string name){try{act();}catch(InvalidDataException){passed++;return;}throw new Exception("ABE names/packers accepted: "+name);}
        private static XElement Part(string name,string lot,int quantity,decimal thick,decimal length,decimal width,string main="A")=>new XElement("WorkOrderPart",
            new XElement("JobNumber","H0306"),new XElement("MainMark",main),new XElement("PieceMark",name),new XElement("LotNumber",lot),new XElement("Quantity",quantity),
            new XElement("Shape","PLT"),new XElement("Grade","250"),new XElement("Dimensions",thick+"X"+width),
            new XElement("Length",new XAttribute("UOM","MM"),length),new XElement("Width",new XAttribute("UOM","MM"),width),
            new XElement("PlateThickness",new XAttribute("UOM","MM"),thick));
        private static NestingSnapshot Snapshot(IEnumerable<XElement> parts)=>NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",
            new XElement("WorkOrderID","NAMING-RUN"),new XElement("WorkOrderParts",parts),new XElement("AvailableInventory"))));
        private static NestingSnapshot SnapshotFor(AbeNestDocument document)
        {
            var parts=document.Placements.Where(p=>!AbePartNames.IsPacker(p.Name)).GroupBy(p=>p.Name).Select(g=>
            {
                var m=Regex.Match(g.Key,@"^NE(\d+)-(.*)$");var p=g.First();
                return Part(m.Groups[2].Value.ToLowerInvariant(),"Lot "+m.Groups[1].Value+" - Shafts(F)",g.Count(),document.PowerFabThickness,(decimal)p.OriginalX,(decimal)p.OriginalY);
            });return Snapshot(parts);
        }
        public static int Run()
        {
            foreach(string name in new[]{"NE14-08-P-5","ne14-08-p-5","NE1408P5","ne1408p5","NE+14+08-P-5"})
                Check(AbePartNames.Matches(name,"H0306","08-p-5","Lot 14 - Shafts(F)"),"Declared prefix/lot/piece form: "+name);
            Check(AbePartNames.Matches("ZZ01408P5","H0306","08-P-5","14"),"Other alphabetic prefixes and numeric lot representation");
            Check(!AbePartNames.Matches("NE1508P5","H0306","08-p-5","Lot 14 - Shafts(F)"),"Wrong lot is not ignored");
            Check(!AbePartNames.Matches("NE1408P5","H0306","08-p-4","Lot 14 - Shafts(F)"),"Wrong piece is not ignored");
            Check(!AbePartNames.Matches("NE1408P5","H0306","08-p-5",""),"Prefixed lot needs actual PowerFab lot metadata");
            Check(AbePartNames.Matches("NFF1P4","H0413","NFF1-p-4",""),"Existing bare names remain supported");
            Check(AbePartNames.IsPacker("100X100-PACKER")&&AbePartNames.IsPacker("50x50_packer"),"Explicit rectangular packers recognized");
            Check(!AbePartNames.IsPacker("MISSING-PART")&&!AbePartNames.IsPacker("PL-PACKER-42"),"Unmatched production names do not silently become packers");
            Check(AbePartNames.PowerFabThickness("SS400",19)==20,"Confirmed SS400 19-to-20 setting mapping");
            Check(AbePartNames.PowerFabThickness("SUS304",19)==20,"Nominal thickness mapping ignores machine-profile letters; PowerFab supplies grade");
            Check(AbePartNames.PowerFabThickness("SS400",19,"H0306-PHASE8+9-20MM")==20,"20MM filename is PowerFab target");
            Check(AbePartNames.PowerFabThickness("SS400",10,"H0306-12mm")==12,"Filename declaration takes precedence over the internal setting");
            Reject(()=>AbePartNames.PowerFabThickness("SS400",10,"JOB-10MM-12MM"),"Conflicting filename thicknesses");
            foreach(var (nominal,code) in new[]{(1m,1.2m),(2m,2m),(3m,3.2m),(4m,4.5m),(5m,4.5m),(6m,6m),(8m,8m),(10m,9m),(12m,12m),(16m,16m),(20m,19m),(25m,25m)})
                Check(AbePartNames.AcceptsPowerFabThickness(nominal,code),"Configured nominal/profile thickness "+nominal+" / "+code);
            Check(AbePartNames.MachineThickness("SPH 4.5E-L2")==4.5m&&AbePartNames.MachineThickness("SPC4.5E-L2")==4.5m
                &&AbePartNames.MachineThickness("SS400-4.5E-L2")==4.5m,"Profile letters and spacing do not change the numeric 4.5 setting");
            Check(AbePartNames.AcceptsPowerFabThickness(4,4.5m,"JOB-4MM")&&!AbePartNames.AcceptsPowerFabThickness(5,4.5m,"JOB-4MM"),"A 4MM filename distinguishes the shared 4.5 profile");
            Check(AbePartNames.AcceptsPowerFabThickness(5,4.5m,"JOB-5MM")&&!AbePartNames.AcceptsPowerFabThickness(4,4.5m,"JOB-5MM"),"A 5MM filename distinguishes the shared 4.5 profile");
            Check(!AbePartNames.AcceptsPowerFabThickness(6,4.5m),"Unconfigured 4.5-to-6 conversion remains invalid");
            foreach(string stem in new[]{"H0306-10MM","H0306-20MM"})
            {
                string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","Received",stem+".nc");
                var doc=AbeNestImport.Read(path,Path.ChangeExtension(path,".sy2"));
                int expected=stem.Contains("10MM")?16:20;
                Check(doc.ProductionCount==expected&&doc.PackerCount==12,"Actual archived counts for "+stem);
                var match=AbeNestImport.MatchParts(doc,SnapshotFor(doc),Array.Empty<SavedNestReceipt>());
                Check(match.Bindings.Count==expected&&match.Group.Quantity==expected,"Actual prefixed archive names bind all job copies for "+stem);
                Check(match.Bindings.All(b=>!b.Part.PieceMark.Contains("PACKER")),"Packers consume no PowerFab job quantity for "+stem);
                Check(match.Bindings.All(b=>AbePartNames.Matches(b.Placement.Name,b.Part.JobNumber,b.Part.PieceMark,b.Part.LotNumber)),"Original PowerFab lot/piece identities retained for "+stem);
                var packers=AbeNestImport.ReadPackers(doc,CancellationToken.None);
                Check(packers.Count==12&&doc.Reference.Packers.Count==12,"All 12 actual packers register against NC rectangle cuts for "+stem);
                if(stem.Contains("20MM"))Check(doc.Thickness==19&&match.Group.ThicknessMm==20,"Actual 19 mm ABE declaration targets PowerFab 20 mm filename");
            }
            LinkedPackerChecks();
            Console.WriteLine($"PASS: {passed} naming, lot, packer and filename-thickness checks, including the supplied ImportedABE pairs.");return passed;
        }

        private static void LinkedPackerChecks()
        {
            string root=Path.Combine(Path.GetTempPath(),"HanlonPackerChecks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                string nc="M102(SS400-19.0E-L2)\nG92X3070Y1550\nG93X0Y0\nM100\nG00X20Y30\nM103\nG01X40\nG01Y40\nG01X20\nG01Y30\nM104\nG00X100Y30\nM103\nG01X110\nG01Y40\nG01X100\nG01Y30\nM104\nM101\nG10\n";
                string Section(int id,string name,int x,int sx)=>$"[PD_{id:0000}]\nHNO={id}\nHNAME={name}\nHKIND=1\nHMTT=SS400,19.0\nP98DT=0,1,1,0,0\nPBX={x}\nPBY=30\nPBAX={x}\nPBAY=30\nPANG=0\nHSX={sx}\nHSY=10\nHSZX={sx}\nHSZY=10\n";
                string sy2="[HEADER_INFO]\nVER=1.00\nSTYPE=2\nPDNUM=2\nMACHINENAME=TEST\n"+Section(1,"NE1408P5",20,20)+Section(2,"10X10-PACKER",100,10);
                string ncPath=Path.Combine(root,"fixture-20MM.nc"),sy2Path=Path.ChangeExtension(ncPath,".sy2");File.WriteAllText(ncPath,nc);File.WriteAllText(sy2Path,sy2);
                var doc=AbeNestImport.Read(ncPath,sy2Path);doc.Reference.MaterialConfirmed=true;
                var snapshot=Snapshot(new[]{Part("08-p-5","Lot 14 - Shafts(F)",1,20,20,10),Part("08-p-5","Lot 15 - Shafts(F)",1,20,20,10,"OTHER")});
                var match=AbeNestImport.MatchParts(doc,snapshot,Array.Empty<SavedNestReceipt>());
                Check(match.Bindings.Single().Part.LotNumber.StartsWith("Lot 14"),"Identical part in lot 15 is excluded");
                var template=PlateSelection.ForMaterial(snapshot,match.Group,new NestMaterial{Source="Purchase",Shape="PLT",Grade="250",Thickness=20,Length=200,Width=100,Dimensions="20X100",Unit="MM"},new LayoutConditions());template.RequisitionNumber="REQ1";
                var shapes=match.Bindings.ToDictionary(b=>LoadedNestResult.PartKey(b.Part),b=>new DxfShape{Source="fixture.dxf",Outer=new PathD{new PointD(0,0),new PointD(20,0),new PointD(20,10),new PointD(0,10)}});
                var plan=AbeNestImport.CreatePlan(doc,match,template,shapes,100,CancellationToken.None);
                Check(plan.Package.Parts.Sum(p=>p.Quantity)==1&&plan.Placed.Count==1&&doc.Reference.Packers.Count==1,"Packer kept as provenance/geometry, not a job placement");
                Check(plan.Package.Remnants.Single().X==110,"Packer space is excluded from future remnant");
                plan.Package.Placements=plan.Placed.Select(p=>new NestPlacement{JobNumber=p.Part.JobNumber,MainMark=p.Part.MainMark,PieceMark=p.Part.PieceMark,LotNumber=p.Part.LotNumber,Sequence=p.Part.Sequence,X=p.X,Y=p.Y,Rotation=p.Angle}).ToList();
                plan.Package.NestImageFile="image.png";foreach(var remnant in plan.Package.Remnants)remnant.ImageFile="image.png";
                File.WriteAllBytes(Path.Combine(root,"image.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
                File.Copy(ncPath,Path.Combine(root,doc.Reference.NcFile));File.Copy(sy2Path,Path.Combine(root,doc.Reference.Sy2File));
                string resultPath=Path.Combine(root,"result.json");void Write()=>File.WriteAllText(resultPath,JsonSerializer.Serialize(plan.Package,LoadedNestResult.JsonOptions));Write();
                var loaded=LoadedNestResult.Load(resultPath,snapshot);
                var wire=XDocument.Parse(PowerFabNestApi.Serialize(PowerFabNestApi.BuildSaveRequest(loaded,snapshot)));
                var wireParts=wire.Descendants().Where(e=>e.Name.LocalName=="NestPart").ToList();
                Check(wireParts.Count==1&&!wireParts.Single().Value.Contains("PACKER"),"Actual SaveNest XML contains only the PowerFab job plate");
                Check(loaded.Result.Material.Thickness==20&&loaded.Result.AbeImport!.PowerFabThicknessMm==20,"Confirmed 19-to-filename20 mapping survives reload");
                plan.Package.AbeImport!.Packers[0].X++;Write();Reject(()=>LoadedNestResult.Load(resultPath,snapshot),"Edited packer geometry on reload");plan.Package.AbeImport.Packers[0].X--;
                plan.Package.Placements[0].LotNumber="Lot 15 - Shafts(F)";Write();Reject(()=>LoadedNestResult.Load(resultPath,snapshot),"Wrong lot on reload");plan.Package.Placements[0].LotNumber="Lot 14 - Shafts(F)";
                var ambiguous=Snapshot(new[]{Part("08-p-5","Lot 14 - A",1,20,20,10),Part("08-p-5","Lot 14 - B",1,20,20,10,"B")});
                Reject(()=>AbeNestImport.MatchParts(doc,ambiguous,Array.Empty<SavedNestReceipt>()),"Ambiguous lot labels sharing the same number");
                var wrong=AbeNestImport.Parse(nc,sy2.Replace("NE1408P5","UNMATCHED"));Reject(()=>AbeNestImport.MatchParts(wrong,snapshot,Array.Empty<SavedNestReceipt>()),"Unmatched production item stays blocked");
                var onlyPackers=AbeNestImport.Parse(nc,sy2.Replace("NE1408P5","20X10-PACKER"));Reject(()=>AbeNestImport.MatchParts(onlyPackers,snapshot,Array.Empty<SavedNestReceipt>()),"Packer-only sheet does not create an empty job nest");
                var badSize=AbeNestImport.Parse(nc,sy2.Replace("10X10-PACKER","50X50-PACKER"));Reject(()=>AbeNestImport.ReadPackers(badSize,CancellationToken.None),"Incorrect packer size is rejected");
            }
            finally{Directory.Delete(root,true);}
        }
    }
}

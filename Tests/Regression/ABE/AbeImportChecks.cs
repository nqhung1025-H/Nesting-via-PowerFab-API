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
    internal static class AbeImportChecks
    {
        private static int passed;
        private static readonly CultureInfo C=CultureInfo.InvariantCulture;
        private static void Check(bool value,string name){if(!value)throw new Exception("ABE: "+name);passed++;}
        private static void Reject(Action action,string name)
        {try{action();}catch(InvalidDataException){passed++;return;}throw new Exception("ABE accepted: "+name);}
        private static string Section(int i,double x,double y,double angle=0)
        {
            double a=angle*Math.PI/180,c=Math.Round(Math.Cos(a),8),s=Math.Round(Math.Sin(a),8);
            var corners=new[]{new PointD(0,0),new PointD(20,0),new PointD(20,10),new PointD(0,10)}.Select(p=>new PointD(p.x*c-p.y*s,p.x*s+p.y*c)).ToArray();
            var b=ShapeBounds.Of(corners);double bx=x-b.Left,by=y-b.Bottom;
            return $"[PD_{i:0000}]\nHNO={i}\nHNAME=P1\nHKIND=1\nHMTT=SS400,10.0\nP98DT=0,1,1,0,0\nPBX={x.ToString(C)}\nPBY={y.ToString(C)}\nPBAX={bx.ToString(C)}\nPBAY={by.ToString(C)}\nPANG={angle.ToString(C)}\nHSX=20\nHSY=10\nHSZX={b.Width.ToString(C)}\nHSZY={b.Height.ToString(C)}\n";
        }
        private static (string Nc,string Sy2) Fixture(int count=1,double angle=0,bool overlap=false)
        {
            string sy2=$"[HEADER_INFO]\nVER=1.00\nSTYPE=2\nPDNUM={count}\nMACHINENAME=GENERIC-TEST\n";
            for(int i=0;i<count;i++)sy2+=Section(i+1,20+(overlap?0:50*i),30,angle);
            string nc="M102(SS400-10.0E-L2)\nG92X3070.Y1550.\nG93X0.Y0.\nM100\n";
            for(int i=0;i<count;i++)
            {
                double a=angle*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a);
                var points=new[]{new PointD(0,0),new PointD(20,0),new PointD(20,10),new PointD(0,10),new PointD(0,0)}.Select(q=>new PointD(q.x*c-q.y*s,q.x*s+q.y*c)).ToList();
                var b=ShapeBounds.Of(points);points=new List<PointD>(points.Select(q=>new PointD(q.x+20+(overlap?0:50*i)-b.Left,q.y+30-b.Bottom)));
                nc+=$"G00X{points[0].x.ToString(C)}Y{points[0].y.ToString(C)}\nM103\n";
                nc+=string.Join("\n",points.Skip(1).Select(q=>$"G01X{q.x.ToString(C)}Y{q.y.ToString(C)}"))+"\nM104\n";
            }
            return(nc+"M101\nG10\n",sy2);
        }
        private static NestingSnapshot Snapshot(int qty=1,bool multiple=false)
        {
            XElement Part(string main,int n)=>new XElement("WorkOrderPart",new XElement("JobNumber","ANOTHER-JOB"),new XElement("MainMark",main),
                new XElement("PieceMark","P-1"),new XElement("Quantity",n),new XElement("Shape","PLT"),new XElement("Grade","250"),new XElement("Dimensions","10X10"),
                new XElement("Length",new XAttribute("UOM","MM"),20),new XElement("Width",new XAttribute("UOM","MM"),10),new XElement("PlateThickness",new XAttribute("UOM","MM"),10));
            var parts=new XElement("WorkOrderParts",Part("A",qty));if(multiple)parts.Add(Part("B",qty));
            return NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",new XElement("WorkOrderID","OTHER-RUN"),parts,new XElement("AvailableInventory"))));
        }
        private static NestResultPackage Template()=>new NestResultPackage{WorkOrderId="OTHER-RUN",RequisitionNumber="REQ1",Material=new NestMaterial
            {Source="Purchase",Shape="PLT",Grade="250",Dimensions="10X100",Length=200,Width=100,Thickness=10,Unit="MM"}};
        private static Dictionary<string,DxfShape> Shapes(AbePartMatch match)=>match.Bindings.Select(b=>b.Part).Distinct().ToDictionary(LoadedNestResult.PartKey,p=>new DxfShape
            {Source="PowerFab test DXF",Outer=new PathD{new PointD(0,0),new PointD(20,0),new PointD(20,10),new PointD(0,10)}});
        public static int Run()
        {
            string example=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","H0413-NFF-PHASE1TO3-10MM-01");
            var original=AbeNestImport.Read(example,example+".SY2");
            Check(original.Placements.Count==170,"User example has 170 individual SY2 placements, not 89 NC labels");
            Check(original.Placements.Select(p=>p.Name).Distinct().Count()==18,"User example has 18 part names");
            Check(original.Thickness==10&&original.Reference.Material=="SS400","User example material is read from the files");
            Check(original.Reference.Machine=="ENSIS3015AJ","Machine metadata retained");
            var second=original.Placements[1];
            Check(second.Angle==180&&second.X==15&&second.Y==1305&&second.BaseX==105.5&&second.BaseY==1395,"Independent second-part coordinates and rotation");
            var last=original.Placements.Last();
            Check(last.Angle==270&&last.X==2833&&last.Y==88&&last.SizeX==90&&last.SizeY==102,"Last rotated instance dimensions and position");
            Check(original.Marks.Count>original.Cuts.Count&&original.Cuts.Count>170,"Marking and merged/common-cut paths are separated");
            Check(Math.Abs(original.Cuts[0][0].x-110.5)<1e-6&&Math.Abs(original.Cuts[0][0].y-1395)<1e-6,"G93 offset applied to NC; G92 is not a sheet dimension");
            Check(AbeNestImport.PreviewSvg(original).Contains("Physical sheet size unconfirmed"),"Unselected sheet dimensions are not invented in preview");
            Reject(()=>AbeNestImport.Read(example,example+"different.SY2"),"Mismatched program filenames");
            Reject(()=>AbeNestImport.Parse(original.NcText,original.Sy2Text.Replace("PDNUM=170","PDNUM=171")),"Truncated or missing SY2 records");
            Reject(()=>AbeNestImport.Parse(original.NcText,original.Sy2Text.Replace("P98DT=0,1,1","P98DT=1,1,1")),"Unimplemented mirrored transform");
            Reject(()=>AbeNestImport.Parse(original.NcText.Replace("G01","G02"),original.Sy2Text),"Arc without a centre or radius is rejected");
            Reject(()=>AbeNestImport.Parse(original.NcText.Replace("M101","M98P123\nM101"),original.Sy2Text),"Subprograms are not silently omitted");
            Reject(()=>AbeNestImport.Parse(original.NcText.Replace("M101",""),original.Sy2Text),"Incomplete NC program");
            Reject(()=>AbeNestImport.Parse(original.NcText.Replace("SS400-10.0","SS400-12.0"),original.Sy2Text),"Mismatched pair thickness");
            Reject(()=>AbeNestImport.MatchParts(original,Snapshot(),Array.Empty<SavedNestReceipt>()),"Other-job example cannot allocate to unrelated part names");
            foreach(double angle in new double[]{0,90,180,270})
            {
                var f=Fixture(angle:angle);var doc=AbeNestImport.Parse(f.Nc,f.Sy2);doc.Reference.MaterialConfirmed=true;
                var snapshot=Snapshot();var match=AbeNestImport.MatchParts(doc,snapshot,Array.Empty<SavedNestReceipt>());
                var plan=AbeNestImport.CreatePlan(doc,match,Template(),Shapes(match),100,CancellationToken.None);
                Check(plan.Placed.Single().Angle==angle&&Math.Abs(plan.Placed[0].Bounds.Left-20)<0.001&&Math.Abs(plan.Placed[0].Bounds.Bottom-30)<0.001,"Rotation "+angle+" preserves imported placement");
                Check(plan.Package.Parts.Single().JobNumber=="ANOTHER-JOB"&&plan.Package.Parts.Single().PieceMark=="P-1","No example job or mark is hardcoded");
                Check(plan.Package.AbeImport!=null&&plan.Package.Status=="completed","Verified imported plan uses the normal save result contract");
                Check(plan.Package.Remnants.All(r=>r.Y==0&&r.Edge!="Bottom"),"100 mm bottom remnant rule retained");
            }
            var two=Fixture(2);var twoDoc=AbeNestImport.Parse(two.Nc,two.Sy2);twoDoc.Reference.MaterialConfirmed=true;
            var phases=AbeNestImport.MatchParts(twoDoc,Snapshot(1,true),Array.Empty<SavedNestReceipt>());
            Check(phases.Bindings.Select(b=>b.Part.MainMark).SequenceEqual(new[]{"A","B"}),"Exact selected assembly quantities retain both identities");
            Reject(()=>AbeNestImport.MatchParts(twoDoc,Snapshot(2,true),Array.Empty<SavedNestReceipt>()),"Partial ambiguous assembly quantities");
            Reject(()=>AbeNestImport.MatchParts(twoDoc,Snapshot(1),Array.Empty<SavedNestReceipt>()),"Excess quantity");
            var small=Template();small.Material.Length=30;
            Reject(()=>AbeNestImport.CreatePlan(twoDoc,phases,small,Shapes(phases),100,CancellationToken.None),"Physical sheet too small");
            var collision=Fixture(2,overlap:true);var collisionDoc=AbeNestImport.Parse(collision.Nc,collision.Sy2);collisionDoc.Reference.MaterialConfirmed=true;
            var collisionMatch=AbeNestImport.MatchParts(collisionDoc,Snapshot(2),Array.Empty<SavedNestReceipt>());
            Reject(()=>AbeNestImport.CreatePlan(collisionDoc,collisionMatch,Template(),Shapes(collisionMatch),100,CancellationToken.None),"Overlapping imported placements");
            var wrong=Shapes(phases);foreach(var shape in wrong.Values)shape.Holes.Add(new PathD{new PointD(2,2),new PointD(8,2),new PointD(8,8),new PointD(2,8)});
            Reject(()=>AbeNestImport.CreatePlan(twoDoc,phases,Template(),wrong,100,CancellationToken.None),"DXF hole absent from the cutting program");
            var changed=Fixture();var changedDoc=AbeNestImport.Parse(changed.Nc,changed.Sy2.Replace("HNAME=P1","HNAME=H0413P1"));
            Reject(()=>AbeNestImport.MatchParts(changedDoc,Snapshot(),Array.Empty<SavedNestReceipt>()),"Different job prefix cannot be discarded to force a match");
            var partial=Fixture();var partialDoc=AbeNestImport.Parse(partial.Nc,partial.Sy2);
            var partialMatch=AbeNestImport.MatchParts(partialDoc,Snapshot(5),Array.Empty<SavedNestReceipt>());
            Check(partialMatch.Group.Quantity==1,"Import material dialog counts only the copies on this sheet");
            var autoGrade=AbeNestImport.CreatePlan(partialDoc,partialMatch,Template(),Shapes(partialMatch),100,CancellationToken.None);
            Check(autoGrade.Package.Material.Grade=="250"&&partialDoc.Reference.MaterialConfirmed,"ABE code automatically uses the matched PowerFab grade");
            Check(AbeNestImport.ProgramStem("program.v2")=="program.v2"&&AbeNestImport.ProgramStem("program.v2.nc")=="program.v2","Extensionless programs with dots retain their complete names");
            Reject(()=>AbeNestImport.Parse(partial.Nc.Replace("G92X3070.Y1550.","G92X3070.Y1550.M98P100"),partial.Sy2),"Unsupported command hidden in a coordinate reset");
            var skeletonDoc=AbeNestImport.Parse(partial.Nc.Replace("M101","G00X150Y0\nM103\nG01Y100\nM104\nM101"),partial.Sy2);
            skeletonDoc.Reference.MaterialConfirmed=true;var skeletonMatch=AbeNestImport.MatchParts(skeletonDoc,Snapshot(),Array.Empty<SavedNestReceipt>());
            var skeletonPlan=AbeNestImport.CreatePlan(skeletonDoc,skeletonMatch,Template(),Shapes(skeletonMatch),100,CancellationToken.None);
            Check(skeletonPlan.Package.Remnants.Single().X==150,"Remnant excludes skeleton cuts beyond part bounds");
            LoadChecks();
            ArcChecks();
            Console.WriteLine($"PASS: {passed} ABE import checks, including the 170-part pair and H0306 circular-cut NC file.");return passed;
        }

        private static void ArcChecks()
        {
            string example=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","H0306-PHASE8+9-10MM");
            var doc=AbeNestImport.ReadNcOnly(example);
            Check(File.ReadAllLines(example)[119]=="G03I0.192J10.998","Actual reported failure is at line 120");
            Check(doc.ArcCount==48&&doc.FullCircleCount==32,"All 48 H0306 arcs, including 32 full circular holes, are read");
            Check(doc.Placements.Count==0&&doc.Reference.Material=="SS400"&&doc.Thickness==10,"NC-only diagnostics do not invent SY2 part identities");
            var circle=new PathD(doc.Cuts[0].Skip(1));double radius=Math.Sqrt(0.192*0.192+10.998*10.998);
            var bounds=ShapeBounds.Of(circle);
            Check(Math.Abs(bounds.Left-(117-radius))<1e-7&&Math.Abs(bounds.Right-(117+radius))<1e-7
                &&Math.Abs(bounds.Bottom-(1445-radius))<1e-7&&Math.Abs(bounds.Top-(1445+radius))<1e-7,"First H0306 hole has the expected centre, radius and G93 translation");
            Check(Math.Abs(Clipper.Area(circle)-Math.PI*radius*radius)<0.3,"Full circle stays curved with bounded approximation error and CCW direction");
            Check(Math.Abs(circle[0].x-circle[^1].x)<1e-9&&Math.Abs(circle[0].y-circle[^1].y)<1e-9,"G03 without X/Y returns to its exact start point");
            foreach(bool clockwise in new[]{false,true})
            {
                var start=clockwise?new PointD(0,10):new PointD(10,0);var end=clockwise?new PointD(10,0):new PointD(0,10);
                var curve=AbeNestImport.ArcPoints(start,end,clockwise,null,null,10);
                var mid=curve.OrderBy(p=>Math.Abs(p.x-p.y)).First();
                Check(Math.Abs(mid.x-Math.Sqrt(50))<0.4&&Math.Abs(mid.y-Math.Sqrt(50))<0.4,"Positive R selects the minor "+(clockwise?"clockwise":"counterclockwise")+" arc");
                var major=AbeNestImport.ArcPoints(start,end,clockwise,null,null,-10);
                Check(major.Any(p=>p.x>19.9)&&major.Any(p=>p.y>19.9),"Negative R selects the major "+(clockwise?"clockwise":"counterclockwise")+" arc");
            }
            Reject(()=>AbeNestImport.ArcPoints(new PointD(0,0),new PointD(10,0),false,null,null,2),"R diameter smaller than chord");
            Reject(()=>AbeNestImport.ArcPoints(new PointD(0,0),new PointD(0,0),false,null,null,10),"Full circle in ambiguous R format");
            Reject(()=>AbeNestImport.ArcPoints(new PointD(0,0),new PointD(0,0),false,0,0,null),"Zero-radius I/J circle");
            Reject(()=>AbeNestImport.ArcPoints(new PointD(10,0),new PointD(0,20),false,-10,0,null),"Inconsistent I/J endpoint radius");
            Reject(()=>AbeNestImport.ArcPoints(new PointD(10,0),new PointD(0,10),false,-10,0,10),"Mixed R and I/J definitions");
            var f=Fixture();string nc=f.Nc.Replace("M101","G00X7Y5\nM103\nG03I-2\nM104\nM101");
            var hole=AbeNestImport.Parse(nc,f.Sy2);hole.Reference.MaterialConfirmed=true;
            var match=AbeNestImport.MatchParts(hole,Snapshot(),Array.Empty<SavedNestReceipt>());var shapes=Shapes(match);
            foreach(var shape in shapes.Values)shape.Holes.Add(new PathD(Enumerable.Range(0,64).Select(n=>new PointD(5+2*Math.Cos(n*Math.PI/32),5+2*Math.Sin(n*Math.PI/32)))));
            // The fixture's placement starts at 20,30; use its actual hole coordinates.
            hole=AbeNestImport.Parse(nc.Replace("G00X7Y5","G00X27Y35"),f.Sy2);hole.Reference.MaterialConfirmed=true;
            var plan=AbeNestImport.CreatePlan(hole,match,Template(),shapes,100,CancellationToken.None);
            Check(plan.Placed.Single().Holes.Count==1&&plan.Package.Status=="completed","Circular NC hole registers with the real part outline for a saveable imported plan");
        }

        private static void LoadChecks()
        {
            string folder=Path.Combine(Path.GetTempPath(),"HanlonAbeImportChecks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try
            {
                var f=Fixture();string nc=Path.Combine(folder,"ABE-source.nc"),sy2=Path.Combine(folder,"ABE-source.sy2");
                File.WriteAllText(nc,f.Nc);File.WriteAllText(sy2,f.Sy2);
                var doc=AbeNestImport.Read(nc,sy2);doc.Reference.MaterialConfirmed=true;
                var snapshot=Snapshot();var match=AbeNestImport.MatchParts(doc,snapshot,Array.Empty<SavedNestReceipt>());
                var plan=AbeNestImport.CreatePlan(doc,match,Template(),Shapes(match),100,CancellationToken.None);
                var p=plan.Placed.Single();plan.Package.Remnants.Clear();plan.Package.NestImageFile="preview.png";
                plan.Package.Placements.Add(new NestPlacement{JobNumber=p.Part.JobNumber,MainMark=p.Part.MainMark,PieceMark=p.Part.PieceMark,
                    Sequence=p.Part.Sequence,LotNumber=p.Part.LotNumber,X=p.X,Y=p.Y,Rotation=p.Angle});
                File.WriteAllBytes(Path.Combine(folder,"preview.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
                string path=Path.Combine(folder,"result.json");void Write()=>File.WriteAllText(path,JsonSerializer.Serialize(plan.Package,LoadedNestResult.JsonOptions));
                Write();Check(LoadedNestResult.Load(path,snapshot).Result.Parts.Single().Quantity==1,"Verified ABE package reloads through the real save-result loader");
                plan.Package.Placements[0].Rotation=90;Write();Reject(()=>LoadedNestResult.Load(path,snapshot),"Stored placement angle differs from original SY2");
                plan.Package.Placements[0].Rotation=0;plan.Package.Placements[0].PieceMark="WRONG";Write();Reject(()=>LoadedNestResult.Load(path,snapshot),"Stored placement identity differs from original SY2");
                plan.Package.Placements[0].PieceMark=p.Part.PieceMark;plan.Package.AbeImport!.MaterialConfirmed=false;Write();Reject(()=>LoadedNestResult.Load(path,snapshot),"Unconfirmed material cannot reload as saveable");
                plan.Package.AbeImport.MaterialConfirmed=true;Write();File.AppendAllText(nc,"\n");Reject(()=>LoadedNestResult.Load(path,snapshot),"Changed NC source hash");
            }
            finally{Directory.Delete(folder,true);}
        }
    }
}

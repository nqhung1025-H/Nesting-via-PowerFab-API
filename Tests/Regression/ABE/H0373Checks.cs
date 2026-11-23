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
    internal static class H0373Checks
    {
        private static int passed;
        private static void Check(bool ok,string message){if(!ok)throw new Exception("H0373: "+message);passed++;}
        private static void Reject(Action action,string message)
        {try{action();}catch(InvalidDataException){passed++;return;}throw new Exception("H0373 accepted: "+message);}
        private static readonly string Fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE","H0373");
        public static int Run()
        {
            var docs=new Dictionary<int,AbeNestDocument>();
            foreach(var pair in new[]{(4,12),(5,1),(8,2),(10,79)})
            {
                string nc=Path.Combine(Fixture,"H0373-PHASE1-"+pair.Item1+"MM");
                var doc=AbeNestImport.Read(nc,nc+".SY2");docs.Add(pair.Item1,doc);
                Check(doc.Placements.Count==pair.Item2,"Actual "+pair.Item1+" mm pair retains every placement");
                Check(doc.PowerFabThickness==pair.Item1,"Filename preserves PowerFab "+pair.Item1+" mm thickness independently of the machine code");
                Check(doc.Cuts.Count>0&&doc.Marks.Count>0,"Actual cutting and marking paths retained for "+pair.Item1+" mm");
            }
            var batch=AbeImportBatch.ReadFolder(Fixture,CancellationToken.None,null);
            Check(batch.Items.Count==4&&batch.Items.All(i=>i.Document!=null),"All four actual NC/SY2 pairs import in one folder");
            Check(docs[10].PackerCount==65&&docs[10].ProductionCount==14,"Actual 10 mm packers remain separate from the 14 job parts");
            Check(docs[4].Thickness==4.5m&&docs[5].Thickness==4.5m,"SPH4.5 machine thickness remains unchanged for the 4/5 mm files");
            Reject(()=>AbeNestImport.Parse(docs[4].NcText.Replace("SPH4.5E","SPH6.0E"),docs[4].Sy2Text),"Genuine NC/SY2 thickness mismatch");
            Reject(()=>AbeNestImport.Parse(docs[5].NcText,docs[5].Sy2Text.Replace("STYPE=0","STYPE=1")),"Unimplemented SY2 record type");
            Reject(()=>AbeNestImport.Parse(docs[4].NcText.Replace("M100","M100\nM28"),docs[4].Sy2Text),"M28 inside a running cutting program");
            Reject(()=>AbeNestImport.Parse(docs[4].NcText.Replace("M28","M28X10"),docs[4].Sy2Text),"Unexpected geometry hidden in an M28 header");

            var doc8=docs[8];
            Check(doc8.Placements.All(p=>p.Angle==45&&p.SizeX==110&&p.SizeY==110),"Actual round parts retain the 45-degree source rotation and 110 mm bounds");
            XElement Part(AbePlacement p)=>new XElement("WorkOrderPart",new XElement("JobNumber","H0373"),
                new XElement("MainMark",p.Name),new XElement("PieceMark",p.Name),new XElement("Quantity",1),
                new XElement("Shape","PLT"),new XElement("Grade","250"),new XElement("Dimensions","8X110"),
                new XElement("Length",new XAttribute("UOM","MM"),110),new XElement("Width",new XAttribute("UOM","MM"),110),
                new XElement("PlateThickness",new XAttribute("UOM","MM"),8));
            var snapshot=NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",new XElement("WorkOrderID","H0373-CONTROLLED"),
                new XElement("WorkOrderParts",doc8.Placements.Select(Part)),new XElement("AvailableInventory"))));
            var match=AbeNestImport.MatchParts(doc8,snapshot,Array.Empty<SavedNestReceipt>());
            PathD Circle(double radius,bool reverse=false)
            {
                var path=new PathD(Enumerable.Range(0,720).Select(i=>new PointD(55+radius*Math.Cos(i*Math.PI/360),55+radius*Math.Sin(i*Math.PI/360))));
                if(reverse)path.Reverse();return path;
            }
            var shapes=match.Bindings.ToDictionary(b=>LoadedNestResult.PartKey(b.Part),b=>new DxfShape
                {Source="Controlled round PowerFab outline matched to actual H0373 NC",Outer=Circle(55),Holes=new List<PathD>{Circle(32,true)}});
            NestResultPackage Template()=>PlateSelection.ForMaterial(snapshot,match.Group,new NestMaterial
                {Source="Purchase",Shape="PLT",Grade="250",Thickness=8,Length=3000,Width=1500,Unit="MM",Dimensions="8X1500"},new LayoutConditions());
            var plan=AbeNestImport.CreatePlan(doc8,match,Template(),shapes,100,CancellationToken.None);
            Check(plan.Placed.Count==2&&plan.Placed.All(p=>p.Angle==45),"True circular outlines verify against actual 8 mm NC without changing their rotation");
            Check(plan.Placed.Zip(doc8.Placements,(a,b)=>Math.Abs(a.Bounds.Left-b.X)<0.001&&Math.Abs(a.Bounds.Bottom-b.Y)<0.001).All(v=>v),"Actual circular placement positions are preserved");
            var wrong=shapes.ToDictionary(p=>p.Key,p=>new DxfShape{Source="Incorrect square",Outer=new PathD{new PointD(0,0),new PointD(110,0),new PointD(110,110),new PointD(0,110)}});
            Reject(()=>AbeNestImport.CreatePlan(doc8,match,Template(),wrong,100,CancellationToken.None),"A rotated square cannot pass as the actual round part");
            var shifted=AbeNestImport.Parse(doc8.NcText,doc8.Sy2Text.Replace("PBX=15.0000","PBX=17.0000"));
            shifted.Reference.ProgramName=doc8.Reference.ProgramName;
            var shiftedMatch=AbeNestImport.MatchParts(shifted,snapshot,Array.Empty<SavedNestReceipt>());
            Reject(()=>AbeNestImport.CreatePlan(shifted,shiftedMatch,Template(),shapes,100,CancellationToken.None),"Incorrect oblique SY2 bounds are rejected against the real outline");
            var selected=Template();selected.Material.RequiresOutline=true;
            selected.Material.StockContour=new[]{(0d,0d),(3000d,0d),(3000d,1500d),(1000d,1500d),(1000d,200d),(0d,200d)}
                .Select(p=>new LayoutPoint{X=p.Item1,Y=p.Item2}).ToList();
            Reject(()=>AbeNestImport.CreatePlan(doc8,match,selected,shapes,100,CancellationToken.None),"A missing corner in the selected inventory remnant is still enforced");
            Check(AbeNestImport.CreatePlan(doc8,match,Template(),shapes,100,CancellationToken.None).Placed.Count==2,"The same unchanged layout verifies after choosing a compatible new sheet");

            var doc10=docs[10];
            var ncPlan=new CalculatedNest{Package=new NestResultPackage{NestId="H0373-10MM-REGRESSION",Material=new NestMaterial
                {Source="Purchase",Shape="PLT",Grade="250",Thickness=10,Length=3000,Width=1500,Unit="MM"}},SheetLengthMm=3000,SheetWidthMm=1500};
            ncPlan.Placed.AddRange(doc10.Cuts.Concat(doc10.Marks).Select(c=>new PlacedOutline{Outer=c}));
            var rules=new LayoutConditions{Spacing=0,Left=0,Right=0,Top=0,Bottom=0,MinimumBottomRemnant=100};
            var bottom=RemnantGeometry.Create(ncPlan,rules).Single();
            Check(bottom.Edge=="Bottom"&&bottom.X==0&&bottom.Y==0&&bottom.Length==3000,"Actual 10 mm top-filled NC layout retains the full-length bottom rectangle");
            Check(bottom.Width>1000&&bottom.Width<1500,"Bottom cut is below every actual cut, packer, lead-in and mark");
            Check(bottom.Contour.Count==4&&bottom.Holes.Count==0&&bottom.Area==bottom.Length*bottom.Width,"Actual bottom remnant is one rectangle with matching area");
            RemnantGeometry.ValidateContour(bottom);Check(true,"Actual bottom remnant DXF validates");
            Check(Math.Abs(DxfGeometry.Read(RemnantGeometry.Definition(bottom),"H0373 bottom").Area-(double)bottom.Area)<0.01,"Actual bottom DXF area equals saved stock area");
            var savedBottom=plan.Package.Remnants.Single();
            savedBottom.Edge="Bottom";savedBottom.X=0;savedBottom.Y=0;savedBottom.Length=3000;savedBottom.Width=1000;savedBottom.Area=3000000;
            savedBottom.Contour=new[]{(0d,0d),(3000d,0d),(3000d,1000d),(0d,1000d)}.Select(v=>new LayoutPoint{X=v.Item1,Y=v.Item2}).ToList();
            string root=Path.Combine(Path.GetTempPath(),"H0373Save",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                plan.Package.Placements=plan.Placed.Select(p=>new NestPlacement{JobNumber=p.Part.JobNumber,MainMark=p.Part.MainMark,PieceMark=p.Part.PieceMark,
                    LotNumber=p.Part.LotNumber,Sequence=p.Part.Sequence,X=p.X,Y=p.Y,Rotation=p.Angle}).ToList();
                plan.Package.RequisitionNumber="REQH0373";plan.Package.NestImageFile="preview.png";savedBottom.ImageFile="preview.png";
                File.WriteAllBytes(Path.Combine(root,"preview.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
                File.WriteAllText(Path.Combine(root,doc8.Reference.NcFile),doc8.NcText);File.WriteAllText(Path.Combine(root,doc8.Reference.Sy2File),doc8.Sy2Text);
                string json=Path.Combine(root,"result.json");File.WriteAllText(json,JsonSerializer.Serialize(plan.Package,LoadedNestResult.JsonOptions));
                var loaded=LoadedNestResult.Load(json,snapshot);
                var xml=XDocument.Parse(PowerFabNestApi.Serialize(PowerFabNestApi.BuildSaveRequest(loaded,snapshot)));
                Check(xml.Descendants().Any(e=>e.Name.LocalName=="Area"&&e.Value=="3000000"),"SaveNest XML accepts a usable rectangular bottom remnant");
            }
            finally{Directory.Delete(root,true);}
            Console.WriteLine($"PASS: {passed} H0373 actual NC/SY2 and controlled-outline regression checks; 10 mm bottom remnant = {bottom.Length} x {bottom.Width} mm.");
            return passed;
        }
    }
}

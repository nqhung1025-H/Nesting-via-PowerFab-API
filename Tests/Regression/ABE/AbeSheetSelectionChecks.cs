using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class AbeSheetSelectionChecks
    {
        public static int Run()
        {
            int passed=0;
            void Check(bool ok,string name){if(!ok)throw new Exception("ABE sheet selection: "+name);passed++;}
            string root=Path.Combine(AppContext.BaseDirectory,"Fixtures","ABE");
            var wide=AbeNestImport.Read(Path.Combine(root,"H0373","H0373-PHASE1-10MM"),Path.Combine(root,"H0373","H0373-PHASE1-10MM.SY2"));
            var group=new PlateNestGroup{Shape="PLT",Grade="250",ThicknessMm=10};
            var stock=new NestMaterial{Source="Inventory",Shape="PLT",Grade="250",Thickness=10,Length=2400,Width=1500,Unit="MM",Metric=true,
                InventoryId="REMNANT-2400",ExternalInventoryLinkId="EXISTING-LINK",ParentRemnantPowerFabId="193",RequiresOutline=true,
                PurchaseOrderNumber="OLD-PO",StockJob="PREVIOUS-JOB",StockContour=new[]{(0d,0d),(2400d,0d),(2400d,1500d),(0d,1500d)}
                    .Select(p=>new LayoutPoint{X=p.Item1,Y=p.Item2}).ToList()};
            string before=JsonSerializer.Serialize(stock);
            var placements=wide.Placements.Select(p=>(p.X,p.Y,p.BaseX,p.BaseY,p.Angle)).ToArray();
            Check(!AbeSheetSelection.FitsEnvelope(wide,stock),"Actual top-filled H0373 layout does not fit a 2400 mm remnant");
            var proposed=AbeSheetSelection.SuggestNewRequirement(wide,stock,group);
            Check(proposed!=null&&proposed.Length==3000&&proposed.Width==1500,"The unsuitable remnant offers a 3000 x 1500 new sheet");
            Check(proposed!.Source=="Purchase"&&proposed.Unit=="MM"&&proposed.Metric,"Replacement is a new metric requisition requirement");
            Check(proposed.Shape=="PLT"&&proposed.Grade=="250"&&proposed.Thickness==10&&proposed.Dimensions=="10X1500","Replacement uses the matched PowerFab material");
            MaterialRequirements.ValidateUnallocated(proposed);
            Check(proposed.InventoryId.Length==0&&proposed.ExternalInventoryLinkId.Length==0&&proposed.ParentRemnantPowerFabId.Length==0,
                "The new requirement carries none of the old remnant identities");
            Check(proposed.StockContour.Count==0&&proposed.StockHoles.Count==0&&!proposed.RequiresOutline&&proposed.PurchaseOrderNumber.Length==0,
                "The new full sheet inherits no old stock shape or PO");
            Check(AbeSheetSelection.FitsEnvelope(wide,proposed),"The proposed sheet contains the unchanged imported extent");
            string prompt=AbeSheetSelection.Prompt(stock,proposed);
            Check(prompt.Contains("2400 x 1500 mm")&&prompt.Contains("3000 x 1500 mm")&&prompt.Contains("new requisition")&&prompt.EndsWith("continue?"),
                "The question explains the smaller stock and asks to continue using a new requisition sheet");
            Check(JsonSerializer.Serialize(stock)==before,"Offering or declining the proposal leaves the inventory remnant unchanged");
            Check(wide.Placements.Select(p=>(p.X,p.Y,p.BaseX,p.BaseY,p.Angle)).SequenceEqual(placements),"Sheet choice does not move, rotate or scale ABE placements");
            Check(AbeSheetSelection.SuggestNewRequirement(wide,proposed,group)==null,"An already suitable 3000 mm sheet needs no replacement prompt");
            var narrow=NestingBatch.Clone(proposed);narrow.Width=1200;
            Check(AbeSheetSelection.SuggestNewRequirement(wide,narrow,group)!=null,"Insufficient sheet width also offers the compatible standard sheet");
            var inches=NestingBatch.Clone(stock);inches.Length=2400m/25.4m;inches.Width=1500m/25.4m;inches.Unit="IN";
            Check(AbeSheetSelection.SuggestNewRequirement(wide,inches,group)!=null,"An imperial stock dimension is compared in millimetres");
            var left=AbeNestImport.Read(Path.Combine(root,"Phase10and11","H0306-PHASE10+11-10MM.nc"),Path.Combine(root,"Phase10and11","H0306-PHASE10+11-10MM.sy2"));
            Check(AbeSheetSelection.FitsEnvelope(left,stock),"A left-filled layout really fits the smaller stock in the right-remnant case");
            Check(AbeSheetSelection.SuggestNewRequirement(left,stock,group)==null,"A fitting right-remnant layout is not forced onto a new full sheet");
            var oversize=new AbeNestDocument();oversize.Cuts.Add(new PathD{new PointD(0,0),new PointD(3100,1500)});
            Check(AbeSheetSelection.SuggestNewRequirement(oversize,stock,group)==null,"A 3100 mm layout is never offered a too-small 3000 mm replacement");
            var smaller=new NestMaterial{Source="Inventory",Length=(decimal)wide.Bounds.Right+1,Width=(decimal)wide.Bounds.Top+1,Unit="MM"};
            Check(AbeSheetSelection.SuggestNewRequirement(wide,smaller,group)==null,"The fit decision uses occupied layout dimensions rather than a filename guess");
            Console.WriteLine($"PASS: {passed} ABE sheet-size and new-requisition proposal checks using both actual layout directions.");
            return passed;
        }
    }
}

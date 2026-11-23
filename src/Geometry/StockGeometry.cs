using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class StockGeometry
    {
        public static bool IsFuture(XElement stock) => NestingSnapshot.Field(stock,"Type").Equals("FutureRemnant",StringComparison.OrdinalIgnoreCase);
        public static bool IsRemnant(XElement stock) => IsFuture(stock)
            || NestingSnapshot.Field(stock,"Type").Contains("Remnant",StringComparison.OrdinalIgnoreCase)
            || NestingSnapshot.Field(stock,"RemnantName").Length>0
            || PowerFabRemnantId(stock).Length>0
            || InventoryTracking.PositiveId(NestingSnapshot.Field(stock,"TrueShapeID"));
        public static string PowerFabRemnantId(XElement stock)
        {
            foreach(string field in new[]{"RemnantPowerFabID","ParentRemnantPowerFabID","RemnantFabSuiteID","RemnantID","FabSuiteRemnantID"})
            {string value=NestingSnapshot.Field(stock,field);if(InventoryTracking.PositiveId(value))return value;}
            return "";
        }
        internal static void SeparateFutureFromParent(XElement stock,string parentJob="")
        {
            if(!IsFuture(stock))return;
            if(NestingSnapshot.Field(stock,"HanlonParentJob").Length==0)
                Set(stock,"HanlonParentJob",parentJob.Length>0?parentJob:NestingSnapshot.Field(stock,"Job","JobNumber"));
            // These describe the parent sheet in an exported future-remnant row.
            // Its own availability is verified against children using this ID.
            foreach(string field in new[]{"Job","JobNumber","JobReserve","NestID","ExternalNestID","PercentCombined","ExternalInventoryLinkID","Cut","TakenFromStock"})
                NestingSnapshot.Child(stock,field)?.Remove();
        }
        public static PathsD Region(NestMaterial material)
        {
            double factor=material.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4:1;
            PathD outer=material.StockContour.Count>0?Path(material.StockContour):new PathD {
                new PointD(0,0),new PointD((double)material.Length*factor,0),
                new PointD((double)material.Length*factor,(double)material.Width*factor),new PointD(0,(double)material.Width*factor) };
            if(Clipper.Area(outer)<0)outer.Reverse();
            var paths=new PathsD {outer};
            foreach(var hole in material.StockHoles){var p=Path(hole);if(Clipper.Area(p)>0)p.Reverse();paths.Add(p);}
            return paths;
        }
        public static bool Contains(NestMaterial material, PathsD clearance)
        {
            if(material.StockContour.Count==0)return true;
            if(material.StockCurveToleranceMm>0)clearance=Clipper.InflatePaths(clearance,material.StockCurveToleranceMm,JoinType.Miter,EndType.Polygon,50,4,0.005);
            return Math.Abs(Clipper.Area(Clipper.Difference(clearance,Region(material),FillRule.NonZero,4)))<0.00001;
        }
        public static PathD Path(IEnumerable<LayoutPoint> points)=>new PathD(points.Select(p=>new PointD(p.X,p.Y)));
        public static List<LayoutPoint> Points(PathD path)=>path.Select(p=>new LayoutPoint{X=p.x,Y=p.y}).ToList();
        public static void SetOutline(XElement stock,string dxf)
        {
            var shape=DxfGeometry.Read(dxf,"Remnant outline");
            double left=shape.Bounds.Left,bottom=shape.Bounds.Bottom;
            List<LayoutPoint> Local(PathD p)=>p.Select(v=>new LayoutPoint{X=v.x-left,Y=v.y-bottom}).ToList();
            decimal length=Mm(stock,"Length"),width=Mm(stock,"Width");
            if(Math.Abs(shape.Bounds.Width-(double)length)>0.5 || Math.Abs(shape.Bounds.Height-(double)width)>0.5)
                throw new InvalidDataException("The remnant DXF bounds do not match its PowerFab length and width. No outline was assigned.");
            Set(stock,"HanlonContour",JsonSerializer.Serialize(Local(shape.Outer),LoadedNestResult.JsonOptions));
            Set(stock,"HanlonHoles",JsonSerializer.Serialize(shape.Holes.Select(Local).ToList(),LoadedNestResult.JsonOptions));
            Set(stock,"HanlonCurveTolerance",shape.CurveTolerance.ToString(CultureInfo.InvariantCulture));
        }
        public static void Apply(XElement stock,NestMaterial material)
        {
            material.RequiresOutline=IsRemnant(stock);
            string contour=NestingSnapshot.Field(stock,"HanlonContour"),holes=NestingSnapshot.Field(stock,"HanlonHoles");
            if(contour.Length>0)material.StockContour=JsonSerializer.Deserialize<List<LayoutPoint>>(contour,LoadedNestResult.JsonOptions)??new List<LayoutPoint>();
            if(holes.Length>0)material.StockHoles=JsonSerializer.Deserialize<List<List<LayoutPoint>>>(holes,LoadedNestResult.JsonOptions)??new List<List<LayoutPoint>>();
            if(double.TryParse(NestingSnapshot.Field(stock,"HanlonCurveTolerance"),NumberStyles.Float,CultureInfo.InvariantCulture,out double tolerance))material.StockCurveToleranceMm=tolerance;
        }
        public static void Validate(NestMaterial material)
        {
            if(!double.IsFinite(material.StockCurveToleranceMm)||material.StockCurveToleranceMm<0||material.StockCurveToleranceMm>0.5)
                throw new InvalidDataException("Invalid stock curve tolerance.");
            if(material.RequiresOutline && material.StockContour.Count<3)
                throw new InvalidDataException("The saved outline for this remnant could not be loaded automatically. Refresh inventory. Keep the original Hanlon nest data so its leftover shape can be recovered.");
            if(material.StockContour.Count==0)return;
            if(material.StockContour.Any(p=>p==null||!double.IsFinite(p.X)||!double.IsFinite(p.Y))
                || material.StockHoles.Any(h=>h==null||h.Count<3||h.Any(p=>p==null||!double.IsFinite(p.X)||!double.IsFinite(p.Y))))
                throw new InvalidDataException("The stock outline has invalid coordinates.");
            var b=ShapeBounds.Of(Path(material.StockContour));double f=material.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4:1;
            if(Math.Abs(b.Left)>0.01 || Math.Abs(b.Bottom)>0.01 || Math.Abs(b.Width-(double)material.Length*f)>0.5 || Math.Abs(b.Height-(double)material.Width*f)>0.5)
                throw new InvalidDataException("The stock outline does not match the selected sheet bounds.");
            var r=new NestRemnant{Contour=material.StockContour,Holes=material.StockHoles,Length=(decimal)b.Width,Width=(decimal)b.Height,Unit="MM"};
            DxfGeometry.Read(RemnantGeometry.Definition(r),"Stock outline");
        }
        public static NestRemnant? ClipRemnant(NestRemnant candidate,NestMaterial material)
        {
            if(material.StockContour.Count==0)return candidate;
            var region=Region(material);
            if(material.StockCurveToleranceMm>0)region=Clipper.InflatePaths(region,-material.StockCurveToleranceMm,JoinType.Miter,EndType.Polygon,50,4,0.005);
            var paths=Clipper.Intersect(new PathsD{Path(candidate.Contour)},region,FillRule.NonZero,4);
            var outer=paths.Where(p=>Clipper.Area(p)>0.01).OrderByDescending(p=>Clipper.Area(p)).FirstOrDefault();
            if(outer==null)return null;
            // Keep one physically connected remnant. Disconnected scraps cannot form one stock item.
            var holes=paths.Where(p=>Clipper.Area(p)<-0.01 && Inside(outer,p[0])).ToList();
            var bounds=ShapeBounds.Of(outer);
            candidate.Contour=Points(outer);candidate.Holes=holes.Select(Points).ToList();
            candidate.X=(decimal)bounds.Left;candidate.Y=(decimal)bounds.Bottom;
            candidate.Length=(decimal)bounds.Width;candidate.Width=(decimal)bounds.Height;
            candidate.Area=(decimal)(Math.Abs(Clipper.Area(outer))-holes.Sum(p=>Math.Abs(Clipper.Area(p))));candidate.Edge="Combined";
            return candidate.Area>1?candidate:null;
        }
        private static bool Inside(PathD p,PointD v)
        {bool inside=false;for(int i=0,j=p.Count-1;i<p.Count;j=i++)if((p[i].y>v.y)!=(p[j].y>v.y)&&v.x<(p[j].x-p[i].x)*(v.y-p[i].y)/(p[j].y-p[i].y)+p[i].x)inside=!inside;return inside;}
        public static decimal Mm(XElement stock,params string[] fields)
        {
            var node=NestingSnapshot.Child(stock,fields);if(node==null||!decimal.TryParse(node.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out decimal n))return 0;
            string u=node.Attributes().FirstOrDefault(a=>a.Name.LocalName.Equals("UOM",StringComparison.OrdinalIgnoreCase))?.Value??"MM";
            return u.Equals("IN",StringComparison.OrdinalIgnoreCase)?n*25.4m:n;
        }
        internal static void Set(XElement x,string field,string value)
        {var e=NestingSnapshot.Child(x,field);if(e==null)x.Add(new XElement(field,value));else e.Value=value;}
    }
}

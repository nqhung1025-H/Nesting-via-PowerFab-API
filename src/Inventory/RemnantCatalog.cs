using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class RemnantOutlineRecord
    {
        public string PowerFabRemnantId {get;set;}="";
        public string InventoryId {get;set;}="";
        public string TrueShapeId {get;set;}="";
        public string ExternalRemnantId {get;set;}="";
        public string Name {get;set;}="";
        public string Shape {get;set;}="";
        public string Grade {get;set;}="";
        public decimal ThicknessMm {get;set;}
        public decimal LengthMm {get;set;}
        public decimal WidthMm {get;set;}
        public double CurveToleranceMm {get;set;}
        public List<LayoutPoint> Contour {get;set;}=new List<LayoutPoint>();
        public List<List<LayoutPoint>> Holes {get;set;}=new List<List<LayoutPoint>>();
    }
    internal static class RemnantCatalog
    {
        public static List<RemnantOutlineRecord> Read(string exportFolder)
        {
            string folder=Path.Combine(exportFolder,"RemnantOutlines");
            return Directory.Exists(folder)?Directory.GetFiles(folder,"*.json").Select(p=>JsonSerializer.Deserialize<RemnantOutlineRecord>(File.ReadAllText(p),LoadedNestResult.JsonOptions)
                ??throw new InvalidDataException("Could not read remnant outline: "+p)).ToList():new List<RemnantOutlineRecord>();
        }
        private static void Write(string folder,RemnantOutlineRecord record)
        {
            string key=InventoryTracking.PositiveId(record.PowerFabRemnantId)?"R-"+record.PowerFabRemnantId:"I-"+record.InventoryId;
            if(!InventoryTracking.PositiveId(record.PowerFabRemnantId)&&!InventoryTracking.PositiveId(record.InventoryId))return;
            folder=Path.Combine(folder,"RemnantOutlines");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,key+".json"),temp=path+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(record,LoadedNestResult.JsonOptions));File.Move(temp,path,true);
        }
        public static void RememberStock(XElement stock,string folder)
        {
            var m=new NestMaterial();
            StockGeometry.Apply(stock,m);m.Length=StockGeometry.Mm(stock,"Length");m.Width=StockGeometry.Mm(stock,"Width");m.Unit="MM";StockGeometry.Validate(m);
            Write(folder,new RemnantOutlineRecord{InventoryId=StockGeometry.IsFuture(stock)?"":NestingSnapshot.Field(stock,"ID"),
                PowerFabRemnantId=StockGeometry.PowerFabRemnantId(stock),
                Name=NestingSnapshot.Field(stock,"RemnantName"),Shape=NestingSnapshot.Field(stock,"Shape"),Grade=NestingSnapshot.Field(stock,"Grade"),
                ThicknessMm=StockGeometry.Mm(stock,"PlateThickness","Thickness"),LengthMm=m.Length,WidthMm=m.Width,Contour=m.StockContour,Holes=m.StockHoles,CurveToleranceMm=m.StockCurveToleranceMm});
        }
        public static void RememberSaved(LoadedNestResult loaded,SavedNestReceipt receipt,string folder)=>RememberSaved(loaded.Result,receipt,folder);
        private static void RememberSaved(NestResultPackage result,SavedNestReceipt receipt,string folder)
        {
            for(int i=0;i<result.Remnants.Count;i++)
            {
                var r=result.Remnants[i];string id=receipt.RemnantPowerFabIds.ElementAtOrDefault(i)??"";
                var points=RemnantGeometry.Outline(r);double left=points.Min(p=>p.X),bottom=points.Min(p=>p.Y);
                double f=r.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4:1;
                List<LayoutPoint> Local(IEnumerable<LayoutPoint> p)=>p.Select(v=>new LayoutPoint{X=(v.X-left)*f,Y=(v.Y-bottom)*f}).ToList();
                Write(folder,new RemnantOutlineRecord{PowerFabRemnantId=id,ExternalRemnantId=r.RemnantId,Name=r.Name,
                    Shape=result.Material.Shape,Grade=result.Material.Grade,
                    ThicknessMm=result.Material.Thickness*(result.Material.Unit.Equals("IN",StringComparison.OrdinalIgnoreCase)?25.4m:1),
                    LengthMm=r.Length*(decimal)f,WidthMm=r.Width*(decimal)f,Contour=Local(points),Holes=r.Holes.Select(h=>Local(h)).ToList()});
            }
        }
        public static void Apply(XElement stock,IEnumerable<RemnantOutlineRecord> records)
        {
            string remnant=StockGeometry.PowerFabRemnantId(stock),inv=StockGeometry.IsFuture(stock)?"":NestingSnapshot.Field(stock,"ID"),name=NestingSnapshot.Field(stock,"RemnantName"),trueShape=NestingSnapshot.Field(stock,"TrueShapeID");
            var matches=records.Where(r=>InventoryTracking.PositiveId(remnant)?r.PowerFabRemnantId==remnant
                :InventoryTracking.PositiveId(inv)&&r.InventoryId==inv).ToList();
            if(matches.Count==0&&!InventoryTracking.PositiveId(remnant)&&InventoryTracking.PositiveId(trueShape))matches=records.Where(r=>r.TrueShapeId==trueShape).ToList();
            if(matches.Count==0&&!InventoryTracking.PositiveId(remnant)&&name.Length>0)matches=records.Where(r=>r.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToList();
            if(matches.Count!=1)return;var match=matches[0];
            if(!match.Shape.Equals(NestingSnapshot.Field(stock,"Shape"),StringComparison.OrdinalIgnoreCase)
                ||!match.Grade.Equals(NestingSnapshot.Field(stock,"Grade"),StringComparison.OrdinalIgnoreCase)
                ||Math.Abs(match.LengthMm-StockGeometry.Mm(stock,"Length"))>0.05m
                ||Math.Abs(match.WidthMm-StockGeometry.Mm(stock,"Width"))>0.05m
                ||Math.Abs(match.ThicknessMm-StockGeometry.Mm(stock,"PlateThickness","Thickness"))>0.01m)return;
            if(decimal.TryParse(NestingSnapshot.Field(stock,"HanlonVerifiedRemnantAreaSqm"),NumberStyles.Number,CultureInfo.InvariantCulture,out var area)
                &&area>0&&Math.Abs((double)(area*1000000m)-(Math.Abs(Clipper2Lib.Clipper.Area(StockGeometry.Path(match.Contour)))
                    -match.Holes.Sum(h=>Math.Abs(Clipper2Lib.Clipper.Area(StockGeometry.Path(h))))))>1)return;
            StockGeometry.Set(stock,"HanlonContour",JsonSerializer.Serialize(match.Contour,LoadedNestResult.JsonOptions));
            StockGeometry.Set(stock,"HanlonHoles",JsonSerializer.Serialize(match.Holes,LoadedNestResult.JsonOptions));
            StockGeometry.Set(stock,"HanlonCurveTolerance",match.CurveToleranceMm.ToString(CultureInfo.InvariantCulture));
        }
        public static void LinkPhysical(string folder,string remnantId,string inventoryId,string trueShapeId)
        {foreach(var r in Read(folder).Where(r=>r.PowerFabRemnantId==remnantId&&(r.InventoryId!=inventoryId||r.TrueShapeId!=trueShapeId))){r.InventoryId=inventoryId;r.TrueShapeId=trueShapeId;Write(folder,r);}}

        public static void RecoverSavedOutlines(string folder)
        {
            var known=Read(folder).Select(r=>r.PowerFabRemnantId).ToHashSet();
            foreach(var receipt in PowerFabNestApi.ReadReceipts(folder).Where(r=>r.RemnantPowerFabIds.Any(id=>!known.Contains(id))))
            {
                if(!File.Exists(receipt.ResultFile))continue;
                try
                {
                    var result=JsonSerializer.Deserialize<NestResultPackage>(File.ReadAllText(receipt.ResultFile),LoadedNestResult.JsonOptions);
                    if(result==null||result.Material==null||result.Remnants==null||result.NestId!=receipt.NestId||result.Remnants.Count!=receipt.RemnantPowerFabIds.Count)continue;
                    foreach(var r in result.Remnants){RemnantGeometry.ValidateContour(r);}
                    RememberSaved(result,receipt,folder);
                    foreach(string id in receipt.RemnantPowerFabIds)known.Add(id);
                }
                catch(Exception e)when(e is JsonException||e is IOException||e is InvalidDataException) { /* Other known outlines remain usable. */ }
            }
        }
    }
}

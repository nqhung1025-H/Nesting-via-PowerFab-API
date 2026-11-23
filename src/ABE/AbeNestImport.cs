using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    public sealed class AbeImportReference
    {
        public string NcFile { get; set; } = "ABE-source.nc";
        public string Sy2File { get; set; } = "ABE-source.sy2";
        public string NcSha256 { get; set; } = "";
        public string Sy2Sha256 { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string Machine { get; set; } = "";
        public string Material { get; set; } = "";
        public bool MaterialConfirmed { get; set; }
        public string GeometrySource { get; set; } = "";
        public decimal? PowerFabThicknessMm { get; set; }
        public AbeProcessTime? ProcessTime { get; set; }
        public string ProcessTimeIssue { get; set; } = "";
        public List<AbePackerReference> Packers { get; set; } = new List<AbePackerReference>();
    }

    public sealed class AbePackerReference
    {
        public int SourceNumber { get; set; }
        public string Name { get; set; } = "";
        public double X { get; set; } public double Y { get; set; } public double Rotation { get; set; }
        public double Length { get; set; } public double Width { get; set; }
    }

    internal sealed class AbePlacement
    {
        public int Number;
        public string Name = "";
        public double X, Y, BaseX, BaseY, Angle, OriginalX, OriginalY, SizeX, SizeY;
        public ShapeBounds Bounds => new ShapeBounds(X, Y, X + SizeX, Y + SizeY);
        public PointD Transform(PointD p)
        {
            double a = Angle * Math.PI / 180;
            return new PointD(BaseX + p.x * Math.Cos(a) - p.y * Math.Sin(a), BaseY + p.x * Math.Sin(a) + p.y * Math.Cos(a));
        }
    }

    internal sealed class AbeNestDocument
    {
        public AbeImportReference Reference = new AbeImportReference();
        public string NcText = "", Sy2Text = "";
        public decimal Thickness;
        public List<AbePlacement> Placements = new List<AbePlacement>();
        public List<PathD> Cuts = new List<PathD>(), Marks = new List<PathD>();
        public List<PathD> CutCurves = new List<PathD>();
        public int ArcCount, FullCircleCount;
        public int PackerCount=>Placements.Count(p=>AbePartNames.IsPacker(p.Name));
        public int ProductionCount=>Placements.Count-PackerCount;
        public decimal PowerFabThickness=>Reference.PowerFabThicknessMm??AbePartNames.PowerFabThickness(Reference.Material,Thickness,Reference.ProgramName);
        public ShapeBounds Bounds => ShapeBounds.Of(Placements.SelectMany(p => new[] { new PointD(p.X,p.Y), new PointD(p.X+p.SizeX,p.Y+p.SizeY) })
            .Concat(Cuts.SelectMany(p => p)).Concat(Marks.SelectMany(p => p)));
    }

    internal sealed class AbePartBinding
    {
        public AbePlacement Placement = null!;
        public NestPart Part = null!;
    }

    internal sealed class AbePartMatch
    {
        public PlateNestGroup Group = null!;
        public List<AbePartBinding> Bindings = new List<AbePartBinding>();
    }

    // Import positions from SY2 without repacking. For ABE imports, recover
    // verified planning contours from the corresponding NC burn paths.
    internal static class AbeNestImport
    {
        private const double Tolerance = 0.02;
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        private static readonly Regex Word = new Regex(@"([A-Z])([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.CultureInvariant);
        public static string Canonical(string value) => Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]", "");

        public static AbeNestDocument Read(string ncPath, string sy2Path)
        {
            if (!ProgramStem(ncPath).Equals(Path.GetFileNameWithoutExtension(sy2Path),StringComparison.OrdinalIgnoreCase))
                throw Error("Choose the NC and SY2 files with the same program name.");
            byte[] nc = ReadBytes(ncPath), sy2 = ReadBytes(sy2Path);
            var result = Parse(Encoding.UTF8.GetString(nc), Encoding.UTF8.GetString(sy2));
            result.Reference.NcSha256 = Convert.ToHexString(SHA256.HashData(nc));
            result.Reference.Sy2Sha256 = Convert.ToHexString(SHA256.HashData(sy2));
            result.Reference.ProgramName = ProgramStem(ncPath);
            _=result.PowerFabThickness;
            return result;
        }

        internal static string ProgramStem(string path)
        {
            string extension=Path.GetExtension(path);
            return new[]{".NC",".CNC",".GCD",".LCD",".TXT"}.Contains(extension,StringComparer.OrdinalIgnoreCase)
                ?Path.GetFileNameWithoutExtension(path):Path.GetFileName(path);
        }

        // NC-only reading is for diagnostics/preview. It creates no part bindings,
        // completed result, stock dimensions or PowerFab allocations.
        internal static AbeNestDocument ReadNcOnly(string path)
        {
            byte[] bytes=ReadBytes(path);
            var document=new AbeNestDocument{NcText=Encoding.UTF8.GetString(bytes)};
            document.Reference.ProgramName=ProgramStem(path);
            document.Reference.NcSha256=Convert.ToHexString(SHA256.HashData(bytes));
            Match material=Regex.Match(document.NcText,@"M102\((.+?)-(\d+(?:\.\d*)?)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(material.Success){document.Reference.Material=material.Groups[1].Value;document.Thickness=decimal.Parse(material.Groups[2].Value,Culture);}
            ReadNc(document);return document;
        }

        private static byte[] ReadBytes(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw Error("Each ABE input must be smaller than 16 MiB.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Any(b => b > 127 || b == 0)) throw Error("This importer requires ASCII NC/SY2 text. The selected file has an unsupported encoding.");
            return bytes;
        }

        internal static AbeNestDocument Parse(string nc, string sy2)
        {
            var result = new AbeNestDocument { NcText = nc, Sy2Text = sy2 };
            var sections = new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string,string>? current = null;
            foreach (string raw in sy2.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string key = line.Substring(1,line.Length-2);
                    if (sections.ContainsKey(key)) throw Error("Duplicate SY2 section " + key + ".");
                    sections.Add(key,current = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    int equals = line.IndexOf('=');
                    if (current == null || equals < 1 || !current.TryAdd(line.Substring(0,equals),line.Substring(equals+1)))
                        throw Error("Malformed or duplicate SY2 field: " + line + ".");
                }
            }
            if (!sections.TryGetValue("HEADER_INFO",out var header) || Field(header,"VER") != "1.00" || !new[]{"0","2"}.Contains(Field(header,"STYPE")))
                throw Error("This version supports SY2 VER=1.00 / STYPE=0 or 2 placement records.");
            int count = Integer(header,"PDNUM");
            if (count < 1 || count > 2000 || sections.Count != count+1) throw Error("SY2 placement count must match PDNUM and be between 1 and 2000.");
            result.Reference.Machine = Field(header,"MACHINENAME");
            for (int i=1;i<=count;i++)
            {
                if (!sections.TryGetValue("PD_"+i.ToString("0000",Culture),out var fields) || Integer(fields,"HNO") != i || Field(fields,"HKIND") != "1")
                    throw Error("Unsupported, missing or out-of-order SY2 part " + i + ".");
                string[] transform = Field(fields,"P98DT").Split(',');
                if (transform.Length != 5 || transform.Select(ParseNumber).Where((v,n) => Math.Abs(v-(n is 1 or 2 ? 1:0))>1e-8).Any())
                    throw Error("Mirrored, scaled or offset SY2 P98DT transforms are not supported.");
                string[] material = Field(fields,"HMTT").Split(',');
                if (material.Length != 2 || !decimal.TryParse(material[1],NumberStyles.Float,Culture,out decimal thickness) || thickness<=0)
                    throw Error("SY2 material and metric thickness are required.");
                if (i==1) { result.Reference.Material=material[0].Trim();result.Thickness=thickness; }
                if (result.Thickness!=thickness)
                    throw Error("One NC/SY2 pair must use a single numeric profile thickness.");
                var p = new AbePlacement { Number=i, Name=Field(fields,"HNAME"), X=Number(fields,"PBX"),Y=Number(fields,"PBY"),
                    BaseX=Number(fields,"PBAX"),BaseY=Number(fields,"PBAY"),Angle=Number(fields,"PANG"),
                    SizeX=Number(fields,"HSZX"),SizeY=Number(fields,"HSZY"),OriginalX=Number(fields,"HSX"),OriginalY=Number(fields,"HSY") };
                if (Canonical(p.Name).Length==0 || p.Angle<0 || p.Angle>=360 || new[]{p.SizeX,p.SizeY,p.OriginalX,p.OriginalY}.Any(x=>x<=0))
                    throw Error("Invalid SY2 name, size or angle at part " + i + ".");
                var b=ShapeBounds.Of(new[]{new PointD(0,0),new PointD(p.OriginalX,0),new PointD(p.OriginalX,p.OriginalY),new PointD(0,p.OriginalY)}.Select(p.Transform));
                // For oblique rotations a rotated bounding rectangle encloses
                // the true outline but is generally larger (e.g. a circle at 45
                // degrees). Check exact bounds once the actual DXF is available.
                bool quarterTurn=Math.Abs(p.Angle/90-Math.Round(p.Angle/90))<1e-8;
                if (quarterTurn ? Math.Abs(b.Left-p.X)>Tolerance || Math.Abs(b.Bottom-p.Y)>Tolerance || Math.Abs(b.Width-p.SizeX)>Tolerance || Math.Abs(b.Height-p.SizeY)>Tolerance
                    : p.X<b.Left-Tolerance || p.Y<b.Bottom-Tolerance || p.X+p.SizeX>b.Right+Tolerance || p.Y+p.SizeY>b.Top+Tolerance)
                    throw Error("SY2 position, rotation and size disagree at part " + i + ".");
                result.Placements.Add(p);
            }
            ReadNc(result);
            return result;
        }

        private static void ReadNc(AbeNestDocument document)
        {
            double x=0,y=0,ox=0,oy=0;
            bool located=false, offset=false, started=false, ended=false;
            PathD? active=null;
            bool marking=false;
            int motion=0,lineNumber=0;
            foreach (string raw in document.NcText.Replace("\r", "").Split('\n'))
            {
                lineNumber++;
                string line=Regex.Replace(raw.ToUpperInvariant(),@"\([^)]*\)","").Trim();
                if (line.Length==0 || line=="%") continue;
                if (ended && line!="G10" && line!="M30") throw Error("Unexpected NC data after M101 at line " + lineNumber + ".");
                var words=Word.Matches(line).Select(m=>(Key:m.Groups[1].Value, Value:ParseNumber(m.Groups[2].Value))).ToList();
                string Context()=>" at line "+lineNumber+": "+raw.Trim();
                if (Word.Replace(line,"").Trim().Length>0 || words.Any(w=>!new[]{"N","G","M","X","Y","A","E","F","I","J","R"}.Contains(w.Key)))
                    throw Error("Unsupported NC word" + Context());
                foreach (string unique in new[]{"X","Y","A","I","J","R"}) if(words.Count(w=>w.Key==unique)>1)throw Error("Duplicate NC word" + Context());
                var gs=words.Where(w=>w.Key=="G").Select(w=>w.Value).ToList();
                if(gs.Any(g=>!new double[]{0,1,2,3,10,17,21,40,41,42,90,91.1,92,93}.Contains(g)))
                    throw Error("Unsupported NC G-code" + Context() + ". This reader supports absolute metric XY moves with incremental I/J arc centres.");
                if(gs.Count(g=>g is 0 or 1 or 2 or 3)>1)throw Error("Conflicting NC motion modes"+Context());
                if (gs.Contains(92) || gs.Contains(93))
                {
                    if (started || active!=null || gs.Count!=1 || words.Any(w=>!new[]{"N","G","X","Y"}.Contains(w.Key))
                        || !words.Any(w=>w.Key=="X") || !words.Any(w=>w.Key=="Y"))throw Error("Unsupported NC coordinate reset at line " + lineNumber + ".");
                    if(gs.Contains(93)) { if(offset)throw Error("Multiple NC G93 transforms are not supported.");ox=words.Single(w=>w.Key=="X").Value;oy=words.Single(w=>w.Key=="Y").Value;offset=true; }
                    // G92 is the machine travel/coordinate reference, not sheet size.
                    continue;
                }
                var ms=words.Where(w=>w.Key=="M").Select(w=>w.Value).ToList();
                if(ms.Any(m=>!new double[]{20,28,30,33,100,101,102,103,104}.Contains(m)))throw Error("Unsupported NC M-code" + Context() + ".");
                if(gs.Contains(10)&&!ended || ms.Any(m=>m is 20 or 28 or 33 or 102)&&started)throw Error("Unsupported mid-program NC setup command.");
                if(ms.Contains(28)&&words.Any(w=>w.Key is not ("N" or "M")))throw Error("Unsupported NC M28 setup block"+Context());
                if(ms.Contains(100)) { if(started)throw Error("Multiple NC programs are not supported in one file.");started=true; }
                if(ms.Contains(104)) { if(active==null)throw Error("NC M104 has no matching M103.");if(active.Count<2)throw Error("Empty NC burn path.");active=null; }
                if(ms.Contains(101)) { if(!started||active!=null)throw Error("NC program is incomplete.");ended=true; }
                if(gs.Contains(0))motion=0;
                if(gs.Contains(1))motion=1;
                if(gs.Contains(2))motion=2;
                if(gs.Contains(3))motion=3;
                bool axes=words.Any(w=>w.Key=="X"||w.Key=="Y"),arcParameters=words.Any(w=>w.Key is "I" or "J" or "R");
                if(axes||arcParameters||gs.Any(g=>g is 2 or 3))
                {
                    if(!started)throw Error("NC motion before M100.");
                    double nx=words.Any(w=>w.Key=="X")?words.Single(w=>w.Key=="X").Value:x;
                    double ny=words.Any(w=>w.Key=="Y")?words.Single(w=>w.Key=="Y").Value:y;
                    if(motion is 2 or 3)
                    {
                        if(!located)throw Error("NC arc has no known starting position"+Context());
                        double? Optional(string key)=>words.Any(w=>w.Key==key)?words.Single(w=>w.Key==key).Value:null;
                        var curve=ArcPoints(new PointD(x,y),new PointD(nx,ny),motion==2,Optional("I"),Optional("J"),Optional("R"),Context());
                        if(active!=null)
                        {
                            var global=new PathD(new[]{new PointD(x+ox,y+oy)}.Concat(curve.Select(p=>new PointD(p.x+ox,p.y+oy))));
                            active.AddRange(global.Skip(1));
                            if(!marking)document.CutCurves.Add(global);
                        }
                        document.ArcCount++;if(Math.Abs(nx-x)<1e-8&&Math.Abs(ny-y)<1e-8)document.FullCircleCount++;
                    }
                    else
                    {
                        if(arcParameters)throw Error("I/J/R parameters require G02 or G03"+Context());
                        if(active!=null) { if(motion!=1)throw Error("Rapid NC motion with the laser active.");active.Add(new PointD(nx+ox,ny+oy)); }
                    }
                    x=nx;y=ny;located=true;
                }
                if(ms.Contains(103))
                {
                    if(!started||!located||active!=null)throw Error("Invalid NC M103 start at line " + lineNumber + ".");
                    if(words.Any(w=>w.Key=="A"&&w.Value!=0))throw Error("Unsupported NC M103 A mode.");
                    active=new PathD{new PointD(x+ox,y+oy)};
                    marking=words.Any(w=>w.Key=="A");
                    (marking?document.Marks:document.Cuts).Add(active);
                }
                else if(words.Any(w=>w.Key=="A"))throw Error("Unsupported NC A-axis transform.");
                if(document.Cuts.Count+document.Marks.Count>50000)throw Error("NC path limit exceeded.");
            }
            if(!started||!ended||active!=null||document.Cuts.Count==0)throw Error("NC cutting program must have complete M100/M101 and M103/M104 paths.");
            // Every placement must be represented by cutting data in this pair.
            var segments=Segments(document.Cuts).ToList();
            foreach(var p in document.Placements)
                if(!segments.Any(s=>Math.Max(s.A.x,s.B.x)>=p.X-1 && Math.Min(s.A.x,s.B.x)<=p.X+p.SizeX+1
                    &&Math.Max(s.A.y,s.B.y)>=p.Y-1 && Math.Min(s.A.y,s.B.y)<=p.Y+p.SizeY+1))
                    throw Error("NC paths do not register with SY2 part " + p.Number + ".");
            Match ncMaterial=Regex.Match(document.NcText,@"M102\(([^\r\n)]+)\)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(ncMaterial.Success&&document.Reference.Material.Length>0)
            {
                // SPH/SPC/SS400 are machine-profile names. The PowerFab matched
                // parts supply the real grade; only the NC/SY2 numeric settings
                // must agree (e.g. both 4 and 5 mm PowerFab plates use 4.5).
                if(Math.Abs(AbePartNames.MachineThickness(ncMaterial.Groups[1].Value)-document.Thickness)>0.01m)
                    throw Error("NC and SY2 numeric profile thicknesses disagree.");
            }
        }

        // XY plane, absolute end points, incremental I/J centres. Positive R
        // selects the minor arc; negative R the major arc. A full circle needs
        // I/J because R with coincident end points cannot define a centre.
        internal static PathD ArcPoints(PointD start,PointD end,bool clockwise,double? i,double? j,double? r,string context="")
        {
            const double tau=2*Math.PI,chordTolerance=0.005;
            double dx=end.x-start.x,dy=end.y-start.y,chord=Math.Sqrt(dx*dx+dy*dy),cx,cy,radius;
            bool full=chord<1e-8;
            double Sweep(double x,double y)
            {
                double a=Math.Atan2(start.y-y,start.x-x),b=Math.Atan2(end.y-y,end.x-x);
                double magnitude=clockwise?a-b:b-a;
                magnitude=(magnitude%tau+tau)%tau;
                if(full)magnitude=tau;
                return clockwise?-magnitude:magnitude;
            }
            if(r.HasValue)
            {
                if(i.HasValue||j.HasValue||full||Math.Abs(r.Value)<1e-8)throw Error("Ambiguous NC radius arc"+context);
                radius=Math.Abs(r.Value);
                if(chord>2*radius+1e-7)throw Error("NC arc diameter is smaller than its chord"+context);
                double middleX=(start.x+end.x)/2,middleY=(start.y+end.y)/2;
                double height=Math.Sqrt(Math.Max(0,radius*radius-chord*chord/4));
                var candidates=new[]{new PointD(middleX-dy/chord*height,middleY+dx/chord*height),new PointD(middleX+dy/chord*height,middleY-dx/chord*height)};
                var matching=candidates.Where(c=>r.Value>0?Math.Abs(Sweep(c.x,c.y))<=Math.PI+1e-8:Math.Abs(Sweep(c.x,c.y))>=Math.PI-1e-8).ToList();
                if(matching.Count==0)throw Error("NC radius arc has no consistent centre"+context);
                var centre=matching[0];
                cx=centre.x;cy=centre.y;
            }
            else
            {
                if(!i.HasValue&&!j.HasValue)throw Error("NC arc needs I/J centre offsets or R radius"+context);
                cx=start.x+(i??0);cy=start.y+(j??0);radius=Math.Sqrt(Math.Pow(i??0,2)+Math.Pow(j??0,2));
                if(radius<1e-8)throw Error("NC arc centre equals its starting point"+context);
                double endRadius=Math.Sqrt(Math.Pow(end.x-cx,2)+Math.Pow(end.y-cy,2));
                if(Math.Abs(radius-endRadius)>0.01)throw Error("NC arc start/end radii disagree"+context);
            }
            double sweep=Sweep(cx,cy),startAngle=Math.Atan2(start.y-cy,start.x-cx);
            double maxStep=Math.Min(Math.PI/8,2*Math.Acos(Math.Clamp(1-chordTolerance/radius,-1,1)));
            int count=(int)Math.Ceiling(Math.Abs(sweep)/maxStep);
            if(count<1||count>20000)throw Error("NC arc exceeds the supported tessellation limit"+context);
            var fractions=new SortedSet<double>(Enumerable.Range(1,count).Select(n=>(double)n/count));
            for(int quadrant=0;quadrant<4;quadrant++)
            {
                double a=quadrant*Math.PI/2,distance=clockwise?startAngle-a:a-startAngle;
                distance=(distance%tau+tau)%tau;
                if(distance>1e-10&&distance<Math.Abs(sweep)-1e-10)fractions.Add(distance/Math.Abs(sweep));
            }
            var points=new PathD();
            foreach(double t in fractions)
            {
                double angle=startAngle+t*sweep;
                points.Add(t==1?new PointD(end):new PointD(cx+radius*Math.Cos(angle),cy+radius*Math.Sin(angle)));
            }
            return points;
        }

        public static AbePartMatch MatchParts(AbeNestDocument document, NestingSnapshot snapshot, IReadOnlyList<SavedNestReceipt> receipts,IReadOnlyDictionary<string,int>? pendingParts=null)
        {
            var groups=PlateSelection.Groups(snapshot,receipts);
            var candidates=groups.Where(g=>AbePartNames.AcceptsPowerFabThickness(g.ThicknessMm,document.Thickness,document.Reference.ProgramName)).SelectMany(g=>
                PlateSelection.ForMaterial(snapshot,g,new NestMaterial{Source="Purchase",Shape=g.Shape,Grade=g.Grade,Thickness=g.ThicknessMm,
                    Length=1,Width=1,Dimensions=g.ThicknessMm+"X1",Unit="MM"},new LayoutConditions()).Parts.Select(p=>(Part:p,Group:g))).ToList();
            var match=new AbePartMatch();
            var localUsed=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            foreach(var candidate in candidates)
                if(pendingParts!=null&&pendingParts.TryGetValue(LoadedNestResult.PartKey(candidate.Part),out int reserved))candidate.Part.Quantity=Math.Max(0,candidate.Part.Quantity-reserved);
            foreach(var named in document.Placements.Where(p=>!AbePartNames.IsPacker(p.Name)).GroupBy(p=>Canonical(p.Name)))
            {
                bool Named(NestPart p) => AbePartNames.Matches(named.First().Name,p.JobNumber,p.PieceMark,p.LotNumber);
                int Available(NestPart p)=>Math.Max(0,p.Quantity-(localUsed.TryGetValue(LoadedNestResult.PartKey(p),out int n)?n:0));
                var choices=candidates.Where(c=>Available(c.Part)>0&&Named(c.Part)).ToList();
                if(choices.Count==0)throw Error("ABE part " + named.First().Name + " does not match a checked part with remaining quantity in this PowerFab request.");
                if(choices.Select(c=>c.Group).Distinct().Count()!=1 || choices.Select(c=>c.Part.JobNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1)
                    throw Error("ABE part " + named.First().Name + " matches multiple jobs or materials. Select only the intended PowerFab rows.");
                if(choices.Select(c=>c.Part.PieceMark).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1)
                    throw Error("ABE part name is ambiguous after punctuation matching: " + named.First().Name + ".");
                if(choices.Any(c=>!AbePartNames.Direct(named.First().Name,c.Part.JobNumber,c.Part.PieceMark))
                    &&choices.Select(c=>c.Part.LotNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1)
                    throw Error("ABE lot number is ambiguous: " + named.First().Name + ". Select the exact PowerFab lot.");
                int copies=named.Count(), available=choices.Sum(c=>Available(c.Part));
                if(copies>available || choices.Count>1 && copies!=available)
                    throw Error("ABE quantity for " + named.First().Name + " is " + copies + "; checked PowerFab quantity is " + available + ". Select the exact assemblies/phases/lots for this nest.");
                if(match.Group!=null&&!ReferenceEquals(match.Group,choices[0].Group))throw Error("A single imported sheet cannot span different PowerFab materials.");
                match.Group=choices[0].Group;
                var queue=new Queue<AbePlacement>(named.OrderBy(p=>p.Number));
                foreach(var choice in choices)
                {
                    int take=Math.Min(Available(choice.Part),queue.Count);
                    for(int n=0;n<take;n++)match.Bindings.Add(new AbePartBinding{Placement=queue.Dequeue(),Part=choice.Part});
                    string key=LoadedNestResult.PartKey(choice.Part);localUsed[key]=(localUsed.TryGetValue(key,out int used)?used:0)+take;
                }
            }
            if(match.Bindings.Count==0)throw Error("This sheet contains packers only; no PowerFab job parts can be linked.");
            var selected=new PlateNestGroup{Shape=match.Group.Shape,Grade=match.Group.Grade,ThicknessMm=match.Group.ThicknessMm};
            foreach(var used in match.Bindings.GroupBy(b=>LoadedNestResult.PartKey(b.Part)))selected.Quantities.Add(used.Key,used.Count());
            match.Group=selected;
            document.Reference.PowerFabThicknessMm=selected.ThicknessMm;
            return match;
        }

        public static CalculatedNest CreateNcPlan(AbeNestDocument document, AbePartMatch match, NestResultPackage template,
            decimal minimumBottomRemnant, CancellationToken token) =>
            CreatePlan(document, match, template, new Dictionary<string,DxfShape>(), minimumBottomRemnant, token, true);

        public static CalculatedNest CreatePlan(AbeNestDocument document, AbePartMatch match, NestResultPackage template,
            IReadOnlyDictionary<string,DxfShape> drawings, decimal minimumBottomRemnant, CancellationToken token, bool useImportedContours = false)
        {
            if(!match.Group.Matches(template.Material)||Math.Abs(document.PowerFabThickness-template.Material.Thickness*(template.Material.Unit=="IN"?25.4m:1))>0.01m)
                throw Error("The selected sheet does not match the imported material thickness.");
            // PowerFab's matched parts are authoritative for the grade for
            // every ABE material code. Retain the legacy linkage flag on disk.
            document.Reference.MaterialConfirmed=true;
            StockGeometry.Validate(template.Material);
            var rules=new LayoutConditions{Spacing=0,Left=0,Right=0,Top=0,Bottom=0,MinimumBottomRemnant=minimumBottomRemnant};
            var plan=new CalculatedNest{Package=template,SheetLengthMm=DxfNestingEngine.Mm(template.Material.Length,template.Material.Unit),
                SheetWidthMm=DxfNestingEngine.Mm(template.Material.Width,template.Material.Unit)};
            foreach(var binding in match.Bindings.OrderBy(b=>b.Placement.Number))
            {
                token.ThrowIfCancellationRequested();
                AbePlacement p=binding.Placement;
                double l=DxfNestingEngine.Mm(binding.Part.Length,binding.Part.Unit),w=DxfNestingEngine.Mm(binding.Part.Width,binding.Part.Unit);
                DxfShape shape; Func<PointD,PointD> transform;
                if(useImportedContours)
                {
                    if(!(Math.Abs(p.OriginalX-l)<=0.5&&Math.Abs(p.OriginalY-w)<=0.5
                        ||Math.Abs(p.OriginalX-w)<=0.5&&Math.Abs(p.OriginalY-l)<=0.5))
                        throw Error("ABE and PowerFab part sizes disagree for "+p.Name+": ABE "+p.OriginalX.ToString("0.###",Culture)+" x "+p.OriginalY.ToString("0.###",Culture)
                            +" mm; PowerFab "+l.ToString("0.###",Culture)+" x "+w.ToString("0.###",Culture)+" mm.");
                    shape=AbeNcGeometry.Read(document,p,token);transform=point=>point;
                }
                else
                {
                    if(!drawings.TryGetValue(LoadedNestResult.PartKey(binding.Part),out shape!))throw Error("Verified PowerFab outline missing for " + p.Name + ".");
                    if(Math.Abs(shape.Bounds.Width-p.OriginalX)>0.5||Math.Abs(shape.Bounds.Height-p.OriginalY)>0.5)
                        throw Error("PowerFab DXF axes differ from ABE for " + p.Name + ". Use the same part drawing orientation in both systems.");
                    if(!(Math.Abs(shape.Bounds.Width-l)<=0.5&&Math.Abs(shape.Bounds.Height-w)<=0.5
                        ||Math.Abs(shape.Bounds.Width-w)<=0.5&&Math.Abs(shape.Bounds.Height-l)<=0.5))throw Error("PowerFab part dimensions disagree with its outline: " + p.Name + ".");
                    transform=p.Transform;
                }
                var outer=new PathD(shape.Outer.Select(transform));
                var placedBounds=ShapeBounds.Of(outer);
                if(Math.Abs(placedBounds.Left-p.X)>0.5||Math.Abs(placedBounds.Bottom-p.Y)>0.5
                    ||Math.Abs(placedBounds.Width-p.SizeX)>0.5||Math.Abs(placedBounds.Height-p.SizeY)>0.5)
                    throw Error("The actual PowerFab outline, SY2 position and rotated size disagree for "+p.Name+".");
                var item=new PlacedOutline{Part=binding.Part,SourceDxf=shape.Source,X=p.X,Y=p.Y,Angle=p.Angle,Outer=outer,
                    Holes=shape.Holes.Select(h=>new PathD(h.Select(transform))).ToList(),CurveTolerance=shape.ClearanceOutline.Count>0?0:shape.CurveTolerance};
                item.Clearance=shape.ClearanceOutline.Count>0?new PathsD(shape.ClearanceOutline.Select(c=>new PathD(c.Select(transform))))
                    :shape.CurveTolerance>0?Clipper.InflatePaths(new PathsD{outer},shape.CurveTolerance,JoinType.Miter,EndType.Polygon,50,4,0.005):new PathsD{outer};
                RegisterOutline(item,document,token);
                plan.Placed.Add(item);plan.NetPartAreaMm+=shape.Area;
            }
            var packers=ReadPackers(document,token);
            plan.Placed.AddRange(packers);DxfNestingEngine.VerifyPlan(plan,rules);plan.Placed.RemoveRange(plan.Placed.Count-packers.Count,packers.Count);
            template.LayoutSource="ABE Planner NC/SY2 import via Hanlon v1.12";
            template.LayoutConditions=rules;template.Status="completed";template.NestId=Guid.NewGuid().ToString("N");
            template.Parts=plan.Placed.GroupBy(p=>LoadedNestResult.PartKey(p.Part)).Select(g=>Copy(g.First().Part,g.Count())).ToList();
            template.AbeImport=document.Reference;
            document.Reference.GeometrySource=useImportedContours?"ABE NC/SY2 cutting paths":"PowerFab DXF/NC1 registered to ABE";
            template.NestName=NestNaming.ForAbe(template);
            document.Reference.PowerFabThicknessMm=(decimal)DxfNestingEngine.Mm(template.Material.Thickness,template.Material.Unit);
            // Exclude packers, lead-ins, common cuts and skeleton cuts from future stock.
            var temporary=document.Cuts.Select(c=>new PlacedOutline{Outer=c}).Concat(packers).ToList();
            plan.Placed.AddRange(temporary);template.Remnants=RemnantGeometry.Create(plan,rules);plan.Placed.RemoveRange(plan.Placed.Count-temporary.Count,temporary.Count);
            return plan;
        }

        internal static List<PlacedOutline> ReadPackers(AbeNestDocument document,CancellationToken token)
        {
            var packers=new List<PlacedOutline>();
            document.Reference.Packers=new List<AbePackerReference>();
            foreach(var p in document.Placements.Where(p=>AbePartNames.IsPacker(p.Name)))
            {
                token.ThrowIfCancellationRequested();
                if(AbePartNames.PackerSize(p.Name,out double px,out double py)
                    &&(Math.Abs(px-p.OriginalX)>0.5||Math.Abs(py-p.OriginalY)>0.5))throw Error("Packer name and SY2 size disagree: "+p.Name);
                var outer=new PathD(new[]{new PointD(0,0),new PointD(p.OriginalX,0),new PointD(p.OriginalX,p.OriginalY),new PointD(0,p.OriginalY)}.Select(p.Transform));
                var filler=new PlacedOutline{Part=new NestPart{PieceMark=p.Name},X=p.X,Y=p.Y,Angle=p.Angle,Outer=outer,Clearance=new PathsD{outer}};
                RegisterOutline(filler,document,token);packers.Add(filler);
                document.Reference.Packers.Add(new AbePackerReference{SourceNumber=p.Number,Name=p.Name,X=p.X,Y=p.Y,Rotation=p.Angle,Length=p.SizeX,Width=p.SizeY});
            }
            return packers;
        }

        private static void RegisterOutline(PlacedOutline part,AbeNestDocument doc,CancellationToken token)
        {
            ShapeBounds b=part.Bounds;
            var cuts=Segments(doc.Cuts).Where(s=>Math.Max(s.A.x,s.B.x)>=b.Left-6&&Math.Min(s.A.x,s.B.x)<=b.Right+6
                &&Math.Max(s.A.y,s.B.y)>=b.Bottom-6&&Math.Min(s.A.y,s.B.y)<=b.Top+6).ToArray();
            foreach(PathD loop in new[]{part.Outer}.Concat(part.Holes))
            {
                double total=0,covered=0;
                for(int i=0;i<loop.Count;i++)
                {
                    token.ThrowIfCancellationRequested();PointD a=loop[i],z=loop[(i+1)%loop.Count];
                    double length=Math.Sqrt((a.x-z.x)*(a.x-z.x)+(a.y-z.y)*(a.y-z.y));int n=Math.Max(1,(int)Math.Ceiling(length/2));
                    for(int j=0;j<n;j++)
                    {
                        double t=(j+0.5)/n;var p=new PointD(a.x+t*(z.x-a.x),a.y+t*(z.y-a.y));total+=length/n;
                        if(cuts.Any(s=>DistanceSquared(p,s.A,s.B)<=1))covered+=length/n;
                    }
                }
                if(total<=0 || covered/total<0.90)throw Error("PowerFab outline does not register with the NC cutting paths for " + part.Part.PieceMark + ". The preview remains available; this layout cannot be saved as verified geometry.");
            }
        }

        internal static IEnumerable<(PointD A,PointD B)> Segments(IEnumerable<PathD> paths)
        { foreach(var path in paths)for(int i=1;i<path.Count;i++)yield return(path[i-1],path[i]); }
        private static double DistanceSquared(PointD p,PointD a,PointD b)
        { double x=b.x-a.x,y=b.y-a.y,d=x*x+y*y,t=d==0?0:Math.Clamp(((p.x-a.x)*x+(p.y-a.y)*y)/d,0,1);return Math.Pow(p.x-a.x-t*x,2)+Math.Pow(p.y-a.y-t*y,2); }
        private static NestPart Copy(NestPart p,int quantity)=>new NestPart{JobNumber=p.JobNumber,MainMark=p.MainMark,PieceMark=p.PieceMark,
            Phase=p.Phase,Sequence=p.Sequence,LotNumber=p.LotNumber,Length=p.Length,Width=p.Width,Unit=p.Unit,Quantity=quantity};
        private static string Field(Dictionary<string,string> d,string key)=>d.TryGetValue(key,out string? v)?v:throw Error("Missing SY2 field " + key + ".");
        private static int Integer(Dictionary<string,string> d,string key)=>int.TryParse(Field(d,key),NumberStyles.None,Culture,out int n)?n:throw Error("Invalid SY2 integer " + key + ".");
        private static double Number(Dictionary<string,string> d,string key)=>ParseNumber(Field(d,key));
        private static double ParseNumber(string text)=>double.TryParse(text,NumberStyles.Float,Culture,out double n)&&double.IsFinite(n)&&Math.Abs(n)<=1000000?n:throw Error("Invalid or excessive coordinate " + text + ".");
        private static InvalidDataException Error(string text)=>new InvalidDataException("ABE import: " + text);

        public static string PreviewSvg(AbeNestDocument doc)
        {
            double length=Math.Max(1,doc.Bounds.Right+15),width=Math.Max(1,doc.Bounds.Top+15),scale=2280/length;
            var s=new StringBuilder();string N(double x)=>x.ToString("0.###",Culture);
            s.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2400\" height=\"").Append(N(width*scale+120)).Append("\" viewBox=\"0 0 2400 ").Append(N(width*scale+120)).Append("\"><title>ABE layout</title><desc>Physical sheet size unconfirmed. Original NC cutting and marking paths.</desc><rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
            string Points(PathD p)=>string.Join(" ",p.Select(v=>N(60+v.x*scale)+","+N(60+(width-v.y)*scale)));
            foreach(var p in doc.Cuts)s.Append("<polyline points=\"").Append(Points(p)).Append("\" fill=\"none\" stroke=\"#0a2f4e\" stroke-width=\"1.2\"/>");
            foreach(var p in doc.Marks)s.Append("<polyline points=\"").Append(Points(p)).Append("\" fill=\"none\" stroke=\"#c87620\" stroke-width=\"0.8\"/>");
            return s.Append("</svg>").ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal readonly struct ShapeBounds
    {
        public readonly double Left, Bottom, Right, Top;
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public ShapeBounds(double left, double bottom, double right, double top)
        { Left = left; Bottom = bottom; Right = right; Top = top; }
        public static ShapeBounds Of(IEnumerable<PointD> points)
        {
            var p = points.ToArray();
            return new ShapeBounds(p.Min(x => x.x), p.Min(x => x.y), p.Max(x => x.x), p.Max(x => x.y));
        }
    }

    internal sealed class DxfShape
    {
        public string Source { get; set; } = string.Empty;
        public PathD Outer { get; set; } = new PathD();
        public List<PathD> Holes { get; set; } = new List<PathD>();
        public PathsD ClearanceOutline { get; set; } = new PathsD();
        // Sampling allowance for the external boundary only. Curved holes do
        // not enlarge the plate or cause adjacent straight edges to overlap.
        public double CurveTolerance { get; set; }
        public ShapeBounds Bounds => ShapeBounds.Of(Outer);
        public double Area => Math.Abs(Clipper.Area(Outer)) - Holes.Sum(p => Math.Abs(Clipper.Area(p)));
        public bool AssumedMillimetres { get; set; }
    }

    internal static class DxfGeometry
    {
        public const double CurveToleranceMm = 0.02;
        private const double JoinTolerance = 0.01;
        private readonly record struct Pair(int Code, string Value);
        private sealed class Entity
        {
            public string Kind = string.Empty;
            public List<Pair> Data = new List<Pair>();
            public double N(int code, double fallback = 0)
            {
                string? value = Data.FirstOrDefault(p => p.Code == code).Value;
                return value == null ? fallback : ReadNumber(value);
            }
        }
        private readonly record struct Vertex(PointD Point, double Bulge);

        public static DxfShape ReadFile(string file) => Read(File.ReadAllText(file), file);

        public static DxfShape Read(string text, string source)
        {
            if (text.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
                throw new InvalidDataException("Export an ASCII 2D DXF for " + source + ". Binary DXF is not supported.");
            string[] lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int length = lines.Length;
            while (length > 0 && string.IsNullOrWhiteSpace(lines[length - 1])) length--;
            if (length % 2 != 0) throw new InvalidDataException("DXF group/value pairs are incomplete: " + source);
            if (length > 2000000) throw new InvalidDataException("DXF is too large for this first nesting version: " + source);
            var pairs = new List<Pair>(length / 2);
            for (int i = 0; i < length; i += 2)
            {
                if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    throw new InvalidDataException("Invalid DXF group code near line " + (i + 1) + ": " + source);
                pairs.Add(new Pair(code, lines[i + 1].Trim()));
            }
            int units = 0;
            for (int i = 0; i + 1 < pairs.Count; i++)
                if (pairs[i].Code == 9 && pairs[i].Value == "$INSUNITS")
                    units = (int)ReadNumber(pairs[i + 1].Value);
            double scale = units switch { 0 or 4 => 1, 1 => 25.4, 2 => 304.8, 5 => 10, 6 => 1000,
                _ => throw new InvalidDataException("Unsupported DXF insertion units " + units + ": " + source) };
            var entities = new List<Entity>();
            string section = string.Empty;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Code != 0) continue;
                if (pairs[i].Value == "SECTION" && i + 1 < pairs.Count && pairs[i + 1].Code == 2)
                { section = pairs[i + 1].Value; continue; }
                if (pairs[i].Value == "ENDSEC") { section = string.Empty; continue; }
                if (section != "ENTITIES") continue;
                var entity = new Entity { Kind = pairs[i].Value };
                while (i + 1 < pairs.Count && pairs[i + 1].Code != 0) entity.Data.Add(pairs[++i]);
                entities.Add(entity);
            }
            var loops = new List<PathD>();
            var chains = new List<PathD>();
            var curvePoints = new HashSet<(double X, double Y)>();
            var curveSegments = new List<PathD>();
            PathD TrackCurve(PathD path)
            { foreach (PointD p in path) curvePoints.Add((p.x, p.y)); curveSegments.Add(path); return path; }
            bool curves = false;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity e = entities[i];
                CheckPlane(e, source);
                switch (e.Kind)
                {
                    case "LINE":
                        if (Math.Abs(e.N(30) - e.N(31)) > 0.000001) throw new InvalidDataException("3D linework is not supported: " + source);
                        chains.Add(new PathD { new PointD(e.N(10) * scale, e.N(20) * scale), new PointD(e.N(11) * scale, e.N(21) * scale) });
                        break;
                    case "ARC":
                        curves = true;
                        double start = e.N(50) * Math.PI / 180, sweep = (e.N(51) - e.N(50)) * Math.PI / 180;
                        while (sweep <= 0) sweep += Math.PI * 2;
                        chains.Add(TrackCurve(Arc(new PointD(e.N(10) * scale, e.N(20) * scale), e.N(40) * scale, start, sweep)));
                        break;
                    case "CIRCLE":
                        curves = true;
                        loops.Add(TrackCurve(Arc(new PointD(e.N(10) * scale, e.N(20) * scale), e.N(40) * scale, 0, Math.PI * 2)));
                        break;
                    case "LWPOLYLINE":
                        var vertices = new List<Vertex>();
                        for (int p = 0; p < e.Data.Count; p++)
                        {
                            if (e.Data[p].Code != 10) continue;
                            double x = ReadNumber(e.Data[p].Value), y = double.NaN, bulge = 0;
                            while (p + 1 < e.Data.Count && e.Data[p + 1].Code != 10)
                            {
                                Pair next = e.Data[++p];
                                if (next.Code == 20) y = ReadNumber(next.Value);
                                if (next.Code == 42) bulge = ReadNumber(next.Value);
                            }
                            if (double.IsNaN(y)) throw new InvalidDataException("DXF polyline vertex has no Y coordinate: " + source);
                            vertices.Add(new Vertex(new PointD(x * scale, y * scale), bulge));
                        }
                        AddPolyline(vertices, ((int)e.N(70) & 1) != 0, loops, chains, ref curves, curvePoints, curveSegments);
                        break;
                    case "POLYLINE":
                        int flags = (int)e.N(70);
                        if ((flags & (2 | 4 | 8 | 16 | 64)) != 0) throw new InvalidDataException("Explode/convert fitted or 3D polylines to 2D geometry: " + source);
                        var oldVertices = new List<Vertex>();
                        while (i + 1 < entities.Count && entities[i + 1].Kind == "VERTEX")
                        {
                            Entity v = entities[++i]; CheckPlane(v, source);
                            if (Math.Abs(v.N(30) - e.N(30)) > 0.000001) throw new InvalidDataException("Polyline vertices must share a 2D plane: " + source);
                            oldVertices.Add(new Vertex(new PointD(v.N(10) * scale, v.N(20) * scale), v.N(42)));
                        }
                        if (i + 1 < entities.Count && entities[i + 1].Kind == "SEQEND") i++;
                        AddPolyline(oldVertices, (flags & 1) != 0, loops, chains, ref curves, curvePoints, curveSegments);
                        break;
                    case "TEXT": case "MTEXT": case "DIMENSION": case "POINT": case "SEQEND":
                        break; // annotations are not cut contours
                    default:
                        throw new InvalidDataException("Unsupported DXF entity " + e.Kind + " in " + source
                            + ". Explode blocks and convert curves to supported 2D outlines before nesting.");
                }
            }
            JoinChains(chains, loops, source);
            loops = loops.Select(Clean).Where(p => p.Count >= 3).ToList();
            if (loops.Count == 0) throw new InvalidDataException("No closed cut outline was found: " + source);
            if (loops.Sum(p => p.Count) > 20000) throw new InvalidDataException("DXF contours have too many vertices: " + source);
            foreach (PathD loop in loops)
            {
                double area = Math.Abs(Clipper.Area(loop));
                PathsD clean = Clipper.Union(new PathsD { loop }, new PathsD(), FillRule.NonZero, 4);
                if (area < 0.0001 || clean.Count != 1 || Math.Abs(Math.Abs(Clipper.Area(clean)) - area) > 0.01)
                    throw new InvalidDataException("Self-intersecting or degenerate cut outline: " + source);
            }
            loops = loops.OrderByDescending(p => Math.Abs(Clipper.Area(p))).ToList();
            PathD outer = loops[0];
            if (!Clipper.IsPositive(outer)) outer.Reverse();
            var holes = loops.Skip(1).ToList();
            foreach (PathD hole in holes)
            {
                if (hole.Any(p => Clipper.PointInPolygon(p, outer, 4) != PointInPolygonResult.IsInside)
                    || Math.Abs(Clipper.Area(Clipper.Difference(new PathsD { hole }, new PathsD { outer }, FillRule.NonZero, 4))) > 0.0001)
                    throw new InvalidDataException("DXF must contain one outer part outline and internal holes; disconnected or touching contours were found: " + source);
                if (Clipper.IsPositive(hole)) hole.Reverse();
            }
            for (int a = 0; a < holes.Count; a++)
                for (int b = a + 1; b < holes.Count; b++)
                    if (Math.Abs(Clipper.Area(Clipper.Intersect(new PathsD { holes[a] }, new PathsD { holes[b] }, FillRule.NonZero, 4))) > 0.0001)
                        throw new InvalidDataException("Overlapping or nested hole contours require a cleaned single-part DXF: " + source);
            ShapeBounds bounds = ShapeBounds.Of(outer);
            PathD Normalize(PathD path) => new PathD(path.Select(p => new PointD(p.x - bounds.Left, p.y - bounds.Bottom)));
            var shape = new DxfShape { Source = source, Outer = Normalize(outer), Holes = holes.Select(Normalize).ToList(),
                CurveTolerance = curves && outer.Any(p => curvePoints.Contains((p.x, p.y))) ? CurveToleranceMm : 0, AssumedMillimetres = units == 0 };
            var outlinePoints = outer.Select(p => (p.x,p.y)).ToHashSet();
            var envelope = new PathsD { outer };
            // Only curved external segments need a sampling allowance. In ABE
            // common-line layouts, extending every straight edge invents overlap.
            foreach (PathD arc in curveSegments.Where(a => a.Any(p => outlinePoints.Contains((p.x,p.y)))))
                envelope.AddRange(Clipper.InflatePaths(new PathsD { arc }, CurveToleranceMm, JoinType.Round, EndType.Round, 2, 4, 0.005));
            var box = new PathD { new PointD(bounds.Left,bounds.Bottom),new PointD(bounds.Right,bounds.Bottom),
                new PointD(bounds.Right,bounds.Top),new PointD(bounds.Left,bounds.Top) };
            shape.ClearanceOutline = Clipper.Intersect(Clipper.Union(envelope,new PathsD(),FillRule.NonZero,4),new PathsD { box },FillRule.NonZero,4);
            shape.ClearanceOutline = new PathsD(shape.ClearanceOutline.Select(Normalize));
            if (shape.Area <= 0) throw new InvalidDataException("The DXF has no positive retained part area: " + source);
            return shape;
        }

        private static void AddPolyline(List<Vertex> vertices, bool closed, List<PathD> loops, List<PathD> chains, ref bool curves,
            HashSet<(double X, double Y)> curvePoints, List<PathD> curveSegments)
        {
            if (vertices.Count < 2) throw new InvalidDataException("A DXF polyline has fewer than two vertices.");
            if (!closed && Distance(vertices[0].Point, vertices[^1].Point) <= JoinTolerance) closed = true;
            var path = new PathD();
            int segments = closed ? vertices.Count : vertices.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vertex first = vertices[i], next = vertices[(i + 1) % vertices.Count];
                if (Math.Abs(first.Bulge) < 1e-12) { path.Add(first.Point); continue; }
                curves = true;
                double chord = Distance(first.Point, next.Point), b = first.Bulge;
                if (chord < 1e-10) continue;
                double offset = chord * (1 - b * b) / (4 * b);
                var center = new PointD((first.Point.x + next.Point.x) / 2 - (next.Point.y - first.Point.y) / chord * offset,
                    (first.Point.y + next.Point.y) / 2 + (next.Point.x - first.Point.x) / chord * offset);
                double radius = chord * (1 + b * b) / (4 * Math.Abs(b));
                PathD arc = Arc(center, radius, Math.Atan2(first.Point.y - center.y, first.Point.x - center.x), 4 * Math.Atan(b));
                foreach (PointD p in arc) curvePoints.Add((p.x, p.y));
                curveSegments.Add(arc);
                path.AddRange(arc.Take(arc.Count - 1));
            }
            if (!closed) path.Add(vertices[^1].Point);
            (closed ? loops : chains).Add(path);
        }

        private static PathD Arc(PointD center, double radius, double start, double sweep)
        {
            if (radius <= 0 || !double.IsFinite(radius) || Math.Abs(sweep) > Math.PI * 2 + 1e-7)
                throw new InvalidDataException("DXF arc/circle has an invalid radius or sweep.");
            double step = Math.Min(Math.PI / 18, 2 * Math.Acos(Math.Max(-1, Math.Min(1, 1 - CurveToleranceMm / radius))));
            int count = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / Math.Max(1e-6, step)));
            if (count > 10000) throw new InvalidDataException("DXF curve is too large or detailed for this first version.");
            var times = new SortedSet<double>(Enumerable.Range(0, count + 1).Select(i => (double)i / count));
            for (int k = -12; k <= 12; k++)
            {
                double t = (k * Math.PI / 2 - start) / sweep;
                if (t > 0 && t < 1) times.Add(t);
            }
            return new PathD(times.Select(t => new PointD(center.x + radius * Math.Cos(start + t * sweep), center.y + radius * Math.Sin(start + t * sweep))));
        }

        private static void JoinChains(List<PathD> input, List<PathD> loops, string source)
        {
            var chains = input.Where(p => p.Count >= 2 && Distance(p[0], p[^1]) > 1e-10).ToList();
            foreach (PathD p in input.Where(p => p.Count >= 3 && Distance(p[0], p[^1]) <= 1e-10)) loops.Add(p);
            while (chains.Count > 0)
            {
                var path = new PathD(chains[0]); chains.RemoveAt(0);
                while (Distance(path[0], path[^1]) > JoinTolerance)
                {
                    var matches = chains.Select((p, i) => new { Path = p, Index = i,
                        Forward = Distance(path[^1], p[0]) <= JoinTolerance, Reverse = Distance(path[^1], p[^1]) <= JoinTolerance })
                        .Where(m => m.Forward || m.Reverse).ToList();
                    if (matches.Count != 1) throw new InvalidDataException("Open or branching LINE/ARC cut contour in " + source + ". Join/clean the outline first.");
                    var match = matches[0];
                    var segment = new PathD(match.Path);
                    if (!match.Forward) segment.Reverse();
                    path.AddRange(segment.Skip(1)); chains.RemoveAt(match.Index);
                }
                loops.Add(path);
            }
        }

        private static PathD Clean(PathD path)
        {
            var result = new PathD();
            foreach (PointD p in path) if (result.Count == 0 || Distance(p, result[^1]) > 1e-8) result.Add(p);
            if (result.Count > 1 && Distance(result[0], result[^1]) <= JoinTolerance) result.RemoveAt(result.Count - 1);
            return result;
        }
        private static void CheckPlane(Entity e, string source)
        {
            if (Math.Abs(e.N(210)) > 1e-8 || Math.Abs(e.N(220)) > 1e-8 || Math.Abs(e.N(230, 1) - 1) > 1e-8)
                throw new InvalidDataException("Only default-plane 2D DXFs are supported; flatten the geometry: " + source);
        }
        private static double ReadNumber(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n))
                throw new InvalidDataException("Invalid numeric DXF value: " + value);
            return n;
        }
        internal static double Distance(PointD a, PointD b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
    }
}

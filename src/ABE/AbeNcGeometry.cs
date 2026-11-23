using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    // Recover closed faces from the actual burn paths inside one SY2 placement.
    // Only short, unambiguous perimeter micro-joints may close an NC contour.
    internal static class AbeNcGeometry
    {
        private const double Join = 0.01;
        private sealed class Segment
        {
            public PointD A, B;
            public List<double> Splits = new List<double> { 0, 1 };
            public Segment(PointD a, PointD b) { A = a; B = b; }
        }

        public static DxfShape Read(AbeNestDocument document, AbePlacement placement, CancellationToken token)
        {
            var bounds = placement.Bounds;
            var lines = new List<Segment>();
            foreach (var edge in AbeNestImport.Segments(document.Cuts))
            {
                token.ThrowIfCancellationRequested();
                if (Clip(edge.A, edge.B, bounds, out PointD a, out PointD b)
                    && Distance2(a, b) > 0.00000001) lines.Add(new Segment(a, b));
            }
            if (lines.Count > 5000) throw Error(placement, "too many local cutting segments to verify");
            for (int i = 0; i < lines.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                for (int j = i + 1; j < lines.Count; j++) Node(lines[i], lines[j]);
            }

            var points = new List<PointD>();
            var buckets = new Dictionary<(long, long), List<int>>();
            int FindVertex(PointD p)
            {
                long x = (long)Math.Floor(p.x / Join), y = (long)Math.Floor(p.y / Join);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                    if (buckets.TryGetValue((x + dx, y + dy), out var near))
                        foreach (int id in near) if (Distance2(points[id], p) <= Join * Join) return id;
                return -1;
            }
            int Vertex(PointD p)
            {
                int found = FindVertex(p); if (found >= 0) return found;
                long x = (long)Math.Floor(p.x / Join), y = (long)Math.Floor(p.y / Join);
                int index = points.Count; points.Add(p);
                if (!buckets.TryGetValue((x, y), out var list)) buckets[(x, y)] = list = new List<int>();
                list.Add(index); return index;
            }
            var edges = new HashSet<(int, int)>();
            foreach (var line in lines)
            {
                var splits = line.Splits.OrderBy(t => t).ToArray();
                for (int i = 1; i < splits.Length; i++)
                {
                    int a = Vertex(At(line, splits[i - 1])), b = Vertex(At(line, splits[i]));
                    if (a != b) edges.Add(a < b ? (a, b) : (b, a));
                }
            }
            var neighbours = Enumerable.Range(0, points.Count).Select(_ => new List<int>()).ToArray();
            foreach (var (a, b) in edges) { neighbours[a].Add(b); neighbours[b].Add(a); }
            // AMADA cuts leave short holding micro-joints on straight, angled
            // and circular edges. A joint can end at the start of another cut,
            // where its lead-in is still attached. Require a unique pairing
            // within 2 mm; never complete a missing side or choose a branch.
            var ends = Enumerable.Range(0, points.Count).Where(i => neighbours[i].Count == 1).ToArray();
            var anchors = document.Cuts.SelectMany(c => c.Take(2)).Where(p => p.x >= bounds.Left - Join
                && p.x <= bounds.Right + Join && p.y >= bounds.Bottom - Join && p.y <= bounds.Top + Join)
                .Select(FindVertex).Where(i => i >= 0).Concat(ends).Distinct().ToArray();
            var joins = ends.ToDictionary(a => a, a => anchors.Where(b => a != b && !neighbours[a].Contains(b)
                && Distance2(points[a], points[b]) <= 2.01 * 2.01).ToArray());
            foreach (int a in ends)
                if (joins[a].Length == 1 && ends.Count(e => joins[e].Contains(joins[a][0])) == 1)
                {
                    int b = joins[a][0];
                    if (edges.Add(a < b ? (a, b) : (b, a))) { neighbours[a].Add(b); neighbours[b].Add(a); }
                }
            for (int i = 0; i < points.Count; i++)
            {
                PointD origin = points[i];
                neighbours[i].Sort((a, b) => Math.Atan2(points[a].y - origin.y, points[a].x - origin.x)
                    .CompareTo(Math.Atan2(points[b].y - origin.y, points[b].x - origin.x)));
            }
            var visited = new HashSet<(int, int)>();
            var faces = new List<PathD>();
            foreach (var edge in edges)
                foreach (var start in new[] { edge, (edge.Item2, edge.Item1) })
                {
                    token.ThrowIfCancellationRequested();
                    if (visited.Contains(start)) continue;
                    var walk = new PathD(); var current = start;
                    bool closed = false;
                    for (int step = 0; step <= edges.Count * 2; step++)
                    {
                        if (!visited.Add(current)) break;
                        int a = current.Item1, b = current.Item2;
                        walk.Add(points[a]);
                        var adjacent = neighbours[b];
                        int reverse = adjacent.IndexOf(a);
                        current = (b, adjacent[(reverse + adjacent.Count - 1) % adjacent.Count]);
                        if (current == start) { closed = true; break; }
                    }
                    if (!closed || walk.Count < 3 || Clipper.Area(walk) <= 0.0001) continue;
                    foreach (PathD face in Clipper.Union(new PathsD { walk }, new PathsD(), FillRule.NonZero, 4))
                        if (Clipper.Area(face) > 0.0001) faces.Add(face);
                }
            bool MatchesBounds(PathD path)
            {
                var b = ShapeBounds.Of(path);
                return Math.Abs(b.Left - bounds.Left) <= 0.05 && Math.Abs(b.Bottom - bounds.Bottom) <= 0.05
                    && Math.Abs(b.Right - bounds.Right) <= 0.05 && Math.Abs(b.Top - bounds.Top) <= 0.05;
            }
            var candidates = faces.Where(MatchesBounds).ToList();
            if (candidates.Count != 1) throw Error(placement, candidates.Count == 0
                ? "the NC cuts do not form a closed part outline matching its SY2 bounds"
                : "more than one NC outline matches its SY2 bounds");
            PathD outer = candidates[0];
            var holes = faces.Where(f => !ReferenceEquals(f, outer)
                && f.All(p => Clipper.PointInPolygon(p, outer, 4) == PointInPolygonResult.IsInside)).ToList();
            for (int i = 0; i < holes.Count; i++)
                for (int j = i + 1; j < holes.Count; j++)
                    if (Math.Abs(Clipper.Area(Clipper.Intersect(new PathsD { holes[i] }, new PathsD { holes[j] }, FillRule.NonZero, 4))) > 0.0001)
                        throw Error(placement, "the NC cuts contain overlapping or nested internal contours");
            foreach (PathD hole in holes) if (Clipper.IsPositive(hole)) hole.Reverse();
            var outlineVertices = outer.Select(FindVertex).Where(i => i >= 0).ToHashSet();
            var curves = document.CutCurves.Where(c => c.Any(p => outlineVertices.Contains(FindVertex(p)))).ToList();
            var envelope = new PathsD { outer };
            foreach (PathD curve in curves)
                envelope.AddRange(Clipper.InflatePaths(new PathsD { curve }, 0.005, JoinType.Round, EndType.Round, 2, 4, 0.005));
            var box = new PathD { new PointD(bounds.Left,bounds.Bottom),new PointD(bounds.Right,bounds.Bottom),
                new PointD(bounds.Right,bounds.Top),new PointD(bounds.Left,bounds.Top) };
            // Preserve common straight cuts; only sampled curved edges receive
            // the NC reader's 0.005 mm approximation allowance.
            var clearance = Clipper.Intersect(Clipper.Union(envelope,new PathsD(),FillRule.NonZero,4),new PathsD { box },FillRule.NonZero,4);
            return new DxfShape { Source = "ABE NC/SY2: " + document.Reference.ProgramName + " / " + placement.Name,
                Outer = outer, Holes = holes, CurveTolerance = curves.Count > 0 ? 0.005 : 0, ClearanceOutline = clearance };
        }

        private static void Node(Segment a, Segment b)
        {
            if (Math.Max(a.A.x, a.B.x) + Join < Math.Min(b.A.x, b.B.x)
                || Math.Max(b.A.x, b.B.x) + Join < Math.Min(a.A.x, a.B.x)
                || Math.Max(a.A.y, a.B.y) + Join < Math.Min(b.A.y, b.B.y)
                || Math.Max(b.A.y, b.B.y) + Join < Math.Min(a.A.y, a.B.y)) return;
            double ax = a.B.x - a.A.x, ay = a.B.y - a.A.y;
            double bx = b.B.x - b.A.x, by = b.B.y - b.A.y;
            double cross = ax * by - ay * bx;
            if (Math.Abs(cross) > 1e-10)
            {
                double dx = b.A.x - a.A.x, dy = b.A.y - a.A.y;
                double t = (dx * by - dy * bx) / cross, u = (dx * ay - dy * ax) / cross;
                if (t >= 0 && t <= 1 && u >= 0 && u <= 1) { a.Splits.Add(t); b.Splits.Add(u); }
            }
            Project(a.A, b); Project(a.B, b); Project(b.A, a); Project(b.B, a);
        }
        private static void Project(PointD p, Segment line)
        {
            double x = line.B.x - line.A.x, y = line.B.y - line.A.y;
            double t = ((p.x - line.A.x) * x + (p.y - line.A.y) * y) / (x * x + y * y);
            if (t >= 0 && t <= 1 && Distance2(p, At(line, t)) <= Join * Join) line.Splits.Add(t);
        }
        private static PointD At(Segment line, double t) => new PointD(line.A.x + (line.B.x - line.A.x) * t, line.A.y + (line.B.y - line.A.y) * t);
        private static double Distance2(PointD a, PointD b) => (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y);
        private static bool Clip(PointD a, PointD b, ShapeBounds bounds, out PointD start, out PointD end)
        {
            double low = 0, high = 1, dx = b.x - a.x, dy = b.y - a.y;
            bool Side(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= -0.000001;
                double t = q / p;
                if (p < 0) low = Math.Max(low, t); else high = Math.Min(high, t);
                return low <= high;
            }
            start = end = default;
            if (!Side(-dx, a.x - bounds.Left + Join) || !Side(dx, bounds.Right - a.x + Join)
                || !Side(-dy, a.y - bounds.Bottom + Join) || !Side(dy, bounds.Top - a.y + Join)) return false;
            double Snap(double n, double left, double right) => Math.Abs(n - left) <= Join + 0.000001 ? left
                : Math.Abs(n - right) <= Join + 0.000001 ? right : n;
            PointD SnapPoint(double t) => new PointD(Snap(a.x + dx * t, bounds.Left, bounds.Right), Snap(a.y + dy * t, bounds.Bottom, bounds.Top));
            start = SnapPoint(low); end = SnapPoint(high); return true;
        }
        private static InvalidDataException Error(AbePlacement p, string message) =>
            new InvalidDataException("ABE import: Cannot verify " + p.Name + " at SY2 part " + p.Number + ": " + message + ".");
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hanlon.PowerFab.Nesting
{
    internal static class RemnantGeometry
    {
        // Keep one reusable rectangle: the right strip or the bottom strip.
        // Small bottom holding margins remain excluded by the configured minimum.
        public static List<NestRemnant> Create(CalculatedNest plan, LayoutConditions conditions)
        {
            if (plan.Placed.Count == 0) return new List<NestRemnant>();
            decimal length = Down(plan.SheetLengthMm), width = Down(plan.SheetWidthMm);
            decimal cutX = Up(plan.Placed.Max(p => p.Bounds.Right + p.CurveTolerance) + (double)conditions.Spacing);
            decimal cutY = Down(plan.Placed.Min(p => p.Bounds.Bottom - p.CurveTolerance) - (double)conditions.Spacing);
            decimal rightMinimum = Math.Max(conditions.Right, 1m);
            decimal bottomMinimum = Math.Max(Math.Max(conditions.MinimumBottomRemnant, conditions.Bottom), 1m);
            string id = NumericExternalId(plan.Package.NestId, 1), name = NestNaming.Display(plan.Package);
            NestRemnant? Candidate(decimal cut, bool right)
            {
                decimal x = right ? cut : 0, height = right ? width : cut;
                decimal remaining = right ? length-cut : height;
                if (remaining <= (right ? rightMinimum : bottomMinimum) || height <= 0) return null;
                var rectangle = new NestRemnant { RemnantId = id, Name = name, Unit = "MM", Edge = right ? "Right" : "Bottom",
                    X = x, Y = 0, Length = length-x, Width = height, Area = (length-x)*height,
                    Contour = Points((x,0),(length,0),(length,height),(x,height)) };
                return StockGeometry.ClipRemnant(rectangle, plan.Package.Material);
            }
            NestRemnant? Find(bool right)
            {
                decimal cut = right ? cutX : cutY;
                var clipped = Candidate(cut, right);
                if (clipped == null) return null;
                // Move the cut past an existing notch/hole; never substitute
                // the bounding box of an irregular or disconnected offcut.
                var edges = new[]{cut}.Concat(clipped.Contour.Concat(clipped.Holes.SelectMany(h=>h))
                    .Select(p=>right ? Up(p.X) : Down(p.Y)))
                    .Where(v=>right ? v>=cut && length-v>rightMinimum : v<=cut && v>bottomMinimum).Distinct();
                foreach(decimal edge in right ? edges.OrderBy(v=>v) : edges.OrderByDescending(v=>v))
                {
                    var r = edge==cut ? clipped : Candidate(edge,right);
                    if (r==null || !IsRectangle(r) || (right ? r.Length<=rightMinimum : r.Width<=bottomMinimum)) continue;
                    decimal x=r.X??0,y=r.Y??0;
                    r.Contour=Points((x,y),(x+r.Length,y),(x+r.Length,y+r.Width),(x,y+r.Width));
                    if(!StockGeometry.Contains(plan.Package.Material,new Clipper2Lib.PathsD{StockGeometry.Path(r.Contour)}))continue;
                    r.Edge=right ? "Right" : "Bottom";
                    return r;
                }
                return null;
            }
            var right=Find(true);var bottom=Find(false);
            var best=right==null ? bottom : bottom!=null && bottom.Area>right.Area ? bottom : right;
            return best==null ? new List<NestRemnant>() : new List<NestRemnant>{best};
        }

        internal static bool IsRectangle(NestRemnant remnant) => remnant.Holes.Count == 0
            && remnant.Length > 0 && remnant.Width > 0
            && Math.Abs(remnant.Area - remnant.Length * remnant.Width) <= 0.01m;

        private static List<LayoutPoint> Points(params (decimal X, decimal Y)[] points)
            => points.Select(p => new LayoutPoint { X = (double)p.X, Y = (double)p.Y }).ToList();
        private static decimal Down(double value) => decimal.Floor((decimal)Math.Max(0, value) * 10m) / 10m;
        private static decimal Up(double value) => decimal.Ceiling((decimal)Math.Max(0, value) * 10m) / 10m;
        internal static string ImageFilename(string stem, int index) => stem + "_R" + (index + 1) + ".png";

        // PowerFab 2026i's native TFS link update uses this value as an unquoted
        // SQL expression. GUID-R1 tokens fail as identifiers/subtraction. Keep
        // the external identity numeric; the readable Name is independent.
        internal static string NumericExternalId(string nestId, int ordinal)
        {
            if (string.IsNullOrWhiteSpace(nestId) || ordinal < 1) throw new ArgumentException("A nest identity and positive remnant ordinal are required.");
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("HanlonRemnant:" + nestId + ":" + ordinal.ToString(CultureInfo.InvariantCulture)));
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | digest[i];
            value &= 0x7fffffffffffffffUL;
            return (value == 0 ? 1UL : value).ToString(CultureInfo.InvariantCulture);
        }

        internal static bool IsNumericExternalId(string value) => !string.IsNullOrEmpty(value)
            && value.All(c => c >= '0' && c <= '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number > 0
            && value == number.ToString(CultureInfo.InvariantCulture);

        public static IReadOnlyList<LayoutPoint> Outline(NestRemnant r) => r.Contour.Count > 0 ? r.Contour
            : Points((r.X ?? 0, r.Y ?? 0), ((r.X ?? 0) + r.Length, r.Y ?? 0),
                ((r.X ?? 0) + r.Length, (r.Y ?? 0) + r.Width), (r.X ?? 0, (r.Y ?? 0) + r.Width));

        public static string Definition(NestRemnant r)
        {
            var points = Outline(r);
            double left = points.Min(p => p.X), bottom = points.Min(p => p.Y);
            var text = new StringBuilder();
            void Pair(int code, string value) => text.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(value).Append('\n');
            Pair(0, "SECTION"); Pair(2, "HEADER"); Pair(9, "$INSUNITS"); Pair(70, r.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? "1" : "4"); Pair(0, "ENDSEC");
            Pair(0, "SECTION"); Pair(2, "ENTITIES");
            foreach (var polygon in new[] { points }.Concat(r.Holes.Select(h => (IReadOnlyList<LayoutPoint>)h)))
            {
                Pair(0, "LWPOLYLINE"); Pair(90, polygon.Count.ToString(CultureInfo.InvariantCulture)); Pair(70, "1");
                foreach (LayoutPoint p in polygon) { Pair(10, (p.X - left).ToString("0.########", CultureInfo.InvariantCulture)); Pair(20, (p.Y - bottom).ToString("0.########", CultureInfo.InvariantCulture)); }
            }
            Pair(0, "ENDSEC"); Pair(0, "EOF"); return text.ToString();
        }

        public static void ValidateContour(NestRemnant r)
        {
            if (r.Contour.Count == 0) return; // Legacy rectangular imports.
            if (r.Contour.Count < 3 || r.Contour.Any(p => p == null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                throw new InvalidDataException("The remnant outline contains invalid coordinates.");
            DxfShape geometry = DxfGeometry.Read(Definition(r), "Future remnant " + r.RemnantId);
            double mm = r.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4 : 1;
            if (Math.Abs(geometry.Bounds.Width - (double)r.Length * mm) > 0.01
                || Math.Abs(geometry.Bounds.Height - (double)r.Width * mm) > 0.01
                || Math.Abs(geometry.Area - (double)r.Area * mm * mm) > 0.01)
                throw new InvalidDataException("The remnant outline, bounding dimensions and actual area do not agree.");
        }

        public static string Dimensions(NestMaterial material, NestRemnant remnant)
        {
            decimal thickness = material.Thickness * (material.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m);
            decimal width = remnant.Width * (remnant.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m);
            decimal divisor = material.Metric ? 1m : 25.4m;
            return (thickness / divisor).ToString("0.########", CultureInfo.InvariantCulture) + " x "
                + (width / divisor).ToString("0.########", CultureInfo.InvariantCulture);
        }
    }
}

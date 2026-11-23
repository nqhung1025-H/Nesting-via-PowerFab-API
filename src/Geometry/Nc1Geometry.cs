using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class Nc1Drawing
    {
        public string DxfText { get; set; } = string.Empty;
        public double ThicknessMm { get; set; }
    }

    // Planning geometry for flat B-profile plates. This is not a CAM
    // postprocessor: unsupported cutting features are rejected, never dropped.
    internal static class Nc1Geometry
    {
        private sealed class Block
        {
            public string Code = string.Empty;
            public List<string> Rows = new List<string>();
        }
        private readonly record struct Numeric(double Value, string Suffix);
        private readonly record struct ContourPoint(double X, double Y, double Radius);
        private static readonly Regex Token = new Regex(@"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?[A-Za-z]*|[A-Za-z]+|\S", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"^(?<n>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?)(?<s>[A-Za-z]*)$", RegexOptions.CultureInvariant);

        public static bool LooksLike(string text) => text.TrimStart('\ufeff', ' ', '\t', '\r', '\n')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
            .FirstOrDefault(s => !s.StartsWith("**", StringComparison.Ordinal)) == "ST";

        public static Nc1Drawing Convert(string text, string source)
        {
            string[] lines = text.TrimStart('\ufeff', ' ', '\t', '\r', '\n').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int position = 0;
            while (position < lines.Length && (lines[position].Trim().Length == 0 || lines[position].TrimStart().StartsWith("**", StringComparison.Ordinal))) position++;
            if (position >= lines.Length || lines[position++].Trim() != "ST") throw Error("missing ST header", source);
            var header = new List<string>();
            while (position < lines.Length && header.Count < 20)
            {
                string line = lines[position++];
                if (line.TrimStart().StartsWith("**", StringComparison.Ordinal)) continue;
                header.Add(line.Trim());
            }
            if (header.Count < 20 || header[7] != "B") throw Error("only flat B-profile plates with a complete header are supported", source);
            double length = HeaderNumber(header[8], source), width = HeaderNumber(header[9], source), thickness = HeaderNumber(header[12], source);
            if (length <= 0 || width <= 0 || thickness <= 0) throw Error("header length, width and thickness must be positive", source);
            if (header.Skip(16).Take(4).Any(s => Math.Abs(HeaderNumber(s, source)) > 1e-9))
                throw Error("skew cuts in the header are not supported; use a verified DXF", source);
            var blocks = new List<Block>();
            Block? current = null;
            bool ended = false;
            int optionalHeaderLines = 0;
            for (; position < lines.Length; position++)
            {
                string line = lines[position], trimmed = line.Trim();
                if (trimmed.StartsWith("**", StringComparison.Ordinal)) continue;
                if (trimmed.Length == 0) { if (current == null && !ended) optionalHeaderLines++; continue; }
                bool code = line.Length >= 2 && !char.IsWhiteSpace(line[0]) && Regex.IsMatch(trimmed, "^[A-Z][A-Z0-9]$", RegexOptions.CultureInvariant);
                if (ended) throw Error("unexpected data after EN", source);
                if (code)
                {
                    if (trimmed == "EN") { ended = true; current = null; continue; }
                    if (trimmed is not ("AK" or "IK" or "BO" or "SI" or "PU" or "KO"))
                        throw Error("unsupported block " + trimmed + "; use a verified DXF", source);
                    current = new Block { Code = trimmed }; blocks.Add(current);
                }
                else if (current != null) current.Rows.Add(trimmed);
                else if (++optionalHeaderLines > 4) throw Error("could not locate the first geometry block", source);
            }
            if (!ended) throw Error("missing EN terminator", source);
            if (blocks.Count(b => b.Code == "AK") != 1) throw Error("exactly one explicit AK outer contour is required", source);
            string outerDxf = Polyline(blocks.Single(b => b.Code == "AK"), source);
            DxfShape outer = DxfGeometry.Read(Document(outerDxf), source + " / AK");
            if (Math.Abs(outer.Bounds.Width - length) > 0.5 || Math.Abs(outer.Bounds.Height - width) > 0.5)
                throw Error("AK outline dimensions disagree with the NC1 header", source);
            var entities = new StringBuilder(outerDxf);
            foreach (Block block in blocks)
            {
                if (block.Code == "IK") entities.Append(Polyline(block, source));
                if (block.Code == "BO") entities.Append(Holes(block, source));
                // SI/PU/KO are identification or marking, not through-cut outlines.
            }
            string dxf = Document(entities.ToString());
            DxfShape complete = DxfGeometry.Read(dxf, source + " / NC1 contours");
            if (Math.Abs(Math.Abs(Clipper.Area(complete.Outer)) - Math.Abs(Clipper.Area(outer.Outer))) > 0.01)
                throw Error("an internal feature is larger than the AK outer contour", source);
            return new Nc1Drawing { DxfText = dxf, ThicknessMm = thickness };
        }

        private static string Polyline(Block block, string source)
        {
            string face = string.Empty;
            var points = new List<ContourPoint>();
            var notches = new Dictionary<int, ContourPoint>();
            foreach (string row in block.Rows)
            {
                Numeric[] numbers = Values(row, ref face, source);
                if (numbers.Length < 2 || numbers.Length > 7) throw Error("invalid " + block.Code + " vertex", source);
                CheckCoordinates(numbers, source, true);
                if (numbers.Skip(2).Any(n => n.Suffix.Length != 0)
                    || numbers.Skip(3).Any(n => Math.Abs(n.Value) > 1e-9))
                    throw Error("beveled " + block.Code + " contours need a verified DXF", source);
                var point = new ContourPoint(numbers[0].Value, numbers[1].Value, numbers.Length > 2 ? numbers[2].Value : 0);
                if (numbers[1].Suffix == "t")
                {
                    // DSTV p14: this is the theoretical notch corner/tool hint,
                    // not another contour vertex. The neighbouring arc endpoints
                    // must already describe that notch, and must be tangential.
                    if (points.Count == 0 || Math.Abs(point.Radius) < 1e-9
                        || Math.Abs(points[^1].Radius - point.Radius) > 0.01
                        || !notches.TryAdd(points.Count - 1, point))
                        throw Error("tangential notch has no explicit matching arc endpoints", source);
                }
                else points.Add(point);
            }
            if (points.Count < 4 || Math.Abs(points[0].X - points[^1].X) > 1e-6 || Math.Abs(points[0].Y - points[^1].Y) > 1e-6)
                throw Error(block.Code + " contour must explicitly close", source);
            if (Math.Abs(points[^1].Radius) > 1e-9 && Math.Abs(points[^1].Radius - points[0].Radius) > 0.01)
                throw Error("contradictory radius at the closing " + block.Code + " vertex", source);
            points.RemoveAt(points.Count - 1);
            var entity = new StringBuilder(Pair(0, "LWPOLYLINE") + Pair(70, 1) + Pair(90, points.Count));
            for (int i = 0; i < points.Count; i++)
            {
                ContourPoint point = points[i], next = points[(i + 1) % points.Count];
                double bulge = 0;
                if (Math.Abs(point.Radius) > 1e-9)
                {
                    double dx = next.X - point.X, dy = next.Y - point.Y, chord = Math.Sqrt(dx * dx + dy * dy);
                    double radius = Math.Abs(point.Radius);
                    if (chord < 1e-9 || chord > 2 * radius + 0.02)
                        throw Error("invalid signed-radius arc in " + block.Code, source);
                    // DSTV p13: sign gives direction; each arc is at most 180°.
                    bulge = Math.Sign(point.Radius) * Math.Tan(Math.Asin(Math.Min(1, chord / (2 * radius))) / 2);
                    if (notches.TryGetValue(i, out var corner))
                    {
                        double offset = Math.Sign(point.Radius) * Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
                        double cx = (point.X + next.X) / 2 - dy / chord * offset;
                        double cy = (point.Y + next.Y) / 2 + dx / chord * offset;
                        double StartError() => Math.Abs((corner.X - point.X) * (point.X - cx) + (corner.Y - point.Y) * (point.Y - cy)) / radius;
                        double EndError() => Math.Abs((corner.X - next.X) * (next.X - cx) + (corner.Y - next.Y) * (next.Y - cy)) / radius;
                        if (StartError() > 0.03 || EndError() > 0.03)
                            throw Error("notch tool hint disagrees with its tangential arc", source);
                    }
                }
                entity.Append(Pair(10, point.X)).Append(Pair(20, point.Y));
                if (bulge != 0) entity.Append(Pair(42, bulge));
            }
            if (notches.Keys.Any(i => i >= points.Count)) throw Error("notch tool hint has no following contour vertex", source);
            return entity.ToString();
        }
        private static string Holes(Block block, string source)
        {
            var entities = new StringBuilder();
            string face = string.Empty;
            foreach (string row in block.Rows)
            {
                Numeric[] numbers = Values(row, ref face, source);
                if (numbers.Length < 3 || numbers.Length > 7) throw Error("only round through-holes are supported in BO", source);
                CheckCoordinates(numbers, source);
                if (numbers[2].Suffix.Length > 0 || numbers[2].Value <= 0
                    || numbers.Skip(3).Any(n => n.Suffix.Length > 0 || Math.Abs(n.Value) > 1e-9))
                    throw Error("blind, threaded, countersunk or slotted BO holes need a verified DXF", source);
                entities.Append(Pair(0, "CIRCLE")).Append(Pair(10, numbers[0].Value)).Append(Pair(20, numbers[1].Value)).Append(Pair(40, numbers[2].Value / 2));
            }
            return entities.ToString();
        }
        private static Numeric[] Values(string row, ref string face, string source)
        {
            var tokens = Token.Matches(row).Select(m => m.Value).ToList();
            if (tokens.Count > 0 && Regex.IsMatch(tokens[0], "^[A-Za-z]+$", RegexOptions.CultureInvariant))
            { face = tokens[0]; tokens.RemoveAt(0); }
            if (face != "v") throw Error("only the front v face of a flat plate is supported; found " + (face.Length == 0 ? "no face" : face), source);
            return tokens.Select(t =>
            {
                Match match = Number.Match(t);
                if (!match.Success || !double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n))
                    throw Error("unsupported coordinate or hole code " + t, source);
                return new Numeric(n, match.Groups["s"].Value);
            }).ToArray();
        }
        private static void CheckCoordinates(Numeric[] values, string source, bool allowTangentialNotch = false)
        {
            // DSTV u/s/o is a rolling-tolerance reference. It does not alter the
            // absolute coordinate, per DSTV section 'Compensation ... rolling'.
            if (values[0].Suffix is not ("" or "u" or "s" or "o")
                || !(values[1].Suffix.Length == 0 || allowTangentialNotch && values[1].Suffix == "t"))
                throw Error("unsupported coordinate/fabrication suffix", source);
        }
        private static double HeaderNumber(string text, string source)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n)) throw Error("invalid numeric header field", source);
            return n;
        }
        private static string Document(string entities) => Pair(0, "SECTION") + Pair(2, "HEADER") + Pair(9, "$INSUNITS") + Pair(70, 4)
            + Pair(0, "ENDSEC") + Pair(0, "SECTION") + Pair(2, "ENTITIES") + entities + Pair(0, "ENDSEC") + Pair(0, "EOF");
        private static string Pair(int code, object value) => code.ToString(CultureInfo.InvariantCulture) + "\n" + System.Convert.ToString(value, CultureInfo.InvariantCulture) + "\n";
        private static InvalidDataException Error(string message, string source) => new InvalidDataException("NC1 plate geometry: " + message + ". " + source);
    }
}

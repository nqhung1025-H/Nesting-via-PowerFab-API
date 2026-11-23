using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class NestingInput
    {
        public NestPart Part { get; set; } = new NestPart();
        public DxfShape Shape { get; set; } = new DxfShape();
    }

    internal sealed class PlacedOutline
    {
        public NestPart Part { get; set; } = new NestPart();
        public string SourceDxf { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Angle { get; set; }
        public PathD Outer { get; set; } = new PathD();
        public List<PathD> Holes { get; set; } = new List<PathD>();
        public PathsD Clearance { get; set; } = new PathsD();
        public double CurveTolerance { get; set; }
        public ShapeBounds Bounds => ShapeBounds.Of(Outer);
    }

    internal sealed class CalculatedNest
    {
        public NestResultPackage Package { get; set; } = new NestResultPackage();
        public List<PlacedOutline> Placed { get; set; } = new List<PlacedOutline>();
        public List<string> Unplaced { get; set; } = new List<string>();
        public double SheetLengthMm { get; set; }
        public double SheetWidthMm { get; set; }
        public double NetPartAreaMm { get; set; }
        public double Utilization => NetPartAreaMm / (SheetLengthMm * SheetWidthMm) * 100;
    }

    internal static class DxfNestingEngine
    {
        private sealed class Orientation
        {
            public double Angle;
            public PathD Outer = new PathD();
            public List<PathD> Holes = new List<PathD>();
            public PathsD Clearance = new PathsD();
            public ShapeBounds Bounds => ShapeBounds.Of(Outer);
        }

        private sealed class SearchBudget
        {
            public int Remaining;
            public SearchBudget(int remaining) { Remaining = remaining; }
            public bool Use() { if (Remaining <= 0) return false; Remaining--; return true; }
        }

        public static CalculatedNest Calculate(NestResultPackage template, IEnumerable<NestingInput> source,
            LayoutConditions conditions, CancellationToken cancellation, IProgress<string>? progress = null, Action<NestingProgress>? report = null)
        {
            conditions.Validate();
            StockGeometry.Validate(template.Material);
            if (conditions.RotatePitch < 5) throw new InvalidDataException("This first version supports rotation pitches of 5 degrees or larger.");
            double length = Mm(template.Material.Length, template.Material.Unit), width = Mm(template.Material.Width, template.Material.Unit);
            if (length <= (double)(conditions.Left + conditions.Right) || width <= (double)(conditions.Top + conditions.Bottom))
                throw new InvalidDataException("The selected sheet is smaller than its layout borders.");
            List<NestingInput> input = source.OrderByDescending(p => p.Shape.Area).ThenBy(p => LoadedNestResult.PartKey(p.Part), StringComparer.Ordinal).ToList();
            int requested = input.Sum(p => p.Part.Quantity);
            if (requested <= 0 || requested > 2000) throw new InvalidDataException("Use between 1 and 2000 remaining parts in a run for this first version.");
            foreach (NestingInput item in input) VerifyDimensions(item);
            var plan = new CalculatedNest { Package = template, SheetLengthMm = length, SheetWidthMm = width };
            double gap = (double)conditions.Spacing;
            var rotations = new Dictionary<DxfShape, List<Orientation>>();
            List<Orientation> Rotations(DxfShape shape)
            {
                if (!rotations.TryGetValue(shape, out var options))
                    rotations.Add(shape, options = Orientations(shape, (double)conditions.RotatePitch, gap));
                return options;
            }
            int examined = 0, processed = 0, unplaced = 0;
            void Report(string status) => report?.Invoke(NestingProgress.ForMaterial(template.Material, requested, processed, plan.Placed.Count, unplaced, status));
            Report("Starting placement…");
            foreach (NestingInput item in input)
            {
                cancellation.ThrowIfCancellationRequested();
                var orientations = Rotations(item.Shape);
                for (int instance = 0; instance < item.Part.Quantity; instance++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    examined++;
                    progress?.Report("Placing " + item.Part.PieceMark + " — " + examined + " / " + requested);
                    Report("Placing " + item.Part.PieceMark);
                    PlacedOutline? best = FindPlacement(item, orientations, plan.Placed, template.Material,
                        conditions, length, width, cancellation);
                    if (best == null)
                    {
                        int remaining = item.Part.Quantity - instance;
                        plan.Unplaced.Add(item.Part.JobNumber + " / " + item.Part.PieceMark + ": " + remaining);
                        examined += remaining - 1;
                        processed += remaining; unplaced += remaining;
                        Report("No further copies of " + item.Part.PieceMark + " fit this sheet.");
                        break;
                    }
                    plan.Placed.Add(best);
                    plan.NetPartAreaMm += item.Shape.Area;
                    processed++;
                    Report("Placed " + item.Part.PieceMark);
                }
            }
            if (plan.Placed.Count == 0) throw new InvalidDataException("No requested plate fits this sheet with the current borders, spacing and rotation settings.");
            Report("Rotating and compacting parts to the left…");
            CompactLeft(plan, input, Rotations, conditions, cancellation, Report);
            // Compaction may free a position that the first placement pass could
            // not use. Retry those copies before requesting another stock sheet.
            bool recovered = false;
            foreach (NestingInput item in input)
            {
                int missing = item.Part.Quantity - plan.Placed.Count(p => ReferenceEquals(p.Part, item.Part));
                for (int copy = 0; copy < missing; copy++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var added = FindPlacement(item, Rotations(item.Shape), plan.Placed, template.Material,
                        conditions, length, width, cancellation, compact: true);
                    if (added == null) break;
                    plan.Placed.Add(added); plan.NetPartAreaMm += item.Shape.Area; recovered = true;
                    unplaced = Math.Max(0, requested - plan.Placed.Count);
                    Report("Filled a remaining position with " + item.Part.PieceMark);
                }
            }
            if (recovered) CompactLeft(plan, input, Rotations, conditions, cancellation, Report);
            plan.Unplaced.Clear();
            foreach (NestingInput item in input)
            {
                int missing = item.Part.Quantity - plan.Placed.Count(p => ReferenceEquals(p.Part, item.Part));
                if (missing > 0) plan.Unplaced.Add(item.Part.JobNumber + " / " + item.Part.PieceMark + ": " + missing);
            }
            unplaced = Math.Max(0, requested - plan.Placed.Count);
            VerifyPlan(plan, conditions);
            plan.Package.NestId = Guid.NewGuid().ToString("N");
            plan.Package.LayoutSource = "Hanlon plate nesting v1.1";
            plan.Package.LayoutConditions = conditions.Copy();
            plan.Package.Status = "completed";
            plan.Package.Parts = plan.Placed.GroupBy(p => LoadedNestResult.PartKey(p.Part)).Select(group =>
            {
                NestPart p = group.First().Part;
                return new NestPart { JobNumber = p.JobNumber, MainMark = p.MainMark, PieceMark = p.PieceMark,
                    Sequence = p.Sequence, Phase = p.Phase, LotNumber = p.LotNumber, Quantity = group.Count(), Length = p.Length, Width = p.Width, Unit = p.Unit };
            }).ToList();
            plan.Package.NestName = NestNaming.ForParts(plan.Package.Parts);
            plan.Package.Remnants = RemnantGeometry.Create(plan, conditions);
            Report("Placement complete.");
            return plan;
        }

        private static PlacedOutline? FindPlacement(NestingInput item, List<Orientation> orientations,
            List<PlacedOutline> placed, NestMaterial material, LayoutConditions conditions,
            double length, double width, CancellationToken cancellation, bool compact = false,
            double rightLimit = double.PositiveInfinity, SearchBudget? budget = null, int perOrientationLimit = int.MaxValue)
        {
            double occupiedRight = placed.Count == 0 ? 0 : placed.Max(p => p.Bounds.Right + p.CurveTolerance);
            double pitch = (double)conditions.MovePitch, gap = (double)conditions.Spacing;
            PlacedOutline? best = null;
            foreach (Orientation orientation in orientations.OrderBy(o => Math.Round(o.Bounds.Width, 7))
                .ThenBy(o => Math.Round(o.Bounds.Height, 7)).ThenBy(o => o.Angle))
            {
                cancellation.ThrowIfCancellationRequested();
                if (budget != null && budget.Remaining <= 0) break;
                ShapeBounds bounds = orientation.Bounds;
                double pad = item.Shape.CurveTolerance + material.StockCurveToleranceMm;
                double minimumX = (double)conditions.Left + pad, minimumY = (double)conditions.Bottom + pad;
                double maximumX = Math.Min(length - (double)conditions.Right - bounds.Width - pad,
                    rightLimit - bounds.Width - item.Shape.CurveTolerance);
                double maximumY = width - (double)conditions.Top - bounds.Height - pad;
                if (minimumX > maximumX + 1e-7 || minimumY > maximumY + 1e-7) continue;
                var xs = Coordinates(placed, orientation, true, minimumX, maximumX, (double)conditions.Left,
                    pitch, gap + pad, stock: material, fullCoverage: compact);
                var ys = Coordinates(placed, orientation, false, minimumY, maximumY,
                    width - (double)conditions.Top - bounds.Height, pitch, gap + pad, true, material, compact);
                PlacedOutline? SearchOrientation()
                {
                    int examined = 0;
                    PlacedOutline? found = null;
                    foreach (double x in xs)
                    {
                        PlacedOutline? preferred = found != null && (best == null || BetterLeft(found, best, occupiedRight)) ? found : best;
                        if (preferred != null)
                        {
                            double tolerance = compact ? item.Shape.CurveTolerance : pad;
                            double extent = Math.Max(occupiedRight, x + bounds.Width + tolerance);
                            double bestExtent = Math.Max(occupiedRight, preferred.Bounds.Right + preferred.CurveTolerance);
                            if (extent > bestExtent + 1e-7) { if (compact) continue; break; }
                            if (!compact && Math.Abs(extent - bestExtent) < 1e-7 && x > preferred.X + 1e-7) break;
                            if (compact && Math.Abs(extent - bestExtent) < 1e-7
                                && x + bounds.Width + item.Shape.CurveTolerance > preferred.Bounds.Right + preferred.CurveTolerance + 1e-7) continue;
                        }
                        foreach (double y in ys)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (examined++ >= perOrientationLimit || budget != null && !budget.Use()) return found;
                            PathsD clearance = Translate(orientation.Clearance, x, y);
                            if (!StockGeometry.Contains(material, clearance)) continue;
                            if (placed.Any(p => Collides(clearance, p.Clearance))) continue;
                            PathD outer = Translate(orientation.Outer, x, y);
                            if (placed.Any(p => !ClearanceEnough(outer, p.Outer, gap + pad + p.CurveTolerance))) continue;
                            var candidate = new PlacedOutline
                            {
                                Part = item.Part, SourceDxf = item.Shape.Source, X = x, Y = y, Angle = orientation.Angle,
                                Outer = outer, Holes = orientation.Holes.Select(p => Translate(p, x, y)).ToList(),
                                Clearance = clearance, CurveTolerance = item.Shape.CurveTolerance
                            };
                            if (!compact) return candidate;
                            if (found == null || BetterLeft(candidate, found, occupiedRight)) found = candidate;
                            break;
                        }
                    }
                    return found;
                }
                var candidate = SearchOrientation();
                if (candidate != null && (best == null || (compact
                    ? BetterLeft(candidate, best, occupiedRight) : Better(candidate, best, occupiedRight)))) best = candidate;
            }
            return best;
        }

        private static void CompactLeft(CalculatedNest plan, List<NestingInput> input,
            Func<DxfShape, List<Orientation>> rotations, LayoutConditions conditions,
            CancellationToken cancellation, Action<string> report)
        {
            var sources = input.GroupBy(i => i.Part).ToDictionary(g => g.Key, g => g.First());
            // Bound refinement work while retaining every already verified part.
            // The initial pass and retries remain unrestricted by this budget.
            int total = Math.Clamp(2000000 / plan.Placed.Count, 2000, 100000);
            var budget = new SearchBudget(total);
            for (int pass = 0; pass < 3 && budget.Remaining > 0; pass++)
            {
                bool changed = false;
                var order = plan.Placed.OrderByDescending(p => p.Bounds.Right + p.CurveTolerance)
                    .ThenByDescending(p => p.X).ThenBy(p => p.Bounds.Top).ToArray();
                foreach (PlacedOutline current in order)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (budget.Remaining <= 0) break;
                    int index = plan.Placed.IndexOf(current);
                    var others = plan.Placed.Where(p => !ReferenceEquals(p, current)).ToList();
                    double occupied = others.Count == 0 ? 0 : others.Max(p => p.Bounds.Right + p.CurveTolerance);
                    double limit = Math.Max(occupied, current.Bounds.Right + current.CurveTolerance);
                    var item = sources[current.Part]; var options = rotations(item.Shape);
                    int attempts = Math.Max(16, Math.Min(1024, total / (Math.Min(plan.Placed.Count, 32) * options.Count)));
                    var candidate = FindPlacement(item, options, others, plan.Package.Material, conditions,
                        plan.SheetLengthMm, plan.SheetWidthMm, cancellation, true, limit, budget, attempts);
                    if (candidate == null || !BetterLeft(candidate, current, occupied)) continue;
                    plan.Placed[index] = candidate; changed = true;
                    report("Compacting left: " + current.Part.PieceMark);
                }
                if (!changed) break;
            }
        }

        public static void VerifyPlan(CalculatedNest plan, LayoutConditions conditions)
        {
            for (int i = 0; i < plan.Placed.Count; i++)
            {
                PlacedOutline p = plan.Placed[i]; ShapeBounds b = p.Bounds;
                if (!StockGeometry.Contains(plan.Package.Material, p.Clearance))
                    throw new InvalidDataException("Part "+p.Part.PieceMark+" crosses the selected remnant outline. Choose a compatible stock sheet or a new sheet requirement.");
                if (b.Left - p.CurveTolerance < (double)conditions.Left - 0.0001
                    || b.Bottom - p.CurveTolerance < (double)conditions.Bottom - 0.0001
                    || b.Right + p.CurveTolerance > plan.SheetLengthMm - (double)conditions.Right + 0.0001
                    || b.Top + p.CurveTolerance > plan.SheetWidthMm - (double)conditions.Top + 0.0001)
                    throw new InvalidDataException("Calculated geometry crosses a sheet border.");
                for (int j = 0; j < i; j++)
                    if (Collides(p.Clearance, plan.Placed[j].Clearance)
                        || !ClearanceEnough(p.Outer, plan.Placed[j].Outer, (double)conditions.Spacing + p.CurveTolerance + plan.Placed[j].CurveTolerance))
                        throw new InvalidDataException("Part clearance regions overlap: " + p.Part.PieceMark + " at (" + p.X.ToString("0.###", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0.###", CultureInfo.InvariantCulture)
                            + ") and " + plan.Placed[j].Part.PieceMark + " at (" + plan.Placed[j].X.ToString("0.###", CultureInfo.InvariantCulture) + ", " + plan.Placed[j].Y.ToString("0.###", CultureInfo.InvariantCulture) + ") mm.");
            }
        }

        private static List<Orientation> Orientations(DxfShape shape, double step, double gap)
        {
            var result = new List<Orientation>();
            for (double angle = 0; angle < 360 - 1e-7; angle += step)
            {
                double radians = angle * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
                PathD Rotate(PathD path) => new PathD(path.Select(p => new PointD(p.x * c - p.y * s, p.x * s + p.y * c)));
                PathD rotated = Rotate(shape.Outer); ShapeBounds bounds = ShapeBounds.Of(rotated);
                var outer = Translate(rotated, -bounds.Left, -bounds.Bottom);
                var holes = shape.Holes.Select(p => Translate(Rotate(p), -bounds.Left, -bounds.Bottom)).ToList();
                PathsD clearance = gap / 2 + shape.CurveTolerance > 0
                    ? Clipper.InflatePaths(new PathsD { outer }, gap / 2 + shape.CurveTolerance, JoinType.Miter, EndType.Polygon, 50, 4, 0.005)
                    : new PathsD { new PathD(outer) };
                result.Add(new Orientation { Angle = angle, Outer = outer, Holes = holes, Clearance = clearance });
            }
            return result;
        }

        private static List<double> Coordinates(List<PlacedOutline> placed, Orientation orientation, bool horizontal,
            double minimum, double maximum, double anchor, double pitch, double gap, bool descending = false, NestMaterial? stock = null, bool fullCoverage = false)
        {
            var values = new SortedSet<double>();
            var primary = new SortedSet<double>();
            void Add(double value, bool contact = false)
            {
                double step = descending ? Math.Floor((value - anchor + 1e-8) / pitch) : Math.Ceiling((value - anchor - 1e-8) / pitch);
                double snapped = anchor + step * pitch;
                if (snapped >= minimum - 1e-7 && snapped <= maximum + 1e-7)
                {
                    double coordinate = Math.Round(snapped, 7); values.Add(coordinate);
                    if (contact) primary.Add(coordinate);
                }
            }
            Add(minimum, true);
            Add(anchor + Math.Floor((maximum - anchor + 1e-8) / pitch) * pitch, true);
            double size = horizontal ? orientation.Bounds.Width : orientation.Bounds.Height;
            if (stock != null)
                foreach (var vertex in stock.StockContour)
                {
                    double v = horizontal ? vertex.X : vertex.Y;
                    Add(v + gap + stock.StockCurveToleranceMm, true); Add(v - size - gap - stock.StockCurveToleranceMm, true); Add(v, true); Add(v - size, true);
                }
            PointD[] localVertices = Sample(orientation.Outer, 8);
            foreach (PlacedOutline p in placed)
            {
                ShapeBounds b = p.Bounds;
                Add((horizontal ? b.Right : b.Top) + gap + p.CurveTolerance, true);
                Add((horizontal ? b.Left : b.Bottom) - size - gap - p.CurveTolerance, true);
                if (fullCoverage)
                {
                    Add(horizontal ? b.Left : b.Bottom, true);
                    Add((horizontal ? b.Right : b.Top) - size, true);
                }
                foreach (PointD vertex in Sample(p.Outer, 8))
                    foreach (PointD local in localVertices)
                    {
                        double difference = (horizontal ? vertex.x - local.x : vertex.y - local.y);
                        Add(difference); Add(difference + gap + p.CurveTolerance); Add(difference - gap - p.CurveTolerance);
                    }
            }
            // Candidate contacts, not an exhaustive sheet-wide grid; every accepted position is checked geometrically.
            var ordered = (descending ? values.OrderByDescending(v => v) : values.AsEnumerable()).ToList();
            if (!fullCoverage) return ordered.Take(96).ToList();
            List<double> Order(IEnumerable<double> coordinates) => (descending ? coordinates.OrderByDescending(v => v) : coordinates.OrderBy(v => v)).ToList();
            List<double> Cover(List<double> coordinates, int count)
            {
                if (coordinates.Count <= count) return coordinates;
                var selected = new HashSet<double>(coordinates.Take(count / 2));
                int spread = count - selected.Count;
                for (int i = 0; i < spread; i++) selected.Add(coordinates[(int)Math.Round(i * (coordinates.Count - 1) / (double)Math.Max(1, spread - 1))]);
                return Order(selected);
            }
            // Try column/row contacts before sampled vertex contacts. Cover the
            // whole height, including bottom gaps, within the refinement budget.
            var contacts = Cover(Order(primary), 160);
            return contacts.Concat(Cover(ordered.Where(v => !primary.Contains(v)).ToList(), 160 - contacts.Count)).ToList();
        }

        private static PointD[] Sample(PathD path, int count)
            => path.Where((p, i) => i % Math.Max(1, path.Count / count) == 0).Take(count).ToArray();
        private static bool Better(PlacedOutline a, PlacedOutline b, double occupiedRight)
        {
            double aExtent = Math.Max(occupiedRight, a.Bounds.Right + a.CurveTolerance);
            double bExtent = Math.Max(occupiedRight, b.Bounds.Right + b.CurveTolerance);
            if (Math.Abs(aExtent - bExtent) > 1e-7) return aExtent < bExtent;
            if (Math.Abs(a.X - b.X) > 1e-7) return a.X < b.X;
            if (Math.Abs(a.Bounds.Top - b.Bounds.Top) > 1e-7) return a.Bounds.Top > b.Bounds.Top;
            if (Math.Abs(a.Bounds.Width - b.Bounds.Width) > 1e-7) return a.Bounds.Width < b.Bounds.Width;
            return a.Bounds.Height < b.Bounds.Height - 1e-7;
        }
        private static bool BetterLeft(PlacedOutline a, PlacedOutline b, double occupiedRight)
        {
            double aRight = a.Bounds.Right + a.CurveTolerance, bRight = b.Bounds.Right + b.CurveTolerance;
            double aExtent = Math.Max(occupiedRight, aRight), bExtent = Math.Max(occupiedRight, bRight);
            if (Math.Abs(aExtent - bExtent) > 1e-7) return aExtent < bExtent;
            if (Math.Abs(aRight - bRight) > 1e-7) return aRight < bRight;
            if (Math.Abs(a.X - b.X) > 1e-7) return a.X < b.X;
            if (Math.Abs(a.Bounds.Top - b.Bounds.Top) > 1e-7) return a.Bounds.Top > b.Bounds.Top;
            if (Math.Abs(a.Bounds.Width - b.Bounds.Width) > 1e-7) return a.Bounds.Width < b.Bounds.Width;
            return a.Bounds.Height < b.Bounds.Height - 1e-7;
        }
        internal static bool Collides(PathsD a, PathsD b)
        {
            ShapeBounds x = ShapeBounds.Of(a.SelectMany(p => p)), y = ShapeBounds.Of(b.SelectMany(p => p));
            if (x.Right <= y.Left + 1e-8 || y.Right <= x.Left + 1e-8 || x.Top <= y.Bottom + 1e-8 || y.Top <= x.Bottom + 1e-8) return false;
            return Math.Abs(Clipper.Area(Clipper.Intersect(a, b, FillRule.NonZero, 4))) > 0.00001;
        }
        private static bool ClearanceEnough(PathD a, PathD b, double distance)
        {
            if (distance <= 0) return true;
            ShapeBounds x = ShapeBounds.Of(a), y = ShapeBounds.Of(b);
            double dx = Math.Max(0, Math.Max(x.Left - y.Right, y.Left - x.Right));
            double dy = Math.Max(0, Math.Max(x.Bottom - y.Top, y.Bottom - x.Top));
            double limit = Math.Max(0, distance - 0.000001);
            if (dx * dx + dy * dy >= limit * limit) return true;
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                {
                    PointD p = a[i], q = a[(i + 1) % a.Count], r = b[j], s = b[(j + 1) % b.Count];
                    if (SegmentsCross(p, q, r, s)) return false;
                    if (PointSegmentSquared(p, r, s) < limit * limit || PointSegmentSquared(q, r, s) < limit * limit
                        || PointSegmentSquared(r, p, q) < limit * limit || PointSegmentSquared(s, p, q) < limit * limit) return false;
                }
            return true;
        }
        private static double PointSegmentSquared(PointD p, PointD a, PointD b)
        {
            double dx = b.x - a.x, dy = b.y - a.y, length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / length, 0, 1);
            double x = p.x - a.x - t * dx, y = p.y - a.y - t * dy;
            return x * x + y * y;
        }
        private static bool SegmentsCross(PointD a, PointD b, PointD c, PointD d)
        {
            if (Math.Max(a.x, b.x) < Math.Min(c.x, d.x) || Math.Max(c.x, d.x) < Math.Min(a.x, b.x)
                || Math.Max(a.y, b.y) < Math.Min(c.y, d.y) || Math.Max(c.y, d.y) < Math.Min(a.y, b.y)) return false;
            double Side(PointD p, PointD q, PointD r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            return Side(a, b, c) * Side(a, b, d) <= 0 && Side(c, d, a) * Side(c, d, b) <= 0;
        }
        private static PathD Translate(PathD path, double x, double y) => new PathD(path.Select(p => new PointD(p.x + x, p.y + y)));
        private static PathsD Translate(PathsD paths, double x, double y) => new PathsD(paths.Select(p => Translate(p, x, y)));
        private static void VerifyDimensions(NestingInput input)
        {
            ShapeBounds b = input.Shape.Bounds;
            double length = Mm(input.Part.Length, input.Part.Unit), width = Mm(input.Part.Width, input.Part.Unit);
            bool normal = Math.Abs(b.Width - length) <= 0.5 && Math.Abs(b.Height - width) <= 0.5;
            bool swapped = Math.Abs(b.Width - width) <= 0.5 && Math.Abs(b.Height - length) <= 0.5;
            if (!normal && !swapped) throw new InvalidDataException("Plate outline dimensions for " + input.Part.PieceMark + " are "
                + b.Width.ToString("0.###") + " × " + b.Height.ToString("0.###") + " mm, but PowerFab requests "
                + length.ToString("0.###") + " × " + width.ToString("0.###") + " mm. Check file matching and units.");
        }
        internal static double Mm(decimal value, string unit)
        {
            if (string.Equals(unit, "MM", StringComparison.OrdinalIgnoreCase)) return (double)value;
            if (string.Equals(unit, "IN", StringComparison.OrdinalIgnoreCase)) return (double)value * 25.4;
            throw new InvalidDataException("Physical units must be MM or IN.");
        }
    }
}

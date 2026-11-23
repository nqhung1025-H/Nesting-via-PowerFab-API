using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class PackingChecks
    {
        private static int passed;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Packing: " + message);
            passed++;
        }
        private static NestResultPackage Template(decimal length = 3000, decimal width = 1500) => new NestResultPackage
        {
            WorkOrderId = "5774", RequisitionNumber = "REQ184",
            Material = new NestMaterial { Source = "Purchase", Shape = "PLT", Grade = "250", Dimensions = "10X" + width,
                Thickness = 10, Length = length, Width = width, Unit = "MM", Metric = true }
        };
        private static NestingInput Plate(string mark, int w, int h, int qty, bool chamfer = false, bool hole = false)
        {
            PathD outer = chamfer
                ? new PathD { new PointD(0,0), new PointD(w,0), new PointD(w,h-10), new PointD(w-10,h), new PointD(0,h) }
                : new PathD { new PointD(0,0), new PointD(w,0), new PointD(w,h), new PointD(0,h) };
            var shape = new DxfShape { Source = mark + ".dxf", Outer = outer };
            if (hole) shape.Holes.Add(new PathD(Enumerable.Range(0,16).Select(i => new PointD(
                20 + 5 * Math.Cos(i * Math.PI / 8), 20 + 5 * Math.Sin(i * Math.PI / 8)))));
            return new NestingInput { Part = new NestPart { JobNumber = "test", MainMark = "A", PieceMark = mark,
                Length = w, Width = h, Quantity = qty }, Shape = shape };
        }
        private static void VerifyGeometry(CalculatedNest plan, LayoutConditions rules)
        {
            DxfNestingEngine.VerifyPlan(plan, rules);
            Check(plan.Placed.All(p => StockGeometry.Contains(plan.Package.Material, p.Clearance)), "Every placement is inside actual stock");
            Check(plan.Placed.All(p => Clipper.Area(p.Outer) > 0), "Rotations preserve contour orientation without mirroring");
            Check(plan.Package.Parts.Sum(p => p.Quantity) == plan.Placed.Count, "Saved part quantities match physical placements");
            Check(plan.Package.Remnants.Count <= 1, "At most one physically connected remnant is saved");
            foreach (var remnant in plan.Package.Remnants)
            {
                RemnantGeometry.ValidateContour(remnant);
                var paths = new PathsD { StockGeometry.Path(remnant.Contour) };
                foreach (var hole in remnant.Holes)
                {
                    var p = StockGeometry.Path(hole); if (Clipper.Area(p) > 0) p.Reverse(); paths.Add(p);
                }
                Check(plan.Placed.All(p => Math.Abs(Clipper.Area(Clipper.Intersect(paths, new PathsD { p.Outer }, FillRule.NonZero, 4))) < 0.001), "Remnant boundary excludes all nested parts");
                Check(StockGeometry.Contains(plan.Package.Material, paths), "Remnant contains no material outside the real stock");
                var exported = DxfGeometry.Read(RemnantGeometry.Definition(remnant), "Remnant export");
                Check(Math.Abs(exported.Area - (double)remnant.Area) < 0.01, "DXF contour and saved remnant area agree");
            }
        }
        public static int Run()
        {
            var rules = new LayoutConditions();
            var compactInput = new[] {
                Plate("P0",210,280,4,true,true), Plate("P1",210,160,4,true,true),
                Plate("P2",190,200,4,true,true), Plate("P3",90,60,4,true,true),
                Plate("P4",170,140,4,true,true), Plate("P5",130,80,4,true,true),
                Plate("P6",210,180,4,true,true)
            };
            var compact = DxfNestingEngine.Calculate(Template(), compactInput, rules, CancellationToken.None);
            Check(compact.Placed.Count == 28, "Mixed 28-part sheet keeps all copies");
            // v0.19 used 770 mm on this fixed chamfered-profile fixture.
            Check(compact.Placed.Max(p => p.Bounds.Right + p.CurveTolerance) <= 750.001, "Refinement narrows the previous 770 mm occupied strip");
            Check(compact.Placed.Any(p => p.Part.PieceMark == "P3" && p.X < 200 && p.Y < 100
                && Math.Abs(p.Angle % 180 - 90) < 0.001), "A right-side part rotates into a bottom-left gap");
            Check(compact.Placed.All(p => Math.Abs(p.Angle / 90 - Math.Round(p.Angle / 90)) < 0.00001), "Only permitted quarter-turn rotations are used");
            Check(compact.Placed.All(p => p.Holes.Count == 1), "Every rotated part keeps its hole");
            foreach (var p in compact.Placed)
                Check(Math.Abs(Math.Abs(Clipper.Area(p.Holes[0])) - Math.Abs(Clipper.Area(compactInput.First(i => i.Part == p.Part).Shape.Holes[0]))) < 0.00001, "Hole area is preserved by rotation");
            Check(compact.Package.Remnants.Count == 1 && compact.Package.Remnants[0].Length >= 2244.9m, "Full-height right remnant benefits from the narrower strip");
            VerifyGeometry(compact, rules);

            var retryInput = new[] {
                Plate("P0",210,140,4,true), Plate("P1",150,280,4,true), Plate("P2",210,260,4,true),
                Plate("P3",110,60,4,true), Plate("P4",130,220,4,true), Plate("P5",50,180,4,true), Plate("P6",150,180,4,true)
            };
            var retry = DxfNestingEngine.Calculate(Template(), retryInput, rules, CancellationToken.None);
            // v0.19 placed only 23 copies on this fixed fixture.
            Check(retry.Placed.Count == 28 && retry.Unplaced.Count == 0, "Backfilling recovers all five previously unplaced copies");
            Check(retry.Package.Parts.Count == 7 && retry.Package.Parts.All(p => p.Quantity == 4), "Retry never duplicates or loses requested copies");
            VerifyGeometry(retry, rules);

            var sparse = DxfNestingEngine.Calculate(Template(1000,500), new[] { Plate("S",100,60,2) }, rules, CancellationToken.None);
            Check(sparse.Package.Remnants.Count == 1 && sparse.Package.Remnants[0].Edge == "Right", "The larger right-hand rectangle is retained for a left-filled layout");
            Check(sparse.Package.Remnants[0].Contour.Count == 4 && RemnantGeometry.IsRectangle(sparse.Package.Remnants[0]), "The saved offcut is rectangular without a bottom-left leg");
            VerifyGeometry(sparse, rules);

            var actual = Template(400,300);
            actual.Material.RequiresOutline = true;
            actual.Material.StockContour = new[] { (0d,0d),(400d,0d),(400d,300d),(220d,300d),(220d,150d),(0d,150d) }
                .Select(p => new LayoutPoint { X = p.Item1, Y = p.Item2 }).ToList();
            var irregular = DxfNestingEngine.Calculate(actual, new[] { Plate("IR",80,50,3,true) }, rules, CancellationToken.None);
            Check(irregular.Placed.Count == 3, "Refinement works on a real L-shaped stock outline");
            Check(irregular.Package.Remnants.All(r => RemnantGeometry.IsRectangle(r) && (r.Edge == "Right" || r.Edge == "Bottom")), "Reusing old L-shaped stock never creates another L-shaped remnant");
            VerifyGeometry(irregular, rules);

            var noRotation = new LayoutConditions { RotatePitch = 360 };
            try
            {
                DxfNestingEngine.Calculate(Template(100,250), new[] { Plate("TURN",140,80,1) }, noRotation, CancellationToken.None);
                throw new Exception("Packing accepted a forbidden rotation");
            }
            catch (InvalidDataException e) { Check(e.Message.StartsWith("No requested plate fits"), "Disabling rotations is respected"); }
            var turn = DxfNestingEngine.Calculate(Template(100,250), new[] { Plate("TURN",140,80,1) }, rules, CancellationToken.None);
            Check(turn.Placed.Count == 1 && Math.Abs(turn.Placed[0].Angle % 180 - 90) < 0.001, "Enabling quarter turns fits the narrow sheet");
            VerifyGeometry(turn, rules);

            var circle = DxfGeometry.Read("0\nSECTION\n2\nENTITIES\n0\nCIRCLE\n10\n0\n20\n0\n40\n20\n0\nENDSEC\n0\nEOF\n", "Circle.dxf");
            var curved = DxfNestingEngine.Calculate(Template(200,150), new[] { new NestingInput {
                Part = new NestPart { JobNumber="test", MainMark="A", PieceMark="C", Length=40, Width=40, Quantity=4 }, Shape=circle
            } }, rules, CancellationToken.None);
            Check(curved.Placed.Count == 4 && curved.Placed.All(p => p.CurveTolerance > 0), "Curved parts keep the sampling clearance allowance");
            VerifyGeometry(curved, rules);

            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                DxfNestingEngine.Calculate(Template(), new[] { Plate("CANCEL",100,60,1) }, rules, cancellation.Token);
                throw new Exception("Packing ignored cancellation");
            }
            catch (OperationCanceledException) { Check(true, "Cancellation remains supported"); }
            Console.WriteLine($"PASS: {passed} packing/remnant checks.");
            return passed;
        }
    }
}

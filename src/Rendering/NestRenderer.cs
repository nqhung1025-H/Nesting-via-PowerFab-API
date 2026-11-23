using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class NestRenderer
    {
        public static string Save(CalculatedNest plan, string exportFolder)
        {
            string folder = Path.Combine(exportFolder, "GeneratedLayouts", plan.Package.NestId);
            Directory.CreateDirectory(folder);
            string stem = NestNaming.FileStem(NestNaming.Display(plan.Package));
            string imagePath = Path.Combine(folder, stem + ".png");
            Draw(plan, imagePath);
            plan.Package.NestImageFile = stem + ".png";
            for (int i = 0; i < plan.Package.Remnants.Count; i++)
            {
                NestRemnant remnant = plan.Package.Remnants[i];
                remnant.ImageFile = RemnantGeometry.ImageFilename(stem, i);
                remnant.DefinitionFile = Path.ChangeExtension(remnant.ImageFile, ".dxf");
                File.WriteAllText(Path.Combine(folder, remnant.DefinitionFile), RemnantGeometry.Definition(remnant));
                DrawRemnant(plan.Package.Material, remnant, Path.Combine(folder, remnant.ImageFile));
            }
            plan.Package.Placements = plan.Placed.Select(p => new NestPlacement
            {
                JobNumber = p.Part.JobNumber, PieceMark = p.Part.PieceMark, MainMark = p.Part.MainMark,
                Sequence = p.Part.Sequence, LotNumber = p.Part.LotNumber, X = p.X, Y = p.Y, Rotation = p.Angle,
                SourceDxf = p.SourceDxf, Unit = "MM",
                Outer = p.Outer.Select(v => new LayoutPoint { X = v.x, Y = v.y }).ToList(),
                Holes = p.Holes.Select(h => h.Select(v => new LayoutPoint { X = v.x, Y = v.y }).ToList()).ToList()
            }).ToList();
            string jsonPath = Path.Combine(folder, stem + ".json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan.Package, LoadedNestResult.JsonOptions));
            File.WriteAllLines(Path.Combine(folder, "unplaced-parts.txt"), plan.Unplaced.Count == 0
                ? new[] { "All remaining matching parts fitted on this sheet." } : plan.Unplaced);
            return jsonPath;
        }

        private static void Draw(CalculatedNest plan, string path)
        {
            const int width = 2000;
            int height = Math.Clamp((int)(1880 * plan.SheetWidthMm / plan.SheetLengthMm) + 130, 800, 2600);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using var titleFont = new Font("Segoe UI", 27, FontStyle.Bold);
            using var labelFont = new Font("Segoe UI", 12, FontStyle.Bold);
            using var navy = new SolidBrush(Color.FromArgb(10, 47, 78));
            using var partFill = new SolidBrush(Color.FromArgb(220, 235, 247));
            using var outline = new Pen(Color.FromArgb(10, 47, 78), 1.5f);
            using var margins = new Pen(Color.SlateGray, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawString(plan.Package.AbeImport==null?"Calculated Plate Nesting":"Imported ABE Nesting", titleFont, navy, 55, 25);
            LayoutConditions c = plan.Package.LayoutConditions!;
            double scale = Math.Min((width - 120) / plan.SheetLengthMm, (height - 130) / plan.SheetWidthMm);
            double originX = (width - plan.SheetLengthMm * scale) / 2, originY = 95;
            PointF Screen(PointD p) => new PointF((float)(originX + p.x * scale), (float)(originY + (plan.SheetWidthMm - p.y) * scale));
            RectangleF rectangle = new RectangleF((float)originX, (float)originY, (float)(plan.SheetLengthMm * scale), (float)(plan.SheetWidthMm * scale));
            g.FillRectangle(Brushes.WhiteSmoke, rectangle); g.DrawRectangle(outline, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            if (plan.Package.Material.StockContour.Count > 0)
            {
                g.FillRectangle(Brushes.White, rectangle);
                using var stockShape = new GraphicsPath(FillMode.Alternate);
                stockShape.AddPolygon(plan.Package.Material.StockContour.Select(p => Screen(new PointD(p.X,p.Y))).ToArray());
                foreach (var hole in plan.Package.Material.StockHoles) stockShape.AddPolygon(hole.Select(p=>Screen(new PointD(p.X,p.Y))).ToArray());
                g.FillPath(Brushes.WhiteSmoke, stockShape); g.DrawPath(outline, stockShape);
            }
            PointF topLeft = Screen(new PointD((double)c.Left, plan.SheetWidthMm - (double)c.Top));
            g.DrawRectangle(margins, topLeft.X, topLeft.Y,
                (float)((plan.SheetLengthMm - (double)c.Left - (double)c.Right) * scale),
                (float)((plan.SheetWidthMm - (double)c.Top - (double)c.Bottom) * scale));
            using var remnantOutline = new Pen(Color.SeaGreen, 2f) { DashStyle = DashStyle.Dash };
            foreach (NestRemnant remnant in plan.Package.Remnants.Where(r => r.X.HasValue && r.Y.HasValue))
            {
                g.DrawPolygon(remnantOutline, RemnantGeometry.Outline(remnant).Select(p => Screen(new PointD(p.X, p.Y))).ToArray());
            }
            foreach (PlacedOutline part in plan.Placed)
            {
                using var geometry = new GraphicsPath(FillMode.Alternate);
                geometry.AddPolygon(part.Outer.Select(Screen).ToArray());
                foreach (PathD hole in part.Holes) geometry.AddPolygon(hole.Select(Screen).ToArray());
                g.FillPath(partFill, geometry); g.DrawPath(outline, geometry);
                ShapeBounds b = part.Bounds;
                if (b.Width * scale > 75 && b.Height * scale > 35)
                {
                    PointF center = Screen(new PointD((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2));
                    SizeF size = g.MeasureString(part.Part.PieceMark, labelFont);
                    g.DrawString(part.Part.PieceMark, labelFont, navy, center.X - size.Width / 2, center.Y - size.Height / 2);
                }
            }
            bitmap.Save(path, ImageFormat.Png);
        }

        private static void DrawRemnant(NestMaterial material, NestRemnant remnant, string path)
        {
            using var bitmap = new Bitmap(1600, 1000, PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.White);
            using var title = new Font("Segoe UI", 26, FontStyle.Bold);
            using var details = new Font("Segoe UI", 16);
            using var navy = new SolidBrush(Color.FromArgb(10, 47, 78));
            using var outline = new Pen(Color.SeaGreen, 2f);
            g.DrawString("Future Remnant" + (string.IsNullOrWhiteSpace(remnant.Edge) ? "" : " - " + remnant.Edge), title, navy, 45, 25);
            decimal factor = material.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m;
            g.DrawString("Bounds: " + remnant.Length.ToString("0.###") + " x " + remnant.Width.ToString("0.###")
                + " - Thick: " + (material.Thickness * factor).ToString("0.###") + " - Grade: " + material.Grade,
                details, navy, 45, 80);
            double scale = Math.Min(1500.0 / (double)remnant.Length, 820.0 / (double)remnant.Width);
            float w = (float)((double)remnant.Length * scale), h = (float)((double)remnant.Width * scale);
            var points = RemnantGeometry.Outline(remnant);
            double left = points.Min(p => p.X), bottom = points.Min(p => p.Y);
            PointF[] polygon = points.Select(p => new PointF((1600 - w) / 2 + (float)((p.X - left) * scale),
                140 + (820 - h) / 2 + h - (float)((p.Y - bottom) * scale))).ToArray();
            using var remnantShape = new GraphicsPath(FillMode.Alternate);
            remnantShape.AddPolygon(polygon);
            foreach(var hole in remnant.Holes)
                remnantShape.AddPolygon(hole.Select(p=>new PointF((1600-w)/2+(float)((p.X-left)*scale),140+(820-h)/2+h-(float)((p.Y-bottom)*scale))).ToArray());
            g.FillPath(Brushes.WhiteSmoke, remnantShape); g.DrawPath(outline, remnantShape);
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}

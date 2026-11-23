using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    // Native WinForms regression runner. No PowerFab connection or writes.
    // Synthetic blue/red sheets isolate display/navigation from NC parsing.
    internal static class PreviewNavigationChecks
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run these checks on Windows with .NET 8 Desktop Runtime.");
            DirectoryInfo? sourceFolder=new DirectoryInfo(AppContext.BaseDirectory);
            while(sourceFolder!=null&&!File.Exists(Path.Combine(sourceFolder.FullName,"Hanlon.PowerFab.Nesting.csproj")))
                sourceFolder=sourceFolder.Parent;
            string source=sourceFolder?.FullName??throw new DirectoryNotFoundException("Run this check from the extracted Hanlon source project.");
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(source, "lib", name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            if(args.Contains("--reports-only"))
            {
                string fixtures=Path.Combine(source,"Tests","Fixtures","ABE","Reports");
                var names=new[]{"240175-02-PHASE90C-5MM","240175-02-PHASE90C-6MM","240175-02-PHASE90C-12MM"};
                var expected=new long[]{9482,1335,4046};
                var reportPages=AbePdfReportReader.Read(names.Select(n=>Path.Combine(fixtures,n+".pdf")).ToArray(),System.Threading.CancellationToken.None);
                var times=reportPages.SelectMany(p=>AbeSheetReports.Parse(p,names)).ToList();
                for(int n=0;n<names.Length;n++)
                {
                    if(times.Count(t=>t.Name==names[n]&&t.Seconds==expected[n])!=1)
                        throw new Exception("Windows PDF/OCR check failed for "+names[n]+": "+string.Join("; ",reportPages.Select(p=>p.Error)));
                    Console.WriteLine("PASS: Windows PDF/OCR "+names[n]+" = "+AbeProcessTime.Format(expected[n]));
                }
                var mergedPages=AbePdfReportReader.Read(new[]{Path.Combine(fixtures,"Merged-sheet-reports.pdf")},System.Threading.CancellationToken.None);
                var mergedTimes=mergedPages.SelectMany(p=>AbeSheetReports.Parse(p,names)).ToList();
                for(int n=0;n<names.Length;n++)
                    if(mergedTimes.Count(t=>t.Name==names[n]&&t.Seconds==expected[n])!=1)
                        throw new Exception("Merged Windows PDF/OCR check failed for "+names[n]+": "+string.Join("; ",mergedPages.Select(p=>p.Error)));
                Console.WriteLine("PASS: Windows PDF/OCR merged three-page report");
                string importFolder=Path.Combine(Path.GetTempPath(),"HanlonMergedReportChecks",Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(importFolder,"Reports"));
                try
                {
                    foreach(string name in names)
                        foreach(string extension in new[]{".nc",".sy2"})
                            File.Copy(Path.Combine(source,"Tests","Fixtures","ABE","240175",name+extension),Path.Combine(importFolder,name+extension));
                    File.Copy(Path.Combine(fixtures,"Merged-sheet-reports.pdf"),Path.Combine(importFolder,"Reports","All thicknesses.pdf"));
                    var imported=AbeImportBatch.ReadFolder(importFolder,System.Threading.CancellationToken.None);
                    if(imported.Items.Count!=names.Length||imported.ReportWarnings.Count!=0)
                        throw new Exception("Merged folder import failed: "+string.Join("; ",imported.ReportWarnings));
                    for(int n=0;n<names.Length;n++)
                    {
                        var reference=imported.Items.Single(i=>i.Name==names[n]).Document?.Reference;
                        if(reference?.ProcessTime?.Seconds!=expected[n]||reference.ProcessTime.ReportName!="All thicknesses.pdf")
                            throw new Exception("Merged folder import runtime/provenance mismatch for "+names[n]);
                    }
                    Console.WriteLine("PASS: Single folder imports three NC/SY2 sheets and one merged PDF with correct times");
                }
                finally { Directory.Delete(importFolder,true); }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int passed = 0;
            void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
            // This is a Windows pixel check: a zoomed NC marking must remain a
            // thin freshly rendered stroke, rather than an enlarged PNG band.
            var strokeDocument=new AbeNestDocument();
            strokeDocument.Cuts.Add(new Clipper2Lib.PathD{new Clipper2Lib.PointD(0,0),new Clipper2Lib.PointD(100,0),
                new Clipper2Lib.PointD(100,50),new Clipper2Lib.PointD(0,50),new Clipper2Lib.PointD(0,0)});
            strokeDocument.Marks.Add(new Clipper2Lib.PathD{new Clipper2Lib.PointD(50,10),new Clipper2Lib.PointD(50,40)});
            using(var vector=new AbeVectorPreview(strokeDocument))
            {
                var focus=new AbePreviewFrame(strokeDocument).Project(50,25);
                foreach(float zoom in new[]{1F,8F,32F})
                {
                    using var bitmap=new Bitmap(192,128);
                    using(Graphics graphics=Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.White);
                        vector.Draw(graphics,new RectangleF(96-focus.X*zoom,64-focus.Y*zoom,vector.Width*zoom,vector.Height*zoom));
                    }
                    int ink=Enumerable.Range(0,bitmap.Width).Count(x=>{Color c=bitmap.GetPixel(x,64);return c.R>150&&c.G<190&&c.B<140;});
                    Check(ink>=1&&ink<=3,"NC marking stays sharp at "+zoom+"x zoom");
                }
            }
            string temporary = Path.Combine(Path.GetTempPath(), "HanlonNavigationChecks", Guid.NewGuid().ToString("N"));
            string exports = Path.Combine(temporary, "Exports"); Directory.CreateDirectory(exports);
            try
            {
                string fixture = args.Length > 0 ? args[0] : Path.Combine(source, "Tests", "Fixtures", "ABE", "Phase10and11", "PowerFab-request.xml");
                var snapshot = NestingSnapshot.FromDocument(XDocument.Load(fixture));
                using var form = (NestingWindow)Activator.CreateInstance(typeof(NestingWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object?[] { snapshot, Path.Combine(exports, "request.xml"), null, null }, null)!;
                T Field<T>(string name) => (T)typeof(NestingWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                void Invoke(string name, params object?[] values) => typeof(NestingWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, values);
                form.Show(); Application.DoEvents();
                var parts = Field<DataGridView>("partsGrid");
                Check(parts.ContextMenuStrip?.Items.Count == 2, "Both bulk selection actions remain available in the grid menu");
                ((ToolStripMenuItem)parts.ContextMenuStrip!.Items[1]).PerformClick();
                Check(snapshot.Parts.Rows.Cast<DataRow>().All(r => !(bool)r["Select"]), "Select none unchecks every part");
                ((ToolStripMenuItem)parts.ContextMenuStrip.Items[0]).PerformClick();
                Check(snapshot.Parts.Rows.Cast<DataRow>().All(r => (bool)r["Select"]), "Select all checks every part");
                IEnumerable<Control> Descendants(Control parent)
                {
                    foreach (Control c in parent.Controls) { yield return c; foreach (Control child in Descendants(c)) yield return child; }
                }
                Check(!Descendants(form).OfType<Button>().Any(b => b.Text is "Select all" or "Select none"), "Bulk selection has no visible buttons");

                var picker = Field<ComboBox>("resultPicker");
                var previews = Field<TabControl>("previews");
                var frame = Field<Control>("previewNavigation");
                Field<TabControl>("pages").SelectedTab = Field<TabPage>("resultPage");
                Field<Label>("resultSummary").Text = "Constant-height summary\nSheet navigation check\n10 mm / 20 mm";
                Color Expected() => picker.SelectedIndex == 0 ? Color.Blue : Color.Red;
                void RenderSelected()
                {
                    var next = new List<TabPage>();
                    foreach (string title in new[] { "Nest", "Remnant" })
                    {
                        var bitmap = new Bitmap(600, 300);
                        using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(Expected());
                        var page = new TabPage(title + " | " + picker.SelectedItem);
                        page.Controls.Add(new ZoomableImageView(bitmap)); next.Add(page);
                    }
                    Invoke("ReplacePreviewPages", new object?[] { next });
                }
                // Existing picker/arrow handlers run normally; this adapter only
                // supplies synthetic images instead of invoking a nesting engine.
                EventHandler syntheticNavigation = (_, _) => RenderSelected();
                picker.SelectedIndexChanged += syntheticNavigation;
                picker.Items.AddRange(new object[] { "10 mm", "20 mm" }); picker.Visible = true;
                Field<Control>("resultActions").Visible = true;
                picker.SelectedIndex = 0; Application.DoEvents();
                void CheckDrawing(int index)
                {
                    Application.DoEvents();
                    Check(picker.SelectedIndex == index, "Requested sheet selected");
                    Check(frame.Visible && frame.Width > 0 && frame.Height > 0, "Preview frame visible without a resize or summary change");
                    Check(previews.TabCount == 2 && previews.SelectedIndex == 0, "Replacement selects Nest, with remnant available");
                    double drawingCentre=previews.Top+previews.DisplayRectangle.Top+previews.DisplayRectangle.Height/2.0;
                    Check(new[]{Field<Button>("previousSheetButton"),Field<Button>("nextSheetButton")}
                        .All(b=>Math.Abs(b.Top+b.Height/2.0-drawingCentre)<=1),"Sheet arrows align with the drawing viewport centre");
                    var view = previews.SelectedTab!.Controls.OfType<ZoomableImageView>().Single();
                    Check(view.Visible && view.ClientSize.Width > 0 && view.ClientSize.Height > 0, "Current image control has a visible drawing area");
                    using var pixels = new Bitmap(view.Width, view.Height);
                    view.DrawToBitmap(pixels, view.ClientRectangle);
                    Check(pixels.GetPixel(pixels.Width / 2, pixels.Height / 2).ToArgb() == Expected().ToArgb(), "Current sheet pixels replace the previous sheet");
                }
                CheckDrawing(0);
                for (int i = 0; i < 20; i++)
                {
                    previews.SelectedIndex = 1; // Leave a remnant active before changing sheets.
                    Field<Button>("nextSheetButton").PerformClick(); CheckDrawing(1);
                    Field<Button>("previousSheetButton").PerformClick(); CheckDrawing(0);
                }
                frame.Hide(); RenderSelected(); CheckDrawing(0);
                picker.SelectedIndex = 1; CheckDrawing(1);
                picker.SelectedIndex = 0; CheckDrawing(0);
                picker.SelectedIndexChanged -= syntheticNavigation;

                // Exercise the real result picker and saved-review path with two
                // thicknesses and on-disk receipts, without executing any API write.
                var choiceType = typeof(NestingWindow).GetNestedType("ResultChoice", BindingFlags.NonPublic)!;
                var review = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(choiceType))!;
                var loadedSheets = new List<LoadedNestResult>();
                foreach (var group in PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>())
                    .Where(g => g.ThicknessMm is 10 or 20).OrderBy(g => g.ThicknessMm))
                {
                    int number = loadedSheets.Count;
                    string stem = "native-review-" + number;
                    using (var bitmap = new Bitmap(600, 300))
                    {
                        using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(number == 0 ? Color.Blue : Color.Red);
                        bitmap.Save(Path.Combine(exports, stem + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    var package = PlateSelection.ForMaterial(snapshot, group, new NestMaterial
                    { Source = "Purchase", Shape = group.Shape, Grade = group.Grade, Thickness = group.ThicknessMm,
                        Length = 3000, Width = 1500, Dimensions = group.ThicknessMm + "X1500", Unit = "MM" }, new LayoutConditions());
                    package.Status = "completed"; package.LayoutSource = "Native saved-review fixture";
                    package.NestName = group.ThicknessMm + " mm"; package.RequisitionNumber = "REQNATIVE";
                    package.NestImageFile = stem + ".png";
                    package.Remnants.Add(new NestRemnant { RemnantId = RemnantGeometry.NumericExternalId(package.NestId, 1),
                        ImageFile = stem + ".png", Edge = "Right", X = 500, Y = 0, Length = 2500, Width = 1500, Area = 3750000 });
                    string path = Path.Combine(exports, stem + ".json");
                    File.WriteAllText(path, JsonSerializer.Serialize(package, LoadedNestResult.JsonOptions));
                    var loaded = LoadedNestResult.Load(path, snapshot); loadedSheets.Add(loaded);
                    object choice = Activator.CreateInstance(choiceType, true)!;
                    choiceType.GetProperty("Loaded")!.SetValue(choice, loaded); review.Add(choice);
                }
                Check(loadedSheets.Count == 2, "Saved-review fixture contains the 10 mm and 20 mm sheets");
                Invoke("PresentResults", review); picker.SelectedIndex = 1; previews.SelectedIndex = 1;
                string receiptFolder = Path.Combine(exports, "SavedNestReceipts"); Directory.CreateDirectory(receiptFolder);
                void Confirm(int index)
                {
                    var sheet = loadedSheets[index];
                    var receipt = new SavedNestReceipt { WorkOrderId = snapshot.WorkOrderID, NestId = sheet.Result.NestId,
                        NestName = sheet.Result.NestName, Parts = sheet.Result.Parts };
                    File.WriteAllText(Path.Combine(receiptFolder, index + ".json"), JsonSerializer.Serialize(receipt, LoadedNestResult.JsonOptions));
                }
                Confirm(0); Invoke("RefreshSavedResults"); Invoke("RefreshResultReview");
                Check(picker.Items.Count == 2 && picker.SelectedIndex == 1 && previews.SelectedIndex == 1,
                    "Partial save preserves both sheets, the selected thickness and the remnant tab");
                Check(Field<Button>("saveButton").Text == "Save remaining nests", "Partial review offers only remaining sheets for saving");
                Confirm(1); Invoke("RefreshSavedResults"); Invoke("RefreshResultReview");
                Check(picker.Visible && picker.Items.Count == 2 && picker.SelectedIndex == 1,
                    "Save-all completion retains the sheet selector and its current selection");
                Check(Field<TabControl>("pages").SelectedTab == Field<TabPage>("resultPage") && previews.SelectedIndex == 1,
                    "Save-all completion stays in the current result and remnant view");
                Check(Field<Button>("saveButton").Text == "All nests saved" && !Field<Button>("saveButton").Enabled,
                    "A fully saved batch cannot be submitted again from the review");
                Check(Field<Button>("previousSheetButton").Visible && Field<Button>("previousSheetButton").Enabled,
                    "Previous remains available after saving both thicknesses");
                Field<Button>("previousSheetButton").PerformClick(); CheckDrawing(0);
                Field<Button>("nextSheetButton").PerformClick(); CheckDrawing(1);
                Invoke("InvalidateForInputs", "Inventory refreshed.");
                Check(picker.Visible && picker.Items.Count == 2 && picker.SelectedIndex == 1,
                    "Inventory refresh does not erase an already-saved review batch");
                form.Close();
                Console.WriteLine($"PASS: {passed} native preview navigation, pixel and bulk-selection checks. No PowerFab API calls were made.");
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }
    }
}

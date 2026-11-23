using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Clipper2Lib;

namespace Hanlon.PowerFab.Nesting
{
    internal static class BatchSaveChecks
    {
        private static int passed;
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "HanlonNestingBatchChecks", Guid.NewGuid().ToString("N"));
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            passed++;
        }

        public static void Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--report-checks"){AbeSheetReportChecks.Run();return;}
            if(args.Length==1&&args[0]=="--phase10-checks"){Phase10And11Checks.Run();return;}
            if(args.Length==3&&args[0]=="--abe-nc-preview")
            {
                var doc=AbeNestImport.ReadNcOnly(args[1]);
                File.WriteAllText(args[2],AbeNestImport.PreviewSvg(doc));
                Console.WriteLine($"ABE NC preview: {doc.Cuts.Count} cutting paths, {doc.Marks.Count} marking paths, {doc.ArcCount} arcs including {doc.FullCircleCount} full circles. SY2 not supplied; part count unconfirmed.");
                return;
            }
            if(args.Length==4&&args[0]=="--abe-preview")
            {
                var doc=AbeNestImport.Read(args[1],args[2]);
                File.WriteAllText(args[3],AbeNestImport.PreviewSvg(doc));
                Console.WriteLine($"ABE preview: {doc.Placements.Count} placements, {doc.Placements.Select(p=>p.Name).Distinct().Count()} names, {doc.Cuts.Count} cutting paths, {doc.Marks.Count} marking paths.");
                return;
            }
            Directory.CreateDirectory(Root);
            Check(RequisitionNumbering.Next(new[] { "REQ183", "test nnr_PLT_NestRequest" }) == "REQ184", "Native REQ sequence");
            Check(RequisitionNumbering.Next(new[] { "REQ9", "REQ100", "req99" }) == "REQ101", "Numeric suffix ordering");
            Check(RequisitionNumbering.Next(new[] { "REQ00183" }) == "REQ00184", "Padded sequence");
            SaveMixedBatch();
            ReviewConfirmedBatch();
            InterveningRequisition();
            ContinuePartialBatch();
            DeletedConfirmedRequisition();
            ReadFailure();
            ManualAndAutomaticRequisitions();
            ManualRequisitionsWithoutNumberRead();
            ImageFilenames();
            AbeDisplayNames();
            NumericRemnantIds();
            OldRemnantPolicyDrafts();
            Console.WriteLine($"PASS: {passed} focused batch-save checks. PowerFab responses and numbering reads were mocked.");
            int packing = PackingChecks.Run();
            int inventory = RemnantRefreshChecks.Run();
            int abe = AbeImportChecks.Run();
            int abeNc = AbeNcGeometryChecks.Run();
            int h0373 = H0373Checks.Run();
            int sheetSelection = AbeSheetSelectionChecks.Run();
            int batchAbe = AbeBatchChecks.Run();
            int reports = AbeSheetReportChecks.Run();
            int namesAndPackers = AbeNamesAndPackersChecks.Run();
            int phase10 = Phase10And11Checks.Run();
            int preview = PreviewViewportChecks.Run();
            int window = WindowSizingChecks.Run();
            Console.WriteLine($"PASS: {passed + packing + inventory + abe + abeNc + h0373 + sheetSelection + batchAbe + reports + namesAndPackers + phase10 + preview + window} total batch-save, packing, inventory/remnant, ABE import and window-sizing checks.");
        }

        private static NestingSnapshot Snapshot()
        {
            XElement Part(string mark, int qty, decimal length, decimal width, decimal thick) =>
                new XElement("WorkOrderPart", new XElement("JobNumber", "test nnr"),
                    new XElement("MainMark", "A"), new XElement("PieceMark", mark), new XElement("Quantity", qty),
                    new XElement("Shape", "PLT"), new XElement("Grade", "250"),
                    new XElement("Dimensions", thick + "X" + width),
                    new XElement("Length", new XAttribute("UOM", "MM"), length),
                    new XElement("Width", new XAttribute("UOM", "MM"), width),
                    new XElement("PlateThickness", new XAttribute("UOM", "MM"), thick));
            return NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",
                new XElement("WorkOrderID", "5774"),
                new XElement("WorkOrderParts", Part("PL10", 2, 240, 140, 10), Part("PL12", 1, 100, 100, 12)),
                new XElement("AvailableInventory", new XElement("InventoryItem",
                    new XElement("ID", "46766"), new XElement("ExternalInventoryLinkID", "9938"),
                    new XElement("PONumber", "NNR Test"), new XElement("Quantity", 1),
                    new XElement("Shape", "PLT"), new XElement("Grade", "250"),
                    new XElement("Dimensions", new XAttribute("Metric", true), "10X200"),
                    new XElement("Length", new XAttribute("UOM", "MM"), 300),
                    new XElement("Width", new XAttribute("UOM", "MM"), 200),
                    new XElement("PlateThickness", new XAttribute("UOM", "MM"), 10))))));
        }

        private static List<LoadedNestResult> Layouts(NestingSnapshot snapshot, string folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "sheet.png"), Png);
            var groups = PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>());
            Check(groups.Count == 2, "Separate thickness groups");
            string batch = Guid.NewGuid().ToString("N");
            var shapes = new Dictionary<string, DxfShape>();
            foreach (var node in snapshot.PartNodes())
            {
                double length = (double)decimal.Parse(NestingSnapshot.Field(node, "Length"));
                double width = (double)decimal.Parse(NestingSnapshot.Field(node, "Width"));
                shapes[LoadedNestResult.PartKey(node)] = new DxfShape { Source = "fixture.dxf", Outer = new PathD
                    { new PointD(0,0), new PointD(length,0), new PointD(length,width), new PointD(0,width) } };
            }
            var layouts = new List<LoadedNestResult>();
            foreach (var group in groups)
            {
                var choice = new MaterialPlan
                {
                    Group = group, RequisitionNumber = "REQ184", AutoRequisitionNumber = true, RequisitionBatchId = batch,
                    NewSheet = new NestMaterial { Source = "Purchase", Shape = "PLT", Grade = "250",
                        Dimensions = group.ThicknessMm + "X200", Thickness = group.ThicknessMm,
                        Length = 300, Width = 200, Unit = "MM", Metric = true }
                };
                if (group.ThicknessMm == 10) choice.StockIndexes.Add(0);
                var plans = NestingBatch.Calculate(snapshot, choice, Array.Empty<SavedNestReceipt>(),
                    new LayoutConditions(), shapes, CancellationToken.None);
                foreach (var plan in plans)
                {
                    var result = plan.Package;
                    result.NestImageFile = "sheet.png";
                    foreach (var remnant in result.Remnants) remnant.ImageFile = "sheet.png";
                    string path = Path.Combine(folder, result.NestId + ".json");
                    File.WriteAllText(path, JsonSerializer.Serialize(result, LoadedNestResult.JsonOptions));
                    layouts.Add(LoadedNestResult.Load(path, snapshot));
                }
            }
            Check(layouts.Count == 3, "Three calculated sheets across two thickness groups");
            Check(layouts.Count(l => l.Result.Material.Source == "Requirement") == 1, "Selected stock uses size requirement");
            Check(layouts.Count(l => l.Result.Material.Source == "Purchase") == 2, "Shortage uses entered new sheets");
            Check(layouts.All(l => l.Result.RequisitionBatchId == batch && l.Result.RequisitionNumber == "REQ184"), "One preview REQ across all calculated sheets");
            return layouts;
        }

        private static void SaveMixedBatch()
        {
            string folder = Path.Combine(Root, "mixed");
            var snapshot = Snapshot(); var layouts = Layouts(snapshot, folder); var server = new FakePowerFab();
            var api = server.Api();
            using (api.BeginRequisitionSave(true))
            {
                api.PrepareRequisitionBatch(layouts, folder);
                foreach (var layout in layouts) api.Save(layout, snapshot, folder);
            }
            var receipts = PowerFabNestApi.ReadReceipts(folder);
            Check(server.Created.SequenceEqual(new[] { "REQ184" }), "Exactly one requisition created for the mixed batch");
            Check(server.SentNumbers.Count == 3 && server.SentNumbers.All(n => n == "REQ184"), "All SaveNest requests target REQ184");
            Check(receipts.Count == 3 && receipts.All(r => r.RequisitionNumber == "REQ184"), "Three confirmed receipts on one REQ");
            Check(receipts.Select(r => r.RequisitionItemId).Distinct().Count() == 3, "Sheets have distinct confirmed requisition items");
            Check(receipts.Select(r => r.NestId).Distinct().Count() == 3, "Unique nest identity per sheet");
            Check(server.SentImageNames.Contains("Hanlon-Nest-REQ184-10mm.png")
                && server.SentImageNames.Contains("Hanlon-Nest-REQ184-10mm-2.png")
                && server.SentImageNames.Contains("Hanlon-Nest-REQ184-12mm.png"), "Same-thickness sheets have numbered image names; another thickness starts separately");
            Check(receipts.Select(r => r.NestImageFilename).OrderBy(n => n)
                .SequenceEqual(server.SentImageNames.OrderBy(n => n)), "Confirmed receipts retain the exact submitted image filenames");
            Check(layouts.All(l => JsonSerializer.Deserialize<NestResultPackage>(File.ReadAllText(l.SourceFile), LoadedNestResult.JsonOptions)!.RequisitionNumber == "REQ184"), "Shared number persisted into every result file");
            Check(server.ForcedLinks == 0, "No legacy link 9938 forced for stock-size/new-sheet requirements");
            Check(server.LockCount == 1 && server.ReleaseCount == 1, "One numbering lock covers the whole Save-all batch");
            int before = server.SentNumbers.Count;
            try { api.Save(layouts[0], snapshot, folder); throw new Exception("Duplicate save unexpectedly accepted"); }
            catch (InvalidOperationException) { }
            Check(server.SentNumbers.Count == before, "Confirmed layout is not submitted twice");
        }

        private static void ReviewConfirmedBatch()
        {
            string folder = Path.Combine(Root, "review-saved-batch");
            var snapshot = Snapshot(); var layouts = Layouts(snapshot, folder);
            var ids = layouts.Select(l => l.Result.NestId).ToArray();
            var images = layouts.Select(l => l.NestImage.ToArray()).ToArray();
            var server = new FakePowerFab(); var api = server.Api();
            Check(NestReview.Pending(layouts, Array.Empty<SavedNestReceipt>()).SequenceEqual(layouts), "Review starts with the complete ordered unsaved batch");
            using (api.BeginRequisitionSave(true))
            {
                api.PrepareRequisitionBatch(layouts, folder);
                api.Save(layouts[0], snapshot, folder);
                var partial = PowerFabNestApi.ReadReceipts(folder);
                Check(NestReview.IsSaved(layouts[0], partial), "Confirmed first sheet is viewable as saved");
                Check(NestReview.Pending(layouts, partial).SequenceEqual(layouts.Skip(1)), "A partial save leaves only the unconfirmed sheets eligible, in order");
                foreach (var sheet in NestReview.Pending(layouts, partial)) api.Save(sheet, snapshot, folder);
            }
            var receipts = PowerFabNestApi.ReadReceipts(folder);
            snapshot.RefreshQuantities(receipts);
            Check(NestReview.Pending(layouts, receipts).Count == 0, "A completed review batch has no sheet eligible for saving again");
            Check(layouts.All(l => NestReview.IsSaved(l, receipts)), "Every thickness remains identifiable as a saved sheet after stock refresh");
            Check(layouts.Select(l => l.Result.NestId).SequenceEqual(ids), "Saving preserves all sheet identities and their original review order");
            Check(layouts.Select((l, i) => l.NestImage.SequenceEqual(images[i])).All(same => same), "Saved sheets retain their preview bytes");
            Check(server.SentNumbers.Count == layouts.Count, "Continuing the batch submits each sheet exactly once");
            foreach (var receipt in receipts) { receipt.InactiveInPowerFab = true; receipt.PartsReleased = true; }
            Check(NestReview.Pending(layouts, receipts).Count == 0, "Removed or released receipts do not make old Nest IDs saveable again");
        }

        private static void ManualAndAutomaticRequisitions()
        {
            string folder=Path.Combine(Root,"manual-and-auto");
            var snapshot=Snapshot();var layouts=Layouts(snapshot,folder);var server=new FakePowerFab();var api=server.Api();
            layouts[0].Result.AutoRequisitionNumber=false;layouts[0].Result.RequisitionNumber="REQ900";
            layouts[0].Result.RequisitionBatchId="manual-sheet";
            using(api.BeginRequisitionSave(true))
            {
                api.PrepareRequisitionBatch(layouts,folder);
                Check(layouts[0].Result.RequisitionNumber=="REQ900","Manual requisition is not overwritten during batch preparation");
                Check(layouts.Skip(1).All(l=>l.Result.RequisitionNumber=="REQ901"),"Automatic numbers avoid pending manually entered references");
                foreach(var layout in layouts)api.Save(layout,snapshot,folder);
            }
            Check(server.SentNumbers.SequenceEqual(new[]{"REQ900","REQ901","REQ901"}),"Actual SaveNest XML preserves the manual reference and shares the automatic one");
            var saved=PowerFabNestApi.ReadReceipts(folder);
            Check(saved.Count(r=>r.RequisitionNumber=="REQ900")==1&&saved.Count(r=>r.RequisitionNumber=="REQ901")==2,"Manual/automatic receipts retain distinct references");
        }

        private static void ManualRequisitionsWithoutNumberRead()
        {
            string folder=Path.Combine(Root,"manual-only");
            var snapshot=Snapshot();var layouts=Layouts(snapshot,folder);var server=new FakePowerFab{FailRead=true};var api=server.Api();
            foreach(var layout in layouts){layout.Result.AutoRequisitionNumber=false;layout.Result.RequisitionNumber="HANLON-CUSTOM-42";}
            api.PrepareRequisitionBatch(layouts,folder);
            foreach(var layout in layouts)api.Save(layout,snapshot,folder);
            Check(server.SentNumbers.Count==3&&server.SentNumbers.All(n=>n=="HANLON-CUSTOM-42"),"Manual-only saves preserve custom text without automatic numbering reads");
            Check(server.LockCount==0&&server.ReleaseCount==0,"Manual-only saves do not acquire the automatic numbering lock");
        }

        private static void InterveningRequisition()
        {
            string folder = Path.Combine(Root, "intervening");
            var snapshot = Snapshot(); var layouts = Layouts(snapshot, folder); var server = new FakePowerFab();
            server.Numbers.Add("REQ184"); var api = server.Api();
            using (api.BeginRequisitionSave(true))
            {
                api.PrepareRequisitionBatch(layouts, folder);
                foreach (var layout in layouts) api.Save(layout, snapshot, folder);
            }
            Check(server.Created.SequenceEqual(new[] { "REQ185" }), "Intervening native REQ advances the whole batch once");
            Check(server.SentNumbers.All(n => n == "REQ185"), "No sheet retains the stale preview number");
            Check(server.SentImageNames.All(n => n.StartsWith("Hanlon-Nest-REQ185-", StringComparison.Ordinal)), "Image filenames use the final requisition after numbering advances");
        }

        private static void ImageFilenames()
        {
            var snapshot = Snapshot();
            var layout = Layouts(snapshot, Path.Combine(Root, "image-names"))[0];
            var result = layout.Result;
            result.RequisitionNumber = "REQ188";
            result.Material.Unit = "MM";
            string Filename(IReadOnlyCollection<SavedNestReceipt>? previous = null)
                => PowerFabNestApi.BuildSaveRequest(layout, snapshot, previous).SaveNest[0].NestResult[0].NestImageFilename;
            foreach (decimal thickness in new[] { 10m, 12m, 16m, 20m })
            {
                result.Material.Thickness = thickness;
                Check(Filename() == "Hanlon-Nest-REQ188-" + thickness.ToString(CultureInfo.InvariantCulture) + "mm.png", "Requisition and thickness name: " + thickness);
            }
            result.Material.Thickness = 10.000m;
            var previous = new List<SavedNestReceipt>
            {
                new SavedNestReceipt { NestImageFilename = "hanlon-nest-req188-10MM.PNG" },
                new SavedNestReceipt { NestImageFilename = "Hanlon-Nest-REQ188-10mm-2.png" },
                new SavedNestReceipt { NestImageFilename = "Hanlon-Nest-REQ189-20mm.png" },
                new SavedNestReceipt() // Old receipts do not have an image-name field.
            };
            Check(Filename(previous) == "Hanlon-Nest-REQ188-10mm-3.png", "Third matching sheet is distinct even with mixed-case prior names");
            result.Material.Thickness = 20m;
            Check(Filename(previous) == "Hanlon-Nest-REQ188-20mm.png", "Another thickness starts without a suffix");
            result.RequisitionNumber = "REQ189";
            result.Material.Thickness = 10m;
            Check(Filename(previous) == "Hanlon-Nest-REQ189-10mm.png", "Another requisition starts its own thickness sequence");
            result.RequisitionNumber = " req188 ";
            result.Material.Thickness = 3.2m;
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Check(Filename() == "Hanlon-Nest-REQ188-3.2mm.png", "Fractional millimetres use a dot independently of Windows number settings");
            }
            finally { CultureInfo.CurrentCulture = culture; }
            Check(result.RequisitionNumber == " req188 ", "Filename normalization does not change the purchasing reference");
            result.Material.Unit = "IN";
            result.Material.Thickness = 0.5m;
            Check(Filename() == "Hanlon-Nest-REQ188-12.7mm.png", "Inch material is labelled with its converted millimetre thickness");
            result.RequisitionNumber = "REQ188/Part:A";
            Check(Filename() == "Hanlon-Nest-REQ188-PART-A-12.7mm.png", "Custom requisition text cannot introduce a directory or invalid Windows filename");
            result.RequisitionNumber = "";
            string fallback = Filename();
            Check(fallback.StartsWith("Hanlon-Nest-", StringComparison.Ordinal) && fallback.EndsWith(".png", StringComparison.Ordinal)
                && fallback == Filename(), "Stock-only save without a requisition keeps a stable per-nest filename");
            Check(Convert.FromBase64String(PowerFabNestApi.BuildSaveRequest(layout, snapshot).SaveNest[0].NestResult[0].NestImageBase64)
                .SequenceEqual(layout.NestImage), "Filename changes preserve the exact image payload");
            var legacy = JsonSerializer.Deserialize<SavedNestReceipt>("{\"nestId\":\"old\",\"requisitionNumber\":\"REQ188\"}", LoadedNestResult.JsonOptions)!;
            Check(legacy.NestImageFilename == "", "Existing saved receipts load without migration");
        }

        private static void AbeDisplayNames()
        {
            NestResultPackage Sheet(string program, decimal thickness, string job = "H0306") => new NestResultPackage
            {
                AbeImport = new AbeImportReference { ProgramName = program },
                Material = new NestMaterial { Thickness = thickness, Unit = "MM" },
                Parts = new List<NestPart> { new NestPart { JobNumber = job, Phase = "Phase 99", Quantity = 1 } },
                Remnants = new List<NestRemnant> { new NestRemnant { RemnantId = Guid.NewGuid().ToString("N"), Area = 12345, Length = 3000, Width = 1500 } }
            };
            const string base10 = "H0306-PHASE10+11-10MM";
            var first = Sheet("H0306-PHASE10+11-10MM", 10m);
            var second = Sheet("H0306_PHASE10+Phase11_10MM-02", 10m);
            var third = Sheet("H0306-PHASE10+11-10MM-03", 10m);
            var twenty = Sheet("H0306-PHASE10+11-20MM", 20m);
            string identity = first.NestId, remnantId = first.Remnants[0].RemnantId;
            Check(NestNaming.ForAbe(first) == base10, "Sheet name is the exact ABE program name");
            NestNaming.AssignAbeNames(new[] { first, twenty });
            Check(first.NestName == base10 && twenty.NestName == "H0306-PHASE10+11-20MM", "Single sheets retain their exact source names");
            Check(first.Remnants[0].Name == first.NestName, "Remnant name is exactly the parent nest name");
            NestNaming.AssignAbeNames(new[] { first, twenty, second, third });
            Check(first.NestName == base10 && second.NestName == "H0306_PHASE10+Phase11_10MM-02" && third.NestName == base10 + "-03", "Same-thickness sheets retain source capitalization, separators and suffixes without renumbering");
            Check(twenty.NestName == "H0306-PHASE10+11-20MM", "Another thickness retains its source name");
            Check(new[] { first, second, third }.All(r => r.Remnants.All(m => m.Name == r.NestName)), "Every remnant follows its exact parent sheet name");
            Check(first.NestId == identity && first.Remnants[0].RemnantId == remnantId, "Readable names retain the original backend identities");
            Check(first.Remnants[0].Area == 12345 && first.Remnants[0].Length == 3000 && first.Remnants[0].Width == 1500, "Name changes preserve remnant geometry");
            var restored = JsonSerializer.Deserialize<NestResultPackage>(JsonSerializer.Serialize(second, LoadedNestResult.JsonOptions), LoadedNestResult.JsonOptions)!;
            Check(NestNaming.Display(restored) == "H0306_PHASE10+Phase11_10MM-02" && restored.Remnants[0].Name == "H0306_PHASE10+Phase11_10MM-02", "A resumed sheet retains its exact source suffix and separators");
            NestNaming.AssignAbeNames(new[] { first, twenty, second, third });
            Check(second.NestName == "H0306_PHASE10+Phase11_10MM-02", "Repeating full-batch name assignment is stable");
            var other = Sheet("H0413-PHASE1 - Type B-3.2MM", 3.2m, "H0413");
            NestNaming.AssignAbeNames(new[] { other });
            Check(other.NestName == "H0413-PHASE1 - Type B-3.2MM", "Source phase capitalization and spaces remain exact");
            Check(NestNaming.ForAbe(Sheet("H0306-PHASE2+10-10MM", 10m)) == "H0306-PHASE2+10-10MM", "Source phase text is not rebuilt from PowerFab");
            var missingProgram = Sheet("", 16m, "H073");
            missingProgram.Parts[0].Phase = "Phase 02";
            Check(NestNaming.ForAbe(missingProgram) == "H073-Phase 02-16MM", "Missing ABE filename uses matched PowerFab job/phase data");
            var mapped = Sheet("H0306-PHASE10+11-19MM", 20m);
            Check(NestNaming.ForAbe(mapped) == "H0306-PHASE10+11-19MM", "Source name retains its thickness text while material mapping remains separate");
            first.Material.Unit = "IN"; first.Material.Thickness = 0.5m;
            Check(NestNaming.ForAbe(first) == base10, "Material unit conversion does not alter the source sheet name");
            Check(NestNaming.ForAbe(Sheet("240175_02_PHASE90C_5MM", 5m, "240175")) == "240175_02_PHASE90C_5MM", "Source underscores remain literal when present in ABE");
            Check(NestNaming.ForAbe(Sheet("OTHER-PHASE90C-5MM", 5m, "240175")) == "OTHER-PHASE90C-5MM", "Source sheet name and matched job identity are separate");
            foreach (string exact in new[] { "240175-02-PHASE90C-5MM", "240175-02-PHASE90C-12MM", "H0413-NFF-PHASE1-TYPEB-10MM-01",
                "H0413-NFF-Phase  1 - Type B-10mm-01" })
            {
                var sheet = Sheet(exact, 5m);
                sheet.NestName = "Old generated name";
                Check(NestNaming.Display(sheet) == exact, "Exact ABE name overrides an older generated display name: " + exact);
                NestNaming.AssignAbeNames(new[] { sheet });
                Check(sheet.NestName == exact && sheet.Remnants.All(r => r.Name == exact), "Sheet and remnant retain every source character: " + exact);
            }
            var duplicate = Sheet(base10, 10m);
            NestNaming.AssignAbeNames(new[] { first, duplicate });
            Check(first.NestName == base10 && duplicate.NestName == base10 && first.NestId != duplicate.NestId,
                "Repeated source names stay exact without invented suffixes; backend identities remain distinct");
            var legacy = new NestResultPackage { NestName = "Legacy calculated nest", Remnants = new List<NestRemnant> { new NestRemnant { Name = "Legacy remnant" } } };
            NestNaming.AssignAbeNames(new[] { legacy });
            Check(legacy.NestName == "Legacy calculated nest" && legacy.Remnants[0].Name == "Legacy remnant", "ABE batch assignment leaves legacy calculated results unchanged");
        }

        private static void NumericRemnantIds()
        {
            foreach (string old in new[] { "ad0f244a7d3b48fa9cbe9ff360436950-R1", "b34c03fffc5c4c1abeb6ff3016312800-R1",
                "0", "-1", "+42", "042", "1.0", "1e3", "9223372036854775808", "42;DROP TABLE x", "１２３" })
                Check(!RemnantGeometry.IsNumericExternalId(old), "Reject unsafe/noncanonical remnant ID: " + old);
            Check(RemnantGeometry.IsNumericExternalId("1") && RemnantGeometry.IsNumericExternalId(long.MaxValue.ToString(CultureInfo.InvariantCulture)), "Numeric remnant IDs stay within positive signed 64-bit range");
            const string parent = "ad0f244a7d3b48fa9cbe9ff360436950";
            string numeric = RemnantGeometry.NumericExternalId(parent, 1);
            Check(RemnantGeometry.IsNumericExternalId(numeric) && numeric == RemnantGeometry.NumericExternalId(parent, 1), "Generated remnant identity is numeric and stable");
            Check(numeric != RemnantGeometry.NumericExternalId(parent, 2), "Different remnants on one nest have different external identities");
            var samples = Enumerable.Range(1, 2000).Select(i => RemnantGeometry.NumericExternalId("nest-" + i, 1)).ToArray();
            Check(samples.All(RemnantGeometry.IsNumericExternalId) && samples.Distinct().Count() == samples.Length, "Sampled nest identities are distinct numeric SQL literals");

            int index = 0;
            foreach (string old in new[] { parent + "-R1", "b34c03fffc5c4c1abeb6ff3016312800-R1" })
            {
                string folder = Path.Combine(Root, "legacy-id-" + (++index));
                var snapshot = Snapshot(); var layout = Layouts(snapshot, folder)[0]; var result = layout.Result;
                Check(result.Remnants.All(r => RemnantGeometry.IsNumericExternalId(r.RemnantId)), "Calculated layouts generate numeric remnant IDs");
                string nestId = result.NestId, name = result.Remnants[0].Name;
                decimal area = result.Remnants[0].Area;
                result.Remnants[0].RemnantId = old;
                File.WriteAllText(layout.SourceFile, JsonSerializer.Serialize(result, LoadedNestResult.JsonOptions));
                try { PowerFabNestApi.BuildSaveRequest(layout, snapshot); throw new Exception("Legacy remnant ID escaped the request guard"); }
                catch (InvalidDataException e) { Check(e.Message.Contains("non-numeric"), "Request builder blocks the exact ID format from the native error log"); }
                var server = new FakePowerFab();
                var receipt = server.Api().Save(layout, snapshot, folder);
                string upgraded = result.Remnants[0].RemnantId;
                Check(RemnantGeometry.IsNumericExternalId(upgraded) && server.SentRemnantIds.Contains(upgraded), "Actual Save upgrades an unsent legacy draft before submitting its remnant ID");
                var persisted = JsonSerializer.Deserialize<NestResultPackage>(File.ReadAllText(layout.SourceFile), LoadedNestResult.JsonOptions)!;
                Check(persisted.Remnants[0].RemnantId == upgraded && persisted.NestId == nestId, "Numeric remnant identity is persisted without changing the parent NestID");
                Check(result.Remnants[0].Name == name && result.Remnants[0].Area == area, "Identity workaround preserves remnant display name and geometry");
                Check(receipt.RemnantPowerFabIds.Count > 0 && RemnantCatalog.Read(folder).Any(r => r.ExternalRemnantId == upgraded), "Confirmed PowerFab IDs and outline catalogue remain linked to the upgraded external identity");
                Check(Convert.FromBase64String(PowerFabNestApi.BuildSaveRequest(layout, snapshot).SaveNest[0].NestResult[0].Remnant[0].RemnantImageBase64)
                    .SequenceEqual(layout.RemnantImages[0]!), "Numeric identity does not change the remnant image bytes");
            }
            foreach (string state in new[] { "confirmed", "pending", "transmitted" })
            {
                string folder = Path.Combine(Root, "protect-legacy-" + state);
                var snapshot = Snapshot(); var layout = Layouts(snapshot, folder)[0];
                string old = parent + "-R1"; layout.Result.Remnants[0].RemnantId = old;
                File.WriteAllText(layout.SourceFile, JsonSerializer.Serialize(layout.Result, LoadedNestResult.JsonOptions));
                string stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.Result.NestId))).ToLowerInvariant();
                if (state == "confirmed")
                {
                    string receipts = Path.Combine(folder, "SavedNestReceipts"); Directory.CreateDirectory(receipts);
                    File.WriteAllText(Path.Combine(receipts, stem + ".json"), JsonSerializer.Serialize(new SavedNestReceipt
                    { WorkOrderId = layout.Result.WorkOrderId, NestId = layout.Result.NestId, SheetQuantity = 1 }, LoadedNestResult.JsonOptions));
                }
                else
                {
                    string audit = Path.Combine(folder, "NestApiLog"); Directory.CreateDirectory(audit);
                    if (state == "pending") File.WriteAllText(Path.Combine(audit, stem + ".pending"), "unknown outcome");
                    else File.WriteAllText(Path.Combine(audit, stem + ".request.xml"), "<FabSuiteXMLRequest><SaveNest /></FabSuiteXMLRequest>");
                }
                var server = new FakePowerFab();
                try { server.Api().Save(layout, snapshot, folder); throw new Exception("Previously submitted legacy draft was resaved"); }
                catch (InvalidOperationException) { Check(server.SentNumbers.Count == 0, state + " draft makes no second SaveNest call"); }
                Check(layout.Result.Remnants[0].RemnantId == old, state + " external remnant identity is not changed in memory");
                var persisted = JsonSerializer.Deserialize<NestResultPackage>(File.ReadAllText(layout.SourceFile), LoadedNestResult.JsonOptions)!;
                Check(persisted.Remnants[0].RemnantId == old, state + " external remnant identity is not changed on disk");
            }
        }

        private static void OldRemnantPolicyDrafts()
        {
            foreach (string shape in new[] { "L-shaped", "Bottom" })
            {
                string folder = Path.Combine(Root, "old-remnant-policy-" + shape);
                var snapshot = Snapshot(); var layout = Layouts(snapshot, folder)[0];
                var material = layout.Result.Material; var remnant = layout.Result.Remnants.Single();
                double length = (double)material.Length, width = (double)material.Width, x = (double)remnant.X!.Value;
                LayoutPoint P(double px, double py) => new LayoutPoint { X = px, Y = py };
                remnant.Edge = shape; remnant.X = 0; remnant.Y = 0; remnant.Length = material.Length;
                if (shape == "L-shaped")
                {
                    remnant.Width = material.Width; remnant.Area = (material.Length - (decimal)x) * material.Width + (decimal)x * 10;
                    remnant.Contour = new List<LayoutPoint> { P(0,0), P(length,0), P(length,width), P(x,width), P(x,10), P(0,10) };
                }
                else
                {
                    remnant.Width = 10; remnant.Area = material.Length * 10;
                    remnant.Contour = new List<LayoutPoint> { P(0,0), P(length,0), P(length,10), P(0,10) };
                }
                RemnantGeometry.ValidateContour(remnant);
                File.WriteAllText(layout.SourceFile, JsonSerializer.Serialize(layout.Result, LoadedNestResult.JsonOptions));
                var oldDraft = LoadedNestResult.Load(layout.SourceFile, snapshot); var server = new FakePowerFab();
                try { server.Api().Save(oldDraft, snapshot, folder); throw new Exception("Old " + shape + " remnant was submitted"); }
                catch (InvalidDataException e) { Check(e.Message.Contains("right-hand or bottom rectangle"), "An otherwise valid old " + shape + " draft requires regeneration under the new policy"); }
                Check(server.SentNumbers.Count == 0, "The old " + shape + " remnant makes no SaveNest call");
            }
        }

        private static void ContinuePartialBatch()
        {
            string folder = Path.Combine(Root, "partial");
            var snapshot = Snapshot(); var layouts = Layouts(snapshot, folder); var server = new FakePowerFab { FailRequest = 2 };
            var api = server.Api();
            using (api.BeginRequisitionSave(true))
            {
                api.PrepareRequisitionBatch(layouts, folder);
                api.Save(layouts[0], snapshot, folder);
                try { api.Save(layouts[1], snapshot, folder); throw new Exception("Expected mocked rejection"); }
                catch (InvalidDataException) { }
            }
            Check(PowerFabNestApi.ReadReceipts(folder).Count == 1, "Partial batch preserves first confirmation");
            server.Numbers.Add("REQ200"); var reopened = server.Api();
            using (reopened.BeginRequisitionSave(true))
            {
                reopened.PrepareRequisitionBatch(layouts.Skip(1), folder);
                foreach (var layout in layouts.Skip(1)) reopened.Save(layout, snapshot, folder);
            }
            Check(server.Created.SequenceEqual(new[] { "REQ184" }), "Partial continuation creates no second requisition");
            Check(server.SentNumbers.All(n => n == "REQ184"), "Continuation keeps confirmed REQ184 despite newer REQ200");
            Check(PowerFabNestApi.ReadReceipts(folder).Count == 3, "Partial batch eventually confirms all three sheets");
        }

        private static void DeletedConfirmedRequisition()
        {
            string folder = Path.Combine(Root, "deleted");
            var snapshot = Snapshot(); var layouts = Layouts(snapshot, folder); var server = new FakePowerFab(); var api = server.Api();
            api.Save(layouts[0], snapshot, folder);
            server.Numbers.Remove("REQ184"); int before = server.SentNumbers.Count;
            try { server.Api().Save(layouts[1], snapshot, folder); throw new Exception("Deleted requisition unexpectedly recreated"); }
            catch (InvalidOperationException e) { Check(e.Message.Contains("removed"), "Removed-confirmation error is explicit"); }
            Check(server.SentNumbers.Count == before, "Deleted confirmed REQ prevents another SaveNest request");
        }

        private static void ReadFailure()
        {
            string folder = Path.Combine(Root, "failed-read");
            var snapshot = Snapshot(); var layouts = Layouts(snapshot, folder); var server = new FakePowerFab { FailRead = true };
            try { server.Api().Save(layouts[0], snapshot, folder); throw new Exception("Failed numbering read unexpectedly accepted"); }
            catch (InvalidOperationException) { }
            Check(server.SentNumbers.Count == 0, "Numbering read failure sends no nest");
            Check(server.ReleaseCount == 1, "Numbering failure releases the batch lock");
        }

        private sealed class FakePowerFab
        {
            public List<string> Numbers { get; } = new List<string> { "REQ183", "test nnr_PLT_NestRequest" };
            public List<string> Created { get; } = new List<string>();
            public List<string> SentNumbers { get; } = new List<string>();
            public List<string> SentImageNames { get; } = new List<string>();
            public List<string> SentRemnantIds { get; } = new List<string>();
            public int FailRequest, ForcedLinks, LockCount, ReleaseCount;
            public bool FailRead;
            public PowerFabNestApi Api() => new PowerFabNestApi(Execute, new PowerFabReadModel(sql => new Command(this, sql)));
            private string Execute(string xml)
            {
                var request = XDocument.Parse(xml);
                var save = request.Descendants().Single(e => e.Name.LocalName == "SaveNest");
                string Value(XElement e, string name) => e.Elements().First(x => x.Name.LocalName == name).Value;
                string number = Value(save, "RequisitionNumber"); SentNumbers.Add(number);
                SentImageNames.Add(Value(save.Elements().Single(e => e.Name.LocalName == "NestResult"), "NestImageFilename"));
                SentRemnantIds.AddRange(save.Descendants().Where(e => e.Name.LocalName == "RemnantID").Select(e => e.Value));
                if (request.Descendants().Any(e => e.Name.LocalName == "ExternalInventoryLinkID")) ForcedLinks++;
                if (SentNumbers.Count == FailRequest)
                    return "<FabSuiteXMLResponse><SaveNest><Successful>0</Successful><TotalUnSuccessful>1</TotalUnSuccessful><ErrorMessage>Mock rejection</ErrorMessage></SaveNest></FabSuiteXMLResponse>";
                if (!Numbers.Contains(number)) { Numbers.Add(number); Created.Add(number); }
                var nest = save.Elements().Single(e => e.Name.LocalName == "NestResult");
                string id = Value(nest, "NestID");
                return new XDocument(new XElement("FabSuiteXMLResponse", new XElement("SaveNest",
                    new XElement("Successful", 1), new XElement("TotalSuccessful", 1), new XElement("TotalUnSuccessful", 0),
                    new XElement("SavedNest", new XElement("NestID", id), new XElement("ExternalNestID", 1000 + SentNumbers.Count),
                        new XElement("SavedTo", "Requisition"), new XElement("RequisitionItem",
                            new XElement("RequisitionNumber", number), new XElement("RequisitionItemID", 2000 + SentNumbers.Count))),
                    nest.Elements().Where(e => e.Name.LocalName == "Remnant").Select((r,i) => new XElement("SavedRemnant",
                        new XElement("NestID", id), new XElement("ExternalRemnantID", Value(r, "RemnantID")),
                        new XElement("PowerFabRemnantID", 3000 + 10 * SentNumbers.Count + i)))))).ToString();
            }

            private sealed class Command : IDbCommand
            {
                private readonly FakePowerFab owner;
                public Command(FakePowerFab owner, string sql) { this.owner = owner; CommandText = sql; }
                [AllowNull] public string CommandText { get; set; }
                public int CommandTimeout { get; set; }
                public CommandType CommandType { get; set; }
                public IDbConnection? Connection { get; set; }
                public IDbTransaction? Transaction { get; set; }
                public UpdateRowSource UpdatedRowSource { get; set; }
                public IDataParameterCollection Parameters => throw new NotSupportedException();
                public void Dispose() { }
                public void Cancel() => throw new NotSupportedException();
                public void Prepare() => throw new NotSupportedException();
                public IDbDataParameter CreateParameter() => throw new NotSupportedException();
                public int ExecuteNonQuery() => throw new NotSupportedException();
                public object ExecuteScalar()
                {
                    if (CommandText.Contains("GET_LOCK")) owner.LockCount++;
                    else if (CommandText.Contains("RELEASE_LOCK")) owner.ReleaseCount++;
                    else throw new InvalidOperationException(CommandText);
                    return 1;
                }
                public IDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);
                public IDataReader ExecuteReader(CommandBehavior behavior)
                {
                    if (owner.FailRead) throw new InvalidOperationException("Mock failed requisition read");
                    if (CommandText != "SELECT RequisitionNumber FROM requisitions") throw new InvalidOperationException(CommandText);
                    var table = new DataTable(); table.Columns.Add("RequisitionNumber", typeof(string));
                    foreach (string number in owner.Numbers) table.Rows.Add(number);
                    return table.CreateDataReader();
                }
            }
        }
    }
}

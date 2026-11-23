using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;
using Clipper2Lib;
using Tekla.PowerFab.resPowerFabAPI;

namespace Hanlon.PowerFab.Nesting
{
    internal static partial class RemnantRefreshChecks
    {
        private static int passed;
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "HanlonRemnantRefreshChecks", Guid.NewGuid().ToString("N"));
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        private static string F(XElement e, string field) => NestingSnapshot.Field(e, field);
        private static XElement Mm(string name, decimal value) => new XElement(name, new XAttribute("UOM", "MM"), value);
        private static NestingSnapshot Snapshot(params XElement[] stock) => NestingSnapshot.FromDocument(new XDocument(new XElement("FabSuiteWorkOrder",
            new XElement("WorkOrderID", "5779"), new XElement("CreationDateTime", DateTime.UtcNow.AddHours(-2).ToString("o")),
            new XElement("WorkOrderParts", new XElement("WorkOrderPart", new XElement("JobNumber", "H0306"),
                new XElement("MainMark", "08-B-9"), new XElement("PieceMark", "08-p-5"), new XElement("Quantity", 1),
                new XElement("Shape", "PLT"), new XElement("Grade", "250"), Mm("Length", 100), Mm("Width", 50), Mm("PlateThickness", 10))),
            new XElement("AvailableInventory", stock))));
        private static XElement Sheet(string id, string job = "", decimal thick = 10) => new XElement("InventoryRecord",
            new XElement("Type", "INV"), new XElement("ID", id), new XElement("ExternalInventoryLinkID", "9" + id),
            new XElement("Quantity", 1), new XElement("Shape", "PLT"), new XElement("Grade", "250"), new XElement("Job", job),
            new XElement("Dimensions", new XAttribute("Metric", true), thick + "X500"),
            Mm("Length", 1000), Mm("Width", 500), Mm("Thickness", thick));
        private static XElement Future(string id = "42") => new XElement("InventoryItem", new XElement("Type", "FutureRemnant"), new XElement("RemnantID", id),
            new XElement("Quantity", 1), new XElement("Shape", "PLT"), new XElement("Grade", "250"),
            new XElement("Dimensions", "10X500"), Mm("Length", 1000), Mm("Width", 500), Mm("PlateThickness", 10));
        private static XElement Parent(string job = "H0306", decimal quantity = 1) => new XElement("ParentInventoryItem",
            new XElement("InventoryItemID", "101"), new XElement("Job", job), new XElement("JobReserve", true),
            new XElement("Quantity", quantity), new XElement("PONumber", "301026"), new XElement("ExternalNestID", "1001"));
        private static XElement Remnant(XElement? parent) => new XElement("Remnant", new XElement("RemnantID", "42"),
            new XElement("RemnantName", "H0306_S1_R1"), new XElement("Length", 1000), new XElement("Width", 500),
            new XElement("Area", 0.34m), new XElement("InventoryItemID", 0), new XElement("ExternalNestID", 0), parent);
        private static string Folder(string scenario, bool outline = true)
        {
            string folder = Path.Combine(Root, scenario); Directory.CreateDirectory(folder);
            if (outline)
            {
                var saved = new RemnantOutlineRecord { PowerFabRemnantId = "42", Name = "H0306_S1_R1", Shape = "PLT", Grade = "250",
                    ThicknessMm = 10, LengthMm = 1000, WidthMm = 500,
                    Contour = new[] { (0d, 0d), (1000d, 0d), (1000d, 500d), (400d, 500d), (400d, 100d), (0d, 100d) }
                        .Select(p => new LayoutPoint { X = p.Item1, Y = p.Item2 }).ToList() };
                Directory.CreateDirectory(Path.Combine(folder, "RemnantOutlines"));
                File.WriteAllText(Path.Combine(folder, "RemnantOutlines", "R-42.json"), JsonSerializer.Serialize(saved, LoadedNestResult.JsonOptions));
            }
            return folder;
        }
        private sealed class Server
        {
            public List<XElement> Inventory = new List<XElement>();
            public List<XElement> Remnants = new List<XElement>();
            public bool FailRemnants, MissingRemnantResult, MalformedRemnantResponse, ThrowRemnantRead, FailInventory;
            public HashSet<string> FailedRemnantIds = new HashSet<string>();
            public bool LastRequestsWereIndividual;
            public int RemnantReads;
            public XDocument? LastSave;
            public Action? AfterSave;
            public string Execute(string xml)
            {
                var request = XDocument.Parse(xml);
                var save=request.Descendants().FirstOrDefault(e=>e.Name.LocalName=="SaveNest");
                if(save!=null)
                {
                    LastSave=request;
                    var result=save.Elements().Single(e=>e.Name.LocalName=="NestResult");
                    string nest=NestingSnapshot.Field(result,"NestID");
                    AfterSave?.Invoke();
                    return new XDocument(new XElement("FabSuiteXMLResponse",new XElement("SaveNest",
                        new XElement("Successful",1),new XElement("TotalSuccessful",1),new XElement("TotalUnSuccessful",0),
                        new XElement("SavedNest",new XElement("NestID",nest),new XElement("ExternalNestID",2002),new XElement("SavedTo","FutureRemnant")),
                        result.Elements().Where(e=>e.Name.LocalName=="Remnant").Select((r,i)=>new XElement("SavedRemnant",
                            new XElement("NestID",nest),new XElement("ExternalRemnantID",NestingSnapshot.Field(r,"RemnantID")),
                            new XElement("PowerFabRemnantID",8000+i)))))).ToString();
                }
                if (request.Descendants().Any(e => e.Name.LocalName == "GetInventory"))
                    return new XDocument(new XElement("FabSuiteXMLResponse", request.Descendants().Where(e => e.Name.LocalName == "GetInventory").Select(_ =>
                        new XElement("GetInventory", new XElement("Successful", FailInventory ? 0 : 1), Inventory.Select(e => new XElement(e)))))).ToString();
                var commands = request.Descendants().Where(e => e.Name.LocalName == "Remnant_Get").ToList(); RemnantReads++;
                LastRequestsWereIndividual = commands.Count > 0 && commands.All(c => c.Elements().Count(e => e.Name.LocalName == "RemnantID") == 1);
                if (ThrowRemnantRead) throw new InvalidOperationException("Mock remnant transport failure");
                if (MissingRemnantResult) return "<FabSuiteXMLResponse><XMLWarning>Remnant query unavailable</XMLWarning></FabSuiteXMLResponse>";
                if (MalformedRemnantResponse) return "<FabSuiteXMLResponse>";
                const string ns = "http://www.fabsuite.com/xml/fabsuite-xml-response-v0108.xsd";
                fsresRemnant Native(XElement element)
                {
                    var copy = new XElement(element);
                    foreach (var e in copy.DescendantsAndSelf()) e.Name = XName.Get(e.Name.LocalName, ns);
                    using var reader = copy.CreateReader();
                    return (fsresRemnant)new XmlSerializer(typeof(fsresRemnant), new XmlRootAttribute("Remnant") { Namespace = ns }).Deserialize(reader)!;
                }
                var response = new fsresFabSuiteXMLResponse { Remnant_Get = commands.Select(command => {
                    var ids = command.Elements().Where(e => e.Name.LocalName == "RemnantID").Select(e => e.Value).ToHashSet();
                    bool failed = FailRemnants || ids.Any(FailedRemnantIds.Contains);
                    return new fsresRemnant_Get { Successful = !failed, ErrorMessage = failed ? "Mock remnant read failure" : null,
                        Remnant = failed ? Array.Empty<fsresRemnant>() : Remnants.Where(e => ids.Contains(F(e, "RemnantID"))).Select(Native).ToArray() };
                }).ToArray() };
                using var writer = new StringWriter(); new XmlSerializer(typeof(fsresFabSuiteXMLResponse)).Serialize(writer, response);
                return writer.ToString();
            }
            public PowerFabNestApi Api() => new PowerFabNestApi(Execute);
        }
        public static int Run()
        {
            ReservedSheetsRemainVisible();
            FutureAppearsAfterOriginalExport();
            RecoverFromConfirmedResult();
            PhysicalStockReplacesFuture();
            DeletedOrConsumedRemnant();
            MissingOrChangedOutline();
            OptionalRemnantFailureDoesNotBlockSheets();
            PartialRemnantResponses();
            InventoryNavigationIdentity();
            SmallBottomTail();
            RightRectanglePolicy();
            RegisteredRemnantChecks();
            SelectedThicknessInventoryView();
            Console.WriteLine($"PASS: {passed} inventory/remnant lifecycle and bottom-strip checks. PowerFab responses were mocked.");
            return passed;
        }
        private static void SelectedThicknessInventoryView()
        {
            XElement Item(string id, decimal thick, string job = "") => new XElement("InventoryItem", Sheet(id, job, thick).Elements());
            var document = new XDocument(new XElement(Snapshot().RequestRoot));
            var parts = document.Descendants().Single(e => e.Name.LocalName == "WorkOrderParts");
            var second = new XElement(parts.Elements().Single());
            StockGeometry.Set(second, "PieceMark", "20-mm-part");
            StockGeometry.Set(second, "PlateThickness", "20");
            parts.Add(second);
            var snapshot = NestingSnapshot.FromDocument(document);
            var reserved = Item("101", 10, "test nnr");
            snapshot.ReplaceInventory(new[] { Item("100", 28), reserved, Item("102", 20), Item("103", 12) }, Array.Empty<SavedNestReceipt>());
            var view = snapshot.Inventory.DefaultView;
            Check(view.Count == 2 && view.Cast<System.Data.DataRowView>().Select(r => (string)r["PowerFab ID"]).SequenceEqual(new[] { "101", "102" }),
                "Inventory view shows only the selected 10 and 20 mm materials");
            Check(snapshot.Inventory.Rows.Count == 4 && (string)view[0]["Status"] == "Reserved for test nnr",
                "Filtering preserves full stock identities and the status of matching reserved material");
            snapshot.Parts.Rows[1]["Select"] = false; snapshot.ApplyInventoryFilter();
            Check(view.Count == 1 && (string)view[0]["PowerFab ID"] == "101", "Unticking 20 mm immediately leaves only 10 mm stock");
            snapshot.Parts.Rows[0]["Select"] = false; snapshot.ApplyInventoryFilter();
            Check(view.Count == 0, "Select none leaves no unrelated inventory visible");
            snapshot.Parts.Rows[1]["Select"] = true; snapshot.ApplyInventoryFilter();
            view.Sort = "[PowerFab ID] DESC";
            int originalIndex = snapshot.Inventory.Rows.IndexOf(view[0].Row);
            Check(originalIndex == 2 && LoadedNestResult.CreateTemplate(snapshot, originalIndex).Material.Thickness == 20,
                "Selecting a filtered/sorted first row retains the correct original sheet identity");
            snapshot.ReplaceInventory(new[] { Item("201", 20), Item("202", 12) }, Array.Empty<SavedNestReceipt>());
            Check(view.Count == 1 && (string)view[0]["PowerFab ID"] == "201", "Inventory refresh reapplies the current thickness selection");
            var imperial = new XDocument(new XElement(Snapshot().RequestRoot));
            var thickness = imperial.Descendants().Single(e => e.Name.LocalName == "PlateThickness");
            thickness.Value = "0.5"; thickness.SetAttributeValue("UOM", "IN");
            snapshot = NestingSnapshot.FromDocument(imperial);
            snapshot.ReplaceInventory(new[] { Item("301", 12.7m), Item("302", 12) }, Array.Empty<SavedNestReceipt>());
            Check(snapshot.Inventory.DefaultView.Count == 1 && (string)snapshot.Inventory.DefaultView[0]["PowerFab ID"] == "301",
                "Thickness matching compares normalized measurements rather than display strings");
        }
        private static void ReservedSheetsRemainVisible()
        {
            var reserved = Sheet("101", "test nnr"); var allocated = Sheet("102", "H0306");
            allocated.Add(new XElement("PercentCombined", 0.8m), new XElement("NestID", "parent-nest"));
            var server = new Server { Inventory = new List<XElement> { reserved, allocated, Sheet("103", "", 28), Sheet("104") } };
            var snapshot = Snapshot(); server.Api().RefreshInventory(snapshot, Folder("reserved", false));
            Check(snapshot.Inventory.Rows.Count == 4, "Other-job sheets and allocated parents stay visible");
            Check((string)snapshot.Inventory.Rows[0]["Status"] == "Reserved for test nnr", "Reservation explains screenshot mismatch");
            Check((string)snapshot.Inventory.Rows[0]["Qty"] == "1" && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "0", "Reserved sheet count is distinct from availability");
            Check((string)snapshot.Inventory.Rows[1]["Status"] == "Allocated to nest" && (string)snapshot.Inventory.Rows[1]["Available Qty"] == "0", "Combined parent cannot be counted as free stock");
            var options = PlateSelection.StockOptions(snapshot, PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>()).Single(), Array.Empty<SavedNestReceipt>());
            Check(options.Count == 1 && options[0].InventoryIndex == 3, "Only free matching-thickness sheet can be selected");
            try { LoadedNestResult.CreateTemplate(snapshot, 0); throw new Exception("Reserved stock selectable"); }
            catch (InvalidDataException) { Check(true, "Direct template path also blocks another job's stock"); }
            reserved.Add(new XElement("JobReserve", false));
            Check(InventoryTracking.UnavailableReason(reserved, snapshot) == "", "Explicitly unreserved job tag is not a reservation");
        }
        private static void FutureAppearsAfterOriginalExport()
        {
            string folder = Folder("future"); var snapshot = Snapshot();
            var parent = Sheet("101", "H0306"); parent.Add(new XElement("NestID", "parent-nest"));
            var liveFuture = Future(); StockGeometry.Set(liveFuture, "PONumber", "301026");
            var server = new Server { Inventory = new List<XElement> { parent, liveFuture }, Remnants = new List<XElement> { Remnant(Parent()) } };
            var api = server.Api(); api.RefreshInventory(snapshot, folder);
            var stock = LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Single(StockGeometry.IsFuture);
            Check(snapshot.OriginalStock.Count == 0 && snapshot.Inventory.Rows.Count == 2, "Newly saved remnant appears without reopening old export");
            Check(StockGeometry.PowerFabRemnantId(stock) == "42" && F(stock, "HanlonContour").Length > 0, "Future remnant retains confirmed identity and real outline");
            Check(F(stock, "PONumber") == "301026" && (string)snapshot.Inventory.Rows[1]["Available Qty"] == "1", "Live inventory supplies PO and future quantity");
            var options = PlateSelection.StockOptions(snapshot, PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>()).Single(), Array.Empty<SavedNestReceipt>());
            Check(options.Count == 1 && options[0].InventoryIndex == 1, "Future remnant can be chosen; allocated parent cannot");
            var material = LoadedNestResult.CreateTemplate(snapshot, 1).Material;
            Check(material.ParentRemnantPowerFabId == "42" && material.InventoryId == "" && material.StockContour.Count == 6, "Automatic future material uses parent remnant identity and six-corner boundary");
            var receipt = new SavedNestReceipt { NestId = "old-consumer", WorkOrderId = "100", MaterialKey = "R:42", SavedAtUtc = DateTime.UtcNow, SheetQuantity = 1 };
            Directory.CreateDirectory(Path.Combine(folder, "SavedNestReceipts"));
            File.WriteAllText(Path.Combine(folder, "SavedNestReceipts", "old.json"), JsonSerializer.Serialize(receipt, LoadedNestResult.JsonOptions));
            api.RefreshInventory(snapshot, folder);
            Check((string)snapshot.Inventory.Rows[1]["Available Qty"] == "1", "Fresh server quantity is not reduced again by a baseline receipt");
            snapshot.RefreshQuantities(new[] { receipt, new SavedNestReceipt { NestId = "new-consumer", MaterialKey = "R:42", SavedAtUtc = DateTime.UtcNow, SheetQuantity = 1 } });
            Check((string)snapshot.Inventory.Rows[1]["Available Qty"] == "0", "A new local use after the live read still reduces remaining quantity");
        }
        private static void PhysicalStockReplacesFuture()
        {
            string folder = Folder("physical"); var snapshot = Snapshot(Future());
            var server = new Server { Inventory = new List<XElement> { Future() }, Remnants = new List<XElement> { Remnant(Parent()) } }; var api = server.Api();
            api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 1 && (string)snapshot.Inventory.Rows[0]["Type"] == "Future remnant", "Uncut remnant starts as future stock");
            var physical = Sheet("777"); StockGeometry.Set(physical, "RemnantID", "42"); server.Inventory.Add(physical);
            var cut = Remnant(null); StockGeometry.Set(cut, "InventoryItemID", "777"); StockGeometry.Set(cut, "TrueShapeID", "987");
            server.Remnants = new List<XElement> { cut }; api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 1 && (string)snapshot.Inventory.Rows[0]["Type"] == "Remnant", "Cut stock replaces future row without duplication");
            var stock = LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Single();
            Check(F(stock, "ID") == "777" && StockGeometry.PowerFabRemnantId(stock) == "42", "Physical remnant acquires its new inventory ID");
            var material = LoadedNestResult.CreateTemplate(snapshot, 0).Material; StockGeometry.Validate(material);
            Check(material.InventoryId == "777" && material.ParentRemnantPowerFabId == "" && material.StockContour.Count == 6, "Physical remnant loads original outline automatically");
            Check(RemnantCatalog.Read(folder).Single().InventoryId == "777", "Cut identity persists for the next window/run");
            api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 1, "Repeated refresh does not duplicate a physical remnant");
            server.Inventory.Clear(); api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 0, "Physical ID without current stock is not recreated from local geometry");
        }
        private static void RecoverFromConfirmedResult()
        {
            string folder = Folder("recover"); var outline = RemnantCatalog.Read(folder).Single();
            Directory.Delete(Path.Combine(folder, "RemnantOutlines"), true);
            var result = new NestResultPackage { NestId = "parent-result", Material = new NestMaterial { Shape = "PLT", Grade = "250", Thickness = 10, Unit = "MM" },
                Remnants = new List<NestRemnant> { new NestRemnant { RemnantId = "external-R1", Name = outline.Name, Length = 1000, Width = 500,
                    Area = 340000, Unit = "MM", Contour = outline.Contour } } };
            string path = Path.Combine(folder, "parent-result.json");
            File.WriteAllText(path, JsonSerializer.Serialize(result, LoadedNestResult.JsonOptions));
            Directory.CreateDirectory(Path.Combine(folder, "SavedNestReceipts"));
            File.WriteAllText(Path.Combine(folder, "SavedNestReceipts", "parent.json"), JsonSerializer.Serialize(new SavedNestReceipt {
                NestId = "parent-result", ResultFile = path, RemnantPowerFabIds = new List<string> { "42" } }, LoadedNestResult.JsonOptions));
            var snapshot = Snapshot(); var server = new Server { Inventory = new List<XElement> { Future() }, Remnants = new List<XElement> { Remnant(Parent()) } };
            server.Api().RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 1 && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "1", "Confirmed save result restores the outline for a current PowerFab remnant without an upload");
            Check(RemnantCatalog.Read(folder).Single().PowerFabRemnantId == "42", "Recovered outline keeps the confirmed PowerFab remnant ID");
        }
        private static void DeletedOrConsumedRemnant()
        {
            string folder = Folder("deleted"); var snapshot = Snapshot(Future());
            var server = new Server { Inventory = new List<XElement> { Future() }, Remnants = new List<XElement> { Remnant(Parent()) } }; var api = server.Api();
            api.RefreshInventory(snapshot, folder); server.Remnants.Clear(); api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 0, "Deleted remnant disappears despite old export/catalog entry");
            server.Remnants.Add(Remnant(null)); api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 0, "Orphan remnant without a live parent is unavailable");
            server.Remnants = new List<XElement> { Remnant(Parent(quantity: 0)) }; api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 0, "Zero-quantity cut parent cannot recreate an uncut remnant");
            var broken = Parent(); broken.Add(new XElement("ExternalNestBroken", 1)); server.Remnants = new List<XElement> { Remnant(broken) }; api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 0, "Broken parent nest cannot supply available remnants");
            var used = Remnant(Parent()); StockGeometry.Set(used, "ExternalNestID", "2002"); server.Remnants = new List<XElement> { used }; api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 1 && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "0", "Already nested future remains visible with no free quantity");
            Check(PlateSelection.StockOptions(snapshot, PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>()).Single(), Array.Empty<SavedNestReceipt>()).Count == 0, "Already nested future cannot be selected twice");
            server.Remnants = new List<XElement> { Remnant(Parent("test nnr")) }; api.RefreshInventory(snapshot, folder);
            Check((string)snapshot.Inventory.Rows[0]["Status"] == "Future remnant" && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "1",
                "Future remnant is available across jobs despite the parent reservation");
            var requisition = new XElement("ParentRequisitionItem", new XElement("Quantity", 1), new XElement("JobNumber", "H0306"), new XElement("RequisitionNumber", "REQ184"));
            server.Remnants = new List<XElement> { Remnant(requisition) }; api.RefreshInventory(snapshot, folder);
            Check((string)snapshot.Inventory.Rows[0]["Available Qty"] == "1", "Uncut remnant can refresh while its parent is still on a requisition");
        }
        private static void MissingOrChangedOutline()
        {
            string folder = Folder("no-outline", false); var snapshot = Snapshot(Future());
            var server = new Server { Inventory = new List<XElement> { Future() }, Remnants = new List<XElement> { Remnant(Parent()) } }; var api = server.Api(); api.RefreshInventory(snapshot, folder);
            Check(snapshot.Inventory.Rows.Count == 1 && (string)snapshot.Inventory.Rows[0]["Status"] == "Remnant outline unavailable", "Missing outline is visible and explained");
            Check((string)snapshot.Inventory.Rows[0]["Available Qty"] == "0", "Missing outline is never treated as a bounding rectangle");
            folder = Folder("changed-outline"); snapshot = Snapshot(); StockGeometry.Set(server.Remnants[0], "Area", "0.33"); api.RefreshInventory(snapshot, folder);
            Check((string)snapshot.Inventory.Rows[0]["Available Qty"] == "0", "Changed live area rejects the old stored outline");
        }
        private static void OptionalRemnantFailureDoesNotBlockSheets()
        {
            string folder = Folder("failure"); var snapshot = Snapshot();
            var server = new Server { Inventory = new List<XElement> { Sheet("103"), Future() }, Remnants = new List<XElement> { Remnant(Parent()) } }; var api = server.Api(); api.RefreshInventory(snapshot, folder);
            server.Inventory = new List<XElement> { Sheet("104"), Future() }; server.FailRemnants = true; api.RefreshInventory(snapshot, folder);
            Check(F(LoadedNestResult.InventoryNodes(snapshot.RequestRoot).First(), "ID") == "104", "Failed optional remnant read still applies the fresh sheet inventory");
            Check(snapshot.RemnantRefreshWarning.Length > 0 && snapshot.LiveInventoryLoaded, "Optional failure is reported without discarding the confirmed stock read");
            Check(snapshot.Inventory.Rows.Count == 1 && !LoadedNestResult.InventoryNodes(snapshot.RequestRoot).Any(StockGeometry.IsFuture), "Failed remnant details add no cached or zero-quantity placeholder row");
            var options = PlateSelection.StockOptions(snapshot, PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>()).Single(), Array.Empty<SavedNestReceipt>());
            Check(options.Count == 1 && options[0].InventoryIndex == 0, "Normal matching sheets remain selectable during a remnant query failure");
            var choice = new MaterialPlan { Group = PlateSelection.Groups(snapshot, Array.Empty<SavedNestReceipt>()).Single() }; choice.StockIndexes.Add(0);
            var part = LoadedNestResult.CreateTemplate(snapshot, 0).Parts.Single();
            var shapes = new Dictionary<string, DxfShape> { [LoadedNestResult.PartKey(part)] = new DxfShape { Source = "fixture", Outer = new PathD {
                new PointD(0,0),new PointD(100,0),new PointD(100,50),new PointD(0,50) } } };
            Check(NestingBatch.Calculate(snapshot, choice, Array.Empty<SavedNestReceipt>(), new LayoutConditions(), shapes, CancellationToken.None).Count == 1,
                "Actual calculation continues on a verified sheet when remnant verification fails");
            try { LoadedNestResult.CreateTemplate(snapshot, 1); throw new Exception("Removed remnant remained selectable"); }
            catch (InvalidDataException) { Check(true, "A removed remnant cannot be used through direct template creation"); }
            Check(Directory.GetFiles(Path.Combine(folder, "RemnantInventoryLog"), "*.response.xml").Length == 2, "Remnant refresh responses are retained for diagnosis");
            server.FailRemnants = false; server.MissingRemnantResult = true; api.RefreshInventory(snapshot, folder);
            Check(snapshot.RemnantRefreshWarning.Length > 0 && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "1", "Missing command response like the reported failure does not block live sheets");
            server.MissingRemnantResult = false; server.MalformedRemnantResponse = true; api.RefreshInventory(snapshot, folder);
            Check(snapshot.RemnantRefreshWarning.Length > 0 && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "1", "Malformed optional remnant XML does not discard confirmed sheets");
            server.MalformedRemnantResponse = false; server.ThrowRemnantRead = true; api.RefreshInventory(snapshot, folder);
            Check(snapshot.RemnantRefreshWarning.Length > 0 && (string)snapshot.Inventory.Rows[0]["Available Qty"] == "1", "Remnant transport failure does not discard confirmed sheets");
            server.ThrowRemnantRead = false; api.RefreshInventory(snapshot, folder);
            Check(snapshot.RemnantRefreshWarning.Length == 0 && (string)snapshot.Inventory.Rows[1]["Available Qty"] == "1", "A later successful verification restores the remnant and clears the warning");
            string before = snapshot.RequestRoot.ToString(); server.FailInventory = true;
            try { api.RefreshInventory(snapshot, folder); throw new Exception("Unconfirmed inventory was accepted"); }
            catch (InvalidDataException) { Check(snapshot.RequestRoot.ToString() == before, "Mandatory sheet-inventory failure still stops the refresh and keeps the last snapshot"); }
            var planning = Future(); StockGeometry.Set(planning,"Type","INV"); StockGeometry.Set(planning,"Quantity","any");
            Check(InventoryTracking.UnavailableReason(planning, snapshot).Length > 0, "Planning rows with any quantity are not marked as verified stock");
        }
        private static void PartialRemnantResponses()
        {
            string folder = Folder("multiple"); var outline = RemnantCatalog.Read(folder).Single(); outline.PowerFabRemnantId = "43"; outline.Name = "Other_R1";
            File.WriteAllText(Path.Combine(folder,"RemnantOutlines","R-43.json"),JsonSerializer.Serialize(outline,LoadedNestResult.JsonOptions));
            var other = Remnant(Parent()); StockGeometry.Set(other,"RemnantID","43"); StockGeometry.Set(other,"RemnantName","Other_R1");
            var server = new Server { Inventory = new List<XElement>{Sheet("104"),Future(),Future("43")}, Remnants = new List<XElement>{Remnant(Parent()),other} };
            var snapshot = Snapshot(); var api = server.Api(); api.RefreshInventory(snapshot,folder);
            Check(server.LastRequestsWereIndividual, "Each remnant uses an independent request command");
            Check(snapshot.RemnantRefreshWarning.Length == 0 && snapshot.Inventory.Rows.Count == 3,
                "Multiple SDK-serialized successful remnant results are accepted");
            server.FailedRemnantIds.Add("42"); api.RefreshInventory(snapshot,folder);
            var nodes=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList();
            int verified=nodes.FindIndex(s=>StockGeometry.PowerFabRemnantId(s)=="43"), pending=nodes.FindIndex(s=>StockGeometry.PowerFabRemnantId(s)=="42");
            Check(snapshot.RemnantRefreshWarning.Length>0 && (string)snapshot.Inventory.Rows[verified]["Available Qty"]=="1",
                "A failed old remnant does not hide another positively verified remnant");
            Check(pending == -1 && snapshot.Inventory.Rows.Count == 2, "A failed remnant is omitted without adding a pending placeholder");
        }
        private static void SmallBottomTail()
        {
            CalculatedNest Plan(decimal strip, LayoutConditions conditions)
            {
                var part = new NestPart { JobNumber = "H0306", MainMark = "A", PieceMark = "P0", Quantity = 1, Length = 200, Width = 485 - strip };
                var shape = new DxfShape { Source = "fixture", Outer = new PathD { new PointD(0, 0), new PointD(200, 0), new PointD(200, (double)part.Width), new PointD(0, (double)part.Width) } };
                var template = new NestResultPackage { WorkOrderId = "5779", Material = new NestMaterial { Shape = "PLT", Grade = "250", Thickness = 10, Length = 1000, Width = 500, Unit = "MM", Dimensions = "10X500" } };
                return DxfNestingEngine.Calculate(template, new[] { new NestingInput { Part = part, Shape = shape } }, conditions, CancellationToken.None);
            }
            var rules = new LayoutConditions { RotatePitch = 360 };
            var small = Plan(40, rules); var right = small.Package.Remnants.Single();
            Check(right.Edge == "Right" && right.Contour.Count == 4, "40 mm bottom tail is discarded; one simple right remnant remains");
            Check(right.Length == 785 && right.Width == 500 && right.Area == 392500, "Saved right remnant dimensions and area exclude the discarded bottom tail");
            RemnantGeometry.ValidateContour(right);
            Check(Math.Abs(DxfGeometry.Read(RemnantGeometry.Definition(right), "right").Area - 392500) < 0.01, "Exported right-only DXF agrees with saved geometry");
            var useful = Plan(150, rules).Package.Remnants.Single();
            Check(useful.Edge == "Right" && useful.Contour.Count == 4 && useful.Area == useful.Length * useful.Width, "The larger right rectangle wins over a usable 150 mm bottom rectangle");
            var legacy = Plan(40, new LayoutConditions { RotatePitch = 360, MinimumBottomRemnant = 0 }).Package.Remnants.Single();
            Check(legacy.Edge == "Right", "An old zero bottom-strip threshold does not re-enable L-shaped remnants");
            Check(rules.Copy().MinimumBottomRemnant == 100, "Layout-condition copy retains the bottom-remnant threshold");
            string folder = Folder("conditions", false); rules.MinimumBottomRemnant = 120; rules.Save(folder);
            Check(LayoutConditions.Load(folder).MinimumBottomRemnant == 120, "Bottom-remnant threshold persists across sessions");
            File.WriteAllText(Path.Combine(folder, "LayoutConditions.json"), "{\"Spacing\":5}");
            Check(LayoutConditions.Load(folder).MinimumBottomRemnant == 100, "Older settings gain the new default without a migration");
        }
        private static void RightRectanglePolicy()
        {
            List<LayoutPoint> Outline(params (double X, double Y)[] points) => points.Select(p => new LayoutPoint { X = p.X, Y = p.Y }).ToList();
            CalculatedNest Plan(double usedRight, double bottom)
            {
                var plan = new CalculatedNest
                {
                    Package = new NestResultPackage { NestName = "Right-only", Material = new NestMaterial
                        { Source = "Purchase", Shape = "PLT", Grade = "250", Thickness = 10, Length = 1000, Width = 500, Unit = "MM", Dimensions = "10X500" } },
                    SheetLengthMm = 1000, SheetWidthMm = 500
                };
                plan.Placed.Add(new PlacedOutline { Outer = new PathD { new PointD(0,bottom), new PointD(usedRight,bottom),
                    new PointD(usedRight,500), new PointD(0,500) } });
                return plan;
            }
            var rules = new LayoutConditions { Spacing = 0, Left = 0, Right = 0, Top = 0, Bottom = 0, MinimumBottomRemnant = 0 };
            var fullWidth = Plan(1000, 200);
            var bottom=RemnantGeometry.Create(fullWidth,rules).Single();
            Check(bottom.Edge=="Bottom" && bottom.Length==1000 && bottom.Width==200 && bottom.Area==200000, "A full-width top layout retains its usable bottom rectangle");
            var topBand=RemnantGeometry.Create(Plan(980,300),rules).Single();
            Check(topBand.Edge=="Bottom"&&topBand.Length==1000&&topBand.Width==300,"A wide top-filled band keeps the larger bottom rectangle instead of a narrow right strip");
            var holdingRules=new LayoutConditions{Spacing=0,Right=0,Bottom=0,MinimumBottomRemnant=100};
            Check(RemnantGeometry.Create(Plan(1000,80),holdingRules).Count==0,"An 80 mm bottom holding strip is not saved as reusable stock");
            holdingRules.Spacing=5;
            var spaced=RemnantGeometry.Create(Plan(1000,300),holdingRules).Single();
            Check(spaced.Edge=="Bottom"&&spaced.Width==295,"Bottom stock starts clear of the requested cut spacing");
            var right = RemnantGeometry.Create(Plan(250, 200), rules).Single();
            Check(right.X == 250 && right.Y == 0 && right.Length == 750 && right.Width == 500 && right.Area == 375000, "Right rectangle uses the remaining length and full sheet height");
            Check(right.Contour.Count == 4 && right.Holes.Count == 0 && right.Contour.All(p => p.X >= 250), "No saved contour contains the old bottom-left leg");
            RemnantGeometry.ValidateContour(right);
            Check(Math.Abs(DxfGeometry.Read(RemnantGeometry.Definition(right), "right-only").Area - 375000) < 0.01, "The exported rectangular DXF has exactly the saved area");

            var irregular = Plan(250, 100);
            irregular.Package.Material.StockContour = Outline((0,0),(1000,0),(1000,500),(400,500),(400,200),(0,200));
            irregular.Package.Material.RequiresOutline = true;
            var fromOldL = RemnantGeometry.Create(irregular, rules).Single();
            Check(fromOldL.X == 400 && fromOldL.Length == 600 && fromOldL.Width == 500 && fromOldL.Area == 300000, "An old L-shaped stock outline is cropped to its actual rectangular right strip");
            Check(fromOldL.Edge == "Right" && fromOldL.Contour.Count == 4
                && StockGeometry.Contains(irregular.Package.Material, new PathsD { StockGeometry.Path(fromOldL.Contour) }), "The replacement rectangle never invents stock in the missing corner");

            var holed = Plan(250, 100);
            holed.Package.Material.StockContour = Outline((0,0),(1000,0),(1000,500),(0,500));
            holed.Package.Material.StockHoles.Add(Outline((600,100),(650,100),(650,200),(600,200)));
            holed.Package.Material.RequiresOutline = true;
            var afterHole = RemnantGeometry.Create(holed, rules).Single();
            Check(afterHole.X == 650 && afterHole.Length == 350 && afterHole.Width == 500 && afterHole.Holes.Count == 0, "A retained right rectangle starts beyond an existing stock hole");
            Check(StockGeometry.Contains(holed.Package.Material, new PathsD { StockGeometry.Path(afterHole.Contour) }), "The saved rectangular remnant excludes the hole");
            var bottomHole=Plan(1000,300);
            bottomHole.Package.Material.StockContour=Outline((0,0),(1000,0),(1000,500),(0,500));
            bottomHole.Package.Material.StockHoles.Add(Outline((600,100),(650,100),(650,150),(600,150)));
            bottomHole.Package.Material.RequiresOutline=true;
            var belowHole=RemnantGeometry.Create(bottomHole,rules).Single();
            Check(belowHole.Edge=="Bottom"&&belowHole.Width==100&&belowHole.Holes.Count==0,"A bottom remnant ends below an existing stock hole");
            Check(StockGeometry.Contains(bottomHole.Package.Material,new PathsD{StockGeometry.Path(belowHole.Contour)}),"The bottom rectangle never invents stock inside a hole");
            var empty = Plan(250, 100); empty.Placed.Clear();
            Check(RemnantGeometry.Create(empty, rules).Count == 0, "An empty plan does not create a remnant");
        }

        private static void InventoryNavigationIdentity()
        {
            var stock = Sheet("46766", "test nnr"); StockGeometry.Set(stock, "ExternalInventoryLinkID", "9938");
            var target = PowerFabInventoryTarget.FromStock(stock);
            Check(target?.InventoryId == "46766", "Inventory navigation uses the native stock ID rather than external link 9938");
            Check(target!.Reference.Contains("test nnr") && target.Reference.Contains("46766"), "Lookup fallback identifies the selected stock and job");
            Check(PowerFabInventoryTarget.FromStock(Future()) == null, "Future remnant ID cannot be mistaken for a physical inventory ID");
            var planning = Sheet("123"); StockGeometry.Set(planning,"Quantity","any");
            Check(PowerFabInventoryTarget.FromStock(planning) == null, "Planning-size options do not enable physical stock navigation");
            var physical = Sheet("777"); StockGeometry.Set(physical,"RemnantPowerFabID","42");
            Check(PowerFabInventoryTarget.FromStock(physical)?.InventoryId == "777", "A physical remnant navigates by its inventory ID rather than remnant ID");
            var linkOnly = Sheet("0"); StockGeometry.Set(linkOnly,"ExternalInventoryLinkID","9938");
            Check(PowerFabInventoryTarget.FromStock(linkOnly) == null, "External link alone never supplies an inventory navigation target");
        }
    }
}

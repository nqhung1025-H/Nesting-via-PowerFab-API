using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    // Exchange contract for calculated Hanlon layouts and completed external layouts.
    // DXF placement is implemented by DxfNestingEngine; this class stores its result.
    public sealed class NestResultPackage
    {
        public string Format { get; set; } = "HanlonNestResult/v1";
        public string Status { get; set; } = "draft";
        public string WorkOrderId { get; set; } = string.Empty;
        public string NestId { get; set; } = Guid.NewGuid().ToString("N");
        public string NestName { get; set; } = string.Empty;
        public string LayoutSource { get; set; } = string.Empty;
        public string NestImageFile { get; set; } = string.Empty;
        public AbeImportReference? AbeImport { get; set; }
        public LayoutConditions? LayoutConditions { get; set; }
        public string RequisitionNumber { get; set; } = string.Empty;
        public bool AutoRequisitionNumber { get; set; }
        public string RequisitionBatchId { get; set; } = string.Empty;
        public string TargetPurchaseOrderNumber { get; set; } = string.Empty;
        public int SheetNumber { get; set; } = 1;
        public NestMaterial Material { get; set; } = new NestMaterial();
        public List<NestPart> Parts { get; set; } = new List<NestPart>();
        public List<NestRemnant> Remnants { get; set; } = new List<NestRemnant>();
        public List<NestPlacement> Placements { get; set; } = new List<NestPlacement>();
    }

    public sealed class LayoutPoint { public double X { get; set; } public double Y { get; set; } }
    public sealed class NestPlacement
    {
        public string JobNumber { get; set; } = string.Empty;
        public string MainMark { get; set; } = string.Empty;
        public string PieceMark { get; set; } = string.Empty;
        public string Sequence { get; set; } = string.Empty;
        public string LotNumber { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Rotation { get; set; }
        public string Unit { get; set; } = "MM";
        public string SourceDxf { get; set; } = string.Empty;
        public List<LayoutPoint> Outer { get; set; } = new List<LayoutPoint>();
        public List<List<LayoutPoint>> Holes { get; set; } = new List<List<LayoutPoint>>();
    }

    public sealed class NestMaterial
    {
        public string Source { get; set; } = "Inventory";
        // Calculation references only; these IDs are never sent as allocation instructions.
        public string ReferenceInventoryId { get; set; } = string.Empty;
        public string ReferencePurchaseOrderNumber { get; set; } = string.Empty;
        public bool RequiresOutline { get; set; }
        public double StockCurveToleranceMm { get; set; }
        // Stock outlines are local coordinates in millimetres, independent of display units.
        public List<LayoutPoint> StockContour { get; set; } = new List<LayoutPoint>();
        public List<List<LayoutPoint>> StockHoles { get; set; } = new List<List<LayoutPoint>>();
        public string PurchaseOrderNumber { get; set; } = string.Empty;
        public string PurchaseOrderItemNumber { get; set; } = string.Empty;
        public string StockJob { get; set; } = string.Empty;
        public string StockLocation { get; set; } = string.Empty;
        public string StockSecondaryLocation { get; set; } = string.Empty;
        public bool OnOrder { get; set; }
        public string InventoryId { get; set; } = string.Empty;
        public string ExternalInventoryLinkId { get; set; } = string.Empty;
        public string ParentRemnantPowerFabId { get; set; } = string.Empty;
        public string Shape { get; set; } = string.Empty;
        public string Grade { get; set; } = string.Empty;
        public string Dimensions { get; set; } = string.Empty;
        public bool Metric { get; set; } = true;
        public decimal Thickness { get; set; }
        public decimal Length { get; set; }
        public decimal Width { get; set; }
        public string Unit { get; set; } = "MM";
    }

    public sealed class NestPart
    {
        public string Phase { get; set; } = string.Empty;
        public string JobNumber { get; set; } = string.Empty;
        public string MainMark { get; set; } = string.Empty;
        public string PieceMark { get; set; } = string.Empty;
        public string Sequence { get; set; } = string.Empty;
        public string LotNumber { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Length { get; set; }
        public decimal Width { get; set; }
        public string Unit { get; set; } = "MM";
    }

    public sealed class NestRemnant
    {
        public string Edge { get; set; } = string.Empty;
        public decimal? X { get; set; }
        public decimal? Y { get; set; }
        public string RemnantId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public decimal Length { get; set; }
        public decimal Width { get; set; }
        public string Unit { get; set; } = "MM";
        public decimal Area { get; set; }
        public decimal? Weight { get; set; }
        public string ImageFile { get; set; } = string.Empty;
        public string DefinitionFile { get; set; } = string.Empty;
        public List<LayoutPoint> Contour { get; set; } = new List<LayoutPoint>();
        public List<List<LayoutPoint>> Holes { get; set; } = new List<List<LayoutPoint>>();
    }

    internal sealed class LoadedNestResult
    {
        public NestResultPackage Result { get; }
        public byte[] NestImage { get; }
        public IReadOnlyList<byte[]?> RemnantImages { get; }
        public string SourceFile { get; }
        public AbeNestDocument? AbeDocument { get; }

        private LoadedNestResult(NestResultPackage result, byte[] image, List<byte[]?> remnants, string path, AbeNestDocument? abeDocument)
        {
            Result = result; NestImage = image; RemnantImages = remnants; SourceFile = path; AbeDocument = abeDocument;
        }

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static LoadedNestResult Load(string path, NestingSnapshot snapshot)
        {
            string fullPath = Path.GetFullPath(path);
            var result = JsonSerializer.Deserialize<NestResultPackage>(File.ReadAllText(fullPath), JsonOptions)
                ?? throw new InvalidDataException("The nesting result is empty.");
            Validate(result, snapshot);
            string folder = Path.GetDirectoryName(fullPath)!;
            AbeNestDocument? abeDocument = null;
            if (result.AbeImport != null)
            {
                Need(result.AbeImport.MaterialConfirmed, "Link the ABE layout to its PowerFab sheet before saving.");
                string Source(string name)
                {
                    Need(Path.GetFileName(name)==name && !string.IsNullOrWhiteSpace(name),"ABE source files must be beside the result package.");
                    return Path.Combine(folder,name);
                }
                AbeNestDocument original = AbeNestImport.Read(Source(result.AbeImport.NcFile), Source(result.AbeImport.Sy2File));
                original.Reference.ProgramName=result.AbeImport.ProgramName;
                abeDocument=original;
                Need(original.Reference.NcSha256==result.AbeImport.NcSha256 && original.Reference.Sy2Sha256==result.AbeImport.Sy2Sha256,
                    "The ABE source files have changed since this layout was imported. Import the pair again.");
                AbeProcessTime.Validate(result.AbeImport);
                if(result.AbeImport.ProcessTime is AbeProcessTime runtime)
                    Need(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Source(runtime.ReportFile))))==runtime.ReportSha256,
                        "The archived ABE sheet report has changed. Import the folder again to read its runtime.");
                var jobSources=original.Placements.Where(p=>!AbePartNames.IsPacker(p.Name)).ToList();
                var fillerSources=original.Placements.Where(p=>AbePartNames.IsPacker(p.Name)).ToList();
                Need(result.Placements.Count==jobSources.Count && result.Parts.Sum(p=>p.Quantity)==jobSources.Count,
                    "Imported ABE placement counts no longer match the source files.");
                Need(result.AbeImport.Packers!=null&&result.AbeImport.Packers.Count==fillerSources.Count
                    &&result.AbeImport.Packers.Select(p=>p.SourceNumber).Distinct().Count()==fillerSources.Count,"Imported packer records no longer match the source.");
                foreach(var filler in fillerSources)
                {
                    var saved=result.AbeImport.Packers!.SingleOrDefault(p=>p.SourceNumber==filler.Number);
                    Need(saved!=null&&saved.Name.Equals(filler.Name,StringComparison.OrdinalIgnoreCase)
                        &&Math.Abs(saved.X-filler.X)<0.001&&Math.Abs(saved.Y-filler.Y)<0.001&&Math.Abs(saved.Rotation-filler.Angle)<0.001
                        &&Math.Abs(saved.Length-filler.SizeX)<0.001&&Math.Abs(saved.Width-filler.SizeY)<0.001,"Imported packer position/size has changed.");
                }
                if(result.AbeImport.PowerFabThicknessMm.HasValue)
                    Need(AbePartNames.AcceptsPowerFabThickness(result.AbeImport.PowerFabThicknessMm.Value,original.Thickness,original.Reference.ProgramName)
                        &&Math.Abs(result.AbeImport.PowerFabThicknessMm.Value-result.Material.Thickness*(result.Material.Unit=="IN"?25.4m:1m))<0.01m,
                        "The confirmed ABE-to-PowerFab thickness mapping has changed.");
                for(int i=0;i<jobSources.Count;i++)
                {
                    AbePlacement source=jobSources[i];NestPlacement placed=result.Placements[i];
                    Need(Math.Abs(source.X-placed.X)<0.001 && Math.Abs(source.Y-placed.Y)<0.001 && Math.Abs(source.Angle-placed.Rotation)<0.001,
                        "Imported ABE placements have changed. Import the original NC/SY2 pair again.");
                    Need(AbePartNames.Matches(source.Name,placed.JobNumber,placed.PieceMark,placed.LotNumber),
                        "Imported ABE part names no longer match the source placements.");
                }
                var placementCounts=result.Placements.GroupBy(PartKey)
                    .ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.OrdinalIgnoreCase);
                var partCounts=result.Parts.GroupBy(PartKey).ToDictionary(g=>g.Key,g=>g.Sum(p=>p.Quantity),StringComparer.OrdinalIgnoreCase);
                Need(placementCounts.Count==partCounts.Count&&partCounts.All(p=>placementCounts.TryGetValue(p.Key,out int n)&&n==p.Value),
                    "Imported ABE part quantities no longer match their placements.");
            }
            byte[] image = ReadPng(folder, result.NestImageFile);
            var remnants = result.Remnants.Select(r => string.IsNullOrWhiteSpace(r.ImageFile)
                ? null : ReadPng(folder, r.ImageFile)).ToList();
            return new LoadedNestResult(result, image, remnants, fullPath, abeDocument);
        }

        public static void Validate(NestResultPackage result, NestingSnapshot snapshot)
        {
            Need(result.Format == "HanlonNestResult/v1", "Unsupported nesting result format.");
            Need(string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase),
                "This result is a draft. Complete and verify the actual layout before importing it for SaveNest.");
            Need(!string.IsNullOrWhiteSpace(result.WorkOrderId) && result.WorkOrderId == snapshot.WorkOrderID,
                "The result does not belong to this PowerFab work order / combining run.");
            Need(!string.IsNullOrWhiteSpace(result.NestId) && result.NestId.Length <= 100, "A stable Nest ID is required (maximum 100 characters).");
            Need(!string.IsNullOrWhiteSpace(result.LayoutSource), "Identify the software or process that produced the completed layout.");
            Need(!string.IsNullOrWhiteSpace(result.NestImageFile), "The actual nest PNG image is required.");
            Need(result.Material != null && result.Parts != null && result.Remnants != null, "Material, parts and remnants cannot be null.");
            result.LayoutConditions?.Validate();
            Need(!result.AutoRequisitionNumber||Guid.TryParse(result.RequisitionBatchId,out _),"Generate a new layout to assign its requisition batch.");
            NestMaterial m = result.Material!;
            Need(!string.IsNullOrWhiteSpace(m.Shape) && !string.IsNullOrWhiteSpace(m.Grade) && !string.IsNullOrWhiteSpace(m.Dimensions), "Material shape, grade and dimensions are required.");
            Unit(m.Unit);
            Need(m.Length > 0 && m.Width > 0 && m.Thickness > 0, "The sheet length, width and thickness must be positive.");
            Need(m.Source != null && new[]{"Inventory","Purchase","Requirement","FutureRemnant"}.Contains(m.Source,StringComparer.OrdinalIgnoreCase), "Unknown material source.");
            bool inventory = PositiveId(m.InventoryId), link = PositiveId(m.ExternalInventoryLinkId), parentRemnant = PositiveId(m.ParentRemnantPowerFabId);
            bool purchase = MaterialRequirements.UsesPowerFabAllocation(m);
            Need((string.IsNullOrWhiteSpace(m.InventoryId) || inventory)
                && (string.IsNullOrWhiteSpace(m.ExternalInventoryLinkId) || link)
                && (string.IsNullOrWhiteSpace(m.ParentRemnantPowerFabId) || parentRemnant), "Inventory tracking IDs must be positive numbers.");
            Need(purchase ? !inventory && !link && !parentRemnant : parentRemnant ? !inventory && !link : inventory || link,
                "Use a purchase sheet without stock IDs, an existing inventory link, or a parent-remnant PowerFab ID.");
            Need(!purchase || !string.IsNullOrWhiteSpace(result.RequisitionNumber) || !string.IsNullOrWhiteSpace(result.TargetPurchaseOrderNumber),
                "Enter the project requisition or existing purchase order for the new sheets.");
            Need(!purchase || !m.OnOrder, "A new sheet requirement cannot claim to be received or already on order.");
            Need(m.StockContour != null && m.StockHoles != null, "Stock outlines cannot be null.");
            MaterialRequirements.ValidateUnallocated(m);
            StockGeometry.Validate(m);
            Need(!purchase || m.StockContour.Count==0,"New sheet requirements must use rectangular sheet dimensions.");
            if (!purchase)
            {
            XElement? stock = FindStock(snapshot.RequestRoot, m);
            Need(stock != null, "The selected stock is not in this request. Re-export with available inventory included.");
            Need(InventoryTracking.UnavailableReason(stock!, snapshot).Length == 0,
                "The selected sheet or remnant is no longer available for this job. Refresh inventory and generate nesting again.");
            if (inventory && link)
                Need(Field(stock!, "ExternalInventoryLinkID") == m.ExternalInventoryLinkId,
                    "The inventory link has changed. Refresh inventory and generate the layout again.");
            if (!string.IsNullOrWhiteSpace(m.PurchaseOrderNumber))
                Need(Equal(Field(stock!, "PONumber", "PurchaseOrderNumber", "PO"), m.PurchaseOrderNumber), "The selected inventory PO has changed. Refresh inventory and generate the layout again.");
            Need(Equal(Field(stock!, "Location"), m.StockLocation) && Equal(Field(stock!, "SecondaryLocation"), m.StockSecondaryLocation),
                "The selected inventory location has changed. Refresh inventory and generate the layout again.");
            Need(Equal(Field(stock!, "Shape"), m.Shape) && Equal(Field(stock!, "Grade"), m.Grade), "Stock shape or grade differs from the PowerFab request.");
            Need(Equal(Field(stock!, "Dimensions"), m.Dimensions), "Stock dimensions differ from the PowerFab request.");
            CheckMeasurement(stock!, "Length", m.Length, m.Unit);
            CheckMeasurement(stock!, "Width", m.Width, m.Unit);
            CheckMeasurement(stock!, "PlateThickness", m.Thickness, m.Unit, "Thickness");
            if (PositiveId(m.ParentRemnantPowerFabId))
                Need(Equal(Field(stock!, "Type"), "FutureRemnant"), "Parent-remnant ID must refer to a FutureRemnant.");
            else
                Need(!Equal(Field(stock!, "Type"), "FutureRemnant"), "A FutureRemnant must use parentRemnantPowerFabId.");
            Need(!StockGeometry.IsRemnant(stock!) || m.RequiresOutline, "Remnant stock must use its verified actual outline.");
            }

            Need(result.Parts!.Count > 0, "The completed nest must contain at least one part.");
            var requested = PartNodes(snapshot.RequestRoot).ToList();
            var used = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (NestPart part in result.Parts)
            {
                Need(part != null && part.Quantity > 0 && part.Length > 0 && part.Width > 0, "Part quantities and dimensions must be positive.");
                Unit(part!.Unit);
                var matches = requested.Where(p => PartKey(p) == PartKey(part)).ToList();
                Need(matches.Count > 0, "Part is not in this request: " + part.JobNumber + " / " + part.PieceMark);
                string key = PartKey(part);
                decimal total = (used.TryGetValue(key, out decimal count) ? count : 0) + part.Quantity;
                decimal available = matches.Sum(p => Number(Field(p, "Quantity")));
                Need(total <= available, "Nested quantity exceeds the request for " + part.PieceMark + ".");
                used[key] = total;
                foreach (XElement original in matches)
                {
                    Need(Equal(Field(original, "Shape"), m.Shape) && Equal(Field(original, "Grade"), m.Grade), "Part material differs from the selected sheet: " + part.PieceMark);
                    CheckMeasurement(original, "PlateThickness", m.Thickness, m.Unit, "Thickness");
                    CheckMeasurement(original, "Length", part.Length, part.Unit);
                    CheckMeasurement(original, "Width", part.Width, part.Unit);
                }
            }

            var remnantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NestRemnant remnant in result.Remnants!)
            {
                Need(remnant != null && !string.IsNullOrWhiteSpace(remnant.RemnantId), "Each remnant needs a stable ID.");
                Need(remnantIds.Add(remnant!.RemnantId), "Duplicate remnant ID: " + remnant.RemnantId);
                Unit(remnant.Unit);
                Need(remnant.Quantity > 0 && remnant.Length > 0 && remnant.Width > 0 && remnant.Area > 0,
                    "Remnant quantities, dimensions and actual area must be positive.");
                Need(remnant.Area <= remnant.Length * remnant.Width + 0.01m, "Remnant area exceeds its bounding dimensions.");
                Need(remnant.Weight == null || remnant.Weight >= 0, "Remnant weight cannot be negative.");
                Need(!string.IsNullOrWhiteSpace(remnant.ImageFile), "Include the actual PNG image for each remnant.");
                Need(remnant.Contour != null, "Remnant contour cannot be null.");
                Need(remnant.Holes != null, "Remnant holes cannot be null.");
                RemnantGeometry.ValidateContour(remnant);
            }
            decimal sheetAreaMm = ToMm(m.Length, m.Unit) * ToMm(m.Width, m.Unit);
            decimal remnantAreaMm = result.Remnants.Sum(r => r.Area * (Equal(r.Unit, "IN") ? 645.16m : 1m) * r.Quantity);
            Need(remnantAreaMm <= sheetAreaMm + 1m, "Total remnant area exceeds the selected sheet area.");
        }

        public static NestResultPackage CreateTemplate(NestingSnapshot snapshot, int inventoryIndex)
        {
            var stocks = InventoryNodes(snapshot.RequestRoot).ToList();
            Need(inventoryIndex >= 0 && inventoryIndex < stocks.Count, "Select one stock row in the request tab first.");
            XElement stock = stocks[inventoryIndex];
            string unavailable = InventoryTracking.UnavailableReason(stock, snapshot);
            Need(unavailable.Length == 0, unavailable + ". Choose available material or enter new sheet sizes for purchasing.");
            string unit = UnitOf(stock, "Length");
            var material = new NestMaterial
            {
                PurchaseOrderNumber = Field(stock, "PONumber", "PurchaseOrderNumber", "PO"),
                PurchaseOrderItemNumber = Field(stock, "POItemNumber", "PurchaseOrderItemNumber"),
                StockJob = Field(stock, "Job", "JobNumber", "JobReserve"), StockLocation = Field(stock, "Location"),
                StockSecondaryLocation = Field(stock, "SecondaryLocation"),
                OnOrder = Field(stock, "OnOrder") is string onOrder && (onOrder == "1" || Equal(onOrder, "true") || Equal(onOrder, "yes")),
                InventoryId = Field(stock, "ID", "PowerFabID", "InventoryID"),
                ExternalInventoryLinkId = Field(stock, "ExternalInventoryLinkID"),
                Shape = Field(stock, "Shape"), Grade = Field(stock, "Grade"), Dimensions = Field(stock, "Dimensions"),
                Metric = Child(stock, "Dimensions")?.Attributes().FirstOrDefault(a => Equal(a.Name.LocalName, "Metric"))?.Value is string metric
                    ? metric == "1" || Equal(metric, "true") : Equal(unit, "MM"),
                Length = Measurement(stock, "Length"), Width = Measurement(stock, "Width"),
                Thickness = Measurement(stock, "PlateThickness", "Thickness"), Unit = unit
            };
            if (Equal(Field(stock, "Type"), "FutureRemnant"))
            {
                material.Source = "FutureRemnant";
                material.InventoryId = string.Empty;
                material.ExternalInventoryLinkId = string.Empty;
                material.ParentRemnantPowerFabId = StockGeometry.PowerFabRemnantId(stock);
            }
            if (!PositiveId(material.InventoryId) && !PositiveId(material.ParentRemnantPowerFabId))
            {
                material.InventoryId = string.Empty;
                material.ExternalInventoryLinkId = Field(stock, "ExternalInventoryLinkID");
            }
            StockGeometry.Apply(stock, material);
            return CreateForMaterial(snapshot, material);
        }

        internal static NestResultPackage CreateForMaterial(NestingSnapshot snapshot, NestMaterial material)
        {
            var template = new NestResultPackage
            {
                WorkOrderId = snapshot.WorkOrderID, Material = material,
                Parts = PartNodes(snapshot.RequestRoot)
                    .Where(p => Equal(Field(p, "Shape"), material.Shape) && Equal(Field(p, "Grade"), material.Grade)
                        && SameDistance(Measurement(p, "PlateThickness", "Thickness"), UnitOf(p, "PlateThickness", "Thickness"), material.Thickness, material.Unit))
                    .Select(p => new NestPart
                    {
                        JobNumber = Field(p, "JobNumber", "Job"), MainMark = Field(p, "MainMark"),
                        PieceMark = Field(p, "PieceMark"), Sequence = Field(p, "Sequence"), LotNumber = Field(p, "LotNumber"),
                        Phase = NestNaming.PhaseOf(p, snapshot.RequestRoot),
                        Quantity = Quantity(Field(p, "Quantity")), Length = Measurement(p, "Length"),
                        Width = Measurement(p, "Width"), Unit = UnitOf(p, "Length")
                    }).ToList()
            };
            template.NestName = NestNaming.ForParts(template.Parts);
            return template;
        }

        public static IEnumerable<XElement> InventoryNodes(XElement root)
            => Child(root, "AvailableInventory", "Inventory")?.Elements().Where(e => Equal(e.Name.LocalName, "InventoryItem")) ?? Enumerable.Empty<XElement>();

        internal static XElement? FindStock(XElement root, NestMaterial material) => InventoryNodes(root).FirstOrDefault(s =>
            PositiveId(material.ParentRemnantPowerFabId) ? StockGeometry.IsFuture(s) && StockGeometry.PowerFabRemnantId(s) == material.ParentRemnantPowerFabId
            : PositiveId(material.InventoryId) ? Field(s, "ID", "PowerFabID", "InventoryID", "InventoryItemID") == material.InventoryId
            : PositiveId(material.ExternalInventoryLinkId) && Field(s, "ExternalInventoryLinkID") == material.ExternalInventoryLinkId);
        private static IEnumerable<XElement> PartNodes(XElement root)
            => Child(root, "WorkOrderParts", "Parts")?.Elements().Where(e => Equal(e.Name.LocalName, "WorkOrderPart") || Equal(e.Name.LocalName, "Part")) ?? Enumerable.Empty<XElement>();
        private static XElement? Child(XElement root, params string[] names)
            => names.Select(n => root.Elements().FirstOrDefault(e => Equal(e.Name.LocalName, n))).FirstOrDefault(e => e != null);
        private static string Field(XElement root, params string[] names)
        {
            XElement? node = Child(root, names);
            return node == null ? string.Empty : (Child(node, "Value")?.Value ?? node.Value).Trim();
        }
        private static decimal Measurement(XElement root, params string[] names) => Number(Field(root, names));
        private static string UnitOf(XElement root, params string[] names)
        {
            XElement? node = Child(root, names);
            string unit = node?.Attributes().FirstOrDefault(a => Equal(a.Name.LocalName, "UOM"))?.Value
                ?? (node == null ? string.Empty : Field(node, "UOM", "Unit"));
            return unit.ToUpperInvariant();
        }
        private static void CheckMeasurement(XElement node, string name, decimal value, string unit, params string[] aliases)
        {
            string[] names = new[] { name }.Concat(aliases).ToArray();
            Need(Child(node, names) != null, "The request is missing " + name + "; a complete material/part request is needed before saving.");
            Need(SameDistance(Measurement(node, names), UnitOf(node, names), value, unit), name + " differs from the PowerFab request.");
        }
        private static bool SameDistance(decimal a, string aUnit, decimal b, string bUnit)
            => Math.Abs(ToMm(a, aUnit) - ToMm(b, bUnit)) <= 0.01m;
        private static decimal ToMm(decimal value, string unit) { Unit(unit); return Equal(unit, "IN") ? value * 25.4m : value; }
        private static void Unit(string value) => Need(Equal(value, "MM") || Equal(value, "IN"), "Units must be MM or IN.");
        private static decimal Number(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal n) ? n : 0;
        private static int Quantity(string value)
        {
            decimal number = Number(value);
            Need(number > 0 && number <= int.MaxValue && number == decimal.Truncate(number), "Plate quantities in the request must be positive whole numbers.");
            return (int)number;
        }
        private static bool PositiveId(string value) => long.TryParse(value, out long n) && n > 0;
        private static bool Equal(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        internal static string PartKey(XElement p) => Key(Field(p, "JobNumber", "Job"), Field(p, "MainMark"), Field(p, "PieceMark"), Field(p, "Sequence"), Field(p, "LotNumber"));
        internal static string PartKey(NestPart p) => Key(p.JobNumber, p.MainMark, p.PieceMark, p.Sequence, p.LotNumber);
        internal static string PartKey(NestPlacement p) => Key(p.JobNumber, p.MainMark, p.PieceMark, p.Sequence, p.LotNumber);
        private static string Key(params string[] values) => string.Join("\u001f", values.Select(v => (v ?? string.Empty).Trim().ToUpperInvariant()));
        private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        private static byte[] ReadPng(string folder, string file)
        {
            string path = Path.GetFullPath(Path.Combine(folder, file));
            var info = new FileInfo(path);
            Need(info.Exists && info.Length > 24 && info.Length <= 50 * 1024 * 1024, "PNG file is missing, empty or larger than 50 MB: " + file);
            byte[] bytes = File.ReadAllBytes(path);
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            Need(bytes.Take(8).SequenceEqual(signature), "The image must be a PNG: " + file);
            return bytes;
        }
    }
}

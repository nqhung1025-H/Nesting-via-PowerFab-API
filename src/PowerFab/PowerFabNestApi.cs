using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Tekla.PowerFab.reqPowerFabAPI;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class SavedNestReceipt
    {
        public SavedNestReceipt() { }
        public string WorkOrderId { get; set; } = string.Empty;
        public string NestId { get; set; } = string.Empty;
        public string NestName { get; set; } = string.Empty;
        public AbeProcessTime? ProcessTime { get; set; }
        public string NestImageFilename { get; set; } = string.Empty;
        public string PowerFabNestId { get; set; } = string.Empty;
        public string SavedTo { get; set; } = string.Empty;
        public string MaterialKey { get; set; } = string.Empty;
        public string PurchaseOrderNumber { get; set; } = string.Empty;
        public string InventoryId { get; set; } = string.Empty;
        public string ExternalInventoryLinkId { get; set; } = string.Empty;
        public int SheetQuantity { get; set; } = 1;
        public DateTime SavedAtUtc { get; set; }
        public List<NestPart> Parts { get; set; } = new List<NestPart>();
        public List<string> RemnantPowerFabIds { get; set; } = new List<string>();
        public string RequisitionNumber {get;set;}=string.Empty;
        public string RequisitionItemId {get;set;}=string.Empty;
        public string ProjectRequisitionReference {get;set;}=string.Empty;
        public string TargetPurchaseOrderNumber {get;set;}=string.Empty;
        public string MaterialSource {get;set;}="Inventory";
        public string StockStatus {get;set;}=string.Empty;
        public List<string> CurrentInventoryIds {get;set;}=new List<string>();
        public List<string> CurrentPurchaseOrders {get;set;}=new List<string>();
        public string ResultFile {get;set;}=string.Empty;
        public DateTime? LinksRefreshedAtUtc {get;set;}
        public string OutlineWarning {get;set;}=string.Empty;
        public string ReferenceInventoryId {get;set;}=string.Empty;
        public string ReferencePurchaseOrderNumber {get;set;}=string.Empty;
        public string RequisitionBatchId {get;set;}=string.Empty;
        public bool InactiveInPowerFab {get;set;}
        public bool PartsReleased {get;set;}
        public DateTime? RemovedAtUtc {get;set;}
        public decimal ConfirmedCutQuantity {get;set;}
    }

    internal sealed partial class PowerFabNestApi
    {
        public const string SoftwareName = "Hanlon.PowerFab.Nesting";
        public const string SourceApplicationName = "Hanlon - Nesting";
        private readonly Func<string, string> execute;
        private readonly PowerFabReadModel? readModel;
        public PowerFabNestApi(Func<string, string> execute, PowerFabReadModel? readModel=null) { this.execute = execute; this.readModel=readModel; }

        public int RefreshInventory(NestingSnapshot snapshot, string exportFolder)
        {
            // Receipts already reflected in this fresh server response are a baseline,
            // so they must not be deducted a second time from the returned quantities.
            var baseline = ReadReceipts(exportFolder);
            var request = InventoryTracking.BuildRequest(snapshot);
            string audit = Path.Combine(exportFolder, "InventoryApiLog");
            Directory.CreateDirectory(audit);
            string stem = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string xml = Serialize(request);
            File.WriteAllText(Path.Combine(audit, stem + ".request.xml"), xml, new UTF8Encoding(false));
            string responseText = execute(xml);
            File.WriteAllText(Path.Combine(audit, stem + ".response.xml"), responseText, new UTF8Encoding(false));
            XDocument response = ParseResponse(responseText);
            ThrowErrors(response);
            var records = InventoryTracking.ReadRecords(response, snapshot, request.GetInventory.Length);
            records = AddRemnants(records, snapshot, exportFolder, out string remnantWarning);
            snapshot.ReplaceInventory(records, baseline);
            snapshot.RemnantRefreshWarning = remnantWarning;
            return records.Count;
        }

        public Dictionary<string, PowerFabDxf> ReadPartDxfs(NestingSnapshot snapshot, IEnumerable<NestPart> parts, string exportFolder)
            => PowerFabDxfSource.Read(snapshot, parts, execute, exportFolder);

        public string TestConnection()
        {
            var request = new fsreqFabSuiteXMLRequest();
            request.Version = new[] { new fsreqVersion() };
            XDocument response = ParseResponse(execute(Serialize(request)));
            ThrowErrors(response);
            XElement version = response.Descendants().FirstOrDefault(e => Named(e, "Version"))
                ?? throw new InvalidDataException("PowerFab returned no Version result.");
            if (!True(Value(version, "Successful"))) throw new InvalidDataException(Value(version, "ErrorMessage"));
            return string.Join(" ", new[] { Value(version, "MajorVersion"), Value(version, "MinorVersion"), Value(version, "InterfaceVersionNumber") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        public static fsreqFabSuiteXMLRequest BuildSaveRequest(LoadedNestResult loaded, NestingSnapshot? snapshot = null,
            IReadOnlyCollection<SavedNestReceipt>? previousSaves = null)
        {
            NestResultPackage result = loaded.Result;
            if (result.Remnants.Any(r => !RemnantGeometry.IsRectangle(r) || r.Edge.Equals("Bottom", StringComparison.OrdinalIgnoreCase)
                && r.Width <= Math.Max(Math.Max(result.LayoutConditions?.MinimumBottomRemnant??100m,result.LayoutConditions?.Bottom??0m),1m)))
                throw new InvalidDataException("This draft contains an irregular remnant or a bottom holding strip below the minimum size. Import or generate the layout again in Hanlon v1.12 to keep one right-hand or bottom rectangle. No SaveNest request was sent.");
            if (result.Remnants.Any(r => !RemnantGeometry.IsNumericExternalId(r.RemnantId)))
                throw new InvalidDataException("This draft has a non-numeric external remnant ID that triggers PowerFab's native TFS link error. Save through Hanlon v1 to upgrade an unsent draft. Previously submitted remnants need a separate repair review. No SaveNest request was sent.");
            NestMaterial m = result.Material;
            MaterialRequirements.ValidateUnallocated(m);
            XElement? exportedStock = FindExportedInventory(result, snapshot);
            var material = new fsreqMaterialUsed
            {
                Shape = exportedStock == null ? m.Shape : Value(exportedStock, "Shape"),
                Grade = exportedStock == null ? m.Grade : Value(exportedStock, "Grade"),
                Dimensions = SaveDimensions(m, exportedStock),
                Length = Distance(m.Length, m.Unit), Width = Distance(m.Width, m.Unit),
                Location = MaterialRequirements.UsesPowerFabAllocation(m) || string.IsNullOrWhiteSpace(m.StockLocation) ? null : m.StockLocation,
                SecondaryLocation = MaterialRequirements.UsesPowerFabAllocation(m) || string.IsNullOrWhiteSpace(m.StockSecondaryLocation) ? null : m.StockSecondaryLocation
            };
            if (!string.IsNullOrWhiteSpace(m.ParentRemnantPowerFabId)) material.ParentRemnantPowerFabID = m.ParentRemnantPowerFabId;
            else if (!MaterialRequirements.UsesPowerFabAllocation(m))
            {
                // FabSuiteInventoryID is a legacy alias for the EXTERNAL LINK ID,
                // not InventoryRecord.ID. Never send the record ID in that field.
                if (!InventoryTracking.PositiveId(m.ExternalInventoryLinkId))
                    throw new InvalidDataException("PowerFab has not supplied an inventory link for this sheet. Refresh inventory and generate nesting again. No nest was sent.");
                material.ExternalInventoryLinkID = m.ExternalInventoryLinkId;
                material.ForceExternalInventoryLinkID = true;
                material.ForceExternalInventoryLinkIDSpecified = true;
            }

            var nest = new fsreqNestResult
            {
                NestID = result.NestId, NestExtra1 = NestNaming.Display(result), NestExtra2 = AbeProcessTime.PowerFabValue(result.AbeImport), Quantity = "1", MaterialUsed = material,
                NestImageFilename = NestImageName(result, previousSaves), NestImageBase64 = Convert.ToBase64String(loaded.NestImage),
                NestedParts = result.Parts.Select(p => new fsreqNestPart
                {
                    JobNumber = p.JobNumber, MainMark = p.MainMark, PieceMark = p.PieceMark,
                    Sequence = p.Sequence, LotNumber = p.LotNumber,
                    Quantity = p.Quantity.ToString(CultureInfo.InvariantCulture),
                    Length = Distance(p.Length, p.Unit), Width = Distance(p.Width, p.Unit)
                }).ToArray(),
                Remnant = result.Remnants.Select((r, index) => new fsreqRemnant
                {
                    Type = fsreqRemnantType.Remnant, TypeSpecified = true,
                    RemnantID = r.RemnantId, RemnantName = result.AbeImport != null ? NestNaming.Display(result)
                        : string.IsNullOrWhiteSpace(r.Name) ? r.RemnantId : r.Name,
                    Quantity = r.Quantity.ToString(CultureInfo.InvariantCulture),
                    Shape = m.Shape, Grade = m.Grade,
                    Dimensions = new fsDimensionsWithMetric { Value = RemnantGeometry.Dimensions(m, r), Metric = m.Metric },
                    Length = Distance(r.Length, r.Unit), Width = Distance(r.Width, r.Unit),
                    Area = new fsDecimalWithUOM { Value = r.Area, UOM = IsInch(r.Unit) ? fsUOM.SQIN : fsUOM.SQMM },
                    Weight = r.Weight.HasValue ? new fsDecimalWithUOM
                    { Value = r.Weight.Value, UOM = IsInch(r.Unit) ? fsUOM.lb : fsUOM.KG } : null,
                    RemnantImageFilename = ImageName(r.RemnantId, "Remnant"),
                    RemnantImageBase64 = Convert.ToBase64String(loaded.RemnantImages[index]!)
                }).ToArray()
            };
            var save = new fsreqSaveNest
            {
                APILog = fsreqCommandAPILog.all, APILogSpecified = true,
                SourceApplication = SourceApplicationName, SourceApplicationVersion = "1.12.0",
                RequisitionNumber = string.IsNullOrWhiteSpace(result.RequisitionNumber) ? null : result.RequisitionNumber,
                PurchaseOrderNumber = MaterialRequirements.UsesPowerFabAllocation(m)&&!string.IsNullOrWhiteSpace(result.TargetPurchaseOrderNumber) ? result.TargetPurchaseOrderNumber : null,
                CreationDateTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                CombinationRunID = new fsreqSaveNestCombinationRunID { Value = result.WorkOrderId,
                    ReloadInventory = exportedStock == null, ReloadInventorySpecified = true,
                    SaveResults = true, SaveResultsSpecified = true },
                NestResult = new[] { nest }
            };
            return new fsreqFabSuiteXMLRequest { SaveNest = new[] { save } };
        }

        // A fresh GetInventory read still validates the physical record before saving.
        // When that same link and material were exported by this run, keep SaveNest
        // bound to the run's inventory instead of asking PowerFab to reload it again.
        // New or changed stock continues to use ReloadInventory=true.
        internal static bool UsesExportedInventory(NestResultPackage result, NestingSnapshot? snapshot)
            => FindExportedInventory(result, snapshot) != null;

        // GetInventory formats display dimensions differently from WorkOrder exports
        // (for example 10 x 1500.0 versus 10X1500). After verifying the same link and
        // physical size, preserve the run's exact material identity in SaveNest.
        private static fsDimensionsWithMetric SaveDimensions(NestMaterial material, XElement? exportedStock)
        {
            XElement? dimension = exportedStock == null ? null : NestingSnapshot.Child(exportedStock, "Dimensions");
            if (dimension == null || string.IsNullOrWhiteSpace(dimension.Value))
                return new fsDimensionsWithMetric { Value = material.Dimensions, Metric = material.Metric };
            string? metric = dimension.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("Metric", StringComparison.OrdinalIgnoreCase))?.Value;
            bool isMetric = metric == null ? material.Metric : metric == "1" || metric.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (metric != null && metric != "1" && metric != "0"
                && !metric.Equals("true", StringComparison.OrdinalIgnoreCase) && !metric.Equals("false", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The exported stock has an invalid Metric attribute. Re-export the nesting request. No nest was sent.");
            return new fsDimensionsWithMetric { Value = dimension.Value.Trim(), Metric = isMetric };
        }

        private static XElement? FindExportedInventory(NestResultPackage result, NestingSnapshot? snapshot)
        {
            NestMaterial m = result.Material;
            if (snapshot == null || snapshot.WorkOrderID != result.WorkOrderId
                || MaterialRequirements.UsesPowerFabAllocation(m)
                || !InventoryTracking.PositiveId(m.ExternalInventoryLinkId)
                || !string.IsNullOrWhiteSpace(m.ParentRemnantPowerFabId)) return null;
            decimal factor = m.Unit.Equals("MM", StringComparison.OrdinalIgnoreCase) ? 1m
                : m.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 0m;
            if (factor == 0m) return null;
            return snapshot.OriginalStock.FirstOrDefault(stock =>
                Value(stock, "ExternalInventoryLinkID") == m.ExternalInventoryLinkId
                && Same(stock, m.Shape, "Shape") && Same(stock, m.Grade, "Grade")
                && Same(stock, m.PurchaseOrderNumber, "PONumber", "PurchaseOrderNumber", "PO")
                && Same(stock, m.StockJob, "Job", "JobNumber", "JobReserve")
                && Same(stock, m.StockLocation, "Location")
                && Same(stock, m.StockSecondaryLocation, "SecondaryLocation")
                && MatchesDistance(stock, m.Length * factor, "Length")
                && MatchesDistance(stock, m.Width * factor, "Width")
                && MatchesDistance(stock, m.Thickness * factor, "PlateThickness", "Thickness")
                && decimal.TryParse(Value(stock, "Quantity"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quantity)
                && quantity >= 1m);
        }

        private static bool Same(XElement stock, string value, params string[] fields)
            => string.Equals(Value(stock, fields), value.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool MatchesDistance(XElement stock, decimal millimetres, params string[] fields)
        {
            XElement? node = NestingSnapshot.Child(stock, fields);
            if (node == null || !decimal.TryParse(Value(stock, fields), NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal value)) return false;
            string unit = node.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("UOM", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            decimal factor = unit.Equals("MM", StringComparison.OrdinalIgnoreCase) ? 1m
                : unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 0m;
            return factor > 0m && Math.Abs(value * factor - millimetres) <= 0.01m;
        }

        public SavedNestReceipt Save(LoadedNestResult loaded, NestingSnapshot snapshot, string exportFolder)
        {
            try
            {
                bool prepare=loaded.Result.AutoRequisitionNumber&&!requisitionSaveInProgress;
                using var lease=BeginRequisitionSave(prepare);
                if(prepare)PrepareRequisitionBatch(new[]{loaded},exportFolder);
                return SaveCore(loaded,snapshot,exportFolder);
            }
            catch(Exception error)
            {
                string audit=Path.Combine(exportFolder,"NestApiLog");
                string stem=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loaded.Result.NestId))).ToLowerInvariant();
                if(!error.Message.Contains("HanlonLastSaveDiagnostics.zip",StringComparison.Ordinal)
                    &&File.Exists(Path.Combine(audit,stem+".pending"))&&File.Exists(Path.Combine(audit,stem+".response.xml")))
                {
                    string? diagnostics=WriteSaveDiagnostics(exportFolder,audit,stem,loaded.Result,snapshot);
                    if(diagnostics!=null)throw new InvalidDataException(error.Message+"\nDiagnostic ZIP: HanlonLastSaveDiagnostics.zip\nOpen the Exports folder shown in the diagnostic path and upload this ZIP for review.",error);
                }
                throw;
            }
        }

        private SavedNestReceipt SaveCore(LoadedNestResult loaded, NestingSnapshot snapshot, string exportFolder)
        {
            string audit = Path.Combine(exportFolder, "NestApiLog");
            CheckUnresolvedSaves(loaded.Result, audit);
            ResolveRequisition(loaded,exportFolder);
            // GetInventory with LinkSoftware returns the link that SaveNest needs.
            // Resolve it from the same physical record, never from a PO-name match.
            if (MaterialRequirements.UsesPowerFabAllocation(loaded.Result.Material))
            {
                // PowerFab matches by size or creates the purchasing requirement.
                // A sizing reference must not turn back into a forced inventory link.
                ValidatePurchaseOrder(loaded.Result, exportFolder);
            }
            else if (string.IsNullOrWhiteSpace(loaded.Result.Material.ParentRemnantPowerFabId))
            {
                RefreshInventory(snapshot, exportFolder);
                BindCurrentInventoryLink(loaded.Result.Material, snapshot);
            }
            else RefreshInventory(snapshot, exportFolder);
            if (loaded.Result.Material.ParentRemnantPowerFabId.Length>0) ValidateFutureRemnant(loaded.Result.Material,snapshot);
            LoadedNestResult.Validate(loaded.Result, snapshot);
            var prior = ReadReceipts(exportFolder);
            CheckPriorSaves(loaded.Result, prior, snapshot);
            Directory.CreateDirectory(audit);
            string stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loaded.Result.NestId))).ToLowerInvariant();
            string pending = Path.Combine(audit, stem + ".pending");
            if (File.Exists(pending)) throw new InvalidOperationException(
                "A previous save of this Nest ID has no confirmed receipt. Check PowerFab's API Log and the response files before retrying this Nest ID.");
            PrepareNumericRemnantIds(loaded, Path.Combine(audit, stem));
            var requestMessage = BuildSaveRequest(loaded, snapshot, prior);
            string request = Serialize(requestMessage);
            File.WriteAllText(Path.Combine(audit, stem + ".request.xml"), request, new UTF8Encoding(false));
            File.WriteAllText(pending, DateTime.UtcNow.ToString("o"));
            string responseText = execute(request);
            File.WriteAllText(Path.Combine(audit, stem + ".response.xml"), responseText, new UTF8Encoding(false));
            XDocument response = ParseResponse(responseText);
            try { ThrowErrors(response); }
            catch (Exception error)
            {
                // A response with a SavedNest record may represent a partial save.
                // Preserve its pending marker so the same layout is not blindly posted again.
                if (!response.Descendants().Any(e => Named(e, "SavedNest"))) File.Delete(pending);
                string? diagnostics = WriteSaveDiagnostics(exportFolder, audit, stem, loaded.Result, snapshot);
                if (diagnostics != null) throw new InvalidDataException(error.Message + Environment.NewLine
                    + "Diagnostic ZIP: " + Path.GetFileName(diagnostics) + Environment.NewLine
                    + "Open the Exports folder shown in the diagnostic path and upload this ZIP for review.", error);
                throw;
            }
            XElement save = response.Descendants().FirstOrDefault(e => Named(e, "SaveNest"))
                ?? throw new InvalidDataException("No SaveNest result was returned; check the recorded response and API Log.");
            XElement? saved = save.Elements().FirstOrDefault(e => Named(e, "SavedNest") && Value(e, "NestID") == loaded.Result.NestId);
            if (saved == null) throw new InvalidDataException("PowerFab returned no saved record matching this Nest ID. Check the response and API Log.");
            var receipt = new SavedNestReceipt
            {
                WorkOrderId = loaded.Result.WorkOrderId, NestId = loaded.Result.NestId, NestName = NestNaming.Display(loaded.Result),
                ProcessTime = loaded.Result.AbeImport?.ProcessTime,
                NestImageFilename = requestMessage.SaveNest[0].NestResult[0].NestImageFilename,
                PowerFabNestId = Value(saved, "ExternalNestID"), SavedTo = Value(saved, "SavedTo"),
                MaterialKey = MaterialKey(loaded.Result.Material),
                PurchaseOrderNumber = loaded.Result.Material.PurchaseOrderNumber,
                InventoryId = loaded.Result.Material.InventoryId, SheetQuantity = 1,
                ExternalInventoryLinkId = loaded.Result.Material.ExternalInventoryLinkId,
                SavedAtUtc = DateTime.UtcNow, Parts = loaded.Result.Parts,
                ProjectRequisitionReference=loaded.Result.RequisitionNumber,
                TargetPurchaseOrderNumber=loaded.Result.TargetPurchaseOrderNumber,
                MaterialSource=loaded.Result.Material.Source,ResultFile=loaded.SourceFile,
                ReferenceInventoryId=loaded.Result.Material.ReferenceInventoryId,
                ReferencePurchaseOrderNumber=loaded.Result.Material.ReferencePurchaseOrderNumber,
                RequisitionBatchId=loaded.Result.RequisitionBatchId,
                RemnantPowerFabIds = ConfirmedRemnantIds(loaded.Result,save)
            };
            if (string.IsNullOrWhiteSpace(receipt.SavedTo)) throw new InvalidDataException("The response does not identify where the nest was saved.");
            if (!string.Equals(receipt.SavedTo, "Inventory", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(receipt.SavedTo, "FutureRemnant", StringComparison.OrdinalIgnoreCase)
                && !(MaterialRequirements.UsesPowerFabAllocation(loaded.Result.Material)&&string.Equals(receipt.SavedTo,"Requisition",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("PowerFab already saved this nest to " + Destination(saved)
                    + ". The selected PO quantity has not been marked as allocated locally. Do not save these parts again until this record is reviewed."
                    + Environment.NewLine + "Response: " + Path.Combine(audit, stem + ".response.xml"));
            if (receipt.RemnantPowerFabIds.Count < loaded.Result.Remnants.Count)
                throw new InvalidDataException("PowerFab saved the parent nest, but did not confirm every future remnant. Do not save these parts again until the response is reviewed."
                    + Environment.NewLine + "Response: " + Path.Combine(audit, stem + ".response.xml"));
            ReadSavedDestination(receipt,saved);
            // Only the confirmed destination may reserve stock locally. A sizing
            // reference that saved to requisition does not consume the referenced sheet.
            if(MaterialRequirements.UsesPowerFabAllocation(loaded.Result.Material)
                &&receipt.SavedTo.Equals("Inventory",StringComparison.OrdinalIgnoreCase)
                &&receipt.CurrentInventoryIds.Count==1)
            {
                receipt.InventoryId=receipt.CurrentInventoryIds[0];
                receipt.MaterialKey="I:"+receipt.InventoryId;
            }
            if(loaded.Result.Material.Source.Equals("Purchase",StringComparison.OrdinalIgnoreCase)
                &&loaded.Result.TargetPurchaseOrderNumber.Length>0
                &&(!receipt.SavedTo.Equals("Inventory",StringComparison.OrdinalIgnoreCase)
                    ||!receipt.CurrentPurchaseOrders.Contains(loaded.Result.TargetPurchaseOrderNumber,StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("PowerFab saved this nest, but did not confirm the selected purchase order. Review the recorded destination before saving these parts again.");
            string receiptFolder = Path.Combine(exportFolder, "SavedNestReceipts");
            Directory.CreateDirectory(receiptFolder);
            File.WriteAllText(Path.Combine(receiptFolder, stem + ".json"), JsonSerializer.Serialize(receipt, LoadedNestResult.JsonOptions));
            File.Delete(pending);
            try { RemnantCatalog.RememberSaved(loaded,receipt,exportFolder); }
            catch(Exception ex) { receipt.OutlineWarning="Nest saved; remnant outline catalogue could not be updated: "+ex.Message; }
            snapshot.RefreshQuantities(ReadReceipts(exportFolder));
            return receipt;
        }

        public static IReadOnlyList<SavedNestReceipt> ReadReceipts(string exportFolder)
        {
            string folder = Path.Combine(exportFolder, "SavedNestReceipts");
            if (!Directory.Exists(folder)) return Array.Empty<SavedNestReceipt>();
            return Directory.GetFiles(folder, "*.json").Select(path =>
                JsonSerializer.Deserialize<SavedNestReceipt>(File.ReadAllText(path), LoadedNestResult.JsonOptions)
                ?? throw new InvalidDataException("Could not read saved-nest receipt: " + path)).ToList();
        }

        private static void PrepareNumericRemnantIds(LoadedNestResult loaded, string auditStem)
        {
            var remnants = loaded.Result.Remnants;
            if (remnants.All(r => RemnantGeometry.IsNumericExternalId(r.RemnantId))) return;
            // Never rename identities that may already exist in PowerFab. The
            // normal receipt/pending guards also run before reaching this point.
            if (File.Exists(auditStem + ".request.xml"))
                throw new InvalidOperationException("This older draft has already been submitted to PowerFab. Its remnant IDs have not been changed. Review the existing save/TFS state before making a new layout. No SaveNest request was sent.");
            var original = remnants.Select(r => r.RemnantId).ToArray();
            var used = new HashSet<string>(original.Where(RemnantGeometry.IsNumericExternalId), StringComparer.Ordinal);
            string temporary = loaded.SourceFile + ".remnant-ids.tmp";
            try
            {
                for (int i = 0; i < remnants.Count; i++)
                {
                    if (RemnantGeometry.IsNumericExternalId(remnants[i].RemnantId)) continue;
                    int ordinal = i + 1;
                    string id;
                    do { id = RemnantGeometry.NumericExternalId(loaded.Result.NestId, ordinal++); } while (!used.Add(id));
                    remnants[i].RemnantId = id;
                }
                File.WriteAllText(temporary, JsonSerializer.Serialize(loaded.Result, LoadedNestResult.JsonOptions));
                File.Move(temporary, loaded.SourceFile, true);
            }
            catch
            {
                for (int i = 0; i < remnants.Count; i++) remnants[i].RemnantId = original[i];
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }

        private static List<string> ConfirmedRemnantIds(NestResultPackage result,XElement save)
        {
            var records=save.Elements().Where(e=>Named(e,"SavedRemnant")&&(Value(e,"NestID").Length==0||Value(e,"NestID")==result.NestId)).ToList();
            var ids=new List<string>();
            foreach(var remnant in result.Remnants)
            {
                var matching=records.Where(e=>Value(e,"ExternalRemnantID")==remnant.RemnantId).ToList();
                if(matching.Count==0&&result.Remnants.Count==1&&records.Count==1&&Value(records[0],"ExternalRemnantID").Length==0)matching.Add(records[0]);
                if(matching.Count!=1)continue;
                string id=Value(matching[0],"PowerFabRemnantID","RemnantPowerFabID","FabSuiteRemnantID","PowerFabID");
                if(InventoryTracking.PositiveId(id)&&!ids.Contains(id))ids.Add(id);
            }
            return ids;
        }

        internal static void CheckPriorSaves(NestResultPackage result, IEnumerable<SavedNestReceipt> prior, NestingSnapshot snapshot)
        {
            var allReceipts=prior.ToList();
            var receipts = allReceipts.Where(r => r.WorkOrderId == result.WorkOrderId&&!r.PartsReleased).ToList();
            CheckStockQuantity(result, allReceipts, snapshot);
            if (allReceipts.Any(r => r.NestId == result.NestId)) throw new InvalidOperationException("This Nest ID already has a confirmed save receipt. Generate a new layout to replace a removed nest.");
            var all = new NestResultPackage
            {
                Format = result.Format, Status = result.Status, WorkOrderId = result.WorkOrderId, NestId = result.NestId,
                LayoutSource = result.LayoutSource, NestImageFile = result.NestImageFile, Material = result.Material,
                RequisitionNumber=result.RequisitionNumber,TargetPurchaseOrderNumber=result.TargetPurchaseOrderNumber,
                Parts = result.Parts.Concat(receipts.SelectMany(r => r.Parts).Where(p => result.Parts.Any(current =>
                    LoadedNestResult.PartKey(current) == LoadedNestResult.PartKey(p)))).ToList(),
                Remnants = result.Remnants
            };
            LoadedNestResult.Validate(all, snapshot);
        }

        internal static string MaterialKey(NestMaterial m) => MaterialRequirements.UsesPowerFabAllocation(m)
            ? "P:"+m.Shape+"|"+m.Grade+"|"+m.Thickness+"|"+m.Length+"|"+m.Width
            : !string.IsNullOrWhiteSpace(m.ParentRemnantPowerFabId)
            ? "R:" + m.ParentRemnantPowerFabId : InventoryTracking.PositiveId(m.InventoryId)
                ? "I:" + m.InventoryId : "E:" + m.ExternalInventoryLinkId;

        internal static void CheckStockQuantity(NestResultPackage result, IEnumerable<SavedNestReceipt> saved, NestingSnapshot snapshot)
        {
            if(MaterialRequirements.UsesPowerFabAllocation(result.Material))return;
            var prior = saved.Where(r=>!r.PartsReleased).ToList();
            if (!snapshot.LiveInventoryLoaded && prior.Any(r => r.WorkOrderId == result.WorkOrderId && string.IsNullOrWhiteSpace(r.MaterialKey)))
                throw new InvalidOperationException("This request has older save receipts without sheet-use details. Re-export a fresh combining request before calculating another sheet.");
            XElement? stock = LoadedNestResult.FindStock(snapshot.RequestRoot, result.Material);
            if (stock != null && InventoryTracking.UnavailableReason(stock, snapshot) is string reason && reason.Length > 0)
                throw new InvalidOperationException(reason + ". Refresh inventory and choose available material.");
            if (stock == null || !decimal.TryParse(Value(stock, "Quantity"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quantity)
                || quantity - (stock == null ? 0 : InventoryTracking.UnreflectedSaves(snapshot, stock, prior)) < 1)
                throw new InvalidOperationException("No unused sheet remains in the selected stock quantity. Re-export current available inventory or choose another stock row.");
        }

        internal static void BindCurrentInventoryLink(NestMaterial material, NestingSnapshot snapshot)
        {
            XElement? stock = LoadedNestResult.FindStock(snapshot.RequestRoot, material);
            if (stock == null) throw new InvalidOperationException("The selected inventory sheet is no longer available. Refresh inventory and generate the layout again. No nest was sent.");
            string link = Value(stock, "ExternalInventoryLinkID");
            if (!InventoryTracking.PositiveId(link)) throw new InvalidDataException(
                "PowerFab returned no inventory link for the selected sheet. Check GetInventory in the API Log. No nest was sent.");
            material.ExternalInventoryLinkId = link;
        }

        private static string? WriteSaveDiagnostics(string exportFolder, string audit, string stem, NestResultPackage result, NestingSnapshot snapshot)
        {
            string destination = Path.Combine(exportFolder, "HanlonLastSaveDiagnostics.zip");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    foreach (string suffix in new[] { ".request.xml", ".response.xml" })
                        zip.CreateEntryFromFile(Path.Combine(audit, stem + suffix), "SaveNest" + suffix);
                    string folder = Path.Combine(exportFolder, "InventoryApiLog");
                    string? latest = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.response.xml").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
                    if (latest != null)
                    {
                        zip.CreateEntryFromFile(latest, "GetInventory.response.xml");
                        string request = latest.Substring(0, latest.Length - ".response.xml".Length) + ".request.xml";
                        if (File.Exists(request)) zip.CreateEntryFromFile(request, "GetInventory.request.xml");
                    }
                    using (var original = new StreamWriter(zip.CreateEntry("WorkOrder.original.xml").Open()))
                        original.Write(snapshot.OriginalRequestXml);
                    using (var current = new StreamWriter(zip.CreateEntry("WorkOrder.current.xml").Open()))
                        current.Write(snapshot.RequestRoot.ToString());
                    using var writer = new StreamWriter(zip.CreateEntry("SelectedStock.json").Open());
                    var submittedDimensions = SaveDimensions(result.Material, FindExportedInventory(result, snapshot));
                    writer.Write(JsonSerializer.Serialize(new { Version = "0.27.0", result.WorkOrderId, result.NestId,
                        InventoryMode = MaterialRequirements.UsesPowerFabAllocation(result.Material) ? "PowerFabMaterialRequirement" : UsesExportedInventory(result, snapshot) ? "ExportedRunInventory" : "ReloadCurrentInventory",
                        SubmittedMaterialDimensions = submittedDimensions.Value,
                        SubmittedMaterialMetric = submittedDimensions.Metric,
                        Material = result.Material }, LoadedNestResult.JsonOptions));
                }
                File.Move(temporary, destination, true); return destination;
            }
            catch (Exception) { return null; }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
        }

        internal static void CheckUnresolvedSaves(NestResultPackage result, string audit)
        {
            if (!Directory.Exists(audit)) return;
            foreach (string pending in Directory.GetFiles(audit, "*.pending"))
            {
                string stem = pending.Substring(0, pending.Length - ".pending".Length);
                string path = stem + ".request.xml";
                if (!File.Exists(path)) continue;
                XDocument request = ParseResponse(File.ReadAllText(path));
                XElement? save = request.Descendants().FirstOrDefault(e => Named(e, "SaveNest"));
                if (save == null) continue;
                bool sameNest = save.Descendants().Any(e => Named(e, "NestID") && e.Value.Trim() == result.NestId);
                bool sameParts = Value(save, "CombinationRunID") == result.WorkOrderId
                    && save.Descendants().Where(e => Named(e, "NestPart")).Any(p =>
                        result.Parts.Any(current => LoadedNestResult.PartKey(current) == LoadedNestResult.PartKey(p)));
                if (!sameNest && !sameParts) continue;
                string responsePath = stem + ".response.xml", destination = "an unconfirmed destination";
                if (File.Exists(responsePath))
                {
                    XElement? saved = ParseResponse(File.ReadAllText(responsePath)).Descendants().FirstOrDefault(e => Named(e, "SavedNest"));
                    if (saved != null) destination = Destination(saved);
                }
                throw new InvalidOperationException("A previous save for these parts needs review (" + destination
                    + "). Check this PowerFab record before saving again, to avoid duplicate nesting."
                    + Environment.NewLine + "Response: " + responsePath);
            }
        }

        private static string Destination(XElement saved)
        {
            string destination = Value(saved, "SavedTo");
            XElement? requisition = saved.Elements().FirstOrDefault(e => Named(e, "RequisitionItem"));
            if (requisition != null)
            {
                string number = Value(requisition, "RequisitionNumber"), item = Value(requisition, "ItemID");
                if (number.Length > 0) destination += " '" + number + "'";
                if (item.Length > 0) destination += ", item " + item;
            }
            string id = Value(saved, "ExternalNestID");
            return destination + (id.Length > 0 ? " (PowerFab nest ID " + id + ")" : "");
        }

        internal static string Serialize(fsreqFabSuiteXMLRequest request)
        {
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                new XmlSerializer(typeof(fsreqFabSuiteXMLRequest)).Serialize(writer, request);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        private static XDocument ParseResponse(string text)
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        private static void ThrowErrors(XDocument response)
        {
            var errors = response.Descendants().Where(e => Named(e, "XMLError") || Named(e, "NestError"))
                .Select(e => Value(e, "ErrorMessage") is string message && message.Length > 0 ? message : e.Value.Trim()).Where(s => s.Length > 0).ToList();
            XElement? save = response.Descendants().FirstOrDefault(e => Named(e, "SaveNest"));
            if (save != null && ((Value(save, "Successful").Length > 0 && !True(Value(save, "Successful")))
                || (int.TryParse(Value(save, "TotalUnSuccessful"), out int failed) && failed > 0)))
                errors.Add(Value(save, "ErrorMessage") is string message && message.Length > 0 ? message : "PowerFab reported an unsuccessful SaveNest.");
            if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Distinct()));
        }
        private static fsDecimalWithUOM Distance(decimal value, string unit) => new fsDecimalWithUOM { Value = value, UOM = IsInch(unit) ? fsUOM.IN : fsUOM.MM };
        private static bool IsInch(string unit) => string.Equals(unit, "IN", StringComparison.OrdinalIgnoreCase);
        private static bool True(string value) => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        private static bool Named(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);
        private static string Value(XElement e, params string[] names) => names.Select(n => e.Elements().FirstOrDefault(x => Named(x, n))?.Value.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
        private static string NestImageName(NestResultPackage result, IReadOnlyCollection<SavedNestReceipt>? previousSaves)
        {
            if (string.IsNullOrWhiteSpace(result.RequisitionNumber)) return ImageName(result.NestId, "Nest");
            // Use Windows-safe characters even when built or checked on another OS.
            string requisition = new string(result.RequisitionNumber.Trim().ToUpperInvariant()
                .Select(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '-' : c).ToArray()).Trim(' ', '.');
            if (requisition.Length > 120) requisition = requisition.Substring(0, 120).TrimEnd(' ', '.');
            decimal thicknessMm = result.Material.Thickness * (IsInch(result.Material.Unit) ? 25.4m : 1m);
            string stem = "Hanlon-Nest-" + requisition + "-"
                + thicknessMm.ToString("0.############################", CultureInfo.InvariantCulture) + "mm";
            var used = new HashSet<string>((previousSaves ?? Array.Empty<SavedNestReceipt>())
                .Select(r => r.NestImageFilename), StringComparer.OrdinalIgnoreCase);
            string filename = stem + ".png";
            int sheet = 2;
            while (used.Contains(filename)) filename = stem + "-" + (sheet++).ToString(CultureInfo.InvariantCulture) + ".png";
            return filename;
        }

        private static string ImageName(string id, string kind) => "Hanlon-" + kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).Substring(0, 20) + ".png";
    }
}

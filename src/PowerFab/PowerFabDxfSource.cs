using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Tekla.PowerFab.reqPowerFabAPI;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class PowerFabDxf
    {
        public string JobNumber { get; set; } = string.Empty;
        public string PieceMark { get; set; } = string.Empty;
        public string CncFileId { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public string SourceFormat { get; set; } = "DXF";
        public string OriginalText { get; set; } = string.Empty;
        public double? ThicknessMm { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    internal static class PowerFabDxfSource
    {
        private const int MaximumTextLength = 50 * 1024 * 1024;

        public static Dictionary<string, PowerFabDxf> Read(NestingSnapshot snapshot, IEnumerable<NestPart> parts,
            Func<string, string>? execute, string exportFolder)
        {
            var requested = parts.ToList();
            var distinct = requested.GroupBy(p => DrawingKey(p.JobNumber, p.PieceMark)).Select(g => g.First()).ToList();
            var drawings = new Dictionary<string, PowerFabDxf>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<string>();
            foreach (NestPart part in distinct)
            {
                try
                {
                    PowerFabDxf? drawing = Embedded(snapshot, part);
                    if (drawing != null) drawings[DrawingKey(part.JobNumber, part.PieceMark)] = drawing;
                }
                catch (InvalidDataException exception) when (exception.Message.StartsWith("NC1 plate geometry:", StringComparison.Ordinal))
                { issues.Add(exception.Message); }
            }
            var missing = distinct.Where(p => !drawings.ContainsKey(DrawingKey(p.JobNumber, p.PieceMark))).ToList();
            if (missing.Count > 0 && execute == null)
            {
                if (issues.Count > 0) throw new InvalidDataException(issues[0]);
                throw new InvalidOperationException("DXF data is missing from the nesting request for " + Names(missing)
                    + ". Start Hanlon through the PowerFab extension so it can retrieve the linked DXFs directly.");
            }

            string capture = Path.Combine(exportFolder, "PowerFabDxfs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            var warnings = new List<string>();
            foreach (var job in missing.GroupBy(p => p.JobNumber.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (job.Key.Length == 0) throw new InvalidDataException("A job number is required to retrieve the DXF for " + Names(job) + ".");
                // One job per request: the CNC response does not repeat JobNumber.
                // Repeated assembly instances share one job/piece-mark drawing.
                foreach (NestPart[] batch in job.Chunk(100))
                {
                    var request = BuildRequest(job.Key, batch.Select(p => p.PieceMark));
                    string requestXml = PowerFabNestApi.Serialize(request);
                    Directory.CreateDirectory(capture);
                    string stem = Guid.NewGuid().ToString("N");
                    File.WriteAllText(Path.Combine(capture, stem + ".request.xml"), requestXml, new UTF8Encoding(false));
                    string responseXml = execute!(requestXml);
                    File.WriteAllText(Path.Combine(capture, stem + ".response.xml"), responseXml, new UTF8Encoding(false));
                    XDocument response = Parse(responseXml);
                    foreach (string warning in response.Descendants().Where(e => N(e, "XMLWarning")).Select(e => e.Value.Trim()).Where(s => s.Length > 0)) warnings.Add(warning);
                    var errors = response.Descendants().Where(e => N(e, "XMLError")).Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList();
                    if (errors.Count > 0) throw new InvalidDataException("PowerFab could not retrieve CNC data: " + string.Join("; ", errors));
                    XElement? result = response.Root?.Elements().SingleOrDefault(e => N(e, "CNCData"));
                    if (result == null || !True(F(result, "Successful")))
                        throw new InvalidDataException("PowerFab CNC retrieval failed for job " + job.Key + ": " + (result == null ? "No CNCData result was returned." : F(result, "ErrorMessage")));
                    foreach (NestPart part in batch)
                    {
                        var records = result.Elements().Where(e => N(e, "PieceMarkCNCData")
                            && Equal(F(e, "PieceMark"), part.PieceMark)
                            && (F(e, "JobNumber", "Job").Length == 0 || Equal(F(e, "JobNumber", "Job"), part.JobNumber)));
                        try
                        {
                            PowerFabDxf? drawing = Select(records, part, "PowerFab CNC API");
                            if (drawing != null) drawings[DrawingKey(part.JobNumber, part.PieceMark)] = drawing;
                        }
                        catch (InvalidDataException exception) when (exception.Message.StartsWith("NC1 plate geometry:", StringComparison.Ordinal))
                        { issues.Add(exception.Message); }
                    }
                }
            }
            var unavailable = distinct.Where(p => !drawings.ContainsKey(DrawingKey(p.JobNumber, p.PieceMark))).ToList();
            if (unavailable.Count > 0)
                throw new InvalidDataException("No usable DXF or supported NC1 plate geometry was returned by PowerFab for: " + Names(unavailable)
                    + ". Import or link the correct DXF in PowerFab's CNC files for these job/piece marks, then Generate nesting again."
                    + " This version supports flat NC1 plates with straight/signed-radius AK/IK contours, explicit tangential notches and round through-holes."
                    + (issues.Count == 0 ? "" : "\n" + string.Join("\n", issues.Distinct().Take(5)))
                    + (warnings.Count == 0 ? "" : "\nPowerFab: " + string.Join("; ", warnings.Distinct()).Substring(0, Math.Min(1500, string.Join("; ", warnings.Distinct()).Length))));

            Directory.CreateDirectory(capture);
            var map = new List<object>();
            foreach (PowerFabDxf drawing in drawings.Values)
            {
                if (drawing.ThicknessMm.HasValue)
                    foreach (XElement part in snapshot.PartNodes().Where(p => Equal(F(p, "JobNumber", "Job"), drawing.JobNumber) && Equal(F(p, "PieceMark"), drawing.PieceMark)))
                    {
                        XElement? t = NestingSnapshot.Child(part, "PlateThickness", "Thickness");
                        string unit = t?.Attributes().FirstOrDefault(a => Equal(a.Name.LocalName, "UOM"))?.Value ?? "";
                        double expected = double.TryParse(F(part, "PlateThickness", "Thickness"), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                            && (Equal(unit, "MM") || Equal(unit, "IN")) ? n * (Equal(unit, "IN") ? 25.4 : 1) : 0;
                        if (Math.Abs(expected - drawing.ThicknessMm.Value) > 0.01)
                            throw new InvalidDataException("NC1 plate thickness differs from the PowerFab request: " + drawing.JobNumber + " / " + drawing.PieceMark);
                    }
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DrawingKey(drawing.JobNumber, drawing.PieceMark)))).ToLowerInvariant();
                string file = hash + ".dxf";
                File.WriteAllText(Path.Combine(capture, file), drawing.Text, new UTF8Encoding(false));
                if (drawing.SourceFormat == "NC1") File.WriteAllText(Path.Combine(capture, hash + ".nc1"), drawing.OriginalText, new UTF8Encoding(false));
                map.Add(new { drawing.JobNumber, drawing.PieceMark, drawing.CncFileId, drawing.SourceName, drawing.SourceFormat, File = file,
                    Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(drawing.Text))).ToLowerInvariant() });
            }
            File.WriteAllText(Path.Combine(capture, "source-map.json"), JsonSerializer.Serialize(map, LoadedNestResult.JsonOptions));
            return requested.GroupBy(LoadedNestResult.PartKey).ToDictionary(g => g.Key,
                g => drawings[DrawingKey(g.First().JobNumber, g.First().PieceMark)]);
        }

        internal static fsreqFabSuiteXMLRequest BuildRequest(string job, IEnumerable<string> marks)
            => new fsreqFabSuiteXMLRequest
            {
                CNCData = new[] { new fsreqCNCData
                {
                    JobNumber = job, PieceMark = marks.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ZipFiles = false, ZipFilesSpecified = true, APILog = fsreqCommandAPILog.all, APILogSpecified = true
                } }
            };

        internal static PowerFabDxf? Embedded(NestingSnapshot snapshot, NestPart part)
        {
            var records = snapshot.RequestRoot.Descendants().Where(e => N(e, "PartCNCData", "PieceMarkCNCData", "WorkOrderPart", "Part")
                && Equal(F(e, "PieceMark"), part.PieceMark) && NestingSnapshot.Child(e, "CNCData", "CNCFile") != null)
                .Where(e => Equal(F(e, "JobNumber", "Job"), part.JobNumber)
                    || (F(e, "JobNumber", "Job").Length == 0 && snapshot.PartNodes()
                        .Where(p => Equal(F(p, "PieceMark"), part.PieceMark)).Select(p => F(p, "JobNumber", "Job"))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1));
            return Select(records, part, "PowerFab nesting request");
        }

        private static PowerFabDxf? Select(IEnumerable<XElement> records, NestPart part, string source)
        {
            var candidates = new List<PowerFabDxf>();
            var nc1Errors = new List<InvalidDataException>();
            foreach (XElement record in records)
            {
                string format = F(record, "CNCFormat", "Format").TrimStart('.');
                bool nc1 = Equal(format, "DSTV") || Equal(format, "NC1") || Equal(format, "NC");
                if (format.Length > 0 && !Equal(format, "DXF") && !nc1) continue;
                var payloads = record.Elements().Where(e => N(e, "CNCData"));
                foreach (XElement file in record.Elements().Where(e => N(e, "CNCFile")))
                {
                    string filename = F(file, "FileName");
                    if (format.Length == 0 && filename.Length > 0 && !new[] { ".dxf", ".nc1", ".nc" }.Any(s => filename.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                    payloads = payloads.Concat(file.Elements().Where(e => N(e, "FileContents", "Contents", "CNCData")));
                }
                foreach (XElement payload in payloads)
                {
                    string? text = Decode(payload.Value);
                    string raw = DecodeText(payload.Value);
                    bool converted = false;
                    double? thickness = null;
                    if (text == null && (nc1 || (format.Length == 0 && Nc1Geometry.LooksLike(raw))))
                    {
                        try
                        {
                            Nc1Drawing plate = Nc1Geometry.Convert(raw, part.JobNumber + " / " + part.PieceMark);
                            text = plate.DxfText; thickness = plate.ThicknessMm; converted = true;
                        }
                        catch (InvalidDataException exception) { nc1Errors.Add(exception); continue; }
                    }
                    if (text == null) continue;
                    string sourceFormat = converted ? "NC1" : "DXF";
                    if (!candidates.Any(c => c.SourceFormat == sourceFormat && c.ThicknessMm == thickness && Canonical(c.Text) == Canonical(text)))
                        candidates.Add(new PowerFabDxf
                        {
                            JobNumber = part.JobNumber, PieceMark = part.PieceMark, CncFileId = F(record, "CNCFileID"), Text = text,
                            SourceFormat = sourceFormat, OriginalText = converted ? raw : text, ThicknessMm = thickness,
                            SourceName = source + " (" + sourceFormat + "): " + part.JobNumber + " / " + part.PieceMark
                                + (F(record, "CNCFileID").Length == 0 ? "" : " (CNC file " + F(record, "CNCFileID") + ")")
                        });
                }
            }
            if (candidates.Any(c => c.SourceFormat == "DXF")) candidates = candidates.Where(c => c.SourceFormat == "DXF").ToList();
            if (candidates.Count == 0 && nc1Errors.Count > 0) throw nc1Errors[0];
            if (candidates.Count > 1) throw new InvalidDataException("PowerFab supplied multiple different DXFs or NC1 definitions for " + part.JobNumber + " / " + part.PieceMark
                + ". Resolve the linked CNC file in PowerFab before generating the nest.");
            return candidates.FirstOrDefault();
        }

        internal static string? Decode(string data)
        {
            string text = DecodeText(data);
            return LooksLikeDxf(text) ? text : null;
        }
        private static string DecodeText(string data)
        {
            if (data.Length > MaximumTextLength) throw new InvalidDataException("A PowerFab CNC payload exceeds the 50 MB limit.");
            string text = data.TrimStart('\ufeff', '\r', '\n', ' ', '\t');
            if (LooksLikeDxf(text) || Nc1Geometry.LooksLike(text)) return text;
            try
            {
                byte[] bytes = Convert.FromBase64String(data.Trim());
                if (bytes.Length > MaximumTextLength) throw new InvalidDataException("A PowerFab DXF exceeds the 50 MB limit.");
                if (bytes.Length > 1 && bytes[0] == 255 && bytes[1] == 254) text = Encoding.Unicode.GetString(bytes);
                else if (bytes.Length > 1 && bytes[0] == 254 && bytes[1] == 255) text = Encoding.BigEndianUnicode.GetString(bytes);
                else { try { text = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); } }
                text = text.TrimStart('\ufeff', '\r', '\n', ' ', '\t');
                return text;
            }
            catch (FormatException) { return text; }
        }
        private static bool LooksLikeDxf(string text)
        {
            if (text.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal)) return false;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool section = false;
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                if (lines[i].Trim() == "0" && lines[i + 1].Trim() == "SECTION") section = true;
                if (section && lines[i].Trim() == "2" && lines[i + 1].Trim() == "ENTITIES") return true;
            }
            return false;
        }
        private static string Canonical(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        private static XDocument Parse(string text)
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        private static string Names(IEnumerable<NestPart> parts) => string.Join("; ", parts.Take(20).Select(p => p.JobNumber + " / " + p.PieceMark));
        private static string DrawingKey(string job, string mark) => job.Trim().ToUpperInvariant() + "\u001f" + mark.Trim().ToUpperInvariant();
        private static string F(XElement e, params string[] names) => NestingSnapshot.Field(e, names);
        private static bool N(XElement e, params string[] names) => NestingSnapshot.Named(e, names);
        private static bool Equal(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        private static bool True(string value) => value == "1" || Equal(value, "true");
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal static class NestNaming
    {
        public static string ForParts(IEnumerable<NestPart> parts)
        {
            var list = parts.ToList();
            string jobs = Join(list.Select(p => p.JobNumber));
            string phases = Join(list.Select(p => Missing(p.Phase) ? p.Sequence : p.Phase));
            return string.Join("_", new[] { jobs, phases, "PLT", "NestRequest" }.Where(s => !Missing(s)));
        }

        public static string ForRequest(NestingSnapshot snapshot) => ForParts(snapshot.PartNodes().Select(p => new NestPart
        { JobNumber = F(p, "JobNumber", "Job"), Phase = PhaseOf(p, snapshot.RequestRoot) }));

        public static string PhaseOf(XElement part, XElement? root = null)
        {
            foreach (XElement node in new[] { part, root }.OfType<XElement>())
                foreach (string field in new[] { "PhaseNumber", "PhaseName", "Phase", "SequenceName", "Sequence" })
                {
                    string value = F(node, field);
                    if (!Missing(value)) return Clean(value);
                }
            return string.Empty;
        }

        public static string Display(NestResultPackage result) => result.AbeImport != null ? ForAbe(result)
            : Missing(result.NestName) ? ForParts(result.Parts) : result.NestName;

        public static string ForAbe(NestResultPackage result)
        {
            // ProgramName is the imported NC filename without its extension.
            // Preserve every character, space and source suffix for PowerFab.
            string? program = result.AbeImport?.ProgramName;
            if (!string.IsNullOrWhiteSpace(program)) return program;

            // Compatibility fallback for old/in-memory results with no source name.
            string jobs = Join(result.Parts.Select(p => p.JobNumber));
            string Phase(string value)
            {
                string clean = Regex.Replace(Clean(value), @"^PHASE[\s_-]*", "",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Replace('_', ' ').Trim();
                return Missing(clean) ? "" : "Phase " + clean;
            }
            IEnumerable<string> sourcePhases = result.Parts.Select(p => Missing(p.Phase) ? p.Sequence : p.Phase);
            string phases = string.Join("+", sourcePhases.Where(p => !Missing(p)).Select(Phase)
                .Where(p => !Missing(p)).Distinct(StringComparer.OrdinalIgnoreCase));
            decimal mm = result.Material.Thickness * (result.Material.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m);
            return string.Join("-", new[] { jobs, phases,
                mm.ToString("0.############################", CultureInfo.InvariantCulture) + "MM" }.Where(s => !Missing(s)));
        }

        public static void AssignAbeNames(IEnumerable<NestResultPackage> results)
        {
            foreach (var sheet in results.Where(r => r.AbeImport != null))
            {
                string name = ForAbe(sheet);
                sheet.NestName = name;
                foreach (var remnant in sheet.Remnants) remnant.Name = name;
            }
        }
        public static string FileStem(string name)
        {
            string clean = Clean(name);
            if (clean.Length == 0) clean = "PLT_NestRequest";
            if (clean.Length <= 90) return clean;
            // Keep the requested suffix even if an unusually long job/phase list
            // needs shortening for the Windows filename.
            const string suffix = "_PLT_NestRequest";
            return clean.EndsWith(suffix, StringComparison.Ordinal)
                ? clean[..(90 - suffix.Length)].TrimEnd('_', '.', ' ') + suffix : clean[..90].TrimEnd('.', ' ');
        }

        public static string UniqueRequestPath(string folder, string name)
        {
            string stem = FileStem(name);
            for (int i = 1; i < int.MaxValue; i++)
            {
                string path = Path.Combine(folder, stem + (i == 1 ? "" : "_" + i) + ".xml");
                if (!File.Exists(path) && !File.Exists(path + ".tmp")) return path;
            }
            throw new IOException("Could not allocate a new nesting-request filename.");
        }

        private static string Join(IEnumerable<string> values) => string.Join("+", values.Where(s => !Missing(s))
            .Select(Clean).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        private static string Clean(string? value)
        {
            var text = new StringBuilder();
            foreach (char c in value ?? string.Empty) text.Append(char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '-' : c);
            return Regex.Replace(text.ToString(), @"\s+", " ").Trim(' ', '.', '_');
        }
        private static bool Missing(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string key = Regex.Replace(value.Trim().ToLowerInvariant(), @"[\s_()\-/]", "");
            return key is "" or "unsequenced" or "unassigned" or "none" or "na" or "null";
        }
        private static string F(XElement node, params string[] names) => NestingSnapshot.Field(node, names);
    }
}

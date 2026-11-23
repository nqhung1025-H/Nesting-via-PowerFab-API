using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal static class NestingSources
    {
        public static string? EmbeddedDxf(NestingSnapshot snapshot, NestPart part)
            => PowerFabDxfSource.Embedded(snapshot, part)?.Text;

        public static string? MatchFile(IEnumerable<string> files, NestPart part)
        {
            var exact = files.Where(f => Equal(Path.GetFileNameWithoutExtension(f), part.PieceMark)).ToList();
            var suffix = files.Where(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f),
                "(?:^|[_ -])" + Regex.Escape(part.PieceMark) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
            var job = suffix.Where(f => !string.IsNullOrWhiteSpace(part.JobNumber)
                && (new[] { "_", "-", " " }.Any(separator => Path.GetFileNameWithoutExtension(f).StartsWith(part.JobNumber + separator, StringComparison.OrdinalIgnoreCase))
                    || Path.GetDirectoryName(f)?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => Equal(segment, part.JobNumber)) == true)).ToList();
            if (job.Count == 1) return job[0];
            if (job.Count > 1) return null;
            if (exact.Count == 1) return exact[0];
            return exact.Count == 0 && suffix.Count == 1 ? suffix[0] : null;
        }

        public static void SubtractSavedQuantities(NestResultPackage template, IEnumerable<SavedNestReceipt> receipts)
        {
            var saved = receipts.Where(r => r.WorkOrderId == template.WorkOrderId).SelectMany(r => r.Parts)
                .GroupBy(LoadedNestResult.PartKey).ToDictionary(g => g.Key, g => g.Sum(p => (long)p.Quantity));
            template.Parts = template.Parts.GroupBy(LoadedNestResult.PartKey).Select(group =>
            {
                NestPart p = group.First();
                long remaining = group.Sum(x => (long)x.Quantity) - (saved.TryGetValue(group.Key, out long count) ? count : 0);
                return new NestPart { JobNumber = p.JobNumber, MainMark = p.MainMark, PieceMark = p.PieceMark,
                    Sequence = p.Sequence, Phase = p.Phase, LotNumber = p.LotNumber, Quantity = checked((int)Math.Max(0, remaining)),
                    Length = p.Length, Width = p.Width, Unit = p.Unit };
            }).Where(p => p.Quantity > 0).ToList();
            if (template.Parts.Count == 0) throw new InvalidOperationException("All matching quantities in this work order already have confirmed save receipts.");
        }

        private static bool Equal(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        private static string Field(XElement node, params string[] names)
            => names.Select(n => node.Elements().FirstOrDefault(e => Equal(e.Name.LocalName, n))?.Value.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
    }
}

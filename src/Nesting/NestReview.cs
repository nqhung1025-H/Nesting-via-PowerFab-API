using System;
using System.Collections.Generic;
using System.Linq;

namespace Hanlon.PowerFab.Nesting
{
    internal static class NestReview
    {
        public static bool IsSaved(LoadedNestResult sheet, IEnumerable<SavedNestReceipt> receipts)
            => receipts.Any(r => string.Equals(r.NestId, sheet.Result.NestId, StringComparison.Ordinal));

        // Saving changes eligibility, not the order or contents of the review batch.
        // Even removed/released receipts still prevent reusing a transmitted Nest ID.
        public static List<LoadedNestResult> Pending(IEnumerable<LoadedNestResult> sheets,
            IEnumerable<SavedNestReceipt> receipts)
        {
            var saved = receipts.Select(r => r.NestId).ToHashSet(StringComparer.Ordinal);
            return sheets.Where(s => !saved.Contains(s.Result.NestId)).ToList();
        }
    }
}

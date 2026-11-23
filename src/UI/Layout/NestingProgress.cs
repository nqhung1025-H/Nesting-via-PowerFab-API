using System;
using System.Globalization;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class NestingProgress
    {
        public string Shape { get; init; } = string.Empty;
        public string Grade { get; init; } = string.Empty;
        public string Size { get; init; } = string.Empty;
        public string Optimisation => "Top-left, downward";
        public string Pass => "1 of 1";
        public string MaxScrap => "Not set";
        public string Status { get; init; } = string.Empty;
        public string ImportSheetName { get; init; } = string.Empty;
        public bool IsAbeImport => ImportSheetName.Length > 0;
        public int Total { get; init; }
        public int Processed { get; init; }
        public int Placed { get; init; }
        public int Unplaced { get; init; }
        public int MaterialNumber { get; init; } = 1;
        public int MaterialCount { get; init; } = 1;
        public int BatchTotal { get; init; }
        public int BatchProcessed { get; init; }
        public int Percent => BatchTotal == 0 ? 0 : (int)Math.Clamp((long)BatchProcessed * 100 / BatchTotal, 0, 100);

        public static NestingProgress ForAbeSheet(string sheetName, int currentSheet, int totalSheets) => new NestingProgress
        {
            ImportSheetName = sheetName, MaterialNumber = currentSheet, MaterialCount = totalSheets
        };

        public static NestingProgress ForMaterial(NestMaterial material, int total, int processed, int placed, int unplaced, string status)
        {
            decimal mm = material.Unit.Equals("IN", StringComparison.OrdinalIgnoreCase) ? 25.4m : 1m;
            string Measure(decimal value) => (value * mm).ToString("0.###", CultureInfo.InvariantCulture);
            return new NestingProgress
            {
                Shape = material.Shape, Grade = material.Grade,
                Size = Measure(material.Length) + " x " + Measure(material.Width) + " x " + Measure(material.Thickness) + " mm",
                Total = total, Processed = processed, Placed = placed, Unplaced = unplaced,
                Status = status, BatchTotal = total, BatchProcessed = processed
            };
        }

        public NestingProgress ForBatch(int number, int count, int processedBefore, int batchTotal) => new NestingProgress
        {
            Shape = Shape, Grade = Grade, Size = Size, Status = Status, ImportSheetName = ImportSheetName,
            Total = Total, Processed = Processed, Placed = Placed, Unplaced = Unplaced,
            MaterialNumber = number, MaterialCount = count,
            BatchTotal = batchTotal, BatchProcessed = processedBefore + Processed
        };
    }
}

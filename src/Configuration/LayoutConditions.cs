using System;
using System.IO;
using System.Text.Json;

namespace Hanlon.PowerFab.Nesting
{
    public sealed class LayoutConditions
    {
        // Initial editable values come from the layout-condition reference supplied by the user.
        public decimal Spacing { get; set; } = 5.00m;
        public decimal Left { get; set; } = 10.00m;
        public decimal Right { get; set; } = 10.00m;
        public decimal Top { get; set; } = 10.00m;
        public decimal Bottom { get; set; } = 15.00m;
        public decimal MovePitch { get; set; } = 2.50m;
        public decimal RotatePitch { get; set; } = 90.00m;
        public decimal MinimumBottomRemnant { get; set; } = 100.00m;
        public string DistanceUnit { get; set; } = "MM";

        public LayoutConditions Copy() => new LayoutConditions
        {
            Spacing = Spacing, Left = Left, Right = Right, Top = Top, Bottom = Bottom,
            MovePitch = MovePitch, RotatePitch = RotatePitch, MinimumBottomRemnant = MinimumBottomRemnant, DistanceUnit = DistanceUnit
        };

        public void Validate()
        {
            if (Spacing < 0 || Left < 0 || Right < 0 || Top < 0 || Bottom < 0 || MovePitch <= 0
                || Spacing > 100000 || Left > 100000 || Right > 100000 || Top > 100000 || Bottom > 100000 || MovePitch > 100000
                || RotatePitch <= 0 || RotatePitch > 360 || MinimumBottomRemnant < 0 || MinimumBottomRemnant > 100000 || DistanceUnit != "MM")
                throw new InvalidDataException("Spacing, borders and the minimum bottom remnant must be non-negative; move pitch must be positive; rotate pitch must be greater than 0 and at most 360 degrees. Distances use mm.");
        }

        public static LayoutConditions Load(string folder)
        {
            string path = Path.Combine(folder, "LayoutConditions.json");
            if (!File.Exists(path)) return new LayoutConditions();
            LayoutConditions result = JsonSerializer.Deserialize<LayoutConditions>(File.ReadAllText(path), LoadedNestResult.JsonOptions)
                ?? throw new InvalidDataException("The saved layout conditions are empty.");
            result.Validate();
            return result;
        }

        public void Save(string folder)
        {
            Validate();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "LayoutConditions.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, LoadedNestResult.JsonOptions));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

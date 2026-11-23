using System.IO;
using System.Text.Json;

namespace Hanlon.PowerFab.Nesting
{
    // One reversible preference restores the complete inventory nesting UI,
    // including its original Generate action. Default to direct ABE import.
    internal sealed class NestingUiOptions
    {
        public bool UseInventoryNesting { get; set; }

        public static NestingUiOptions Load(string settingsFolder)
        {
            string path = Path.Combine(settingsFolder, "NestingUiOptions.json");
            if (!File.Exists(path)) return new NestingUiOptions();
            return JsonSerializer.Deserialize<NestingUiOptions>(File.ReadAllText(path), LoadedNestResult.JsonOptions)
                ?? throw new InvalidDataException("The nesting UI preferences are empty.");
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace BridgeCrew.Packs;

// Harmless fixtures. These files are examples for the launcher, not working game mods.
public sealed class PreviewCatalog
{
    public Dictionary<string, byte[]> Content { get; } = new(StringComparer.Ordinal);
    public PackManifest[] Packs { get; }
    public PreviewCatalog()
    {
        ModFile Add(string variant, string path, string contents)
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); Content[hash] = bytes;
            return new(variant, path, hash, bytes.Length);
        }
        var shared = Add("shared", "examples/appearance.json", "{\"sampleOnly\":true,\"panelColour\":\"amber\"}\n");
        var meta = Add("meta", "examples/edition.json", "{\"sampleOnly\":true,\"edition\":\"meta\"}\n");
        var steam = Add("steam", "examples/edition.json", "{\"sampleOnly\":true,\"edition\":\"steam\"}\n");
        PackMod[] Mods() => [
            new("bridge-appearance-example", "0.1.0", "Bridge appearance example", "Launcher preview",
                "Example pack data describing an amber bridge theme. This is a harmless launcher fixture; it does not change Bridge Crew.",
                false, [], [shared]),
            new("edition-adapter-example", "0.1.0", "PC edition adapter example", "Launcher preview",
                "Demonstrates separate Meta PC and Steam payloads in one shared pack. These JSON fixtures are not a game plugin or a qualified compatibility adapter.",
                false, [new("bridge-appearance-example", "0.1.0")], [meta, steam])
        ];
        Packs = [new(1, "academy-training", "0.1.0", "Academy Training", "A sample pack for reviewing the subscribe-and-join flow. No actual game mods are included.",
            PackRules.Protocol, "preview-only", new() { ["meta"] = ["preview-meta"], ["steam"] = ["preview-steam"] }, Mods())];
    }
    public LobbyAdvertisement[] Lobbies() => [
        new("academy-crew", "Sample captain", "Academy Training Crew", Packs[0].Id, Packs[0].Version,
            PackRules.Lock(Packs[0]), Packs[0].CompatibilityGroup, PackRules.Protocol, 1, DateTimeOffset.UtcNow.AddMinutes(2), true),
        new("evening-crew", "Sample friend", "Evening Bridge Session", Packs[0].Id, Packs[0].Version,
            PackRules.Lock(Packs[0]), Packs[0].CompatibilityGroup, PackRules.Protocol, 1, DateTimeOffset.UtcNow.AddMinutes(2), true)
    ];
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeCrew.Packs;

public static class PackJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record ModDependency(string Id, string Version);
public sealed record ModFile(string Variant, string Path, string Sha256, long Bytes);
public sealed record PackMod(string Id, string Version, string Name, string Author,
    string Description, bool ContainsCode, ModDependency[] Dependencies, ModFile[] Files);
public sealed record PackManifest(int SchemaVersion, string Id, string Version, string Name,
    string Description, int LoaderProtocol, string CompatibilityGroup,
    Dictionary<string, string[]> SupportedBuilds, PackMod[] Mods);
public sealed record PackPlan(PackManifest Manifest, string Edition, string Build,
    string PackLock, ModFile[] Files)
{
    public long DownloadBytes => Files.DistinctBy(f => f.Sha256).Sum(f => f.Bytes);
}
public sealed record LobbyAdvertisement(string Id, string HostName, string Name,
    string PackId, string PackVersion, string PackLock, string CompatibilityGroup,
    int LoaderProtocol, int Revision, DateTimeOffset ExpiresAt, bool IsPreview);
public sealed record JoinIntent(int SchemaVersion, string LobbyId, int LobbyRevision,
    string PackId, string PackVersion, string PackLock, string Edition, string Build,
    DateTimeOffset CreatedAt, string Status);

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BridgeCrew.Packs;

public static class PackRules
{
    public const int Protocol = 1;
    private static readonly Regex Id = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex Version = new("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant);
    public static bool IsHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsId(string? s) => s != null && Id.IsMatch(s) && IsSafeRelativePath(s);
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    public static void Validate(PackManifest pack)
    {
        Require(pack.SchemaVersion == 1, "Unsupported pack schema.");
        Require(IsId(pack.Id) && pack.Version is { Length: <= 80 } && Version.IsMatch(pack.Version), "Invalid pack identity/version.");
        Require(!string.IsNullOrWhiteSpace(pack.Name) && pack.Name.Length <= 120 &&
            !string.IsNullOrWhiteSpace(pack.Description) && pack.Description.Length <= 8000,
            "A pack needs a bounded name and description.");
        Require(pack.LoaderProtocol == Protocol, "Unsupported loader protocol.");
        Require(IsId(pack.CompatibilityGroup), "Missing compatibility group.");
        Require(pack.SupportedBuilds is { Count: > 0 and <= 2 }, "Pack needs supported builds.");
        foreach (var (edition, builds) in pack.SupportedBuilds)
            Require(edition is "meta" or "steam" && builds is { Length: > 0 and <= 16 } &&
                builds.All(b => !string.IsNullOrWhiteSpace(b) && b.Length <= 160), "Invalid edition/build list.");
        Require(pack.Mods is { Length: > 0 and <= 128 }, "Pack needs 1–128 mods.");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0; int count = 0;
        foreach (var mod in pack.Mods)
        {
            Require(mod != null && IsId(mod.Id) && mod.Version is { Length: <= 80 } && Version.IsMatch(mod.Version), "Invalid mod identity/version.");
            Require(!string.IsNullOrWhiteSpace(mod!.Name) && mod.Name.Length <= 120 &&
                !string.IsNullOrWhiteSpace(mod.Author) && mod.Author.Length <= 120 &&
                !string.IsNullOrWhiteSpace(mod.Description) && mod.Description.Length <= 8000,
                "Each mod needs a name, author and description.");
            Require(!seen.ContainsKey(mod.Id), "Duplicate mod ID.");
            Require(mod.Dependencies is { Length: <= 128 }, "Invalid dependency list.");
            foreach (var dep in mod.Dependencies!)
                Require(dep != null && seen.TryGetValue(dep.Id, out var v) && v == dep.Version,
                    "Dependencies must be pinned and appear before their dependent mod.");
            Require(mod.Files is { Length: > 0 and <= 512 }, "Invalid mod file list.");
            foreach (var file in mod.Files!)
            {
                Require(file != null && file.Variant is "shared" or "meta" or "steam", "Invalid file variant.");
                Require(IsSafeRelativePath(file!.Path), "Unsafe mod path.");
                Require(IsHash(file.Sha256) && file.Bytes is >= 0 and <= 2L * 1024 * 1024 * 1024,
                    "Invalid file hash/size.");
                Require(!sizes.TryGetValue(file.Sha256, out var known) || known == file.Bytes,
                    "The same content hash cannot have conflicting sizes.");
                sizes[file.Sha256] = file.Bytes;
                total = checked(total + file.Bytes); count++;
            }
            seen.Add(mod.Id, mod.Version);
        }
        Require(count <= 4096 && total <= 20L * 1024 * 1024 * 1024, "Pack is too large.");
        foreach (var edition in pack.SupportedBuilds.Keys)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in pack.Mods)
            {
                var files = mod.Files.Where(f => f.Variant == "shared" || f.Variant == edition).ToArray();
                Require(files.Length > 0, "Every mod must provide files for each supported edition.");
                foreach (var file in files) Require(paths.Add(file.Path), "Conflicting mod paths for " + edition + ".");
            }
        }
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') ||
            path.Any(c => c < 32 || ":*?\"<>|".Contains(c)) || path.StartsWith('/')) return false;
        return path.Split('/').All(p => p.Length > 0 && p is not "." and not ".." &&
            !p.EndsWith('.') && !p.EndsWith(' ') &&
            !Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase));
    }

    // Entire manifest is pinned, including descriptions and every edition's payload.
    // Object key order is insignificant; arrays preserve dependency/load order.
    public static string Lock(PackManifest pack)
    {
        Validate(pack);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, JsonSerializer.SerializeToElement(pack, PackJson.Options));
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(p.Name); WriteCanonical(writer, p.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var e in element.EnumerateArray()) WriteCanonical(writer, e); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
    public static PackPlan Plan(PackManifest pack, string edition, string build)
    {
        var copy = JsonSerializer.Deserialize<PackManifest>(JsonSerializer.Serialize(pack, PackJson.Options), PackJson.Options)!;
        var hash = Lock(copy);
        Require(copy.SupportedBuilds.TryGetValue(edition, out var builds) && builds.Contains(build, StringComparer.Ordinal),
            "This pack has not declared support for the selected edition/build.");
        return new(copy, edition, build, hash, copy.Mods.SelectMany(m => m.Files)
            .Where(f => f.Variant == "shared" || f.Variant == edition).ToArray());
    }
    public static void ValidateLobby(LobbyAdvertisement lobby, PackPlan plan, DateTimeOffset now)
    {
        Require(lobby.ExpiresAt > now, "This lobby advertisement has expired.");
        Require(lobby.LoaderProtocol == Protocol && lobby.CompatibilityGroup == plan.Manifest.CompatibilityGroup,
            "Lobby protocol/compatibility group does not match.");
        Require(lobby.PackId == plan.Manifest.Id && lobby.PackVersion == plan.Manifest.Version &&
            lobby.PackLock == plan.PackLock, "Lobby pack changed; review the current pack before joining.");
    }
}

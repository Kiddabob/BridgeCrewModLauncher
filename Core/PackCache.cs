using System.Security.Cryptography;
using System.Text.Json;

namespace BridgeCrew.Packs;

public sealed class PackCache(string root)
{
    private readonly string root = Path.GetFullPath(root);
    public string ContentPath(string hash)
    { if (!PackRules.IsHash(hash)) throw new InvalidDataException("Invalid hash."); return Path.Combine(root, "content", hash); }
    public async Task<bool> IsCached(PackPlan plan, CancellationToken ct = default)
    {
        foreach (var file in plan.Files.DistinctBy(f => f.Sha256))
            if (!await Verify(ContentPath(file.Sha256), file, ct)) return false;
        return true;
    }
    public async Task<bool> IsSubscribed(PackPlan plan,CancellationToken ct=default)
    {
        var current=PackRules.Plan(plan.Manifest,plan.Edition,plan.Build);
        if(current.PackLock!=plan.PackLock||!current.Files.SequenceEqual(plan.Files))return false;
        var path=Path.Combine(root,"subscriptions",current.Manifest.Id,current.Manifest.Version,current.PackLock,current.Edition+".json");
        if(!File.Exists(path))return false;
        try
        {
            var saved=JsonSerializer.Deserialize<PackPlan>(await File.ReadAllTextAsync(path,ct),PackJson.Options);
            return saved!=null&&saved.Manifest!=null&&saved.Files!=null&&saved.PackLock==current.PackLock&&saved.Edition==current.Edition&&saved.Build==current.Build&&
                PackRules.Lock(saved.Manifest)==current.PackLock&&saved.Files.SequenceEqual(current.Files)&&await IsCached(current,ct);
        }
        catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){return false;}
    }
    private static async Task<bool> Verify(string path, ModFile file, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != file.Bytes) return false;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == file.Sha256;
    }
    public async Task Subscribe(PackPlan plan, string approvedLock,
        Func<string, CancellationToken, Task<Stream>> openFile, CancellationToken ct = default)
    {
        // Approval is bound to the complete reviewed manifest, never an unpinned "latest".
        var current = PackRules.Plan(plan.Manifest, plan.Edition, plan.Build);
        if (approvedLock != current.PackLock || plan.PackLock != current.PackLock ||
            !plan.Files.SequenceEqual(current.Files)) throw new InvalidDataException("Pack changed after review.");
        Directory.CreateDirectory(Path.Combine(root, "content"));
        foreach (var file in current.Files.DistinctBy(f => f.Sha256))
        {
            var target = ContentPath(file.Sha256);
            if (await Verify(target, file, ct)) continue;
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var source = await openFile(file.Sha256, ct))
                await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536]; long total = 0; int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > file.Bytes) throw new InvalidDataException("Download exceeds advertised size.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
                if (!await Verify(temp, file, ct)) throw new InvalidDataException("Downloaded content failed its size/hash check.");
                File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        var folder = Path.Combine(root, "subscriptions", current.Manifest.Id, current.Manifest.Version, current.PackLock);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, current.Edition + ".json"),
            JsonSerializer.Serialize(current, PackJson.Options), ct);
    }
    public async Task<string> PreparePreviewJoin(PackPlan plan, LobbyAdvertisement reviewed,
        Func<string, CancellationToken, Task<LobbyAdvertisement>> refreshLobby, CancellationToken ct = default)
    {
        var currentPlan = PackRules.Plan(plan.Manifest, plan.Edition, plan.Build);
        if (currentPlan.PackLock != plan.PackLock || !currentPlan.Files.SequenceEqual(plan.Files))
            throw new InvalidDataException("Pack changed after review.");
        var current = await refreshLobby(reviewed.Id, ct);
        if (current.Id != reviewed.Id || current.Revision != reviewed.Revision)
            throw new InvalidDataException("Lobby changed; refresh and review it again.");
        PackRules.ValidateLobby(current, currentPlan, DateTimeOffset.UtcNow);
        if (!current.IsPreview) throw new InvalidOperationException("Live game joining is not connected in this prototype.");
        if (!await IsCached(currentPlan, ct)) throw new InvalidDataException("Pack files are missing or changed; subscribe again.");
        var intent = new JoinIntent(1, current.Id, current.Revision, current.PackId, current.PackVersion,
            current.PackLock, currentPlan.Edition, currentPlan.Build, DateTimeOffset.UtcNow, "preview-only-game-not-connected");
        var folder = Path.Combine(root, "preview-join-requests"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(intent, PackJson.Options), ct);
        return path;
    }
}

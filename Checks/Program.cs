using System.Security.Cryptography;
using System.Text;
using BridgeCrew.Packs;

var root = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : ".check-state"), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fixture = new PreviewCatalog(); var pack = fixture.Packs[0];
var meta = PackRules.Plan(pack, "meta", "preview-meta"); var steam = PackRules.Plan(pack, "steam", "preview-steam");
var passed = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed.Add(name); Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OperationCanceledException or InvalidOperationException)
    { passed.Add(name); Console.WriteLine("PASS " + name); return; }
    throw new Exception("FAIL (accepted invalid operation): " + name);
}
Task<Stream> Open(string hash, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(fixture.Content[hash], false));
Check(meta.PackLock == steam.PackLock, "Meta and Steam share one complete pack lock");
Check(meta.Files.Single(f => f.Variant == "meta").Sha256 != steam.Files.Single(f => f.Variant == "steam").Sha256,
    "Each edition resolves its own payload");
Check(meta.Files.All(f => f.Variant != "steam") && steam.Files.All(f => f.Variant != "meta"), "No cross-edition payload substitution");
var reordered = pack with { SupportedBuilds = pack.SupportedBuilds.Reverse().ToDictionary(p => p.Key,p => p.Value) };
Check(PackRules.Lock(reordered) == meta.PackLock, "Canonical lock ignores object-key ordering");
Check(PackRules.Lock(pack with { Description = "Changed consent description" }) != meta.PackLock, "Review text changes invalidate approval lock");
await Reject(() => { PackRules.Plan(pack, "steam", "unqualified-build"); return Task.CompletedTask; }, "Undeclared game build rejected");
await Reject(() => { PackRules.Plan(pack with { LoaderProtocol = 9 }, "meta", "preview-meta"); return Task.CompletedTask; }, "Unknown loader protocol rejected");
await Reject(() => { PackRules.Lock(pack with { Mods = pack.Mods.Reverse().ToArray() }); return Task.CompletedTask; }, "Missing or out-of-order dependency rejected");
foreach (var path in new[] { "../outside.dll", "/absolute.dll", "C:/outside.dll", "safe/../../outside.dll", "x\\y.dll", "file:stream", "safe/CON.dll", "trailing. ", "x//y", "aux" })
    Check(!PackRules.IsSafeRelativePath(path), "Unsafe path rejected: " + path);
var conflictingMod = pack.Mods[1] with { Files = [pack.Mods[0].Files[0]] };
await Reject(() => { PackRules.Lock(pack with { Mods = [pack.Mods[0], conflictingMod] }); return Task.CompletedTask; }, "Overlapping mod file paths rejected");
var cache = new PackCache(Path.Combine(root,"good"));
await Reject(() => cache.Subscribe(meta, new string('0',64), Open), "Subscription requires exact review approval");
await cache.Subscribe(meta, meta.PackLock, Open);
Check(await cache.IsCached(meta), "Downloaded content fully verifies");
await cache.Subscribe(steam, steam.PackLock, Open);
Check(await cache.IsCached(meta) && await cache.IsCached(steam), "Meta and Steam subscriptions coexist");
int fetched = 0;
await cache.Subscribe(meta, meta.PackLock, (h,ct) => {fetched++;return Open(h,ct);});
Check(fetched == 0, "Verified cache avoids duplicate downloads");
var altered = meta with { Manifest = meta.Manifest with { Description = "Changed after review" } };
await Reject(() => cache.Subscribe(altered, meta.PackLock, Open), "Manifest mutation after approval rejected");
var fakeFile = meta.Files[0] with { Path = "other/path.json" };
await Reject(() => cache.Subscribe(meta with { Files = [fakeFile] }, meta.PackLock, Open), "Forged resolved file list rejected");
var broken = new PackCache(Path.Combine(root,"corrupt"));
await Reject(() => broken.Subscribe(meta, meta.PackLock, (h,ct) => Task.FromResult<Stream>(new MemoryStream(new byte[fixture.Content[h].Length]))), "Wrong content hash rejected");
Check(!Directory.GetFiles(Path.Combine(root,"corrupt"),"*.part",SearchOption.AllDirectories).Any(), "Failed downloads leave no partial files");
var oversized = new PackCache(Path.Combine(root,"oversized"));
await Reject(() => oversized.Subscribe(meta, meta.PackLock, (h,ct) => Task.FromResult<Stream>(new MemoryStream(new byte[fixture.Content[h].Length+1]))), "Oversized download rejected while streaming");
var truncated = new PackCache(Path.Combine(root,"truncated"));
await Reject(() => truncated.Subscribe(meta, meta.PackLock, (h,ct) => Task.FromResult<Stream>(new MemoryStream(new byte[1]))), "Truncated download rejected");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();var cancelledCache = new PackCache(Path.Combine(root,"cancelled"));
    await Reject(() => cancelledCache.Subscribe(meta,meta.PackLock,Open,cancelled.Token), "Cancelled subscription does not complete");
}
var lobby = fixture.Lobbies()[0];
Task<LobbyAdvertisement> Current(string id, CancellationToken ct) => Task.FromResult(lobby);
var intent = await cache.PreparePreviewJoin(meta,lobby,Current);
Check(File.ReadAllText(intent).Contains("preview-only-game-not-connected",StringComparison.Ordinal), "Join intent explicitly records unconnected preview state");
await Reject(() => cache.PreparePreviewJoin(meta,lobby,(id,ct) => Task.FromResult(lobby with { Revision=2 })), "Lobby revision race rejected");
await Reject(() => cache.PreparePreviewJoin(meta,lobby,(id,ct) => Task.FromResult(lobby with { PackLock=new string('0',64) })), "Lobby pack mismatch rejected");
await Reject(() => cache.PreparePreviewJoin(meta,lobby,(id,ct) => Task.FromResult(lobby with { ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) })), "Stale lobby rejected");
await Reject(() => cache.PreparePreviewJoin(meta,lobby,(id,ct) => Task.FromResult(lobby with { IsPreview=false })), "Unimplemented live game join cannot report success");
await File.WriteAllTextAsync(cache.ContentPath(meta.Files[0].Sha256), "tampered");
Check(!await cache.IsCached(meta), "Post-download cache tampering detected");
await Reject(() => cache.PreparePreviewJoin(meta,lobby,Current), "Tampered cache blocks joining");
await cache.Subscribe(meta,meta.PackLock,Open);
Check(await cache.IsCached(meta), "Resubscription repairs corrupt cached content");
await Reject(() => { using var client = new DirectoryClient(new Uri("http://example.com/"));return Task.CompletedTask; }, "Nonlocal unencrypted content service rejected");
if (args.Contains("--http"))
{
    using var client = new DirectoryClient(new Uri("http://127.0.0.1:38471/"));
    var lobbies = await client.Lobbies(); Check(lobbies.Length==2 && lobbies.All(l=>l.IsPreview), "HTTP directory returns explicitly sample lobbies");
    var advertised = lobbies[0];var remotePack=await client.Pack(advertised.PackId,advertised.PackVersion);
    var httpPlan = PackRules.Plan(remotePack,"steam","preview-steam"); PackRules.ValidateLobby(advertised,httpPlan,DateTimeOffset.UtcNow);
    var httpCache = new PackCache(Path.Combine(root,"http")); await httpCache.Subscribe(httpPlan,httpPlan.PackLock,client.OpenFile);
    Check(await httpCache.IsCached(httpPlan), "Real loopback HTTP downloads pass size/hash verification");
    var httpIntent = await httpCache.PreparePreviewJoin(httpPlan,advertised,client.Lobby);
    Check(File.Exists(httpIntent), "HTTP refresh and join preparation complete without game access");
}
Console.WriteLine($"{passed.Count} checks passed. Evidence: {root}");
await File.WriteAllTextAsync(Path.Combine(root,"result.json"), System.Text.Json.JsonSerializer.Serialize(new { passed=passed.Count, checks=passed },PackJson.Options));

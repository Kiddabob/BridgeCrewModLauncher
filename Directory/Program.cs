using BridgeCrew.Packs;

var builder = WebApplication.CreateBuilder(args);
// Deliberately loopback-only: this development fixture has no production authentication.
builder.WebHost.UseUrls("http://127.0.0.1:38471");
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = PackJson.Options.PropertyNamingPolicy; });
var app = builder.Build(); var catalog = new PreviewCatalog();
app.MapGet("/health", () => Results.Ok(new { mode = "local-preview", protocol = 1, gameConnected = false }));
app.MapGet("/lobbies", () => catalog.Lobbies());
app.MapGet("/lobbies/{id}", (string id) => catalog.Lobbies().FirstOrDefault(l => l.Id == id) is { } lobby
    ? Results.Ok(lobby) : Results.NotFound());
app.MapGet("/packs/{id}/{version}", (string id, string version) => catalog.Packs.FirstOrDefault(p => p.Id == id && p.Version == version) is { } pack
    ? Results.Ok(pack) : Results.NotFound());
app.MapGet("/files/{hash}", (string hash) => PackRules.IsHash(hash) && catalog.Content.TryGetValue(hash, out var bytes)
    ? Results.Bytes(bytes, "application/octet-stream") : Results.NotFound());
app.Run();

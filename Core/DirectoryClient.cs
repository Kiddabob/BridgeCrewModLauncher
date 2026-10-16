using System.Net.Http.Json;
using System.Text.Json;

namespace BridgeCrew.Packs;

public sealed class DirectoryClient : IDisposable
{
    private readonly HttpClient http;
    public DirectoryClient(Uri baseUri)
    {
        if (baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback))
            throw new ArgumentException("Use HTTPS, or loopback HTTP for local development.");
        if (!string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment)) throw new ArgumentException("Invalid directory URL.");
        http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(10) };
    }
    public Task<LobbyAdvertisement[]> Lobbies(CancellationToken ct = default) => Read<LobbyAdvertisement[]>("lobbies", ct);
    public Task<LobbyAdvertisement> Lobby(string id, CancellationToken ct = default)
    { if (!PackRules.IsId(id)) throw new InvalidDataException("Invalid lobby ID."); return Read<LobbyAdvertisement>("lobbies/" + id, ct); }
    public Task<PackManifest> Pack(string id, string version, CancellationToken ct = default)
    {
        if (!PackRules.IsId(id) || version.Length > 80) throw new InvalidDataException("Invalid pack request.");
        return Read<PackManifest>("packs/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(version), ct);
    }
    private async Task<T> Read<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Metadata is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        { if (buffer.Length + read > 1024 * 1024) throw new InvalidDataException("Metadata is too large."); buffer.Write(chunk, 0, read); }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), PackJson.Options) ?? throw new InvalidDataException("Empty metadata.");
    }
    public async Task<Stream> OpenFile(string hash, CancellationToken ct)
    {
        if (!PackRules.IsHash(hash)) throw new InvalidDataException("Invalid content hash.");
        var response = await http.GetAsync("files/" + hash, HttpCompletionOption.ResponseHeadersRead, ct);
        try { response.EnsureSuccessStatusCode(); return new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response); }
        catch { response.Dispose(); throw; }
    }
    public void Dispose() => http.Dispose();
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b,int o,int c)=>inner.Read(b,o,c);
        public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default)=>inner.ReadAsync(b,ct);
        public override void Flush()=>throw new NotSupportedException(); public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();
        public override void SetLength(long v)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if(disposing){inner.Dispose();response.Dispose();}base.Dispose(disposing); }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BridgeCrew.Packs;

public sealed class CrewServiceException(HttpStatusCode status,string message) : HttpRequestException(message,null,status);

public sealed class CrewServiceClient : IDisposable
{
    private readonly HttpClient http;
    private string? token;
    public Uri BaseUri { get; }
    public CrewServiceClient(Uri baseUri,string? accessToken=null)
    {
        if(!baseUri.IsAbsoluteUri || (baseUri.Scheme!="https" && !(baseUri.Scheme=="http"&&baseUri.IsLoopback)) ||
            baseUri.UserInfo!="" || baseUri.Query!="" || baseUri.Fragment!="")throw new ArgumentException("Use an HTTPS service URL, or loopback HTTP for development.");
        BaseUri=new Uri(baseUri.AbsoluteUri.TrimEnd('/')+"/"); token=accessToken;
        http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=BaseUri,Timeout=TimeSpan.FromMinutes(10)};
    }
    public void SetToken(string? accessToken)=>token=accessToken;
    public Task<ServiceHealth> Health(CancellationToken ct=default)=>Send<ServiceHealth>(HttpMethod.Get,"health",null,ct);
    public Task<AccountSession> Register(string username,string displayName,string password,CancellationToken ct=default)=>Send<AccountSession>(HttpMethod.Post,"auth/register",new{username,displayName,password},ct);
    public Task<AccountSession> Login(string username,string password,CancellationToken ct=default)=>Send<AccountSession>(HttpMethod.Post,"auth/login",new{username,password},ct);
    public Task<AccountSession> Recover(string username,string recoveryCode,string password,CancellationToken ct=default)=>Send<AccountSession>(HttpMethod.Post,"auth/recover",new{username,recoveryCode,password},ct);
    public Task<CrewAccount> Me(CancellationToken ct=default)=>Send<CrewAccount>(HttpMethod.Get,"auth/me",null,ct);
    public async Task Logout(CancellationToken ct=default){await Send<JsonElement>(HttpMethod.Post,"auth/logout",null,ct);token=null;}
    public Task<PackRelease[]> Releases(string query="",CancellationToken ct=default)=>Send<PackRelease[]>(HttpMethod.Get,"packs?q="+Uri.EscapeDataString(query),null,ct);
    public Task<PackManifest> Pack(string id,string version,CancellationToken ct=default)=>Send<PackManifest>(HttpMethod.Get,$"packs/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}",null,ct);
    public Task<PackRelease> Release(string id,string version,CancellationToken ct=default)=>Send<PackRelease>(HttpMethod.Get,$"releases/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}",null,ct);
    public async Task<PublishResult> Publish(PackManifest pack,CancellationToken ct=default)
    {
        var expected=PackRules.Lock(pack);var result=await Send<PublishResult>(HttpMethod.Post,"packs",pack,ct);
        if(result.PackLock!=expected)throw new InvalidDataException("Service and launcher disagree on the published manifest lock.");return result;
    }
    public async Task<UploadResult> Upload(string hash,Stream source,long bytes,CancellationToken ct=default)
    {
        if(!PackRules.IsHash(hash)||bytes<0)throw new InvalidDataException("Invalid content identity.");
        using var request=new HttpRequestMessage(HttpMethod.Put,"uploads/"+hash);Authorize(request);
        request.Content=new StreamContent(source);request.Content.Headers.ContentLength=bytes;request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        return await Read<UploadResult>(response,ct);
    }
    public Task<CrewFriend[]> Friends(CancellationToken ct=default)=>Send<CrewFriend[]>(HttpMethod.Get,"friends",null,ct);
    public Task<JsonElement> RequestFriend(string username,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Post,"friends",new{username},ct);
    public Task<JsonElement> AcceptFriend(string id,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Post,"friends/"+Uri.EscapeDataString(id),null,ct);
    public Task<JsonElement> RemoveFriend(string id,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Delete,"friends/"+Uri.EscapeDataString(id),null,ct);
    public Task<JsonElement> Block(string id,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Post,"blocks/"+Uri.EscapeDataString(id),null,ct);
    public Task<JsonElement> Unblock(string id,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Delete,"blocks/"+Uri.EscapeDataString(id),null,ct);
    public Task<LobbyAdvertisement[]> Lobbies(string query="",CancellationToken ct=default)=>Send<LobbyAdvertisement[]>(HttpMethod.Get,"lobbies?q="+Uri.EscapeDataString(query),null,ct);
    public Task<LobbyAdvertisement> Lobby(string id,CancellationToken ct=default)=>Send<LobbyAdvertisement>(HttpMethod.Get,"lobbies/"+Uri.EscapeDataString(id),null,ct);
    public Task<HostedRoom> Host(HostRoomRequest request,CancellationToken ct=default)=>Send<HostedRoom>(HttpMethod.Post,"lobbies",request,ct);
    public Task<LobbyAdvertisement> ResolveInvite(string code,CancellationToken ct=default)=>Send<LobbyAdvertisement>(HttpMethod.Post,"invites/resolve",new{code},ct);
    public Task<CrewRoom> Join(string id,RoomJoinRequest request,CancellationToken ct=default)=>Send<CrewRoom>(HttpMethod.Post,$"lobbies/{Uri.EscapeDataString(id)}/join",request,ct);
    public Task<CrewRoom> Room(string id,CancellationToken ct=default)=>Send<CrewRoom>(HttpMethod.Get,$"lobbies/{Uri.EscapeDataString(id)}/heartbeat",null,ct);
    public Task<CrewRoom> Heartbeat(string id,CancellationToken ct=default)=>Send<CrewRoom>(HttpMethod.Post,$"lobbies/{Uri.EscapeDataString(id)}/heartbeat",null,ct);
    public Task<CrewRoom> Ready(string id,int revision,string packLock,bool ready,CancellationToken ct=default)=>Send<CrewRoom>(HttpMethod.Post,$"lobbies/{Uri.EscapeDataString(id)}/ready",new{revision,packLock,ready},ct);
    public Task<JsonElement> Leave(string id,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Post,$"lobbies/{Uri.EscapeDataString(id)}/leave",null,ct);
    public Task<InviteResult> RotateInvite(string id,CancellationToken ct=default)=>Send<InviteResult>(HttpMethod.Post,$"lobbies/{Uri.EscapeDataString(id)}/invite",null,ct);
    public Task<CrewPresence> Presence(CancellationToken ct=default)=>Send<CrewPresence>(HttpMethod.Get,"presence",null,ct);
    public Task<CrewRoom> Kick(string id,string userId,CancellationToken ct=default)=>Send<CrewRoom>(HttpMethod.Post,$"lobbies/{Uri.EscapeDataString(id)}/kick",new{userId},ct);
    public Task<JsonElement> Report(string packId,string packVersion,string reason,CancellationToken ct=default)=>Send<JsonElement>(HttpMethod.Post,"reports",new{packId,packVersion,reason},ct);
    public async Task<Stream> OpenFile(string hash,CancellationToken ct=default)
    {
        if(!PackRules.IsHash(hash))throw new InvalidDataException("Invalid content hash.");
        using var request=new HttpRequestMessage(HttpMethod.Get,"files/"+hash);Authorize(request);
        var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        try{if(!response.IsSuccessStatusCode)await Read<JsonElement>(response,ct);return new OwnedResponseStream(await response.Content.ReadAsStreamAsync(ct),response);}catch{response.Dispose();throw;}
    }
    private void Authorize(HttpRequestMessage request){if(token!=null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);}
    private async Task<T> Send<T>(HttpMethod method,string path,object? body,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(method,path);Authorize(request);
        if(body!=null)request.Content=JsonContent.Create(body,options:PackJson.Options);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        return await Read<T>(response,ct);
    }
    private static async Task<T> Read<T>(HttpResponseMessage response,CancellationToken ct)
    {
        const int max=1024*1024;if(response.Content.Headers.ContentLength>max)throw new InvalidDataException("Service metadata is too large.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var data=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=await stream.ReadAsync(buffer,ct))>0){if(data.Length+read>max)throw new InvalidDataException("Service metadata is too large.");data.Write(buffer,0,read);}
        if(!response.IsSuccessStatusCode){string? message=null;try{message=JsonSerializer.Deserialize<Dictionary<string,string>>(data.ToArray())?.GetValueOrDefault("error");}catch(JsonException){}throw new CrewServiceException(response.StatusCode,message??$"Service returned HTTP {(int)response.StatusCode}.");}
        return JsonSerializer.Deserialize<T>(data.ToArray(),PackJson.Options)??throw new InvalidDataException("Empty service response.");
    }
    public void Dispose()=>http.Dispose();
    private sealed class OwnedResponseStream(Stream inner,HttpResponseMessage response):Stream
    {
        public override bool CanRead=>inner.CanRead;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int c)=>inner.Read(b,o,c);public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default)=>inner.ReadAsync(b,ct);
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing){inner.Dispose();response.Dispose();}base.Dispose(disposing);}
    }
}

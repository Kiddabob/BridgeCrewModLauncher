using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BridgeCrew.Packs;
using Microsoft.Data.Sqlite;

if(args is ["--remote",var endpoint,var ownerFile,var evidence]){Environment.ExitCode=await HostedSmoke.Run(new Uri(endpoint),ownerFile,evidence);return;}
var root=Path.GetFullPath(args[0]);var php=Path.GetFullPath(args[1]);
var state=Path.Combine(root,"artifacts","backend-checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(state);
var admin=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
var adminHash=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(admin)));
var config=Path.Combine(state,"config.php");File.WriteAllText(config,$"<?php return ['admin_hash'=>'{adminHash}','max_blob'=>1048576];");
using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
var uri=new Uri($"http://127.0.0.1:{port}/");Process? server=null;var checks=new List<string>();
void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);Console.WriteLine("PASS "+label);}
async Task Reject(Func<Task> run,HttpStatusCode expected,string label){try{await run();throw new InvalidOperationException(label+" was accepted");}catch(HttpRequestException e)when(e.StatusCode==expected){Check(true,label);}}
async Task Start()
{
    var start=new ProcessStartInfo(php){WorkingDirectory=Path.GetDirectoryName(php)!,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
    foreach(var s in new[]{"-c",Path.Combine(Path.GetDirectoryName(php)!,"php.ini"),"-d","extension_dir="+Path.Combine(Path.GetDirectoryName(php)!,"ext"),"-S",$"127.0.0.1:{port}",Path.Combine(root,"Backend","router.php")})start.ArgumentList.Add(s);
    start.Environment["BRIDGECREW_PRIVATE_DIR"]=Path.Combine(root,"Backend","private");start.Environment["BRIDGECREW_DATA_DIR"]=Path.Combine(state,"data");start.Environment["BRIDGECREW_CONFIG_FILE"]=config;
    server=Process.Start(start)!;server.OutputDataReceived+=(_,e)=>{};server.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)File.AppendAllText(Path.Combine(state,"php.log"),e.Data+Environment.NewLine);};server.BeginOutputReadLine();server.BeginErrorReadLine();
    for(var i=0;i<60;i++){try{using var client=new CrewServiceClient(uri);await client.Health();return;}catch(HttpRequestException){await Task.Delay(100);}}
    throw new InvalidOperationException("PHP backend did not start.");
}
void Stop(){if(server!=null&&!server.HasExited){server.Kill();server.WaitForExit();}server?.Dispose();server=null;}
async Task Grant(string name,bool enabled=true)
{
    using var http=new HttpClient{BaseAddress=uri};http.DefaultRequestHeaders.Add("X-BridgeCrew-Admin",admin);
    using var r=await http.PostAsJsonAsync("admin/publisher",new{username=name,enabled});r.EnsureSuccessStatusCode();
}
void Execute(string sql)
{
    using var connection=new SqliteConnection("Data Source="+Path.Combine(state,"data","service.sqlite"));connection.Open();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
}
try
{
    await Start();using var host=new CrewServiceClient(uri);using var guest=new CrewServiceClient(uri);using var other=new CrewServiceClient(uri);
    Check(!(await host.Health()).GameConnected,"Live service does not claim game connectivity");
    var captain=await host.Register("captain","Captain <&+é🚀>","Test captain password 734!");host.SetToken(captain.AccessToken);
    var crew=await guest.Register("crew","Crew member","Test crew password 946!");guest.SetToken(crew.AccessToken);
    var outsider=await other.Register("outsider","Other player","Test other password 836!");other.SetToken(outsider.AccessToken);
    Check(captain.RecoveryCode?.Length==43 && (await host.Me()).Id==captain.User.Id,"Accounts persist with opaque authenticated sessions and recovery code");
    await Reject(async()=>await host.Register("CAPTAIN","Duplicate","Other long password 826!"),HttpStatusCode.Conflict,"Case-insensitive duplicate usernames rejected");
    await Reject(async()=>await new CrewServiceClient(uri).Me(),HttpStatusCode.Unauthorized,"Anonymous account access rejected");
    using(var fake=new CrewServiceClient(uri,new string('a',43)))await Reject(async()=>await fake.Me(),HttpStatusCode.Unauthorized,"Forged bearer token rejected");
    var fixtures=new PreviewCatalog();var pack=fixtures.Packs[0] with{Id="backend-training",Name="Backend training",Description="Service checks only; no game mod."};
    var one=fixtures.Content.First();
    await Reject(async()=>await host.Upload(one.Key,new MemoryStream(one.Value),one.Value.Length),HttpStatusCode.Forbidden,"New accounts cannot self-authorize publishing");
    await Grant("captain");Check((await host.Me()).CanPublish,"Owner explicitly grants publisher permission");
    await Reject(async()=>await host.Publish(pack),HttpStatusCode.BadRequest,"Publication requires ownership of every verified payload");
    foreach(var (hash,bytes) in fixtures.Content)Check((await host.Upload(hash,new MemoryStream(bytes),bytes.Length)).Verified,"Content upload verifies SHA-256 and byte length");
    await Reject(async()=>await host.Upload(new string('a',64),new MemoryStream(one.Value),one.Value.Length),HttpStatusCode.BadRequest,"Hash-mismatched uploads rejected");
    await Reject(async()=>await host.Upload(one.Key,new MemoryStream(new byte[1048577]),1048577),HttpStatusCode.RequestEntityTooLarge,"Oversized uploads rejected before storage");
    var published=await host.Publish(pack);Check(published.PackLock==PackRules.Lock(pack),"PHP and .NET share exactly the same canonical pack lock");
    var unicode=pack with{Id="unicode-training",Name="<Captain & crew> + é 🚀 `",Description="Tabs\tand\nnewlines, Ω, 漢字, /, \\, literal \\uabcd, quotes \" and apostrophe '"};
    Check((await host.Publish(unicode)).PackLock==PackRules.Lock(unicode),"Unicode and HTML-sensitive descriptions preserve canonical lock interoperability");
    await Reject(async()=>await host.Publish(pack with{Name="Changed"}),HttpStatusCode.Conflict,"Published versions cannot be overwritten");
    await Grant("outsider");
    foreach(var(hash,bytes)in fixtures.Content)await other.Upload(hash,new MemoryStream(bytes),bytes.Length);
    await Reject(async()=>await other.Publish(pack with{Version="0.2.0"}),HttpStatusCode.Forbidden,"Another publisher cannot take over a reserved pack ID");
    var unsafePack=pack with{Id="unsafe-pack",Mods=[pack.Mods[0] with{Files=[pack.Mods[0].Files[0] with{Path="../game.dll"}]}]};
    using(var raw=new HttpClient{BaseAddress=uri}){raw.DefaultRequestHeaders.Authorization=new("Bearer",captain.AccessToken);using var r=await raw.PostAsJsonAsync("packs",unsafePack,PackJson.Options);Check(r.StatusCode==HttpStatusCode.BadRequest,"Server independently rejects path traversal manifests");}
    Check((await guest.Releases()).Any(p=>p.PublisherId==captain.User.Id),"Pack directory exposes authenticated publisher provenance");
    var manifest=await guest.Pack(pack.Id,pack.Version);var plan=PackRules.Plan(manifest,"steam","preview-steam");var cache=new PackCache(Path.Combine(state,"guest-cache"));
    await cache.Subscribe(plan,plan.PackLock,guest.OpenFile);Check(await cache.IsCached(plan),"Guest subscribes over real HTTP with existing cache integrity checks");
    var newReview=PackRules.Plan(manifest with{Description="Changed review text, same files"},"steam","preview-steam");
    Check(await cache.IsCached(newReview)&&!await cache.IsSubscribed(newReview),"Cached files do not bypass consent to a different manifest");
    await host.RequestFriend("crew");var incoming=(await guest.Friends()).Single();Check(incoming.Status=="incoming","Friend requests require acceptance");
    await Reject(async()=>await host.AcceptFriend(crew.User.Id),HttpStatusCode.NotFound,"Sender cannot accept its own friend request");
    await guest.AcceptFriend(captain.User.Id);Check((await host.Friends()).Single().Status=="accepted","Mutual friendship becomes visible to both accounts");
    var hosted=await host.Host(new("Friends room",pack.Id,pack.Version,"friends",2,"meta","preview-meta",published.PackLock));var id=hosted.Room.Advertisement.Id;var ad=hosted.Room.Advertisement;
    Check((await guest.Lobbies()).Any(l=>l.Id==id)&&(await other.Lobbies()).Length==0,"Friends-only lobby visibility is enforced server-side");
    await Reject(async()=>await other.Lobby(id),HttpStatusCode.NotFound,"Knowing a private lobby ID does not bypass visibility");
    await Reject(async()=>await guest.Join(id,new(ad.Revision,new string('f',64),"steam","preview-steam",null)),HttpStatusCode.Conflict,"Joining with an unreviewed pack lock rejected");
    await Reject(async()=>await guest.Join(id,new(ad.Revision+1,ad.PackLock,"steam","preview-steam",null)),HttpStatusCode.Conflict,"Stale lobby revisions rejected");
    var joined=await guest.Join(id,new(ad.Revision,ad.PackLock,"steam","preview-steam",null));
    Check(joined.Members.Length==2&&!joined.GameConnected&&joined.Status=="launcher-room-only","Meta host and Steam guest join a real launcher room without claiming gameplay");
    var inviteAd=await other.ResolveInvite(hosted.InviteCode);Check(inviteAd.Id==id,"Explicit invitation permits discovery without public listing");
    await Reject(async()=>await other.Join(id,new(ad.Revision,ad.PackLock,"steam","preview-steam",hosted.InviteCode)),HttpStatusCode.Conflict,"Lobby capacity enforced atomically");
    var ready=await guest.Ready(id,ad.Revision,ad.PackLock,true);Check(ready.Members.Single(m=>m.UserId==crew.User.Id).Ready,"Readiness is bound to exact joined pack identity");
    Check((await guest.Presence()).Room?.Advertisement.Id==id,"An account can recover its existing room after reconnecting");
    await Reject(async()=>await guest.Kick(id,captain.User.Id),HttpStatusCode.Forbidden,"Guests cannot remove other crew members");
    Check((await host.Kick(id,crew.User.Id)).Members.Length==1,"Host can remove a selected crew member");
    await Reject(async()=>await guest.Heartbeat(id),HttpStatusCode.Forbidden,"Removed member cannot keep renewing membership");
    await guest.Join(id,new(ad.Revision,ad.PackLock,"steam","preview-steam",null));
    Check((await host.Heartbeat(id)).Members.Length==2,"Host/member leases renew through heartbeat");
    await Reject(async()=>await other.Heartbeat(id),HttpStatusCode.Forbidden,"Nonmembers cannot renew someone else's lobby");
    var rotated=await host.RotateInvite(id);Check(rotated.InviteCode!=hosted.InviteCode,"Host can rotate invitation secrets");
    await Reject(async()=>await other.ResolveInvite(hosted.InviteCode),HttpStatusCode.NotFound,"Rotated invitation cannot be reused");
    await guest.Leave(id);Check((await host.Room(id)).Members.Length==1,"Leaving removes membership immediately");
    await guest.Block(captain.User.Id);Check((await guest.Lobbies()).Length==0,"Blocking removes friendship and hides the blocked host's lobby");
    await Reject(async()=>await guest.ResolveInvite(rotated.InviteCode),HttpStatusCode.NotFound,"Invitations do not bypass blocks");
    await host.Leave(id);Check((await host.Lobbies()).Length==0,"Host departure closes the launcher lobby");
    await guest.Report(pack.Id,pack.Version,"Backend moderation test");checks.Add("Signed-in players can report a published pack");
    using(var raw=new HttpClient{BaseAddress=uri}){raw.DefaultRequestHeaders.Add("X-BridgeCrew-Admin",admin);using var r=await raw.PostAsJsonAsync("admin/hide-pack",new{id=pack.Id,version=pack.Version,hidden=true});r.EnsureSuccessStatusCode();}
    await Reject(async()=>await guest.Pack(pack.Id,pack.Version),HttpStatusCode.NotFound,"Owner moderation removes a release from discovery and retrieval");
    var recovered=await other.Recover("outsider",outsider.RecoveryCode!,"New other password 728!");
    await Reject(async()=>await other.Me(),HttpStatusCode.Unauthorized,"Account recovery revokes previous sessions");
    other.SetToken(recovered.AccessToken);Check(recovered.RecoveryCode!=outsider.RecoveryCode&&(await other.Me()).Id==outsider.User.Id,"Recovery rotates the one-use recovery secret");
    await Reject(async()=>await other.Recover("outsider",outsider.RecoveryCode!,"Yet another password 927!"),HttpStatusCode.Unauthorized,"Old recovery secret cannot be replayed");
    await other.Logout();await Reject(async()=>await other.Me(),HttpStatusCode.Unauthorized,"Sign-out revokes the server session");
    var stored=File.ReadAllBytes(Path.Combine(state,"data","service.sqlite"));Check(!System.Text.Encoding.UTF8.GetString(stored).Contains(captain.AccessToken),"Database does not store raw bearer secrets");
    Stop();await Start();using var persistent=new CrewServiceClient(uri,captain.AccessToken);Check((await persistent.Me()).Id==captain.User.Id&&(await persistent.Releases()).Length==1,"Accounts, sessions and pack releases survive service restart");
    var lasting=await persistent.Host(new("Lease test",unicode.Id,unicode.Version,"public",4,"meta","preview-meta",PackRules.Lock(unicode)));
    Execute("UPDATE lobbies SET expires=0; UPDATE members SET expires=0;");Check((await persistent.Lobbies()).Length==0,"Expired host leases disappear without a daemon or cron job");
    await Reject(async()=>await persistent.Heartbeat(lasting.Room.Advertisement.Id),HttpStatusCode.NotFound,"Expired lobbies cannot be resurrected by stale heartbeat");
    for(var i=0;i<10;i++)await Reject(async()=>await persistent.Login("rate_limit_target","Wrong password 298!"),HttpStatusCode.Unauthorized,"Incorrect sign-in rejected");
    await Reject(async()=>await persistent.Login("rate_limit_target","Wrong password 298!"),HttpStatusCode.TooManyRequests,"Repeated sign-in attempts are rate limited");
    Check(!Directory.EnumerateFiles(Path.Combine(state,"data","blobs"),"*.part").Any(),"Failed and duplicate uploads leave no partial files");
    if(args.Length>2)
    {
        await guest.Unblock(captain.User.Id);
        var hostedNative=await persistent.Host(new("Native service test",unicode.Id,unicode.Version,"public",4,"meta","preview-meta",PackRules.Lock(unicode)));
        var nativeOutput=Path.Combine(state,"native");var input=Path.Combine(state,"native-input.json");
        File.WriteAllText(input,JsonSerializer.Serialize(new{service=uri.AbsoluteUri,username="crew",password="Test crew password 946!",lobbyId=hostedNative.Room.Advertisement.Id,output=nativeOutput},PackJson.Options));
        var launch=new ProcessStartInfo(Path.GetFullPath(args[2])){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(args[2]))!};
        launch.ArgumentList.Add("--service-self-test");launch.ArgumentList.Add(input);
        using var native=Process.Start(launch)!;
        try{await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));}catch{if(!native.HasExited)native.Kill();throw;}
        using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(nativeOutput,"service-native-test.json")));
        if(!result.RootElement.GetProperty("success").GetBoolean())throw new InvalidOperationException(result.RootElement.GetRawText());
        foreach(var name in result.RootElement.GetProperty("checks").EnumerateArray())Check(true,name.GetString()!);
        await persistent.Leave(hostedNative.Room.Advertisement.Id);
    }
    File.WriteAllText(Path.Combine(state,"results.json"),JsonSerializer.Serialize(new{success=true,checks,count=checks.Count},PackJson.Options));Console.WriteLine($"{checks.Count} backend checks passed. Evidence: {state}");
}
catch(Exception e){File.WriteAllText(Path.Combine(state,"results.json"),JsonSerializer.Serialize(new{success=false,checks,error=e.ToString()},PackJson.Options));Console.Error.WriteLine(e);Environment.ExitCode=1;}
finally{Stop();}

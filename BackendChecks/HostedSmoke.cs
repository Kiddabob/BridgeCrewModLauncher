using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BridgeCrew.Packs;

internal static class HostedSmoke
{
    public static async Task<int> Run(Uri endpoint,string ownerPath,string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
        using var config=JsonDocument.Parse(await File.ReadAllTextAsync(ownerPath));
        if(config.RootElement.GetProperty("api").GetString()!=endpoint.AbsoluteUri)throw new InvalidOperationException("Owner credential belongs to another service.");
        using var admin=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=endpoint};admin.DefaultRequestHeaders.Add("X-BridgeCrew-Admin",config.RootElement.GetProperty("adminKey").GetString());
        async Task Admin(string route,object body){using var r=await admin.PostAsJsonAsync("admin/"+route,body);r.EnsureSuccessStatusCode();}
        var nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));var hostName="bctesth_"+nonce;var guestName="bctestg_"+nonce;
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24))+"!a9";
        using var host=new CrewServiceClient(endpoint);using var guest=new CrewServiceClient(endpoint);
        bool hostCreated=false,guestCreated=false,published=false;string? lobbyId=null;var packId="service-check-"+nonce;
        try
        {
            Check((await host.Health()).Mode=="live-service","Hosted API health over verified HTTPS");
            var captain=await host.Register(hostName,"Service verification host",password);hostCreated=true;host.SetToken(captain.AccessToken);
            var crew=await guest.Register(guestName,"Service verification guest",password);guestCreated=true;guest.SetToken(crew.AccessToken);
            Check((await guest.Me()).Id==crew.User.Id,"Hosted registration and bearer authentication");
            await Admin("publisher",new{username=hostName,enabled=true});Check((await host.Me()).CanPublish,"Hosted owner-controlled publishing permission");
            var fixture=new PreviewCatalog();var pack=fixture.Packs[0] with{Id=packId,Name="Service verification fixture",Description="Temporary automated backend verification; harmless JSON examples, not working game mods."};
            foreach(var(hash,bytes)in fixture.Content)await host.Upload(hash,new MemoryStream(bytes),bytes.Length);
            var release=await host.Publish(pack);published=true;Check(release.PackLock==PackRules.Lock(pack),"Hosted uploads and immutable canonical release");
            Check((await guest.Release(pack.Id,pack.Version)).PublisherId==captain.User.Id,"Hosted publisher provenance");
            var meta=PackRules.Plan(pack,"meta","preview-meta");var steam=PackRules.Plan(await guest.Pack(pack.Id,pack.Version),"steam","preview-steam");
            var hostCache=new PackCache(Path.Combine(output,"host-cache"));var guestCache=new PackCache(Path.Combine(output,"guest-cache"));
            await hostCache.Subscribe(meta,meta.PackLock,host.OpenFile);await guestCache.Subscribe(steam,steam.PackLock,guest.OpenFile);
            Check(await hostCache.IsSubscribed(meta)&&await guestCache.IsSubscribed(steam),"Hosted Meta and Steam payloads download and verify independently");
            await host.RequestFriend(guestName);await guest.AcceptFriend(captain.User.Id);Check((await host.Friends()).Single().Status=="accepted","Hosted friend request and acceptance");
            var hosted=await host.Host(new("Service verification room",pack.Id,pack.Version,"friends",4,"meta",meta.Build,meta.PackLock));lobbyId=hosted.Room.Advertisement.Id;
            Check((await guest.Lobbies()).Any(l=>l.Id==lobbyId),"Hosted friends-only room discovery");
            Check((await guest.ResolveInvite(hosted.InviteCode)).Id==lobbyId,"Hosted invitation resolution");
            var ad=hosted.Room.Advertisement;var joined=await guest.Join(lobbyId,new(ad.Revision,ad.PackLock,"steam",steam.Build,hosted.InviteCode));
            Check(joined.Members.Length==2&&!joined.GameConnected,"Hosted two-account launcher-room membership");
            var ready=await guest.Ready(lobbyId,ad.Revision,ad.PackLock,true);Check(ready.Members.Single(m=>m.UserId==crew.User.Id).Ready,"Hosted readiness bound to reviewed pack");
            Check((await host.Heartbeat(lobbyId)).Members.Length==2&&(await guest.Presence()).Room?.Advertisement.Id==lobbyId,"Hosted heartbeat and room reconnection");
            await guest.Leave(lobbyId);await host.Leave(lobbyId);lobbyId=null;Check((await host.Lobbies()).All(l=>l.PackId!=packId),"Hosted room departure removes presence");
            await guest.Logout();await host.Logout();Check(true,"Hosted sign-out revokes sessions");
        }
        catch(Exception e){await File.WriteAllTextAsync(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{success=false,checks,error=e.ToString()},PackJson.Options));Console.Error.WriteLine(e);return 1;}
        finally
        {
            if(published)await Admin("hide-pack",new{id=packId,version="0.1.0",hidden=true});
            if(hostCreated)await Admin("account",new{username=hostName,disabled=true});
            if(guestCreated)await Admin("account",new{username=guestName,disabled=true});
        }
        await File.WriteAllTextAsync(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{success=true,checks,count=checks.Count,testAccountsDisabled=true,testPackHidden=true},PackJson.Options));
        Console.WriteLine($"{checks.Count} hosted HTTPS checks passed; synthetic test accounts disabled and fixture pack hidden.");return 0;
    }
}

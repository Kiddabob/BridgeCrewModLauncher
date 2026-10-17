using System.Text.Json;
using BridgeCrew.Packs;
using Microsoft.UI.Xaml;

namespace BridgeCrew.Launcher;

public sealed partial class MainWindow
{
    private sealed record ServiceTestInput(string Service,string Username,string Password,string LobbyId,string Output);
    private async Task ServiceSelfTest(string inputPath)
    {
        var input=JsonSerializer.Deserialize<ServiceTestInput>(await File.ReadAllTextAsync(inputPath),PackJson.Options)!;
        Directory.CreateDirectory(input.Output);var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            cache=new PackCache(Path.Combine(input.Output,"cache"));ServiceUrl.Text=input.Service;AccountUsername.Text=input.Username;AccountPassword.Password=input.Password;
            await SignIn(false);Check(account?.Username==input.Username,"Native account sign-in over HTTP");
            const string protectedToken="local-user-protected-test-token";
            var protectionFolder=Path.Combine(input.Output,"protected-session");await ServiceSessionStore.Save(service!.BaseUri,protectedToken,protectionFolder);
            Check(await ServiceSessionStore.Load(service.BaseUri,protectionFolder)==protectedToken&&!System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(protectionFolder,"service-session.bin"))).Contains(protectedToken),"Session persistence is encrypted for the current Windows user");
            Check(await ServiceSessionStore.Load(new Uri("https://different-service.example/"),protectionFolder)==null,"Saved session is not sent to a different service origin");
            Check(AccountPassword.Password=="","Password control cleared after sign-in");Check(!sampleMode,"Real service mode selected");
            Check(allLobbies.Any(l=>l.Id==input.LobbyId),"Native crew directory populated from real service");
            Lobbies.SelectedItem=allLobbies.Single(l=>l.Id==input.LobbyId);await SelectLobby();
            var plan=selectedPlan??throw new InvalidOperationException("No online pack selected.");
            Check(plan.Manifest.Id==selectedLobby?.PackId&&PackDescription.Text.Contains("Publisher account:"),"Native review displays published pack and publisher identity");
            await cache.Subscribe(plan,plan.PackLock,SignedService().OpenFile,lifetime.Token);Check(await cache.IsSubscribed(plan),"Reviewed online pack cached outside game files");
            var ad=selectedLobby!;var room=await SignedService().Join(ad.Id,new(ad.Revision,plan.PackLock,plan.Edition,plan.Build,null),lifetime.Token);SetRoom(room,plan);ShowOnline();
            Check(ActiveRoomPanel.Visibility==Visibility.Visible&&room.Members.Any(m=>m.UserId==account!.Id),"Native launcher room displays authenticated crew membership");
            await ChangeReady(true);Check(activeRoom!.Members.Single(m=>m.UserId==account!.Id).Ready,"Native readiness verifies the subscribed pack");
            Check(!activeRoom.GameConnected&&RoomSummary.Text.Contains("inactive"),"Native room does not claim game connectivity");
            Root.RequestedTheme=ElementTheme.Light;var light=Path.Combine(input.Output,"online-light.png");await File.WriteAllBytesAsync(light,[]);await Snapshot(light);
            Root.RequestedTheme=ElementTheme.Dark;var dark=Path.Combine(input.Output,"online-dark.png");await File.WriteAllBytesAsync(dark,[]);await Snapshot(dark);
            await SignedService().Leave(ad.Id,lifetime.Token);DropRoom();Check(activeRoom==null,"Native room leave clears presence display");
            await SignedService().Logout(lifetime.Token);Check(true,"Native account sign-out revokes session");
            await File.WriteAllTextAsync(Path.Combine(input.Output,"service-native-test.json"),JsonSerializer.Serialize(new{success=true,checks},PackJson.Options));
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(input.Output,"service-native-test.json"),JsonSerializer.Serialize(new{success=false,checks,error=ex.ToString()},PackJson.Options));}
        Close();
    }
}

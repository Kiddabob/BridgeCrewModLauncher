using System.Security.Cryptography;
using System.Text.Json;
using BridgeCrew.Packs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace BridgeCrew.Launcher;

public sealed partial class MainWindow
{
    private bool sampleMode=Environment.GetCommandLineArgs().Contains("--self-test"),onlineBusy,heartbeatBusy,changingBuilds;
    private CrewServiceClient? service;
    private CrewAccount? account;
    private PackRelease[] publishedReleases=[];
    private CrewFriend[] crewFriends=[];
    private CrewRoom? activeRoom;
    private PackPlan? activePlan;
    private DispatcherQueueTimer? heartbeat;
    private string? invitedLobbyId,invitation;
    private void UseServiceCache()
    {
        if(service==null||Environment.GetCommandLineArgs().Contains("--service-self-test"))return;
        var origin=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(service.BaseUri.AbsoluteUri)));
        cache=new PackCache(Path.Combine(AppPaths.Root,"services",origin));
    }

    private void OnlineMessage(string message,InfoBarSeverity severity=InfoBarSeverity.Informational)
    {OnlineStatus.Message=message;OnlineStatus.Severity=severity;}
    private CrewServiceClient SignedService()=>account!=null&&service!=null?service:throw new InvalidOperationException("Sign in on the Online page first.");
    private async Task OnlineAction(Func<Task> action)
    {
        if(onlineBusy||busy)return;onlineBusy=true;SignInButton.IsEnabled=false;RegisterButton.IsEnabled=false;
        try{await action();}
        catch(Exception ex){OnlineMessage(ex.Message,InfoBarSeverity.Error);}
        finally{onlineBusy=false;SignInButton.IsEnabled=true;RegisterButton.IsEnabled=true;}
    }
    private void DirectoryMode()
    {
        DirectoryInfo.Title=sampleMode?"Sample lobbies":"Online launcher rooms";
        DirectoryInfo.Message=sampleMode?"Harmless examples for exploring the launcher. No players or game connection.":
            "Real crew accounts and pack subscriptions. Joining a launcher room does not join Bridge Crew yet.";
        JoinButton.Content=sampleMode?"Prepare sample join":"Join launcher room";
        BuildProfile.Visibility=sampleMode?Visibility.Collapsed:Visibility.Visible;
    }
    private void ShowCrew(){Navigation.SelectedItem=Navigation.MenuItems.OfType<NavigationViewItem>().Single(n=>(string)n.Tag=="crew");}
    private void ShowOnline(){Navigation.SelectedItem=Navigation.MenuItems.OfType<NavigationViewItem>().Single(n=>(string)n.Tag=="online");}
    private async Task RestoreService()
    {
        ServiceUrl.Text=AppPaths.ServiceUrl;DirectoryMode();
        service=new CrewServiceClient(new Uri(ServiceUrl.Text));
        UseServiceCache();
        try
        {
            var health=await service.Health(lifetime.Token);
            if(health.Protocol!=PackRules.Protocol||health.Mode!="live-service")throw new InvalidDataException("This is not a compatible crew service.");
            var saved=await ServiceSessionStore.Load(service.BaseUri);
            if(saved!=null){service.SetToken(saved);account=await service.Me(lifetime.Token);}
            await RefreshOnline();
        }
        catch(CrewServiceException ex)when(ex.StatusCode==System.Net.HttpStatusCode.Unauthorized)
        {ServiceSessionStore.Clear();service.SetToken(null);OnlineMessage("Your session expired. Sign in again.");}
        catch(Exception ex){OnlineMessage("Crew service unavailable. "+ex.Message,InfoBarSeverity.Warning);}
        UpdateAccount();if(account==null){SetStatus("Sign in on the Online page to find players, or try sample lobbies.");ShowOnline();}
    }
    private void UpdateAccount()
    {
        AccountLabel.Text=account==null?"Signed out":$"{account.DisplayName} · @{account.Username}";
        AccountForm.Visibility=account==null?Visibility.Visible:Visibility.Collapsed;
        SignOutButton.Visibility=account==null?Visibility.Collapsed:Visibility.Visible;
        if(service!=null)ServicePrivacy.NavigateUri=new Uri(service.BaseUri,"../../privacy.html");
        PublisherStatus.Text=account?.CanPublish==true?"Publishing enabled for your account. Choose a folder containing manifest.json and content/<sha256> payloads.":
            "Publishing requires approval from the service owner. A pack folder contains manifest.json and a content folder with files named by SHA-256.";
    }
    private async Task RefreshOnline()
    {
        if(service==null)return;
        publishedReleases=await service.Releases(ct:lifetime.Token); PublishedPacks.ItemsSource=publishedReleases.Select(p=>$"{p.Name} · {p.Version}\n{p.PublisherName}\n{p.Description}").ToArray();
        if(account!=null)
        {
            account=await service.Me(lifetime.Token);crewFriends=await service.Friends(lifetime.Token); FriendsList.ItemsSource=crewFriends.Select(f=>$"{f.DisplayName} · @{f.Username}\n{f.Status}").ToArray();
            allLobbies=await service.Lobbies(ct:lifetime.Token);appliedQuery=null;Filter();
            if(activeRoom==null&&(await service.Presence(lifetime.Token)).Room is {} previous)
            {
                var member=previous.Members.Single(m=>m.UserId==account.Id);var manifest=await service.Pack(previous.Advertisement.PackId,previous.Advertisement.PackVersion,lifetime.Token);
                SetRoom(previous,PackRules.Plan(manifest,member.Edition,member.Build));
            }
            if(Lobbies.Items.Count>0&&Lobbies.SelectedIndex<0)Lobbies.SelectedIndex=0;
            OnlineMessage($"Connected · {allLobbies.Length} visible launcher room(s).",InfoBarSeverity.Success);
            SetStatus(allLobbies.Length==0?"No crews are hosting yet. Host a published pack from Online.":"Online crews refreshed.");
        }
        UpdateAccount();
    }
    private async Task SignIn(bool register,bool recover=false)
    {
        if(account!=null)throw new InvalidOperationException("Sign out before switching account or service.");
        using var candidate=new CrewServiceClient(new Uri(ServiceUrl.Text.Trim()));
        var health=await candidate.Health(lifetime.Token);
        if(health.Protocol!=1||health.Mode!="live-service")throw new InvalidDataException("Incompatible crew service.");
        AccountSession session;
        try
        {
            session=recover?await candidate.Recover(AccountUsername.Text.Trim(),RecoveryCode.Password,AccountPassword.Password,lifetime.Token):
                register?await candidate.Register(AccountUsername.Text.Trim(),string.IsNullOrWhiteSpace(AccountDisplayName.Text)?AccountUsername.Text.Trim():AccountDisplayName.Text.Trim(),AccountPassword.Password,lifetime.Token):
                await candidate.Login(AccountUsername.Text.Trim(),AccountPassword.Password,lifetime.Token);
        }
        finally{AccountPassword.Password="";RecoveryCode.Password="";}
        service?.Dispose();service=new CrewServiceClient(candidate.BaseUri,session.AccessToken);account=session.User;sampleMode=false;DirectoryMode();
        UpdateAccount();
        UseServiceCache();
        if(session.RecoveryCode!=null)
        {
            var box=new TextBox{Text=session.RecoveryCode,IsReadOnly=true,TextWrapping=TextWrapping.Wrap};
            var panel=new StackPanel{Spacing=12};panel.Children.Add(new TextBlock{Text="Save this recovery code somewhere safe. It is shown once and lets you reset your password. Using it replaces it with a new code.",TextWrapping=TextWrapping.Wrap});panel.Children.Add(box);
            await new ContentDialog{XamlRoot=Root.XamlRoot,Title="Save your account recovery code",Content=panel,CloseButtonText="Done"}.ShowAsync();
        }
        if(!Environment.GetCommandLineArgs().Contains("--service-self-test")){AppPaths.SaveServiceUrl(service.BaseUri.AbsoluteUri);await ServiceSessionStore.Save(service.BaseUri,session.AccessToken);}
        await RefreshOnline();
        OnlineMessage("Signed in. Find a crew, host a pack or invite your friends.",InfoBarSeverity.Success);
    }
    private async void SignInClicked(object sender,RoutedEventArgs e)=>await OnlineAction(()=>SignIn(false));
    private async void RegisterClicked(object sender,RoutedEventArgs e)=>await OnlineAction(()=>SignIn(true));
    private async void RecoverClicked(object sender,RoutedEventArgs e)=>await OnlineAction(()=>SignIn(false,true));
    private async void SignOutClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>
    {
        try{if(activeRoom!=null)await SignedService().Leave(activeRoom.Advertisement.Id,lifetime.Token);if(account!=null)await SignedService().Logout(lifetime.Token);}
        finally{DropRoom();ServiceSessionStore.Clear();account=null;service?.SetToken(null);allLobbies=[];appliedQuery=null;Filter();UpdateAccount();OnlineMessage("Signed out.");}
    });
    private async void OnlineRefreshClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{sampleMode=false;UseServiceCache();DirectoryMode();await RefreshOnline();});
    private async void UseSamplesClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>
    {
        if(activeRoom!=null){await SignedService().Leave(activeRoom.Advertisement.Id,lifetime.Token);DropRoom();}
        sampleMode=true;cache=new PackCache(AppPaths.Root);DirectoryMode();Refresh();ShowCrew();
    });
    private async void BuildProfileChanged(object sender,SelectionChangedEventArgs e){if(ready&&!changingBuilds&&!sampleMode)await SelectLobby();}
    private async Task ReviewAndSubscribe(PackPlan plan,string publisher)
    {
        var panel=new StackPanel{Spacing=12,MaxWidth=520};
        panel.Children.Add(new TextBlock{Text=$"Publisher account: {publisher}\nPC edition: {plan.Edition}\nDeclared build: {plan.Build}",TextWrapping=TextWrapping.Wrap});
        foreach(var mod in plan.Manifest.Mods){panel.Children.Add(new TextBlock{Text=$"{mod.Name} · {mod.Version}\n{mod.Author}\n{mod.Description}\n"+(mod.ContainsCode?"Includes executable code.":"Publisher marks this mod as data only."),TextWrapping=TextWrapping.Wrap});}
        panel.Children.Add(new TextBlock{Text=$"Download {plan.DownloadBytes:N0} bytes to the launcher cache. No game files are activated.",TextWrapping=TextWrapping.Wrap});
        var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,Title="Review "+plan.Manifest.Name,Content=new ScrollViewer{Content=panel,MaxHeight=460},PrimaryButtonText="Subscribe",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Close};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)throw new OperationCanceledException("Subscription cancelled.");
        await cache.Subscribe(plan,plan.PackLock,SignedService().OpenFile,lifetime.Token);
    }
    private async void HostRoomClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>
    {
        var client=SignedService();if(activeRoom!=null)throw new InvalidOperationException("Leave your current room before hosting another.");
        sampleMode=false;UseServiceCache();DirectoryMode();
        if(PublishedPacks.SelectedIndex<0||PublishedPacks.SelectedIndex>=publishedReleases.Length)throw new InvalidOperationException("Select a published pack first."); var release=publishedReleases[PublishedPacks.SelectedIndex];
        var pack=await client.Pack(release.Id,release.Version,lifetime.Token);if(PackRules.Lock(pack)!=release.PackLock)throw new InvalidDataException("The published pack identity changed.");
        var edition=(string)((ComboBoxItem)Edition.SelectedItem).Tag;
        if(!pack.SupportedBuilds.TryGetValue(edition,out var builds))throw new InvalidDataException("This pack does not declare support for the selected PC edition.");
        var chooser=new ComboBox{Header="Declared game build",ItemsSource=builds,SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var choose=new ContentDialog{XamlRoot=Root.XamlRoot,Title="Host "+pack.Name,Content=chooser,PrimaryButtonText="Review pack",CloseButtonText="Cancel"};
        if(await choose.ShowAsync()!=ContentDialogResult.Primary)return;
        var plan=PackRules.Plan(pack,edition,(string)chooser.SelectedItem);await ReviewAndSubscribe(plan,release.PublisherName);
        var name=string.IsNullOrWhiteSpace(RoomName.Text)?account!.DisplayName+"’s crew":RoomName.Text.Trim();
        var hosted=await client.Host(new(name,pack.Id,pack.Version,(string)((ComboBoxItem)RoomVisibility.SelectedItem).Tag,4,edition,plan.Build,plan.PackLock),lifetime.Token);
        sampleMode=false;UseServiceCache();DirectoryMode();SetRoom(hosted.Room,plan);RoomInvite.Text=hosted.InviteCode;await RefreshOnline();
        OnlineMessage("Your launcher room is open. Share its invitation code with friends.",InfoBarSeverity.Success);
    });
    private void SetRoom(CrewRoom room,PackPlan? plan=null)
    {
        activeRoom=room;if(plan!=null)activePlan=plan;ActiveRoomPanel.Visibility=Visibility.Visible;
        RoomSummary.Text=$"{room.Advertisement.Name}\n{room.Members.Length}/{room.Capacity} crew · {room.Visibility}\n{room.Status} · game connection inactive";
        RoomMembers.ItemsSource=room.Members.Select(m=>$"{m.DisplayName} · {m.Edition} · {(m.Ready?"Ready":"Not ready")}").ToArray();
        if(heartbeat==null){heartbeat=DispatcherQueue.CreateTimer();heartbeat.Interval=TimeSpan.FromSeconds(30);heartbeat.Tick+=HeartbeatTick;}
        if(!heartbeat.IsRunning)heartbeat.Start();
        RoomHostControls.Visibility=room.HostId==account?.Id?Visibility.Visible:Visibility.Collapsed;
    }
    private void DropRoom(){heartbeat?.Stop();activeRoom=null;activePlan=null;ActiveRoomPanel.Visibility=Visibility.Collapsed;RoomInvite.Text="";}
    private async void HeartbeatTick(DispatcherQueueTimer timer,object args)
    {
        if(heartbeatBusy||activeRoom==null||account==null||onlineBusy)return;heartbeatBusy=true;var id=activeRoom.Advertisement.Id;
        try{var room=await SignedService().Heartbeat(id,lifetime.Token);if(activeRoom?.Advertisement.Id==id)SetRoom(room);}
        catch(Exception ex){if(activeRoom?.Advertisement.Id==id){DropRoom();OnlineMessage("Room connection ended. "+ex.Message,InfoBarSeverity.Warning);}}
        finally{heartbeatBusy=false;}
    }
    private async Task ChangeReady(bool value)
    {
        var room=activeRoom??throw new InvalidOperationException("Join a launcher room first.");
        if(value&&(activePlan==null||!await cache.IsSubscribed(activePlan,lifetime.Token)))throw new InvalidDataException("Your reviewed pack is missing or changed. Subscribe again before marking ready.");
        SetRoom(await SignedService().Ready(room.Advertisement.Id,room.Advertisement.Revision,room.Advertisement.PackLock,value,lifetime.Token));
    }
    private async void RoomReadyClicked(object sender,RoutedEventArgs e)=>await OnlineAction(()=>ChangeReady(true));
    private async void RoomNotReadyClicked(object sender,RoutedEventArgs e)=>await OnlineAction(()=>ChangeReady(false));
    private async void LeaveRoomClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{if(activeRoom!=null)await SignedService().Leave(activeRoom.Advertisement.Id,lifetime.Token);DropRoom();await RefreshOnline();});
    private async void RotateRoomInviteClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{if(activeRoom==null)throw new InvalidOperationException("No active room.");RoomInvite.Text=(await SignedService().RotateInvite(activeRoom.Advertisement.Id,lifetime.Token)).InviteCode;});
    private async void RemoveCrewClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>
    {
        if(activeRoom==null||RoomMembers.SelectedIndex<0)throw new InvalidOperationException("Select a crew member first.");
        var member=activeRoom.Members[RoomMembers.SelectedIndex];SetRoom(await SignedService().Kick(activeRoom.Advertisement.Id,member.UserId,lifetime.Token));
    });
    private async void ResolveInviteClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>
    {
        var code=InviteCode.Text.Trim();var lobby=await SignedService().ResolveInvite(code,lifetime.Token);invitedLobbyId=lobby.Id;invitation=code;
        sampleMode=false;UseServiceCache();DirectoryMode();allLobbies=[lobby];Search.Text="";appliedQuery=null;Filter();Lobbies.SelectedIndex=0;ShowCrew();
    });
    private async void FriendRequestClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{await SignedService().RequestFriend(FriendUsername.Text.Trim(),lifetime.Token);await RefreshOnline();});
    private CrewFriend SelectedFriend()=>FriendsList.SelectedIndex>=0&&FriendsList.SelectedIndex<crewFriends.Length?crewFriends[FriendsList.SelectedIndex]:throw new InvalidOperationException("Select a friend or incoming request first.");
    private async void AcceptFriendClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{await SignedService().AcceptFriend(SelectedFriend().UserId,lifetime.Token);await RefreshOnline();});
    private async void RemoveFriendClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{await SignedService().RemoveFriend(SelectedFriend().UserId,lifetime.Token);await RefreshOnline();});
    private async void BlockFriendClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>{await SignedService().Block(SelectedFriend().UserId,lifetime.Token);await RefreshOnline();});
    private async void PublishFolderClicked(object sender,RoutedEventArgs e)=>await OnlineAction(async()=>
    {
        var client=SignedService();account=await client.Me(lifetime.Token);UpdateAccount();if(!account.CanPublish)throw new InvalidOperationException("Ask the service owner to enable publishing for your username.");
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder=await picker.PickSingleFolderAsync();if(folder==null)return;
        var manifestPath=Path.Combine(folder.Path,"manifest.json");if(!File.Exists(manifestPath)||new FileInfo(manifestPath).Length>1024*1024)throw new InvalidDataException("Choose a pack folder containing manifest.json (up to 1 MiB).");
        var pack=JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(manifestPath,lifetime.Token),PackJson.Options)??throw new InvalidDataException("Invalid manifest.");PackRules.Validate(pack);
        var confirm=new ContentDialog{XamlRoot=Root.XamlRoot,Title="Publish "+pack.Name+"?",Content=$"Version {pack.Version}\n{pack.Description}\n\nThe pack and descriptions will be public under @{account.Username}. Published versions cannot be overwritten.",PrimaryButtonText="Publish",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Close};
        if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;
        var health=await client.Health(lifetime.Token);
        foreach(var file in pack.Mods.SelectMany(m=>m.Files).DistinctBy(f=>f.Sha256))
        {
            if(file.Bytes>health.MaxUploadBytes)throw new InvalidDataException($"The service accepts payloads up to {health.MaxUploadBytes:N0} bytes each.");
            var path=Path.Combine(folder.Path,"content",file.Sha256);await using var stream=File.OpenRead(path);
            if(stream.Length!=file.Bytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,lifetime.Token))!=file.Sha256)throw new InvalidDataException("Pack content is missing or does not match its manifest.");
            stream.Position=0;await client.Upload(file.Sha256,stream,file.Bytes,lifetime.Token);
        }
        await client.Publish(pack,lifetime.Token);await RefreshOnline();OnlineMessage("Published "+pack.Name+" "+pack.Version+".",InfoBarSeverity.Success);
    });
}

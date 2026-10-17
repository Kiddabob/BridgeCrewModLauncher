using System.Diagnostics;
using System.Text.Json;
using BridgeCrew.Packs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BridgeCrew.Launcher;

public sealed partial class MainWindow : Window
{
    private readonly PreviewCatalog catalog = new();
    private readonly CancellationTokenSource lifetime = new();
    private PackCache cache = new(AppPaths.Root);
    private readonly UpdateService updates = new();
    private readonly WindowShellIcon shellIcon;
    private LobbyAdvertisement[] allLobbies = [];
    private PackPlan? selectedPlan;
    private LobbyAdvertisement? selectedLobby;
    private bool ready, busy, updateBusy, initialized;
    private int selectionGeneration;
    private string? appliedQuery;
    public MainWindow()
    {
        InitializeComponent(); Title = "Bridge Crew Mod Launcher";
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1300,900));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.ico"));
        shellIcon = new WindowShellIcon(WinRT.Interop.WindowNative.GetWindowHandle(this));
        AppPaths.LoadSettings();
        Theme.SelectedIndex = AppPaths.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        ApplyTheme(); Root.ActualThemeChanged += (_,_) => UpdateCaption();
        Root.SizeChanged += (_,_) => AdaptLayout();
        CrewColumns.SizeChanged += (_,_) => AdaptLayout();
        Closed += (_,_) => { heartbeat?.Stop(); lifetime.Cancel(); service?.Dispose(); shellIcon.Dispose(); };
        VersionText.Text = $"Bridge Crew Mod Launcher · {AppPaths.Version} · WinUI 3 · Windows x64";
        ready = true;
    }
    private async void RootLoaded(object sender, RoutedEventArgs e)
    {
        if(initialized)return; initialized=true;
        try
        {
            var earlyArgs=Environment.GetCommandLineArgs();var serviceTest=Array.IndexOf(earlyArgs,"--service-self-test");
            if(serviceTest>=0&&serviceTest+1<earlyArgs.Length){await ServiceSelfTest(earlyArgs[serviceTest+1]);return;}
            var scale=Root.XamlRoot.RasterizationScale;
            var work=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Min(1320*scale,work.Width*0.95),(int)Math.Min(900*scale,work.Height*0.95)));
            if(sampleMode){DirectoryMode();Refresh();}else await RestoreService(); UpdateCaption();
            var args=Environment.GetCommandLineArgs();
            var testIndex=Array.IndexOf(args,"--self-test");
            if(testIndex>=0 && testIndex+1<args.Length) { await SelfTest(args[testIndex+1]); return; }
            var updateIndex=Array.IndexOf(args,"--verify-update");
            if(updateIndex>=0 && updateIndex+1<args.Length) { await VerifyUpdate(args[updateIndex+1]); return; }
            if(updates.IsInstalled) await CheckUpdates();
        }
        catch(Exception ex) { AppPaths.Log(ex.ToString()); SetStatus(ex.Message,InfoBarSeverity.Error); }
    }
    private void UpdateCaption()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
    }
    private void AdaptLayout()
    {
        var stacked = CrewColumns.ActualWidth < 760;
        CrewColumns.Height = stacked ? 860 : 560;
        LobbyColumn.Width = new GridLength(stacked ? 1 : 300,stacked ? GridUnitType.Star : GridUnitType.Pixel);
        CrewColumns.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1,stacked ? GridUnitType.Pixel : GridUnitType.Star);
        CrewColumns.RowDefinitions[0].Height = stacked ? new GridLength(190) : new GridLength(1,GridUnitType.Star);
        CrewColumns.RowDefinitions[1].Height = stacked ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(PackPane,stacked ? 1 : 0); Grid.SetColumn(PackPane,stacked ? 0 : 1);
    }
    private void SetStatus(string text,InfoBarSeverity severity=InfoBarSeverity.Informational) { Status.Message=text;Status.Severity=severity; }
    private void SetBusy(bool value)
    {
        busy=value; Edition.IsEnabled=!value;Lobbies.IsEnabled=!value;Search.IsEnabled=!value;
        SubscribeButton.IsEnabled=!value && selectedPlan!=null;ExportButton.IsEnabled=!value && selectedPlan!=null;JoinButton.IsEnabled=false;
    }
    private void Refresh()
    {
        if(!sampleMode){_=OnlineAction(async()=>{if(account==null)throw new InvalidOperationException("Sign in on the Online page first.");await RefreshOnline();});return;}
        if(busy)return;allLobbies=catalog.Lobbies();appliedQuery=null;Filter();
        SetStatus("Sample lobbies ready. Your game installation is unchanged.");
        if(Lobbies.Items.Count>0)Lobbies.SelectedIndex=0;
    }
    private void Filter()
    {
        if(!ready)return; var query=Search.Text.Trim();
        // AutoSuggestBox can deliver delayed duplicate TextChanged notifications.
        // Rebinding an unchanged result set clears ListView selection unnecessarily.
        if(appliedQuery==query)return;appliedQuery=query;
        var selectedId=(Lobbies.SelectedItem as LobbyAdvertisement)?.Id;
        var matches=allLobbies.Where(l=>l.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||l.HostName.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        Lobbies.ItemsSource=matches;
        if(selectedId!=null)Lobbies.SelectedItem=matches.FirstOrDefault(l=>l.Id==selectedId);
    }
    private void RefreshClicked(object sender,RoutedEventArgs e)=>Refresh();
    private void SearchChanged(AutoSuggestBox sender,AutoSuggestBoxTextChangedEventArgs e) {if(ready&&!busy)Filter();}
    private async void EditionChanged(object sender,SelectionChangedEventArgs e) {if(ready)await SelectLobby();}
    private async void LobbyChanged(object sender,SelectionChangedEventArgs e) {if(ready)await SelectLobby();}
    private async Task SelectLobby()
    {
        int generation=++selectionGeneration;selectedPlan=null;selectedLobby=null;
        SubscribeButton.IsEnabled=false;JoinButton.IsEnabled=false;ExportButton.IsEnabled=false;
        PackTitle.Text="Select a lobby";PackDescription.Text="See which mods the crew is using.";PackSummary.Text="";Mods.ItemsSource=null;
        if(Lobbies.SelectedItem is not LobbyAdvertisement lobby)return;
        try
        {
            var edition=(string)((ComboBoxItem)Edition.SelectedItem).Tag;
            var pack=sampleMode?catalog.Packs.Single(p=>p.Id==lobby.PackId&&p.Version==lobby.PackVersion):await SignedService().Pack(lobby.PackId,lobby.PackVersion,lifetime.Token);
            if(generation!=selectionGeneration)return;
            var build="preview-"+edition;
            if(!sampleMode)
            {
                if(!pack.SupportedBuilds.TryGetValue(edition,out var builds))throw new InvalidDataException("The publisher has not declared support for this PC edition.");
                var selected=BuildProfile.SelectedItem as string;changingBuilds=true;
                try{BuildProfile.ItemsSource=builds;BuildProfile.SelectedItem=selected!=null&&builds.Contains(selected)?selected:builds[0];build=(string)BuildProfile.SelectedItem;}
                finally{changingBuilds=false;}
            }
            var plan=PackRules.Plan(pack,edition,build);
            PackRules.ValidateLobby(lobby,plan,DateTimeOffset.UtcNow);
            var publisher=sampleMode?null:await SignedService().Release(pack.Id,pack.Version,lifetime.Token);
            if(publisher!=null&&publisher.PackLock!=plan.PackLock)throw new InvalidDataException("Published pack identity changed.");
            var cached=await cache.IsSubscribed(plan,lifetime.Token);
            if(generation!=selectionGeneration)return;
            selectedPlan=plan;selectedLobby=lobby;PackTitle.Text=pack.Name;PackDescription.Text=pack.Description+(publisher==null?"":$"\nPublisher account: {publisher.PublisherName} ({publisher.PublisherId})");
            PackSummary.Text=$"{pack.Mods.Length} mods · v{pack.Version} · {plan.DownloadBytes:N0} bytes · {(edition=="meta"?"Meta PC":"Steam PC")}\nPack {plan.PackLock[..16]}…";
            Mods.ItemsSource=pack.Mods;SubscribeButton.IsEnabled=!busy;ExportButton.IsEnabled=!busy;JoinButton.IsEnabled=cached&&!busy;
        }
        catch(Exception ex){if(generation==selectionGeneration)SetStatus(ex.Message,InfoBarSeverity.Error);}
    }
    private Task<Stream> OpenExample(string hash,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();return sampleMode?Task.FromResult<Stream>(new MemoryStream(catalog.Content[hash],false)):SignedService().OpenFile(hash,ct);}
    private Task<LobbyAdvertisement> RefreshExample(string id,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();return sampleMode?Task.FromResult(catalog.Lobbies().Single(l=>l.Id==id)):id==invitedLobbyId&&invitation!=null&&activeRoom?.Advertisement.Id!=id?SignedService().ResolveInvite(invitation,ct):SignedService().Lobby(id,ct);}
    private async void SubscribeClicked(object sender,RoutedEventArgs e)
    {
        if(busy||onlineBusy||selectedPlan is not {} plan||selectedLobby is not {} lobby)return;
        SetBusy(true);
        try
        {
            var content=new StackPanel{Spacing=16,MaxWidth=500};
            foreach(var mod in plan.Manifest.Mods)
            {
                content.Children.Add(new TextBlock{Text=$"{mod.Name} · {mod.Version}",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
                content.Children.Add(new TextBlock{Text=$"{mod.Author}\n{mod.Description}\n"+(mod.ContainsCode?"Includes executable code.":"Publisher marks this mod as data only."),TextWrapping=TextWrapping.Wrap});
            }
            content.Children.Add(new TextBlock{Text=$"Download {plan.DownloadBytes:N0} bytes. The game is not modified.",TextWrapping=TextWrapping.Wrap});
            var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,Title=$"Subscribe to {plan.Manifest.Name}?",Content=new ScrollViewer{Content=content,MaxHeight=450},PrimaryButtonText="Subscribe",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            var current=await RefreshExample(lobby.Id,lifetime.Token);
            if(current.Revision!=lobby.Revision)throw new InvalidDataException("Lobby changed. Review its pack again.");
            PackRules.ValidateLobby(current,plan,DateTimeOffset.UtcNow);
            await cache.Subscribe(plan,plan.PackLock,OpenExample,lifetime.Token);
            SetStatus("Subscribed. All files are verified and stored outside the game.",InfoBarSeverity.Success);
        }
        catch(Exception ex){SetStatus(ex.Message,InfoBarSeverity.Error);}
        finally{SetBusy(false);JoinButton.IsEnabled=await CachedSafely(plan);}
    }
    private async Task<bool> CachedSafely(PackPlan plan)
    {try{return !lifetime.IsCancellationRequested&&await cache.IsSubscribed(plan,lifetime.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException){return false;}}
    private async void JoinClicked(object sender,RoutedEventArgs e)
    {
        if(busy||onlineBusy||selectedPlan is not {} plan||selectedLobby is not {} lobby)return;SetBusy(true);
        try{
            if(sampleMode){await cache.PreparePreviewJoin(plan,lobby,RefreshExample,lifetime.Token);SetStatus("Preview join request prepared. Live game joining is not connected yet.",InfoBarSeverity.Success);}
            else{
                if(!await cache.IsSubscribed(plan,lifetime.Token))throw new InvalidDataException("Review and subscribe to this pack before joining.");
                var current=await RefreshExample(lobby.Id,lifetime.Token);if(current.Revision!=lobby.Revision)throw new InvalidDataException("Lobby changed. Review its pack again.");PackRules.ValidateLobby(current,plan,DateTimeOffset.UtcNow);
                var room=await SignedService().Join(lobby.Id,new(lobby.Revision,plan.PackLock,plan.Edition,plan.Build,lobby.Id==invitedLobbyId?invitation:null),lifetime.Token);
                SetRoom(room,plan);ShowOnline();OnlineMessage("Joined the launcher room. Bridge Crew game connection is not active yet.",InfoBarSeverity.Success);
            }
        }
        catch(Exception ex){SetStatus(ex.Message,InfoBarSeverity.Error);}
        finally{SetBusy(false);JoinButton.IsEnabled=await CachedSafely(plan);}
    }
    private async void ExportClicked(object sender,RoutedEventArgs e)
    {
        if(busy||selectedPlan is not {} plan)return;
        try
        {
            var folder=Path.Combine(AppPaths.Root,"exports");Directory.CreateDirectory(folder);
            var file=Path.Combine(folder,$"{plan.Manifest.Id}-{plan.Manifest.Version}-{plan.PackLock[..12]}.json");
            await File.WriteAllTextAsync(file,JsonSerializer.Serialize(plan.Manifest,PackJson.Options),lifetime.Token);
            SetStatus("Pack manifest exported to your launcher data folder.",InfoBarSeverity.Success);
        }catch(Exception ex){SetStatus(ex.Message,InfoBarSeverity.Error);}
    }
    private void NavigationChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {
        if(CrewPage==null)return;
        var page=args.IsSettingsSelected?"settings":(args.SelectedItem as NavigationViewItem)?.Tag as string??"crew";
        CrewPage.Visibility=page=="crew"?Visibility.Visible:Visibility.Collapsed;
        PacksPage.Visibility=page=="packs"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=page=="settings"?Visibility.Visible:Visibility.Collapsed;
        OnlinePage.Visibility=page=="online"?Visibility.Visible:Visibility.Collapsed;
        if(page=="packs")RefreshSubscriptions();
    }
    private void RefreshSubscriptions()
    {
        var folder=AppPaths.Root;var items=new List<string>();
        var roots=new List<string>{Path.Combine(folder,"subscriptions")};var servicesFolder=Path.Combine(folder,"services");
        if(Directory.Exists(servicesFolder))roots.AddRange(Directory.EnumerateDirectories(servicesFolder).Select(p=>Path.Combine(p,"subscriptions")));
        foreach(var subscriptionRoot in roots.Where(Directory.Exists))foreach(var path in Directory.EnumerateFiles(subscriptionRoot,"*.json",SearchOption.AllDirectories))
        {
            try{var p=JsonSerializer.Deserialize<PackPlan>(File.ReadAllText(path),PackJson.Options);if(p!=null)items.Add($"{p.Manifest.Name} · {p.Manifest.Version} · {(p.Edition=="meta"?"Meta PC":"Steam PC")}\nPack {p.PackLock[..16]}…");}
            catch(Exception ex)when(ex is IOException or JsonException){AppPaths.Log("Cannot read subscription: "+ex.Message);}
        }
        Subscriptions.ItemsSource=items.Count>0?items:["No subscriptions yet. Find a crew and review its pack to get started."];
    }
    private void RefreshPacksClicked(object sender,RoutedEventArgs e)=>RefreshSubscriptions();
    private void OpenPacksClicked(object sender,RoutedEventArgs e){Directory.CreateDirectory(AppPaths.Root);Process.Start(new ProcessStartInfo("explorer.exe",AppPaths.Root){UseShellExecute=true});}
    private void ThemeChanged(object sender,SelectionChangedEventArgs e)
    {if(!ready)return;AppPaths.SaveTheme((string)((ComboBoxItem)Theme.SelectedItem).Tag);ApplyTheme();}
    private void ApplyTheme()=>Root.RequestedTheme=AppPaths.Theme switch{"dark"=>ElementTheme.Dark,"light"=>ElementTheme.Light,_=>ElementTheme.Default};
    private async Task CheckUpdates()
    {
        if(updateBusy)return;updateBusy=true;CheckUpdateButton.IsEnabled=false;
        try{UpdateStatus.Text="Checking GitHub releases…";UpdateStatus.Text=await updates.Check();DownloadUpdateButton.IsEnabled=updates.HasUpdate;RestartUpdateButton.IsEnabled=updates.ReadyToApply;}
        catch(Exception ex){UpdateStatus.Text="Update check unavailable. "+ex.Message;AppPaths.Log(ex.ToString());}
        finally{updateBusy=false;CheckUpdateButton.IsEnabled=true;}
    }
    private async void CheckUpdateClicked(object sender,RoutedEventArgs e)=>await CheckUpdates();
    private async void DownloadUpdateClicked(object sender,RoutedEventArgs e)
    {
        if(updateBusy)return;updateBusy=true;DownloadUpdateButton.IsEnabled=false;CheckUpdateButton.IsEnabled=false;UpdateProgress.Visibility=Visibility.Visible;
        try{await updates.Download(p=>DispatcherQueue.TryEnqueue(()=>UpdateProgress.Value=p),lifetime.Token);UpdateStatus.Text="Update downloaded. Restart the launcher when ready.";RestartUpdateButton.IsEnabled=updates.ReadyToApply;}
        catch(Exception ex){UpdateStatus.Text="Download stopped. "+ex.Message;DownloadUpdateButton.IsEnabled=updates.HasUpdate;}
        finally{updateBusy=false;CheckUpdateButton.IsEnabled=true;UpdateProgress.Visibility=Visibility.Collapsed;}
    }
    private async void RestartUpdateClicked(object sender,RoutedEventArgs e)
    {
        if(busy||!updates.ReadyToApply)return;
        var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,Title="Restart to update?",Content="The launcher will close briefly to install its downloaded update. Pack subscriptions stay in your data folder.",PrimaryButtonText="Restart",CloseButtonText="Later",DefaultButton=ContentDialogButton.Close};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary)updates.Apply();
    }
    private async Task Snapshot(string path)
    {
        await Task.Delay(400);var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Root);
        var pixels=await bitmap.GetPixelsAsync();
        var file=await StorageFile.GetFileFromPathAsync(path);
        using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());await encoder.FlushAsync();
    }
    private async Task SelfTest(string folder)
    {
        Directory.CreateDirectory(folder);var checks=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            await SelectLobby();Check(selectedPlan?.Edition=="meta","Native Meta edition selection");
            Check(shellIcon.Verify(),"Window taskbar identity and both icon sizes registered");
            Edition.SelectedIndex=1;await SelectLobby();Check(selectedPlan?.Edition=="steam","Native Steam edition selection");
            var plan=selectedPlan!;await cache.Subscribe(plan,plan.PackLock,OpenExample,lifetime.Token);Check(await cache.IsCached(plan),"Launcher cache subscription");
            await cache.PreparePreviewJoin(plan,selectedLobby!,RefreshExample,lifetime.Token);checks.Add("Preview join preparation explicitly unconnected");
            Search.Text="Evening";Filter();Check(Lobbies.Items.Count==1,"Native lobby search");Search.Text="";Filter();
            await Task.Delay(150);Lobbies.SelectedIndex=0;await Task.Delay(150);await SelectLobby();
            if(selectedPlan==null)throw new InvalidOperationException($"Selection state: index={Lobbies.SelectedIndex}; count={Lobbies.Items.Count}; type={Lobbies.SelectedItem?.GetType().FullName}; query={Search.Text}; status={Status.Message}");
            checks.Add("Pack review remains populated after search");
            Root.RequestedTheme=ElementTheme.Dark;var dark=Path.Combine(folder,"winui-dark.png");File.WriteAllBytes(dark,[]);await Snapshot(dark);checks.Add("Native dark theme rendered");
            Root.RequestedTheme=ElementTheme.Light;var light=Path.Combine(folder,"winui-light.png");File.WriteAllBytes(light,[]);await Snapshot(light);checks.Add("Native light theme rendered");
            ApplyTheme();AppWindow.Resize(new Windows.Graphics.SizeInt32(1000,1000));await Task.Delay(300);AdaptLayout();Check(Grid.GetRow(PackPane)==1,"Compact layout stacks panels");
            var compact=Path.Combine(folder,"winui-compact.png");File.WriteAllBytes(compact,[]);await Snapshot(compact);
            await File.WriteAllTextAsync(Path.Combine(folder,"native-test.json"),JsonSerializer.Serialize(new{success=true,checks,version=AppPaths.Version,isInstalled=updates.IsInstalled,exe=Environment.ProcessPath,data=AppPaths.Root,framework=typeof(Window).Assembly.FullName,viewport=new{width=Root.ActualWidth,height=Root.ActualHeight,scale=Root.XamlRoot.RasterizationScale}},PackJson.Options));
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(folder,"native-test.json"),JsonSerializer.Serialize(new{success=false,checks,error=ex.ToString()},PackJson.Options));}
        Close();
    }
    private async Task VerifyUpdate(string output)
    {
        try{var status=await updates.Check();await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{installed=updates.IsInstalled,version=updates.InstalledVersion,hasUpdate=updates.HasUpdate,status},PackJson.Options));}
        catch(Exception ex){await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{error=ex.ToString()},PackJson.Options));}
        Close();
    }
}

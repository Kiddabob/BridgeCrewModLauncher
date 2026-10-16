using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Velopack.Locators;
using Velopack.Windows;

namespace BridgeCrew.Launcher;

// Register the window itself as well as its shortcuts: Explorer may retain an
// executable's previous icon even after that executable has been replaced.
internal sealed class WindowShellIcon : IDisposable
{
    private const string InstalledId = "velopack.Kidda.BridgeCrewModLauncher";
    private readonly nint window;
    private readonly string appId, iconPath;
    private nint smallIcon, largeIcon;

    public WindowShellIcon(nint window)
    {
        this.window = window;
        var locator = VelopackLocator.Current;
        var installed = locator.AppId == AppPaths.AppId && locator.CurrentlyInstalledVersion != null && !locator.IsPortable;
        appId = installed ? InstalledId : InstalledId + ".Preview";
        var source = Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.ico");
        iconPath = source;
        if(installed && locator.RootAppDir is { } root)
        {
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant()[..12];
            iconPath = Path.Combine(root,$"Starfleet-Spanner-{digest}.ico");
            if(!File.Exists(iconPath))File.Copy(source,iconPath);
            RefreshOwnedShortcuts(root);
        }

        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
        var store = OpenStore();
        try
        {
            Set(store,3,iconPath+",0");
            Set(store,2,'"'+Environment.ProcessPath+'"');
            Set(store,4,"Bridge Crew Mod Launcher");
            Set(store,5,appId);
            store.Commit();
        }
        finally { Marshal.ReleaseComObject(store); }
        smallIcon = LoadImage(0,iconPath,1,GetSystemMetrics(49),GetSystemMetrics(50),0x10);
        largeIcon = LoadImage(0,iconPath,1,GetSystemMetrics(11),GetSystemMetrics(12),0x10);
        if(smallIcon == 0 || largeIcon == 0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        SendMessage(window,0x80,0,smallIcon);
        SendMessage(window,0x80,1,largeIcon);
    }

    private void RefreshOwnedShortcuts(string root)
    {
        var contentExe = Path.Combine(AppContext.BaseDirectory,"BridgeCrew.Launcher.exe");
        var stubExe = Path.Combine(root,"BridgeCrew.Launcher.exe");
        foreach(var folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.Programs)})
        {
            var path = Path.Combine(folder,"Bridge Crew Mod Launcher.lnk");
            if(!File.Exists(path))continue;
            try
            {
                using var link = new ShellLink(path);
                if(!SamePath(link.Target,contentExe) && !SamePath(link.Target,stubExe))continue;
                if(SamePath(link.IconPath,iconPath))continue;
                link.IconPath = iconPath; link.IconIndex = 0;
                link.Save();
                SHChangeNotify(0x2000,5,path,0);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or COMException)
            { AppPaths.Log("Shortcut icon refresh: "+ex.Message); }
        }
    }
    private static bool SamePath(string? left,string right) => !string.IsNullOrEmpty(left) && string.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);
    public bool Verify()
    {
        var store = OpenStore();
        try { return Read(store,5)==appId && Read(store,3)==iconPath+",0" && SendMessage(window,0x7F,0,0)==smallIcon && SendMessage(window,0x7F,1,0)==largeIcon; }
        finally { Marshal.ReleaseComObject(store); }
    }
    private IPropertyStore OpenStore()
    {
        var id=typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window,ref id,out var store));
        return store;
    }
    private static void Set(IPropertyStore store,uint property,string text)
    {
        var key=new PropertyKey(property);var value=new PropVariant{Type=31,Pointer=Marshal.StringToCoTaskMemUni(text)};
        try { store.SetValue(ref key,ref value); } finally { PropVariantClear(ref value); }
    }
    private static string? Read(IPropertyStore store,uint property)
    {
        var key=new PropertyKey(property);store.GetValue(ref key,out var value);
        try { return value.Type==31?Marshal.PtrToStringUni(value.Pointer):null; } finally { PropVariantClear(ref value); }
    }
    public void Dispose()
    {
        if(smallIcon!=0){DestroyIcon(smallIcon);smallIcon=0;}
        if(largeIcon!=0){DestroyIcon(largeIcon);largeIcon=0;}
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(uint property)
    { public Guid Format=new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");public uint Id=property; }
    [StructLayout(LayoutKind.Explicit,Size=24)] private struct PropVariant
    { [FieldOffset(0)]public ushort Type;[FieldOffset(8)]public nint Pointer; }
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IPropertyStore
    {
        void GetCount(out uint count);void GetAt(uint index,out PropertyKey key);void GetValue(ref PropertyKey key,out PropVariant value);
        void SetValue(ref PropertyKey key,ref PropVariant value);void Commit();
    }
    [DllImport("shell32.dll")]private static extern int SHGetPropertyStoreForWindow(nint window,ref Guid id,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern void SHChangeNotify(uint eventId,uint flags,string path,nint unused);
    [DllImport("ole32.dll")]private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint LoadImage(nint instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
}

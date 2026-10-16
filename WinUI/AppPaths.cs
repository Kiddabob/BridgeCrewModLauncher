using System.Reflection;
using System.Text.Json;

namespace BridgeCrew.Launcher;

public static class AppPaths
{
    public const string Repository = "https://github.com/Kiddabob/BridgeCrewModLauncher";
    public const string AppId = "Kidda.BridgeCrewModLauncher";
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kidda", "BridgeCrewModLauncher");
    public static string Version => typeof(AppPaths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    public static string Theme { get; private set; } = "system";
    public static void LoadSettings()
    {
        try { var settings = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Root,"settings.json"))); Theme = settings?.GetValueOrDefault("theme") ?? "system"; }
        catch (Exception e) when (e is IOException or JsonException) { Theme = "system"; }
    }
    public static void SaveTheme(string theme)
    {
        Theme = theme; Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root,"settings.json"), JsonSerializer.Serialize(new { theme }));
    }
    public static void Log(string message)
    {
        try { Directory.CreateDirectory(Root); File.AppendAllText(Path.Combine(Root,"launcher.log"), DateTimeOffset.Now + " " + message + Environment.NewLine); }
        catch (IOException) { }
    }
}

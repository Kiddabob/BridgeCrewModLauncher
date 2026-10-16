using Velopack;
using Velopack.Sources;

namespace BridgeCrew.Launcher;

public sealed class UpdateService
{
    private readonly UpdateManager manager = new(new GithubSource(AppPaths.Repository, null, false));
    private UpdateInfo? available;
    public bool IsInstalled => manager.IsInstalled;
    public bool ReadyToApply => manager.UpdatePendingRestart != null;
    public string InstalledVersion => manager.CurrentVersion?.ToString() ?? AppPaths.Version;
    public async Task<string> Check()
    {
        if (!manager.IsInstalled) return "Install the release build to receive app updates.";
        available = await manager.CheckForUpdatesAsync();
        return available == null ? $"You’re up to date · {InstalledVersion}" : $"Version {available.TargetFullRelease.Version} is available.";
    }
    public bool HasUpdate => available != null;
    public async Task Download(Action<int> progress, CancellationToken ct)
    {
        if (available == null) throw new InvalidOperationException("Check for an update first.");
        await manager.DownloadUpdatesAsync(available, progress, ct);
    }
    public void Apply(params string[] restartArgs) => manager.ApplyUpdatesAndRestart(null,restartArgs);
}

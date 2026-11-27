using ImageStudio.Core;
using Velopack;
using Velopack.Sources;

namespace ImageStudio.App;

/// <summary>
/// Auto-update from this repo's GitHub Releases (Velopack packages built by release.ps1).
/// Only an installed copy (Setup.exe) can update; a dev build from bin\ reports IsInstalled = false.
/// Set VAS_UPDATE_SOURCE to a local folder or URL to test updates without publishing.
/// </summary>
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/designloves2/Vertex-AI-Studio";

    readonly UpdateManager _mgr;
    UpdateInfo? _pending;

    public AppUpdater()
    {
        var overrideSource = Environment.GetEnvironmentVariable("VAS_UPDATE_SOURCE");
        IUpdateSource source = string.IsNullOrWhiteSpace(overrideSource) ? new GithubSource(RepoUrl, null, false)
            : Directory.Exists(overrideSource) ? new SimpleFileSource(new DirectoryInfo(overrideSource))
            : new SimpleWebSource(overrideSource);
        _mgr = new UpdateManager(source);
    }

    public bool IsInstalled => _mgr.IsInstalled;

    public string CurrentVersion =>
        _mgr.CurrentVersion?.ToString()
        ?? (typeof(AppUpdater).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?");

    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>Returns the newer version, or null when up to date / not installed.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!_mgr.IsInstalled) return null;
        _pending = await _mgr.CheckForUpdatesAsync();
        return PendingVersion;
    }

    public async Task DownloadAsync(Action<int>? progress = null)
    {
        if (_pending == null) throw new InvalidOperationException(L.T("확인된 업데이트가 없습니다."));
        await _mgr.DownloadUpdatesAsync(_pending, progress);
    }

    /// <summary>Exits the app, applies the downloaded update and restarts it.</summary>
    public void ApplyAndRestart()
    {
        if (_pending != null) _mgr.ApplyUpdatesAndRestart(_pending);
    }
}

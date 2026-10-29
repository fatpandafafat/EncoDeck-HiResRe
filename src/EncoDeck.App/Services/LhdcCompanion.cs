// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.InteropServices;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Paths;
using EncoDeck.Platform.Updates;
namespace EncoDeck.App.Services;

/// <summary>Why the LHDC component cannot be removed right now.</summary>
internal sealed class LhdcInUseException(string message) : InvalidOperationException(message);

internal static class LhdcCompanion
{
    private static readonly AppPaths Paths = new();
    private static readonly string LastCheckFile = Path.Combine(Paths.UpdatesDirectory, "component-lhdc-last-check.txt");

    /// <summary>The LHDC component release pinned by this app; independent of the app version.</summary>
    public static ComponentInstaller Component { get; } = LhdcComponent.CreateInstaller(new HttpClient(), Path.Combine(Paths.Root, "Components", "LHDC"));

    /// <summary>A later LHDC component release found online (null until checked, or when there is none).</summary>
    public static ComponentRelease? OnlineRelease { get; private set; }

    public static string? RuntimeDirectory => Component.Installed?.Directory ?? BundledRuntimeDirectory();

    /// <summary>An offline installer (build-installer.ps1 -WithLhdc) puts the runtime next to the app.</summary>
    private static string? BundledRuntimeDirectory()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "LHDC");
        return LhdcComponent.IsRuntime(directory) ? directory : null;
    }

    /// <summary>Only a downloaded component can be updated or removed; a bundled one belongs to the installer.</summary>
    public static bool IsDownloaded => Component.Installed is not null;

    public static string StatusKey => PackageIdentity.IsPackaged ? "Lhdc_Store"
        : RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64 ? "Lhdc_Architecture"
        : !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ? "Lhdc_Windows"
        : RuntimeDirectory is not null ? "Lhdc_Ready"
        : Component.Newest(OnlineRelease) is not null ? "Lhdc_Missing" : "Lhdc_Unpublished";
    public static bool IsReady => StatusKey == "Lhdc_Ready";

    /// <summary>The release an update installs: this app's pin or a later one found online, when newer than the installed one.</summary>
    public static ComponentRelease? UpdateTarget => IsReady ? Component.UpdateTarget(OnlineRelease) : null;

    public static bool UpdateAvailable => UpdateTarget is not null;

    public static bool CanDownload => StatusKey == "Lhdc_Missing" || UpdateAvailable;

    /// <summary>What a download installs now: the update, or the newest release for a first install.</summary>
    private static ComponentRelease? DownloadTarget => UpdateTarget ?? Component.Newest(OnlineRelease);

    /// <summary>Download size of what a download installs, in MB.</summary>
    public static double PackageMegabytes => (DownloadTarget is { } target ? Component.PackageOf(target)?.Size ?? 0 : 0) / 1048576.0;

    public static Task DownloadAsync(IProgress<UpdateDownloadProgress> progress, CancellationToken ct) =>
        DownloadTarget is { } target ? Component.InstallAsync(target, progress, ct) : Component.InstallAsync(progress, ct);

    /// <summary>
    /// Looks for a later LHDC component release (at most once a day unless <paramref name="force"/>). Only releases
    /// for this app's backend interface and version count; the result stays for the session.
    /// </summary>
    public static async Task<ComponentUpdateOutcome?> CheckForUpdateAsync(bool includePrerelease, bool force, CancellationToken ct = default)
    {
        if (PackageIdentity.IsPackaged) return null;
        if (!force && DateTimeOffset.UtcNow - LastChecked() < TimeSpan.FromHours(24)) return null;
        var app = SemanticVersion.FromAssembly(typeof(LhdcCompanion).Assembly);
        using var http = new HttpClient();
        string floor = Component.Installed is { } installed && ComponentVersion.IsNewer(installed.Version, Component.Release.Version) ? installed.Version : Component.Release.Version;
        var result = await new ComponentUpdateClient(http, app).CheckAsync(ComponentCatalog.LhdcId, LhdcComponent.Interface, floor, includePrerelease, ct).ConfigureAwait(false);
        if (result.Outcome is ComponentUpdateOutcome.Available or ComponentUpdateOutcome.UpToDate)
        {
            OnlineRelease = result.Release;
            RecordCheck();
        }
        return result.Outcome;
    }

    private static DateTimeOffset LastChecked()
    {
        try { return DateTimeOffset.Parse(File.ReadAllText(LastCheckFile).Trim(), CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return DateTimeOffset.MinValue; }
    }

    private static void RecordCheck()
    {
        try
        {
            Directory.CreateDirectory(Paths.UpdatesDirectory);
            File.WriteAllText(LastCheckFile, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes the downloaded component. Refused while LHDC may still be in use (the service is not disabled, or an
    /// audio device is bound to the LHDC driver): restoring Windows audio needs these files.
    /// </summary>
    public static async Task UninstallAsync()
    {
        if (!IsDownloaded) return;
        if (LhdcComponent.ServiceEnabled()) throw new LhdcInUseException(Loc.Get("Lhdc_UninstallInUse"));
        if (RuntimeDirectory is { } runtime)
        {
            try
            {
                var inventory = await new LhdcBackend(runtime, Path.Combine(Component.Root, LhdcComponent.ResultsFolder)).QueryAsync(null, "inspect").ConfigureAwait(false);
                var bound = inventory["devices"]?.AsArray().Where(d => d?["service"]?.GetValue<string>() == "lhdc-transport")
                    .Select(d => d?["name"]?.GetValue<string>() ?? "").ToArray() ?? [];
                if (bound.Length > 0) throw new LhdcInUseException(Loc.Format("Lhdc_UninstallBound", string.Join("、", bound.Where(n => n.Length > 0))));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or InvalidOperationException and not LhdcInUseException
                or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                // The backend cannot answer (e.g. damaged files): the service check above is all there is.
            }
        }
        if (!Component.Uninstall()) throw new IOException(Loc.Get("Lhdc_UninstallBusy"));
        OnlineRelease = null;
    }

    public static LhdcBackend Backend => IsReady
        ? new(RuntimeDirectory!, Path.Combine(Component.Root, LhdcComponent.ResultsFolder))
        : throw new InvalidOperationException(Loc.Get(StatusKey));
}

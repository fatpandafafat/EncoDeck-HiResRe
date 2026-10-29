// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Platform.Packaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;

namespace EncoDeck.App.Views;

/// <summary>Version, developer, sponsorship, acknowledgements and legal notices.</summary>
public sealed partial class AboutPage : Page
{
    private const string NoticesUrl = "https://github.com/TIANLI0/EncoDeck/blob/main/THIRD_PARTY_NOTICES.md";

    private DispatcherQueueTimer? _copiedTimer;

    public AboutPage()
    {
        InitializeComponent();
        CopyInfo.Content = Loc.Get("About_CopyInfo");
        Unloaded += (_, _) => _copiedTimer?.Stop();
    }

    public SettingsViewModel ViewModel => App.Instance.Settings;

    /// <summary>What an issue report needs: app version and build, Windows build, architecture and runtime. Not localized, so it pastes the same everywhere.</summary>
    public string SystemInfo => $"EncoDeck {ViewModel.Version} ({(PackageIdentity.IsPackaged ? "Microsoft Store" : "GitHub")}, {Arch(RuntimeInformation.ProcessArchitecture)}) · "
        + $"Windows {WindowsBuild()} {Arch(RuntimeInformation.OSArchitecture)} · .NET {Environment.Version}";

    private static string Arch(Architecture architecture) => architecture.ToString().ToLowerInvariant();

    private static string WindowsBuild()
    {
        var version = Environment.OSVersion.Version;
        try
        {
            // The update revision (UBR) tells cumulative updates apart; Environment.OSVersion leaves it out.
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null) is int ubr)
                return $"{version.Major}.{version.Minor}.{version.Build}.{ubr}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return version.ToString(3);
    }

    private void OnCopySystemInfo(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(SystemInfo);
            Clipboard.SetContent(package);
        }
        catch (COMException)
        {
            // Another app holds the clipboard; the text stays on screen to copy by hand.
            return;
        }
        CopyInfo.Content = Loc.Get("About_Copied");
        if (_copiedTimer is null)
        {
            _copiedTimer = DispatcherQueue.CreateTimer();
            _copiedTimer.Interval = TimeSpan.FromSeconds(2);
            _copiedTimer.IsRepeating = false;
            _copiedTimer.Tick += (_, _) => CopyInfo.Content = Loc.Get("About_CopyInfo");
        }
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private async void OnOpenNotices(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        try
        {
            if (File.Exists(path))
            {
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // No app is associated with .md files: fall back to the online copy.
        }
        await Windows.System.Launcher.LaunchUriAsync(new Uri(NoticesUrl));
    }
}

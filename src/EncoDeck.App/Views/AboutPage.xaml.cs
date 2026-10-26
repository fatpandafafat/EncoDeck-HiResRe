// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

/// <summary>Version, developer, sponsorship and legal notices.</summary>
public sealed partial class AboutPage : Page
{
    public AboutPage() => InitializeComponent();

    public SettingsViewModel ViewModel => App.Instance.Settings;

    private void OnOpenNotices(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        if (File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}

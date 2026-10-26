// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    public SettingsViewModel ViewModel => App.Instance.Settings;

    private void OnPreviewPopup(object sender, RoutedEventArgs e) =>
        App.Instance.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(App.Instance.Host.Settings.Current.PopupDurationSeconds));
}

// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Services;

/// <summary>
/// The labs cards' message bar and dialogs. A result, warning or error replaces the previous one in the card's
/// InfoBar instead of overwriting its status line, and a failure never leaves the card half-updated.
/// </summary>
public static class CardNotice
{
    // A backend can fail with a whole stderr dump; the bar shows its start, the log keeps the rest.
    private const int MaxMessageLength = 600;

    private static bool _dialogOpen;

    public static void Show(InfoBar bar, InfoBarSeverity severity, string? message)
    {
        string text = (message ?? "").Trim();
        if (text.Length > MaxMessageLength) text = text[..MaxMessageLength].TrimEnd() + "…";
        bar.Severity = severity;
        bar.Message = text;
        bar.IsOpen = text.Length > 0;
        // A closed InfoBar still takes a StackPanel spacing slot; collapse it until the next message.
        bar.Visibility = bar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        bar.Closed -= OnClosed;
        bar.Closed += OnClosed;
    }

    private static void OnClosed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    /// <summary>Shows an unexpected failure and logs it with its stack, so the card stays usable and the cause is kept.</summary>
    public static void ShowError(InfoBar bar, Exception ex, string? message = null)
    {
        App.Instance.Host.Loggers.CreateLogger(nameof(CardNotice)).LogWarning(ex, "Labs operation failed");
        Show(bar, InfoBarSeverity.Error, message ?? ex.Message);
    }

    public static void Hide(InfoBar bar)
    {
        bar.IsOpen = false;
        bar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// One dialog at a time across the app (a second ContentDialog throws); None when it could not be shown,
    /// e.g. while another dialog is open or the card has left the window.
    /// </summary>
    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, XamlRoot? root)
    {
        if (_dialogOpen || root is null) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = root;
            return await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}

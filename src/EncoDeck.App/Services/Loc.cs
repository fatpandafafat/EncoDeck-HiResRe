// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace EncoDeck.App.Services;

/// <summary>Localized strings for code (XAML uses x:Uid). Falls back to the key when a string is missing.</summary>
internal static class Loc
{
    private static ResourceLoader? _loader;

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            string value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Gives a window's content the UI language chosen in Settings. WinUI keeps the system language for
    /// XAML text otherwise, and Japanese on Chinese Windows would render with Chinese glyphs and fonts.
    /// </summary>
    public static void ApplyLanguage(Window window)
    {
        try
        {
            string language = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrEmpty(language) && window.Content is FrameworkElement root) root.Language = language;
        }
        catch (Exception)
        {
            // No override in this configuration: the system language is used.
        }
    }
}

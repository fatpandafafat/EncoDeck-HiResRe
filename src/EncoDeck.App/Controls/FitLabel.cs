// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Controls;

/// <summary>
/// Labels of the equal-width noise mode and level buttons: up to two centred lines broken between words
/// only (never "Transpare/ncy"). A word a little too wide for its column shrinks to fit (to at most 80 %);
/// Japanese or Chinese text, which has no spaces, that wraps is split into two even lines instead of
/// leaving one character alone ("外音取り込/み").
/// </summary>
internal static class FitLabel
{
    private const double MinScale = 0.8;
    private const string NoLineStart = "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶー、。・）」』";

    public static TextBlock Create(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };
        Attach(label);
        return label;
    }

    /// <summary>Fits <paramref name="label"/> (styled like <see cref="Create"/>) whenever its column width or text changes.</summary>
    public static void Attach(TextBlock label)
    {
        double baseSize = double.NaN, fittedWidth = double.NaN, oneLine = 0;
        string? text = null, shown = null;
        label.LayoutUpdated += (_, _) =>
        {
            double available = LayoutInformation.GetAvailableSize(label).Width;
            if (double.IsNaN(baseSize)) baseSize = label.FontSize;
            if (double.IsInfinity(available) || available <= 0) return;
            if (label.Text != shown) text = label.Text; // set from outside (x:Uid, a new level name)
            if (text is null) return;

            if (available != fittedWidth || label.Text != shown)
            {
                // New width or text: lay out the original text first, then look at how it wrapped.
                fittedWidth = available;
                var probe = new TextBlock { FontFamily = label.FontFamily, FontWeight = label.FontWeight, FontSize = baseSize, Language = label.Language };
                double size = baseSize;
                if (!IsSpaceless(text))
                {
                    double widest = 0;
                    foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        probe.Text = word;
                        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                        widest = Math.Max(widest, probe.DesiredSize.Width);
                    }
                    double room = available - 2; // a shrunk word measured exactly at the column width can still wrap
                    if (widest > available && room / widest >= MinScale) size = Math.Floor(baseSize * room / widest * 2) / 2;
                }
                probe.Text = text;
                probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                oneLine = probe.DesiredSize.Height;
                label.FontSize = size;
                shown = label.Text = text;
                return;
            }

            if (IsSpaceless(text) && label.Text == text && text.Length > 2 && label.ActualHeight > oneLine * 1.5)
            {
                int split = (text.Length + 1) / 2;
                if (NoLineStart.Contains(text[split]) && split + 1 < text.Length) split++;
                shown = label.Text = text[..split] + "\n" + text[split..];
            }
        };
    }

    private static bool IsSpaceless(string text) => !text.Contains(' ') && text.Any(c => c >= '　');
}

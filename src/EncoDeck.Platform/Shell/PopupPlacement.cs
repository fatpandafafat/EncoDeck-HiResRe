// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Configuration;

namespace EncoDeck.Platform.Shell;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Physical-pixel rectangle.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public static PixelRect FromLtrb(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
}

public readonly record struct PixelPoint(int X, int Y);

/// <summary>Pure placement math for tray-anchored windows (unit tested).</summary>
public static class PopupPlacement
{
    /// <summary>
    /// Infers the taskbar edge from how the work area is inset within the monitor.
    /// Returns null when the taskbar is auto-hidden or on another monitor.
    /// </summary>
    public static TaskbarEdge? InferEdge(PixelRect monitor, PixelRect workArea)
    {
        if (workArea.Bottom < monitor.Bottom) return TaskbarEdge.Bottom;
        if (workArea.Y > monitor.Y) return TaskbarEdge.Top;
        if (workArea.X > monitor.X) return TaskbarEdge.Left;
        if (workArea.Right < monitor.Right) return TaskbarEdge.Right;
        return null;
    }

    /// <summary>
    /// Positions a <paramref name="width"/>×<paramref name="height"/> window next to the taskbar,
    /// aligned with <paramref name="anchor"/> (usually the tray click point) when given, and always
    /// fully inside <paramref name="workArea"/>.
    /// </summary>
    public static PixelRect PlaceNearTaskbar(PixelRect workArea, TaskbarEdge edge, PixelPoint? anchor, int width, int height, int margin)
    {
        width = Math.Min(width, Math.Max(1, workArea.Width - 2 * margin));
        height = Math.Min(height, Math.Max(1, workArea.Height - 2 * margin));

        int minX = workArea.X + margin;
        int maxX = workArea.Right - margin - width;
        int minY = workArea.Y + margin;
        int maxY = workArea.Bottom - margin - height;

        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                y = minY;
                x = anchor is { } at ? at.X - width / 2 : maxX;
                break;
            case TaskbarEdge.Left:
                x = minX;
                y = anchor is { } al ? al.Y - height / 2 : maxY;
                break;
            case TaskbarEdge.Right:
                x = maxX;
                y = anchor is { } ar ? ar.Y - height / 2 : maxY;
                break;
            default:
                y = maxY;
                x = anchor is { } ab ? ab.X - width / 2 : maxX;
                break;
        }

        x = Math.Clamp(x, minX, Math.Max(minX, maxX));
        y = Math.Clamp(y, minY, Math.Max(minY, maxY));
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Positions a window at a corner or edge centre of <paramref name="workArea"/> (the user's
    /// popup position), <paramref name="margin"/> pixels in, always fully inside the work area.
    /// <see cref="PopupPosition.NearTray"/> places it next to the notification area.
    /// </summary>
    public static PixelRect PlaceAt(PopupPosition position, PixelRect workArea, TaskbarEdge edge, int width, int height, int margin)
    {
        if (position == PopupPosition.NearTray) return PlaceNearTaskbar(workArea, edge, anchor: null, width, height, margin);

        width = Math.Min(width, Math.Max(1, workArea.Width - 2 * margin));
        height = Math.Min(height, Math.Max(1, workArea.Height - 2 * margin));
        int left = workArea.X + margin;
        int right = workArea.Right - margin - width;
        int centerX = workArea.X + (workArea.Width - width) / 2;
        int top = workArea.Y + margin;
        int bottom = workArea.Bottom - margin - height;
        return position switch
        {
            PopupPosition.BottomLeft => new PixelRect(left, bottom, width, height),
            PopupPosition.BottomCenter => new PixelRect(centerX, bottom, width, height),
            PopupPosition.TopLeft => new PixelRect(left, top, width, height),
            PopupPosition.TopCenter => new PixelRect(centerX, top, width, height),
            PopupPosition.TopRight => new PixelRect(right, top, width, height),
            _ => new PixelRect(right, bottom, width, height),
        };
    }

    /// <summary>
    /// Places a <paramref name="height"/> pixels tall panel on top of Windows 11 Quick Settings.
    /// <paramref name="cards"/> is the Quick Settings window region: its cards plus the shell's
    /// <paramref name="margin"/> around them. The panel lines up with the cards' edges and keeps the
    /// gap the shell keeps between its own cards. When there is no room above (a short screen, the
    /// media card, a tall sub-page, or the noise level row appearing) it goes beside the cards
    /// instead, on the side facing the middle of the screen, bottoms aligned. Null when it fits
    /// neither way.
    /// </summary>
    public static PixelRect? PlaceAboveQuickSettings(PixelRect cards, PixelRect workArea, int height, int margin)
    {
        int width = QuickSettingsPanelWidth(cards, margin);
        if (width <= 0 || height <= 0) return null;
        int y = cards.Y - height;
        if (y >= workArea.Y + margin) return new PixelRect(cards.X + margin, y, width, height);

        int bottom = cards.Bottom - margin;
        int top = Math.Max(workArea.Y + margin, bottom - height);
        if (bottom - top < height) return null;
        bool onRight = cards.X + cards.Width / 2 >= workArea.X + workArea.Width / 2;
        int x = onRight ? cards.X - width : cards.Right;
        if (x < workArea.X + margin || x + width > workArea.Right - margin) return null;
        return new PixelRect(x, top, width, height);
    }

    /// <summary>
    /// Width of the panel on top of Quick Settings: the cards' column, i.e. the window region
    /// without the shell's margin on either side. It follows <paramref name="cards"/>, so the
    /// panel stays as wide as Quick Settings when the shell's width changes (another display,
    /// scaling factor or Windows build).
    /// </summary>
    public static int QuickSettingsPanelWidth(PixelRect cards, int margin) => cards.Width - 2 * margin;

    /// <summary>True when the popup sits along the top of the screen (it then slides down, not up).</summary>
    public static bool IsTop(PopupPosition position, TaskbarEdge edge) =>
        position is PopupPosition.TopLeft or PopupPosition.TopCenter or PopupPosition.TopRight
        || (position == PopupPosition.NearTray && edge == TaskbarEdge.Top);
}

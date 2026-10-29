// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Core.State;

/// <summary>Which LE Audio hint replaces the connection line, decided from bond, link and wear evidence.</summary>
public static class LeAudioStatus
{
    /// <summary>
    /// One earbud of an LE pair is connected while the other should be. Wearing one earbud while the
    /// other charges is ordinary use, so an earbud reported in the case never warns. With
    /// <paramref name="warnWithoutWear"/> (OPPO: a missing other half means the switch is incomplete)
    /// it warns unless shown otherwise; other brands warn only for a bonded other earbud that is
    /// reported out of the case as well. Wear restored from the cache is not evidence.
    /// </summary>
    public static bool IsMissingEarbud(DeviceState state, bool warnWithoutWear)
    {
        if (!state.IsLeAudio || state.LeAudioLinks.ConnectedEarbuds != 1 || state.Profile?.Form == DeviceForm.Headphones) return false;
        var wear = state.Wear is { Source: not StateSource.Cache } live ? live.Value : null;
        if (wear is { LeftInCase: true } or { RightInCase: true }) return false;
        return warnWithoutWear || state.LeAudioLinks.SecondaryBonded && wear is { LeftInCase: false, RightInCase: false };
    }

    /// <summary>
    /// Switched to LE Audio but no LE link is up (e.g. after the case reopened). When control needs that
    /// link (<paramref name="controlNeedsLeAudio"/>, OPPO) it waits for LC3 whatever Classic does; earbuds
    /// controlled over Classic show their real state while Classic is up and wait only with no link at all.
    /// </summary>
    public static bool IsWaitingForLeAudio(DeviceState state, bool controlNeedsLeAudio) =>
        state.IsLeAudioBonded && !state.IsLeAudio && state.Connection != DeviceConnectionState.ControlReady
        && (controlNeedsLeAudio || !state.Connection.IsLinkUp());
}

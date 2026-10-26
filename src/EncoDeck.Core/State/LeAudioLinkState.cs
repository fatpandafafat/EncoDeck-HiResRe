// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Bond and ACL evidence only; this does not assert that either earbud has a streaming CIS.</summary>
public sealed record LeAudioLinkState(bool PrimaryBonded, bool PrimaryConnected, bool SecondaryBonded, bool SecondaryConnected)
{
    public static LeAudioLinkState Empty { get; } = new(false, false, false, false);
    public int BondedEarbuds => (PrimaryBonded ? 1 : 0) + (SecondaryBonded ? 1 : 0);
    public int ConnectedEarbuds => (PrimaryConnected ? 1 : 0) + (SecondaryConnected ? 1 : 0);
    public bool BothConnected => PrimaryConnected && SecondaryConnected;
}

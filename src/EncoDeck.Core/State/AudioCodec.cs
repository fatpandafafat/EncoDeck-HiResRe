// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Classic (A2DP) codec the earbuds report for the current audio link.</summary>
public enum AudioCodec
{
    Sbc,
    Aac,
    Ldac,
    AptX,
    AptXHd,
    AptXAdaptive,
    Lhdc,
}

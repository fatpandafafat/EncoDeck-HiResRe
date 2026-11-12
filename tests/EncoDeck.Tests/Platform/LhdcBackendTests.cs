// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Lhdc;

namespace EncoDeck.Tests.Platform;

public sealed class LhdcBackendTests
{
    [Theory]
    [InlineData(22050, 24, 400)]
    [InlineData(48000, 32, 400)]
    [InlineData(48000, 24, -1)]
    public void RejectsUnsupportedFormatsBeforeStartingDeployment(int rate, int bits, int kbps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LhdcBackend.ModeCommand("C:/runtime", "BTHENUM\\device", true, new(rate, bits, kbps), "C:/result.json"));

    [Theory]
    // 44.1 kHz uses 240 and 480 where every other rate uses 256 and 500, so the
    // bitrates of the other rate must not be accepted at 44.1 kHz.
    [InlineData(44100, 240, true)]
    [InlineData(44100, 480, true)]
    [InlineData(44100, 256, false)]
    [InlineData(44100, 500, false)]
    [InlineData(48000, 256, true)]
    [InlineData(48000, 500, true)]
    [InlineData(48000, 240, false)]
    [InlineData(48000, 480, false)]
    [InlineData(96000, 400, true)]
    [InlineData(192000, 1000, true)]
    public void AcceptsOnlyTheBitratesTheEncoderHasAtThatSampleRate(int rate, int kbps, bool allowed) =>
        Assert.Equal(allowed, LhdcProfile.AllowsBitrate(rate, kbps));

    [Theory]
    [InlineData(44100, 24, 240)]
    [InlineData(48000, 24, 256)]
    public void AcceptsTheRateSpecificBitrateInAProfile(int rate, int bits, int kbps)
    {
        var profile = new LhdcProfile(rate, bits, kbps);
        profile.Validate();
    }

    [Theory]
    [InlineData(44100, 24, 500)]
    [InlineData(48000, 24, 480)]
    public void RejectsTheOtherRatesBitrateInAProfile(int rate, int bits, int kbps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LhdcProfile(rate, bits, kbps).Validate());

    [Fact]
    public void QuotesDeviceAndPathsAsLiterals()
    {
        string command = LhdcBackend.ModeCommand("C:/O'Brien/runtime", "BTHENUM\\device'; Write-Host injected; 'x", true, new(), "C:/O'Brien/result.json");
        Assert.Contains("C:/O''Brien", command);
        Assert.Contains("-DeviceInstance 'BTHENUM\\device''; Write-Host injected; ''x'", command);
        Assert.Contains("-Mode LHDC", command);
    }

    [Fact]
    public async Task QueryCannotInvokeMutatingCommands()
    {
        var backend = new LhdcBackend("missing", "missing");
        await Assert.ThrowsAsync<ArgumentException>(() => backend.QueryAsync(null, "hires", "on"));
    }

    [Fact]
    public async Task TheLiveBitrateQueryIsAllowed()
    {
        // The panel polls the published bitrate while the earbuds play. The
        // command reads the registry alone, so it does not query the radio the
        // stream runs on, and it has to stay reachable for the read-only query.
        var backend = new LhdcBackend("missing", "missing");
        await Assert.ThrowsAsync<IOException>(() => backend.QueryAsync(null, "active-bitrate"));
    }
}

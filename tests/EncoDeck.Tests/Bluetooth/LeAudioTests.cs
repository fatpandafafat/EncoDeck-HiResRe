// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Core.Commands;

namespace EncoDeck.Tests.Bluetooth;

/// <summary>LE Audio detection and pairing inputs, from the Enco X4 (2026-10-24, values anonymised).</summary>
public class LeAudioTests
{
    private const ulong Primary = 0x112233445566;

    [Fact]
    public void ConcurrentPairingOperationsCannotInterleavePrerequisites()
    {
        const ulong address = 0x112233445579;
        var first = LeAudioLink.BeginPairingOperation(address);
        Assert.Throws<InvalidOperationException>(() => LeAudioLink.BeginPairingOperation(address));
        first.Dispose();
        first.Dispose();
        using var second = LeAudioLink.BeginPairingOperation(address);
        Assert.False(LeAudioLink.IsPairingChanging(address));
    }

    [Theory]
    [InlineData(CommandStatus.Timeout)]
    [InlineData(CommandStatus.Rejected)]
    [InlineData(CommandStatus.NotConnected)]
    [InlineData(CommandStatus.Unsupported)]
    [InlineData(CommandStatus.Cancelled)]
    [InlineData(CommandStatus.TransportError)]
    public async Task MultipointNotConfirmed_StopsBeforeModeChangeOrPairing(CommandStatus status)
    {
        const ulong address = 0x112233445570;
        bool modeCalled = false;
        var result = await LeAudioPairing.SwitchToLeAudioAsync(address, null, TestContext.Current.CancellationToken,
            setLeBrSwitch: (_, _) => { modeCalled = true; return Task.FromResult(CommandResult.Ok); },
            disableMultipoint: _ => Task.FromResult(new CommandResult(status)));
        Assert.Equal(LeAudioPairingOutcome.MultipointDisableFailed, result.Outcome);
        Assert.False(modeCalled);
        Assert.False(LeAudioLink.IsPreferred(address));
        Assert.False(LeAudioLink.IsPairingChanging(address));
    }

    [Fact]
    public async Task MissingMultipointCheck_CannotBypassPrerequisite()
    {
        var result = await LeAudioPairing.SwitchToLeAudioAsync(Primary, null, TestContext.Current.CancellationToken);
        Assert.Equal(LeAudioPairingOutcome.MultipointDisableFailed, result.Outcome);
    }

    [Fact]
    public async Task MultipointFailureException_DoesNotProceedToModeOrPairing()
    {
        var result = await LeAudioPairing.SwitchToLeAudioAsync(Primary, null, TestContext.Current.CancellationToken,
            disableMultipoint: _ => throw new IOException("control disconnected"));
        Assert.Equal(LeAudioPairingOutcome.MultipointDisableFailed, result.Outcome);
    }

    [Fact]
    public async Task MultipointConfirmation_PrecedesLeModeWrite()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => LeAudioPairing.SwitchToLeAudioAsync(Primary, null,
            TestContext.Current.CancellationToken,
            setLeBrSwitch: (on, _) => { calls.Add(on ? "mode-on" : "mode-off"); throw new OperationCanceledException(); },
            disableMultipoint: _ => { calls.Add("multipoint-off-confirmed"); return Task.FromResult(CommandResult.Ok); }));
        Assert.Equal(["multipoint-off-confirmed", "mode-on"], calls);
    }

    [Fact]
    public void SecondaryLookup_UsesPairedEndpointName_WhenDeviceNameIsGeneric()
    {
        var bonds = new[]
        {
            ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", "OPPO Enco X4"),
            ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:55:66", "OPPO Enco X4"),
        };
        Assert.Equal("OPPO Enco X4", LeAudioPairing.ResolveBondName(Primary, "Bluetooth 11:22:33:44:55:66", bonds));
    }

    [Fact]
    public void SecondaryLookup_DoesNotTakeNameFromAnotherPrimary()
    {
        var bonds = new[] { ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", "Another headset") };
        Assert.Equal("OPPO Enco X4", LeAudioPairing.ResolveBondName(Primary, "OPPO Enco X4", bonds));
    }

    [Fact]
    public void SecondaryLookup_UsesTheSetPairingContainerInsteadOfTheName()
    {
        var set = Guid.NewGuid();
        var bonds = new (string, Guid?)[]
        {
            ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:55:66", set),
            ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-99:88:77:66:55:44", set),
            ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", Guid.NewGuid()),
        };
        Assert.Equal(["BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-99:88:77:66:55:44"], LeAudioPairing.SelectContainerPeers(Primary, set, bonds));
    }

    [Fact]
    public void SecondaryLookup_WithoutAContainer_FindsNoContainerPeer()
    {
        var bonds = new (string, Guid?)[] { ("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", null) };
        Assert.Empty(LeAudioPairing.SelectContainerPeers(Primary, null, bonds));
        Assert.Empty(LeAudioPairing.SelectContainerPeers(Primary, Guid.Empty, [("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", Guid.Empty)]));
    }

    [Fact]
    public void X4Bitmap_AllowsTheLeBrSwitchQuery()
    {
        // The X4's handshake bitmap: bit 64 gates 0x0136 / 0x0433 like the official client.
        Assert.True(OppoCapabilityBitmap.TryParse(new OppoFrame(0x8100, 0, [0x00, .. Convert.FromHexString("FF775AEEEE8FA65F1700000000000000")]), out var bitmap));
        Assert.True(bitmap!.Allows(OppoCommand.QueryLeBrSwitch));
        Assert.False(new OppoCapabilityBitmap(new byte[16]).Allows(OppoCommand.QueryLeBrSwitch));
    }

    [Fact]
    public void LeBrSwitchResponse_DecodesTheOfficialBits()
    {
        // X4 answer before and after setting the switch: 00 02 (owner) and 00 03 (on, owner).
        Assert.True(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 2, [0x00, 0x02]), out var off));
        Assert.Equal(new(false, true, false), off);
        Assert.True(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 4, [0x00, 0x07]), out var all));
        Assert.Equal(new(true, true, true), all);
        Assert.False(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 2, [0x01, 0x02]), out _));
        Assert.False(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 2, [0x00]), out _));
    }

    [Fact]
    public void ScanResponse_NamesTheSecondaryEarbud()
    {
        // Manufacturer 0xBF0D payload of the primary's scan response: [00 00][address LE][..].
        byte[] data = Convert.FromHexString("0000B2A144332211" + "82008200");
        Assert.True(LeAudioPairing.TryParseSecondaryAddress(Primary, data, out ulong secondary));
        Assert.Equal(0x11223344A1B2UL, secondary);
    }

    [Theory]
    [InlineData("000066554433221182008200")] // the primary itself
    [InlineData("0000B2A144CCBBAA")] // another vendor's OUI
    [InlineData("0000B2A1443322")] // too short
    public void ScanResponse_RejectsAddressesThatAreNotTheOtherEarbud(string hex)
    {
        Assert.False(LeAudioPairing.TryParseSecondaryAddress(Primary, Convert.FromHexString(hex), out _));
    }
}

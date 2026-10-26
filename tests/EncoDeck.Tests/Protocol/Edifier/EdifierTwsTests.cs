// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Edifier;

public sealed class EdifierTwsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static DeviceIdentity Device(string name) => new("edifier-tws", name, new BluetoothAddress(0x0011223344AA));

    [Theory]
    [InlineData("EDIFIER Atom Max", "edifier-387")]
    [InlineData("Atom Max 2.0", "edifier-605")]
    [InlineData("漫步者 Atom ANC", "edifier-546")]
    [InlineData("ATOM_BUDS", "edifier-264")]
    [InlineData("ATOMBUDS at03", "edifier-250")]
    [InlineData("Loli Pro 2", "edifier-203")]
    [InlineData("EDIFIER Lolli Pro 2S", "edifier-382")]
    [InlineData("Lolli Pro 3i", "edifier-467")]
    [InlineData("Lolli 3 ANC", "edifier-235")]
    [InlineData("Lolli Clip AI", "edifier-519")]
    [InlineData("LolliPods Pro", "edifier-110")]
    public void Registry_ResolvesExactModelAndCommonSpellings(string name, string id)
    {
        var profile = Assert.IsType<EdifierProfile>(new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(Device(name)));
        Assert.Equal(id, profile.ModelId);
        Assert.Equal(DeviceForm.Earbuds, profile.Form);
        Assert.Equal(CapabilitySupport.Unknown, profile.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Empty(profile.NoiseControlOptions);
        Assert.NotEmpty(profile.ControlServiceUuids);
    }

    [Theory]
    [InlineData("Atom laptop")]
    [InlineData("Atom")]
    [InlineData("Lolli Pro 200")]
    [InlineData("Lolli Pro 3i unknown")]
    public void BareUnrelatedNames_AreNotClaimed(string name) =>
        Assert.Null(new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(Device(name)));

    [Fact]
    public void AllTwsModels_HaveArtworkAndModelSpecificGattServices()
    {
        // Catalog models carry their numeric product id; community-recorded models are tested separately.
        var profiles = EdifierProfiles.CreateBuiltIn().OfType<EdifierProfile>()
            .Where(p => p.IsTws && p.ModelId["edifier-".Length..].All(char.IsDigit)).ToArray();
        Assert.Equal(28, profiles.Length);
        foreach (var profile in profiles)
        {
            Assert.Equal(profile.ModelId, new DeviceProfileRegistry(profiles).Match(Device(profile.DisplayName))?.ModelId);
            Assert.Single(profile.ControlServiceUuids, EdifierConstants.IsGattService);
            Assert.NotNull(OfficialArtworkCatalog.Lookup(new DeviceResourceKey(profile.ModelId, null, null, ModelName: profile.DisplayName)));
        }
        var bleOnly = profiles.Single(p => p.ModelId == "edifier-110");
        Assert.DoesNotContain(EdifierConstants.SppServiceId, bleOnly.ControlServiceUuids);
    }

    [Fact]
    public void V2Codec_UsesOfficialHeaderLengthAndChecksum()
    {
        Assert.Equal(new byte[] { 0xAA, 0xEC, 0xF2, 0, 0, 0x88 }, EdifierFrameCodec.EncodeV2(0xF2, []));
        var decoder = new EdifierFrameDecoder(version2: true);
        byte[] payload = [3, 80, 70, 60];
        var bad = EdifierFrameCodec.EncodeV2(0xF2, payload, 0xBB);
        bad[^1] ^= 0xFF;
        var good = EdifierFrameCodec.EncodeV2(0xF2, payload, 0xCC);
        var frames = new List<EdifierFrame>();
        byte[] stream = [0, 0xFF, .. bad, .. good];
        foreach (byte value in stream) frames.AddRange(decoder.Feed([value]));
        Assert.Equal(payload, Assert.Single(frames).Payload);
    }

    [Theory]
    [InlineData(3, 80, 70, 60, 80, 70, 60)]
    [InlineData(1, 80, 70, 255, 80, null, null)]
    [InlineData(2, 80, 70, 0, null, 70, null)]
    [InlineData(3, 0, 255, 100, null, null, 100)]
    [InlineData(3, 101, 254, 101, null, null, null)]
    public async Task V2Controller_ReadsComponentsAndDoesNotGuessNoiseControls(
        byte peer, byte left, byte right, byte box, int? expectedLeft, int? expectedRight, int? expectedBox)
    {
        await RunAsync(version2: true, [peer, left, right, box], async (controller, store, channel) =>
        {
            Assert.Equal(expectedLeft, store.Current.Battery.Left?.Value.Level);
            Assert.Equal(expectedRight, store.Current.Battery.Right?.Value.Level);
            Assert.Equal(expectedBox, store.Current.Battery.Case?.Value.Level);
            Assert.Equal(CommandStatus.Success, (await controller.RefreshAsync(Ct)).Status);
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
            Assert.All(channel.Written, frame => Assert.Equal(EdifierConstants.QueryTwsState, frame[2]));
        });
    }

    [Fact]
    public async Task V1Controller_FallsBackAndConfirmsNoiseWrite()
    {
        await RunAsync(version2: false, [3, 90, 85, 50], async (controller, store, channel) =>
        {
            Assert.Equal(90, store.Current.Battery.Left?.Value.Level);
            Assert.Equal(85, store.Current.Battery.Right?.Value.Level);
            Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
            Assert.Equal(CommandStatus.Success, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
            Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
            Assert.True(store.Current.NoiseControl!.IsConfirmed);
            Assert.Contains(channel.Written, frame => frame.SequenceEqual(EdifierFrameCodec.Encode(EdifierConstants.SetNoiseMode, [2])));
        });
    }

    [Fact]
    public async Task Notifications_ClearOfflineEarAndMissingCase()
    {
        await RunAsync(true, [3, 90, 85, 50], async (controller, store, channel) =>
        {
            channel.Receive(EdifierFrameCodec.EncodeV2(0xF2, [2, 90, 75], 0xBB));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            while (store.Current.Battery.Right?.Value.Level != 75) await Task.Delay(5, timeout.Token);
            Assert.Null(store.Current.Battery.Left);
            Assert.Null(store.Current.Battery.Case);
        });
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 3, 50 })]
    [InlineData(new byte[] { 4, 1, 2, 3 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 255, 90, 80 })]
    public void TwsParser_RejectsMalformedAndOtherLayouts(byte[] payload) =>
        Assert.Null(EdifierDeviceController.ParseTwsBattery(payload, DateTimeOffset.UnixEpoch, StateSource.QueryResponse));

    private static async Task RunAsync(bool version2, byte[] battery,
        Func<IDeviceController, DeviceStateStore, FakeByteChannel, Task> assertions)
    {
        var identity = Device("EDIFIER Atom Max");
        var profile = (IControllableProfile)new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var channel = new FakeByteChannel();
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        byte noise = 1;
        var emulator = Task.Run(async () =>
        {
            int handled = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var writes = channel.Written;
                    for (; handled < writes.Count; handled++)
                    {
                        byte[] request = writes[handled];
                        if ((request[1] == 0xEC) != version2) continue;
                        byte command = request[2];
                        byte[]? payload = command switch
                        {
                            0xF2 => battery,
                            0xCC when !version2 => [noise, 0],
                            0xC1 when !version2 => [],
                            _ => null,
                        };
                        if (payload is null) continue;
                        if (command == 0xC1) noise = request[3];
                        byte[] response = version2 ? EdifierFrameCodec.EncodeV2(command, payload, 0xBB)
                            : EdifierFrameCodec.Encode(0xBB, command, payload);
                        channel.Receive(response[..2]);
                        channel.Receive(response[2..]);
                    }
                    await Task.Delay(5, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }, Ct);
        await using var controller = profile.CreateController(channel, context);
        try
        {
            Assert.True(await controller.InitializeAsync(Ct));
            await assertions(controller, store, channel);
        }
        finally
        {
            await stop.CancelAsync();
            await emulator;
        }
    }
}

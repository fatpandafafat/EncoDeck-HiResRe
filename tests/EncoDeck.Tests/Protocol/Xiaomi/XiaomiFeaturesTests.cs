// SPDX-License-Identifier: GPL-3.0-only
// Simulated sessions over XiaomiFakeEarbuds, whose defaults are the REDMI Buds 6 Pro capture attached to
// BudsLink PR #110 (https://github.com/maniacx/BudsLink/pull/110, GPL-3.0). Push and status-report
// frames below are quoted from the same log; noise strength 19 is from robin-liquidium/redmi-buds-bar
// (GPL-3.0); the wear bits follow android_packages_apps_XiaomiTWS (Apache-2.0) and mebuds (GPL-3.0).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Xiaomi;

namespace EncoDeck.Tests.Protocol.Xiaomi;

public class XiaomiFeaturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(XiaomiFakeEarbuds Buds, IDeviceController Controller, DeviceStateStore Store)> StartAsync(string name = "Redmi Buds 5 Pro", Action<XiaomiFakeEarbuds>? setup = null)
    {
        var buds = new XiaomiFakeEarbuds();
        setup?.Invoke(buds);
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds, name);
        Assert.True(await controller.InitializeAsync(Ct));
        return (buds, controller, store);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition());
    }

    [Fact]
    public async Task ReadWriteFixtures_ControlAllReportedFeatures()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;
        foreach (var cap in new[] { DeviceCapability.NoiseCancellation, DeviceCapability.Equalizer, DeviceCapability.SpatialAudio, DeviceCapability.PromptVolume,
                     DeviceCapability.DualDeviceConnection, DeviceCapability.TouchCustomization, DeviceCapability.FindMyEarbuds, DeviceCapability.InEarDetection,
                     DeviceCapability.AdaptiveNoiseCancellation, DeviceCapability.CustomEqualizer })
            Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(cap).Support);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "nc-2"), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1, 2 }, d.Values[XiaomiConfig.Noise]);
        Assert.True((await controller.SetEqualizerPresetAsync("xiaomi-5", Ct)).IsSuccess);
        Assert.Equal(new byte[] { 5 }, d.Values[XiaomiConfig.EqualizerPreset]);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, Ct)).IsSuccess);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.AutoAnswerCalls, true, Ct)).IsSuccess);
        Assert.True(store.Current.Toggles[DeviceToggle.AutoAnswerCalls].Value);
        d.Values[XiaomiConfig.Spatial] = [0x96];
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct)).IsSuccess);
        Assert.Equal(0x9F, d.Values[XiaomiConfig.Spatial][0]); // Preserve preference, surround and reserved bits.
        Assert.True((await controller.SetPromptVolumeAsync(8, Ct)).IsSuccess);
        Assert.Equal(8, store.Current.PromptVolume!.Value);
        Assert.Equal(StateSource.CommandConfirmation, store.Current.PromptVolume.Source);
    }

    [Fact]
    public async Task NoiseLevels_AreNamedStrengthsInLightToDeepOrder()
    {
        var (_, controller, store) = await StartAsync();
        await using var session = controller;
        var profile = store.Current.Profile!;
        Assert.Equal(new[] { "nc-1", "nc-0", "nc-2" }, profile.NoiseControlOptions.Where(o => o.Mode == NoiseControlMode.NoiseCancellation && o.LevelId is not null).Select(o => o.LevelId));
        Assert.Equal("Xiaomi_Anc_1", profile.NoiseControlOptions.Single(o => o.LevelId == "nc-1").ResourceKey);
        Assert.Equal("Xiaomi_Ambient_2", profile.NoiseControlOptions.Single(o => o.LevelId == "ambient-2").ResourceKey);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "nc-0"), store.Current.NoiseControl!.Value);
    }

    [Fact]
    public async Task TwentyStepAnc_UsesNumberedLevels()
    {
        // robin-liquidium: REDMI Buds 8 Pro reports ANC strength 19 (0–19, shown as 1–20).
        var (_, controller, store) = await StartAsync("REDMI Buds 8 Pro", b => b.Values[XiaomiConfig.Noise] = [1, 0x13]);
        await using var session = controller;
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "nc-19"), store.Current.NoiseControl!.Value);
        Assert.Equal("Xiaomi_Level_19", store.Current.Profile!.NoiseControlOptions.Single(o => o.LevelId == "nc-19").ResourceKey);
    }

    [Fact]
    public async Task AdaptiveAnc_IsAncWithTheAdaptiveSwitch()
    {
        var (d, controller, store) = await StartAsync(setup: b => b.Values[XiaomiConfig.Noise] = [2, 0]);
        await using var session = controller;
        Assert.Contains(store.Current.Profile!.NoiseControlOptions, o => o.Mode == NoiseControlMode.Adaptive);

        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Adaptive), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1, 0 }, d.Values[XiaomiConfig.Noise]);
        Assert.Equal(new byte[] { 1 }, d.Values[XiaomiConfig.AdaptiveAnc]);
        Assert.Equal(NoiseControlMode.Adaptive, store.Current.NoiseControl!.Value.Mode);

        // A manual strength turns the adaptive switch off first.
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "nc-2"), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0 }, d.Values[XiaomiConfig.AdaptiveAnc]);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "nc-2"), store.Current.NoiseControl!.Value);
    }

    [Fact]
    public async Task Gestures_PerEarbudWriteConfirmedByReadback()
    {
        var (d, controller, store) = await StartAsync("Redmi Buds 6 Pro");
        await using var session = controller;
        Assert.Contains(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, XiaomiGestures.PlayPause), store.Current.KeyFunctions!.Value);

        var result = await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Right, 0, GestureAction.DoubleSqueeze, XiaomiGestures.Next), Ct);

        Assert.True(result.IsSuccess);
        Assert.Contains(d.Snapshot(), f => f.OpCode == XiaomiProtocol.OpSetConfig && f.Payload.SequenceEqual(new byte[] { 5, 0, 2, 1, 0xFF, 3 }));
        Assert.Equal(XiaomiFakeEarbuds.Hex("040808 010103 020303 030606 050B0B"), d.Values[XiaomiConfig.Gestures]);
        var keys = store.Current.KeyFunctions!;
        Assert.Equal(StateSource.CommandConfirmation, keys.Source);
        Assert.Contains(new KeyFunction(EarSide.Right, 0, GestureAction.DoubleSqueeze, XiaomiGestures.Next), keys.Value);
        Assert.Contains(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, XiaomiGestures.PlayPause), keys.Value);
    }

    [Fact]
    public async Task Gestures_RefuseUnknownFunctionsAndUnreportedGestures()
    {
        var (d, controller, _) = await StartAsync("Redmi Buds 6 Play", b => b.Values[XiaomiConfig.Gestures] = XiaomiFakeEarbuds.Hex("040303010808020808030800"));
        await using var session = controller;
        int writes = d.Snapshot().Count(f => f.OpCode == XiaomiProtocol.OpSetConfig);
        // Noise control on hold needs confirmed noise control; the 6 Play has none.
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SqueezeAndHold, XiaomiGestures.NoiseControl), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 9), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.Slide, XiaomiGestures.Volume), Ct)).Status);
        Assert.Equal(writes, d.Snapshot().Count(f => f.OpCode == XiaomiProtocol.OpSetConfig));
        Assert.True((await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SqueezeAndHold, XiaomiGestures.VoiceAssistant), Ct)).IsSuccess);
    }

    [Fact]
    public async Task WearState_FromReadAndPush()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;
        Assert.Equal(new WearStatus(false, false, false, false, null), store.Current.Wear!.Value);
        // BudsLink PR #110 push: fe dc ba 87 f4 00 05 02 03 00 0c 0c ef (both earbuds in the ears).
        d.Push(XiaomiFakeEarbuds.Hex("FEDCBA87F4000502 03000C0C EF"));
        await Eventually(() => store.Current.Wear?.Value.AnyInEar == true);
        Assert.Equal(new WearStatus(true, true, false, false, null), store.Current.Wear!.Value);
        Assert.Equal(StateSource.DeviceNotification, store.Current.Wear.Source);
    }

    [Fact]
    public async Task FindEarbuds_RefusedWhileWorn_AllowedWhenOut()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;

        d.Values[XiaomiConfig.WearState] = [0x04]; // right earbud in the ear
        var worn = await controller.FindEarbudsAsync(true, Ct);
        Assert.Equal(CommandStatus.Rejected, worn.Status);
        Assert.Empty(d.FindWrites);
        Assert.False(store.Current.IsFindingEarbuds);

        d.Values[XiaomiConfig.WearState] = [0x00];
        Assert.True((await controller.FindEarbudsAsync(true, Ct)).IsSuccess);
        Assert.True(store.Current.IsFindingEarbuds);
        Assert.True((await controller.FindEarbudsAsync(false, Ct)).IsSuccess);
        Assert.False(store.Current.IsFindingEarbuds);
        // OpenXia capture shapes: ring both [1,3], stop both [0,3].
        Assert.Equal(new[] { new byte[] { 1, 3 }, new byte[] { 0, 3 } }, d.FindWrites);
    }

    [Fact]
    public async Task FindEarbuds_NotOfferedWhereTheCommandIsMissing()
    {
        // Gadgetbridge PR #6818 (facts only): the Redmi Buds 6 Lite protocol has no find command.
        var (d, controller, store) = await StartAsync("Redmi Buds 6 Lite");
        await using var session = controller;
        Assert.NotEqual(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.FindMyEarbuds).Support);
        Assert.Equal(CommandStatus.Unsupported, (await controller.FindEarbudsAsync(true, Ct)).Status);
        Assert.Empty(d.FindWrites);
    }

    [Fact]
    public async Task InEarDetection_InvertedSwitchConfirmedByRunInfo()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;
        Assert.True(store.Current.Toggles[DeviceToggle.InEarDetection].Value); // record 10 = 0 means on
        Assert.Contains(DeviceToggle.InEarDetection, ((XiaomiDeviceProfile)store.Current.Profile!).Toggles);

        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct)).IsSuccess);
        Assert.Equal((byte)1, d.WearDetection);
        Assert.Contains(d.Snapshot(), f => f.OpCode == XiaomiProtocol.OpSetDeviceInfo && f.Payload.SequenceEqual(new byte[] { 2, 6, 1 }));
        Assert.False(store.Current.Toggles[DeviceToggle.InEarDetection].Value);
    }

    [Fact]
    public async Task InEarDetection_HiddenWhenRunInfoLacksTheRecord()
    {
        var (_, controller, store) = await StartAsync(setup: b => b.WearDetection = null);
        await using var session = controller;
        Assert.DoesNotContain(DeviceToggle.InEarDetection, ((XiaomiDeviceProfile)store.Current.Profile!).Toggles);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.InEarDetection, true, Ct)).Status);
    }

    [Fact]
    public async Task CustomCurve_SelectedWithPresetTenAndSavedByReadback()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;
        var custom = Assert.Single(store.Current.CustomEqualizers!.Value);
        Assert.Equal((XiaomiCustomCurve.PresetId, -6, 6, false), (custom.Id, custom.MinGain, custom.MaxGain, custom.IsSelected));
        Assert.Equal(10, custom.Bands.Count);

        Assert.True((await controller.SelectCustomEqualizerAsync(XiaomiCustomCurve.PresetId, Ct)).IsSuccess);
        Assert.True(Assert.Single(store.Current.CustomEqualizers!.Value).IsSelected);

        var bands = custom.Bands.Select((b, i) => b with { Gain = i % 2 == 0 ? 3 : -4 }).ToArray();
        Assert.True((await controller.SaveCustomEqualizerAsync(custom with { Bands = bands }, Ct)).IsSuccess);
        Assert.Equal(XiaomiFakeEarbuds.Hex("01 0A 01 01 01 00 0A 003E03 007D84"), d.Snapshot().Last(f => f.OpCode == XiaomiProtocol.OpSetConfig).Payload.AsSpan(3, 13).ToArray());
        Assert.Equal(bands, store.Current.CustomEqualizers!.Value.Single().Bands);

        Assert.Equal(CommandStatus.Unsupported, (await controller.SaveCustomEqualizerAsync(custom with { Name = "Mine" }, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SaveCustomEqualizerAsync(custom with { Bands = [.. bands.Select(b => b with { Gain = 9 })] }, Ct)).Status);
    }

    [Fact]
    public async Task RefusedWrites_KeepActualReadbackAndReportRejection()
    {
        var (_, controller, store) = await StartAsync(setup: b => b.AcceptWrites = false);
        await using var session = controller;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("xiaomi-5", Ct)).Status);
        Assert.Equal("xiaomi-0", store.Current.Equalizer!.Value.ActivePresetId);
        Assert.NotNull(store.Current.LastError);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetPromptVolumeAsync(8, Ct)).Status);
        Assert.Equal(5, store.Current.PromptVolume!.Value);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, XiaomiGestures.PlayPause), Ct)).Status);
    }

    [Fact]
    public async Task UnknownModel_DoesNotGuessEqPresetOrNoiseLevels()
    {
        var (d, controller, store) = await StartAsync("Redmi future unknown", b => b.TargetInfo = XiaomiFakeEarbuds.Hex("0503 27170000", "0407 646464"));
        await using var session = controller;
        Assert.NotEqual(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.Equalizer).Support);
        Assert.NotEqual(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        int before = d.Snapshot().Count;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("xiaomi-5", Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "nc-2"), Ct)).Status);
        Assert.Equal(before, d.Snapshot().Count);
    }

    [Fact]
    public async Task ProductId_IdentifiesRenamedEarbuds()
    {
        // A name the catalog does not know, but device info reports the REDMI Buds 6 Pro id 2717/509E.
        var (_, controller, store) = await StartAsync("Redmi earbuds of Ann");
        await using var session = controller;
        var profile = (XiaomiDeviceProfile)store.Current.Profile!;
        Assert.Equal("REDMI Buds 6 Pro", profile.ModelName);
        Assert.Contains(profile.NoiseControlOptions, o => o.LevelId == "ambient-1");
    }

    [Fact]
    public async Task Notification_UpdatesStateAndAcknowledgesOriginalSequence()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;
        d.Notify([3, 0, 4, 0]);
        XiaomiFrame? ack = null;
        for (int i = 0; i < 100 && ack is null; i++)
        {
            ack = d.Snapshot().FirstOrDefault(f => f.Kind == 4 && f.OpCode == XiaomiProtocol.OpNotifyConfig);
            if (ack is null) await Task.Delay(10, Ct);
        }
        Assert.NotNull(ack);
        Assert.Equal(0, ack.Sequence); // Response status, followed by the echoed request sequence.
        Assert.Equal(new byte[] { 99 }, ack.Payload);
        await Eventually(() => store.Current.Toggles[DeviceToggle.DualDeviceConnection].Value == false);
        Assert.Equal(StateSource.DeviceNotification, store.Current.Toggles[DeviceToggle.DualDeviceConnection].Source);
    }

    [Fact]
    public async Task StatusReport_UpdatesBatteryAndIsAcknowledged()
    {
        var (d, controller, store) = await StartAsync();
        await using var session = controller;
        // BudsLink PR #110: fe dc ba 87 0e 00 06 1f 04 00 5f 5f ff ef, sent here asking for a reply (C7).
        d.Push(XiaomiFakeEarbuds.Hex("FEDCBAC70E00061F 04005F5FFF EF"));
        await Eventually(() => store.Current.Battery.Right?.Value.Level == 95);
        Assert.Equal(StateSource.DeviceNotification, store.Current.Battery.Right!.Source);
        await Eventually(() => d.Snapshot().Any(f => f.IsResponse && f.OpCode == XiaomiProtocol.OpReportStatus));
    }

    [Fact]
    public void ConfigWireVectors_AndMalformedPackets()
    {
        Assert.Equal("000B001D00A1", Convert.ToHexString(XiaomiConfigCodec.ReadPayload(11, 29, 161)));
        Assert.Equal("04000B0102", Convert.ToHexString(XiaomiConfigCodec.WritePayload(11, [1, 2])));
        Assert.False(XiaomiConfigCodec.TryParse([4, 0, 11, 1], out _));
        Assert.False(XiaomiConfigCodec.TryParse([3, 0, 4, 1, 3, 0, 4, 0], out _));
        var frame = XiaomiConfigCodec.NormalizeResponse(new(4, 0xF3, 0, [9, 3, 0, 4, 1]));
        Assert.Equal(9, frame!.Sequence);
        Assert.Equal(new byte[] { 3, 0, 4, 1 }, frame.Payload);
    }
}

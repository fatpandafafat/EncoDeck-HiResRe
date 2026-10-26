// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;

namespace EncoDeck.Tests.Bluetooth;

/// <summary>
/// Endpoint selection and execution rules for the CTKD Classic A2DP endpoint that can coexist with
/// LE Audio. No Windows PnP/audio device is touched: the tests use a fake platform.
/// </summary>
public class LeAudioClassicEndpointTests
{
    private const ulong Primary = 0x112233445566;
    private const string Address = "112233445566";
    private const string OtherAddress = "AABBCCDDEEFF";

    private static AudioEndpointInfo Le(string name = "扬声器 (3- OPPO Enco X4)") =>
        new("SWD\\MMDEVAPI\\{0.0.0.00000000}.{AAAAAAAA-0000-0000-0000-000000000001}",
            name,
            "APXENUM\\{2D68507B-1813-40EF-A1D6-DB4A25EF1408}&AUDIO\\9&2079be91&1d&Speaker",
            AudioEndpointKind.LeAudioRender,
            false) { AssociatedBluetoothAddresses = [Primary] };

    private static AudioEndpointInfo A2dp(bool disabled = false, string address = Address) =>
        new($"SWD\\MMDEVAPI\\{{0.0.0.00000000}}.{{BBBBBBBB-0000-0000-0000-{address}}}",
            "耳机 (OPPO Enco X4)",
            $"BTHENUM\\{{0000110B-0000-1000-8000-00805F9B34FB}}_VID&000102B0_PID&0000\\7&1&0&{address}_C00000000",
            AudioEndpointKind.ClassicA2dpRender,
            disabled);

    private static AudioEndpointInfo Hfp(bool disabled = false, string address = Address) =>
        new($"SWD\\MMDEVAPI\\{{0.0.0.00000000}}.{{CCCCCCCC-0000-0000-0000-{address}}}",
            "耳机 (OPPO Enco X4)",
            $"BTHENUM\\{{0000111E-0000-1000-8000-00805F9B34FB}}_HCIBYPASS_VID&000102B0_PID&0000\\7&1&0&{address}_C00000000",
            AudioEndpointKind.ClassicHfpRender,
            disabled);

    [Fact]
    public void Inspect_FindsLeAudioAndClassicA2dpForSameAddress()
    {
        var inspection = LeAudioClassicAudioEndpoint.Inspect(Primary, [Le(), A2dp(), Hfp()]);

        Assert.Equal(LeAudioAudioEndpointState.ClassicA2dpEnabled, inspection.State);
        Assert.NotNull(inspection.LeAudioRender);
        Assert.NotNull(inspection.ClassicA2dpRender);
        Assert.NotNull(inspection.ClassicHfpRender);
        Assert.True(inspection.HasResidualClassicAudio);
    }

    [Fact]
    public void Inspect_DoesNotUseAnotherHeadsetEndpoint()
    {
        var inspection = LeAudioClassicAudioEndpoint.Inspect(Primary, [Le(), A2dp(address: OtherAddress)]);

        Assert.Equal(LeAudioAudioEndpointState.LeAudioOnly, inspection.State);
        Assert.Null(inspection.ClassicA2dpRender);
        Assert.NotNull(inspection.LeAudioRender);
    }

    private static AudioEndpointInfo LeMicrophone() =>
        new("SWD\\MMDEVAPI\\{0.0.1.00000000}.{DDDDDDDD-0000-0000-0000-000000000004}",
            "麦克风 (OPPO Enco X4)",
            "APXENUM\\{2D68507B-1813-40EF-A1D6-DB4A25EF1408}&AUDIO\\9&2079be91&1d&Microphone",
            AudioEndpointKind.LeAudioRender,
            false);

    [Fact]
    public void Inspect_IgnoresCaptureEndpointOwnedByTheLeAudioStack()
    {
        var inspection = LeAudioClassicAudioEndpoint.Inspect(Primary, [Le(), LeMicrophone(), A2dp()]);

        Assert.Equal(LeAudioAudioEndpointState.ClassicA2dpEnabled, inspection.State);
        Assert.NotNull(inspection.LeAudioRender);
        Assert.Equal("扬声器 (3- OPPO Enco X4)", inspection.LeAudioRender.FriendlyName);
    }

    [Fact]
    public void Classify_CleanRePairWithoutClassicEndpointIsPaired()
    {
        var inspection = new LeAudioEndpointInspection(LeAudioAudioEndpointState.LeAudioOnly, Le());
        var result = LeAudioPairing.ClassifyOutcome(true, new LeAudioEndpointResult(LeAudioEndpointOutcome.NoClassicEndpoint, Inspection: inspection));

        Assert.Equal(LeAudioPairingOutcome.Paired, result);
    }

    [Fact]
    public void Classify_ResidualClassicA2dpIsNotReportedAsSuccess()
    {
        var inspection = new LeAudioEndpointInspection(LeAudioAudioEndpointState.ClassicA2dpEnabled, Le(), A2dp());
        var result = LeAudioPairing.ClassifyOutcome(true, new LeAudioEndpointResult(LeAudioEndpointOutcome.NoClassicEndpoint, Inspection: inspection));

        Assert.Equal(LeAudioPairingOutcome.PairedWithClassicAudio, result);
    }

    [Fact]
    public void Classify_MissingLeAudioEndpointIsNotReportedAsSuccess()
    {
        var inspection = new LeAudioEndpointInspection(LeAudioAudioEndpointState.LeAudioEndpointMissing, null, A2dp());
        var result = LeAudioPairing.ClassifyOutcome(true, new LeAudioEndpointResult(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, Inspection: inspection));

        Assert.Equal(LeAudioPairingOutcome.PairedButLeAudioEndpointMissing, result);
    }

    [Fact]
    public void Classify_NotBothConnectedWaitsInsteadOfClaimingPairing()
    {
        Assert.Equal(LeAudioPairingOutcome.PairedWaitingForLinks, LeAudioPairing.ClassifyOutcome(false, null));
    }

    [Fact]
    public void Inspect_ReportsMissingLeAudioEndpointWhenClassicIsPresent()
    {
        var inspection = LeAudioClassicAudioEndpoint.Inspect(Primary, [A2dp()]);

        Assert.Equal(LeAudioAudioEndpointState.LeAudioEndpointMissing, inspection.State);
        Assert.True(inspection.ClassicA2dpPresent);
        Assert.False(inspection.LeAudioRenderPresent);
    }

    [Fact]
    public void Inspect_ReportsAmbiguousWhenTwoLeEndpointsMatch()
    {
        var inspection = LeAudioClassicAudioEndpoint.Inspect(Primary, [Le(), Le("扬声器 (4- OPPO Enco X4)"), A2dp()]);

        Assert.Equal(LeAudioAudioEndpointState.Ambiguous, inspection.State);
    }

    [Fact]
    public async Task Suppress_DoesNothingWhenBothLeLinksAreNotConnected()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp(), Hfp()]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: false, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.BlockedLeLinks, result.Outcome);
        Assert.Empty(fake.Calls);
        Assert.Empty(fake.DefaultCalls);
        Assert.False(fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.ClassicA2dpRender).IsDisabled);
    }

    [Fact]
    public async Task Suppress_CleanPairingWithOnlyLeSpeakerDoesNothing()
    {
        var fake = new FakeEndpointPlatform([Le()]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.NoClassicEndpoint, result.Outcome);
        Assert.Empty(fake.Calls);
        Assert.Equal(LeAudioAudioEndpointState.LeAudioOnly, result.Inspection!.State);
    }

    [Fact]
    public async Task Suppress_DoesNothingWhenLeAudioEndpointIsMissing()
    {
        var fake = new FakeEndpointPlatform([A2dp()]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, result.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Suppress_DoesNothingWhenLeEndpointIsAmbiguous()
    {
        var fake = new FakeEndpointPlatform([Le(), Le("扬声器 (4- OPPO Enco X4)"), A2dp()]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.Ambiguous, result.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Suppress_DisablesOnlyClassicA2dpRender()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp(), Hfp()]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.Disabled, result.Outcome);
        var defaultCall = Assert.Single(fake.DefaultCalls);
        Assert.Equal(fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.LeAudioRender).InstanceId, defaultCall);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.ClassicA2dpRender).InstanceId, call.InstanceId);
        Assert.False(call.Enabled);
        var disabled = fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.ClassicA2dpRender);
        Assert.True(disabled.IsDisabled);
        Assert.StartsWith("BTHENUM", disabled.ParentInstanceId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0000110B", disabled.ParentInstanceId, StringComparison.OrdinalIgnoreCase);
        Assert.False(fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.ClassicHfpRender).IsDisabled);
    }

    [Fact]
    public async Task Suppress_IsIdempotentWhenAlreadyDisabled()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp(disabled: true)]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.AlreadyLeAudioOnly, result.Outcome);
        Assert.Empty(fake.Calls);
        Assert.Single(fake.DefaultCalls);
    }

    [Fact]
    public async Task Suppress_DoesNotHideClassicWhenDefaultSwitchFails()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp()]) { FailDefault = true };

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.Failed, result.Outcome);
        Assert.Contains("default", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.Calls);
        Assert.False(fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.ClassicA2dpRender).IsDisabled);
    }

    [Fact]
    public async Task Suppress_ReportsFailureInsteadOfClaimingSuccess()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp()]) { FailSet = true };

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.Failed, result.Outcome);
        Assert.Contains("access denied", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(fake.Endpoints.Single(e => e.Kind == AudioEndpointKind.ClassicA2dpRender).IsDisabled);
    }

    [Fact]
    public async Task Restore_EnablesOnlyDisabledEndpointsForTheSameAddress()
    {
        var primaryA2dp = A2dp(disabled: true);
        var primaryHfp = Hfp(disabled: true);
        var otherA2dp = A2dp(address: OtherAddress);
        var fake = new FakeEndpointPlatform([Le(), primaryA2dp, primaryHfp, otherA2dp]);

        var result = await LeAudioClassicAudioEndpoint.RestoreAsync(Primary, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.Enabled, result.Outcome);
        Assert.Equal(2, fake.Calls.Count);
        Assert.All(fake.Calls, call => Assert.True(call.Enabled));
        Assert.Contains(fake.Calls, call => call.InstanceId == primaryA2dp.InstanceId);
        Assert.Contains(fake.Calls, call => call.InstanceId == primaryHfp.InstanceId);
        Assert.DoesNotContain(fake.Calls, call => call.InstanceId == otherA2dp.InstanceId);
        Assert.False(fake.Endpoints.Single(e => e.InstanceId == primaryA2dp.InstanceId).IsDisabled);
        Assert.False(fake.Endpoints.Single(e => e.InstanceId == primaryHfp.InstanceId).IsDisabled);
        Assert.False(fake.Endpoints.Single(e => e.InstanceId == otherA2dp.InstanceId).IsDisabled);
    }

    [Fact]
    public async Task Restore_DoesNothingWhenNoClassicEndpointIsDisabled()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp(), Hfp()]);

        var result = await LeAudioClassicAudioEndpoint.RestoreAsync(Primary, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.NothingToRestore, result.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Suppress_DoesNothingWhenClassicIsDisabledButLeEndpointIsMissing()
    {
        var fake = new FakeEndpointPlatform([A2dp(disabled: true)]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, result.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Suppress_DoesNothingWhenNoEndpointsExist()
    {
        var fake = new FakeEndpointPlatform([]);

        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, bothLeConnected: true, fake, TestContext.Current.CancellationToken);

        Assert.Equal(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, result.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Classify_DisabledClassicWithoutLeEndpointIsNotReportedAsSuccess()
    {
        var inspection = new LeAudioEndpointInspection(
            LeAudioAudioEndpointState.ClassicA2dpDisabled, null, A2dp(disabled: true));
        var result = LeAudioPairing.ClassifyOutcome(
            true, new LeAudioEndpointResult(LeAudioEndpointOutcome.AlreadyLeAudioOnly, Inspection: inspection));

        Assert.Equal(LeAudioPairingOutcome.PairedButLeAudioEndpointMissing, result);
    }

    [Fact]
    public async Task UnrelatedSoleLeEndpoint_CannotBeUsedToDisableThisHeadset()
    {
        var unrelated = Le() with { AssociatedBluetoothAddresses = [0xAABBCCDDEEFF] };
        var fake = new FakeEndpointPlatform([unrelated, A2dp()]);
        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, true, fake, TestContext.Current.CancellationToken);
        Assert.Equal(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, result.Outcome);
        Assert.Empty(fake.Calls);
        Assert.Empty(fake.DefaultCalls);
    }

    [Fact]
    public async Task DisabledLeEndpoint_CannotReplaceWorkingClassicAudio()
    {
        var fake = new FakeEndpointPlatform([Le() with { IsDisabled = true }, A2dp()]);
        var result = await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, true, fake, TestContext.Current.CancellationToken);
        Assert.Equal(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, result.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task DuplicateClassicEndpoints_AreNotSilentlyPickedOrRestored()
    {
        var first = A2dp(disabled: true);
        var second = first with { InstanceId = first.InstanceId.Replace("BBBBBBBB", "DDDDDDDD") };
        var fake = new FakeEndpointPlatform([Le(), first, second]);
        Assert.Equal(LeAudioEndpointOutcome.Ambiguous,
            (await LeAudioClassicAudioEndpoint.SuppressAsync(Primary, true, fake, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(LeAudioEndpointOutcome.Failed,
            (await LeAudioClassicAudioEndpoint.RestoreAsync(Primary, fake, TestContext.Current.CancellationToken)).Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Restore_CannotConfirmSuccessWhenTheEndpointDisappears()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp(disabled: true)]) { RemoveChangedEndpoint = true };
        var result = await LeAudioClassicAudioEndpoint.RestoreAsync(Primary, fake, TestContext.Current.CancellationToken);
        Assert.Equal(LeAudioEndpointOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Restore_RecoversExactClassicEndpointEvenWhenLeIdentityIsAmbiguous()
    {
        var fake = new FakeEndpointPlatform([Le(), Le("Duplicate LE"), A2dp(disabled: true)]);
        Assert.Equal(LeAudioEndpointOutcome.Enabled,
            (await LeAudioClassicAudioEndpoint.RestoreAsync(Primary, fake, TestContext.Current.CancellationToken)).Outcome);
        Assert.True(Assert.Single(fake.Calls).Enabled);
    }

    [Fact]
    public void OldInactiveLeEndpointDoesNotHideTheActiveOne()
    {
        var active = Le();
        var stale = Le("Old endpoint") with { IsActive = false, InstanceId = active.InstanceId.Replace("AAAAAAAA", "DDDDDDDD") };
        var inspection = LeAudioClassicAudioEndpoint.Inspect(Primary, [active, stale, A2dp()]);
        Assert.Equal(LeAudioAudioEndpointState.ClassicA2dpEnabled, inspection.State);
        Assert.Same(active, inspection.LeAudioRender);
    }

    private static AudioEndpointInfo LeCapture(bool disabled = false) =>
        LeMicrophone() with { Kind = AudioEndpointKind.LeAudioCapture, IsDisabled = disabled, AssociatedBluetoothAddresses = [Primary] };

    [Fact]
    public void ClassifyCapture_RecognizesLeAudioMicrophone()
    {
        Assert.Equal(AudioEndpointKind.LeAudioCapture, WindowsAudioEndpointPlatform.ClassifyCapture(LeMicrophone().ParentInstanceId));
        Assert.Equal(AudioEndpointKind.Other, WindowsAudioEndpointPlatform.ClassifyCapture(Le().ParentInstanceId));
    }

    [Fact]
    public void Microphone_Inspect_IgnoresRenderAndOtherHeadsets()
    {
        var other = LeCapture() with { AssociatedBluetoothAddresses = [0xAABBCCDDEEFF] };
        var inspection = LeAudioMicrophone.Inspect(Primary, [Le(), A2dp(), LeCapture(), other]);

        Assert.Single(inspection.Endpoints);
        Assert.False(inspection.Disabled);
    }

    [Fact]
    public async Task Microphone_DisableHidesOnlyTheCaptureEndpoint()
    {
        var fake = new FakeEndpointPlatform([Le(), A2dp(), LeCapture()]);

        var result = await LeAudioMicrophone.SetEnabledAsync(Primary, false, fake);

        Assert.True(result.Success);
        Assert.Equal((LeCapture().InstanceId, false), Assert.Single(fake.Calls));
        Assert.True(LeAudioMicrophone.Inspect(Primary, fake.Endpoints).Disabled);
        Assert.Empty(fake.DefaultCalls);
    }

    [Fact]
    public async Task Microphone_EnableRestoresAndMissingEndpointFails()
    {
        var fake = new FakeEndpointPlatform([LeCapture(disabled: true)]);
        Assert.True((await LeAudioMicrophone.SetEnabledAsync(Primary, true, fake)).Success);
        Assert.False(fake.Endpoints.Single().IsDisabled);

        var empty = new FakeEndpointPlatform([Le()]);
        Assert.False((await LeAudioMicrophone.SetEnabledAsync(Primary, false, empty)).Success);
        Assert.Empty(empty.Calls);
    }

    private sealed class FakeEndpointPlatform(IEnumerable<AudioEndpointInfo> endpoints) : IAudioEndpointPlatform
    {
        public List<AudioEndpointInfo> Endpoints { get; } = [.. endpoints];
        public List<(string InstanceId, bool Enabled)> Calls { get; } = [];
        public List<string> DefaultCalls { get; } = [];
        public bool FailSet { get; init; }
        public bool FailDefault { get; init; }
        public bool RemoveChangedEndpoint { get; init; }

        public Task<IReadOnlyList<AudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioEndpointInfo>>(Endpoints.ToArray());

        public Task<IReadOnlyList<AudioEndpointInfo>> GetCaptureEndpointsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioEndpointInfo>>(Endpoints.ToArray());

        public Task<AudioEndpointOperationResult> SetDefaultAsync(AudioEndpointInfo endpoint, CancellationToken cancellationToken)
        {
            DefaultCalls.Add(endpoint.InstanceId);
            return Task.FromResult(FailDefault
                ? new AudioEndpointOperationResult(false, "default denied")
                : AudioEndpointOperationResult.Ok);
        }

        public Task<AudioEndpointOperationResult> SetEnabledAsync(AudioEndpointInfo endpoint, bool enabled, CancellationToken cancellationToken)
        {
            Calls.Add((endpoint.InstanceId, enabled));
            if (FailSet) return Task.FromResult(new AudioEndpointOperationResult(false, "access denied"));
            int index = Endpoints.FindIndex(e => e.InstanceId == endpoint.InstanceId);
            if (index >= 0)
            {
                if (RemoveChangedEndpoint) Endpoints.RemoveAt(index);
                else Endpoints[index] = endpoint with { IsDisabled = !enabled };
            }
            return Task.FromResult(AudioEndpointOperationResult.Ok);
        }
    }
}

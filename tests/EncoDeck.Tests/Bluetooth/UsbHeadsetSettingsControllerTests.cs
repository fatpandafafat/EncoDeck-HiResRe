// SPDX-License-Identifier: GPL-3.0-only
// Reply layouts follow the captured 1532:0550 frames replayed by nightdevx/barracuda-x-battery's self-test
// (MIT, Copyright (c) 2026 nightdijital).
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Usb;

namespace EncoDeck.Tests.Bluetooth;

public sealed class UsbHeadsetSettingsControllerTests
{
    /// <summary>The idle power-off the headset reported (a shared device option), in minutes.</summary>
    private static int? AutoOff(DeviceState state) =>
        state.Options.TryGetValue(UsbHeadsetSettingsController.AutoOffOption, out var option) ? option.Value.Value : null;

    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task OnlySettingsWithAValidReadBackAreOffered()
    {
        var h = new Harness { [RazerPaSetting.EqPreset] = [0x07], [RazerPaSetting.DoNotDisturb] = [7], [RazerPaSetting.CustomEq] = [5, 5, 5] };
        Assert.Empty(h.Profile.Settings);
        Assert.Equal(CommandStatus.NotConnected, (await h.Controller.SetOptionAsync(UsbHeadsetSettingsController.AutoOffOption, 15, None)).Status);

        await h.SyncAsync(linked: true, force: false);

        Assert.Equal(new[] { RazerPaSetting.EqPreset }, h.Profile.Settings);
        Assert.Equal("game", h.State.Equalizer?.Value.ActivePresetId);
        Assert.Null(h.State.CustomEqualizers);
        Assert.False(h.State.Toggles.ContainsKey(DeviceToggle.DoNotDisturb));
        Assert.Empty(h.Profile.Toggles);
        Assert.Equal(new[] { "default", "game", "music", "movie", "custom" }, h.Profile.EqualizerPresets.Select(p => p.Id));
        Assert.Equal(CommandStatus.NotConnected, (await h.Controller.SetToggleAsync(DeviceToggle.DoNotDisturb, true, None)).Status);
        Assert.Empty(h.Writes);
    }

    [Fact]
    public async Task ReadBackValuesFillTheSharedEqSwitchAndPickerState()
    {
        var h = Harness.Full(preset: 0xFF);
        await h.SyncAsync(linked: true, force: false);

        Assert.Equal(BarracudaXSettingValues.Offered.Order(), h.Profile.Settings);
        var custom = Assert.Single(h.State.CustomEqualizers!.Value);
        Assert.True(custom.IsSelected);
        Assert.Equal(new[] { -5, -4, -3, -2, -1, 0, 1, 2, 3, 5 }, custom.Bands.Select(b => b.Gain));
        Assert.Equal(16000, custom.Bands[^1].FrequencyHz);
        Assert.True(h.State.Toggles[DeviceToggle.DoNotDisturb].Value);
        Assert.Equal(30, AutoOff(h.State));
        Assert.Equal(new[] { "default", "game", "music", "movie" }, h.Profile.EqualizerPresets.Select(p => p.Id));
    }

    [Fact]
    public async Task UnknownPresetIsKeptRawAndNotGuessed()
    {
        var h = Harness.Full(preset: 0x05);
        await h.SyncAsync(linked: true, force: false);
        Assert.Null(h.State.Equalizer?.Value.ActivePresetId);
        Assert.Equal(5, h.State.Equalizer?.Value.RawPresetValue);
        Assert.False(h.State.CustomEqualizers!.Value[0].IsSelected);
    }

    [Fact]
    public async Task SettingsAreWithdrawnWhenTheHeadsetGoesOffline()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        await h.SyncAsync(linked: false, force: false);

        Assert.Empty(h.Profile.Settings);
        Assert.Null(h.State.Equalizer);
        Assert.Null(h.State.CustomEqualizers);
        Assert.Null(AutoOff(h.State));
        Assert.False(h.State.Toggles.ContainsKey(DeviceToggle.DoNotDisturb));
        Assert.Equal(CommandStatus.NotConnected, (await h.Controller.SetEqualizerPresetAsync("music", None)).Status);
        Assert.Empty(h.Writes);
    }

    [Fact]
    public async Task ConfirmedWriteIsShownOnlyAfterTheReadBack()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        h.OnWrite = () => Assert.Equal(30, AutoOff(h.State)); // still the old value while writing
        var result = await h.Controller.SetOptionAsync(UsbHeadsetSettingsController.AutoOffOption, 15, None);

        Assert.True(result.IsSuccess);
        Assert.Equal(15, AutoOff(h.State));
        Assert.Empty(h.State.PendingCommands);
    }

    [Fact]
    public async Task RejectedWriteRevertsToTheValueTheHeadsetReports()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        h.Ignore = true;
        h[RazerPaSetting.DoNotDisturb] = [0]; // changed on the headset in the meantime

        var result = await h.Controller.SetToggleAsync(DeviceToggle.DoNotDisturb, true, None);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.False(h.State.Toggles[DeviceToggle.DoNotDisturb].Value);
        Assert.Empty(h.State.PendingCommands);
    }

    [Fact]
    public async Task FailedWriteKeepsTheConfirmedValueReleasesTheGateAndReReadsOnTheNextPoll()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        int reads = h.Reads;
        h.Throw = true;

        var result = await h.Controller.SetEqualizerPresetAsync("music", None);

        Assert.Equal(CommandStatus.TransportError, result.Status);
        Assert.Equal("game", h.State.Equalizer?.Value.ActivePresetId);
        Assert.Equal(1, h.Gate.CurrentCount);
        Assert.Empty(h.State.PendingCommands);

        h.Throw = false;
        await h.SyncAsync(linked: true, force: false);
        Assert.Equal(reads + 1, h.Reads);
    }

    [Fact]
    public async Task LinkedSettingsAreNotReReadOnEveryPoll()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        await h.SyncAsync(linked: true, force: false);
        Assert.Equal(1, h.Reads);

        await h.SyncAsync(linked: true, force: true);
        Assert.Equal(2, h.Reads);

        h.Time.Advance(UsbHeadsetSettingsController.ResyncInterval);
        await h.SyncAsync(linked: true, force: false);
        Assert.Equal(3, h.Reads);
    }

    [Fact]
    public async Task WritesWaitForTheBatteryPollAndCollapseToTheNewestBandValues()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        var custom = h.State.CustomEqualizers!.Value[0];
        CustomEqualizer With(int gain) => custom with { Bands = custom.Bands.Select(b => b with { Gain = gain }).ToArray() };

        await h.Gate.WaitAsync(None); // a battery read is in progress
        var first = h.Controller.SaveCustomEqualizerAsync(With(1), None);
        var second = h.Controller.SaveCustomEqualizerAsync(With(2), None);
        var third = h.Controller.SaveCustomEqualizerAsync(With(3), None);
        Assert.True(h.State.IsPending("custom-eq"));
        Assert.Empty(h.Writes);
        h.Gate.Release();

        Assert.Equal(CommandStatus.Cancelled, (await first).Status);
        Assert.Equal(CommandStatus.Cancelled, (await second).Status);
        Assert.True((await third).IsSuccess);
        var write = Assert.Single(h.Writes);
        Assert.Equal(Enumerable.Repeat((byte)8, 10), write.Values);
        Assert.All(h.State.CustomEqualizers!.Value[0].Bands, b => Assert.Equal(3, b.Gain));
        Assert.Empty(h.State.PendingCommands);
    }

    [Fact]
    public async Task OutOfRangeBandsAndUnknownChoicesAreNeverSent()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        var custom = h.State.CustomEqualizers!.Value[0];
        Assert.Equal(CommandStatus.Rejected, (await h.Controller.SaveCustomEqualizerAsync(custom with { Bands = custom.Bands.Select(b => b with { Gain = 6 }).ToArray() }, None)).Status);
        Assert.Equal(CommandStatus.Rejected, (await h.Controller.SetOptionAsync(UsbHeadsetSettingsController.AutoOffOption, 10, None)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetEqualizerPresetAsync("bass", None)).Status);
        Assert.Empty(h.Writes);
    }

    [Fact]
    public async Task SelectingCustomWritesPresetFfAndMarksTheCustomEqSelected()
    {
        var h = Harness.Full();
        await h.SyncAsync(linked: true, force: false);
        Assert.True((await h.Controller.SelectCustomEqualizerAsync(BarracudaXSettingValues.CustomPresetId, None)).IsSuccess);
        var write = Assert.Single(h.Writes);
        Assert.Equal(RazerPaSetting.EqPreset, write.Setting);
        Assert.Equal(new byte[] { 0xFF }, write.Values);
        Assert.True(h.State.CustomEqualizers!.Value[0].IsSelected);
        Assert.Equal("custom", h.State.Equalizer?.Value.ActivePresetId);
    }

    [Fact]
    public async Task RemoteModeIsReleasedWhenTheSessionFails()
    {
        var failing = new Receiver { ThrowOn = 0x13 };
        await Assert.ThrowsAsync<IOException>(() => UsbHeadsetSettings.ReadReportsAsync(0x0550, [RazerPaSetting.EqPreset], failing.SendAsync, failing.ReceiveAsync, None));
        Assert.Equal(new byte[] { 1, 0 }, failing.Routes);

        var throwing = new Receiver { ThrowOn = 0x93 };
        await Assert.ThrowsAsync<IOException>(() => UsbHeadsetSettings.WriteReportsAsync(0x0550, RazerPaSetting.EqPreset, [0x08], throwing.SendAsync, throwing.ReceiveAsync, None));
        Assert.Equal(new byte[] { 1, 0 }, throwing.Routes);
    }

    [Fact]
    public async Task RemoteModeIsReleasedWhenTheSessionIsCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var silent = new Receiver { OnGet = cancel.Cancel }; // the headset never answers, and the caller gives up
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UsbHeadsetSettings.ReadReportsAsync(0x0550, [RazerPaSetting.AutoOff], silent.SendAsync, silent.ReceiveAsync, cancel.Token));
        Assert.Equal(new byte[] { 1, 0 }, silent.Routes);
    }

    private sealed class Harness
    {
        private readonly Dictionary<RazerPaSetting, byte[]> _headset = [];
        private readonly DeviceStateStore _store = new();

        public Harness()
        {
            var identity = new DeviceIdentity("usb", "Razer Barracuda X", new BluetoothAddress(0)) { Transport = DeviceTransport.Usb };
            var profile = UsbHeadsetProfiles.Create().OfType<UsbHeadsetProfile>().Single(p => p.ModelId == "razer-usb-barracuda-x").WithBattery(true);
            _store.Reset(DeviceState.Empty with { Device = identity, Profile = profile, Connection = DeviceConnectionState.UsbHeadsetConnected });
            Controller = new UsbHeadsetSettingsController("usb", _store, Gate, ReadAsync, WriteAsync, time: Time);
        }

        public static Harness Full(byte preset = 0x07) => new()
        {
            [RazerPaSetting.EqPreset] = [preset],
            [RazerPaSetting.CustomEq] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10],
            [RazerPaSetting.DoNotDisturb] = [1],
            [RazerPaSetting.AutoOff] = [30],
        };

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ManualTime Time { get; } = new();
        public UsbHeadsetSettingsController Controller { get; }
        public List<(RazerPaSetting Setting, byte[] Values)> Writes { get; } = [];
        public int Reads { get; private set; }
        public bool Ignore { get; set; }
        public bool Throw { get; set; }
        public Action? OnWrite { get; set; }
        public DeviceState State => _store.Current;

        /// <summary>The battery poll calls the sync while it holds the gate.</summary>
        public async Task SyncAsync(bool linked, bool force)
        {
            await Gate.WaitAsync(None);
            try { await Controller.SyncAsync(linked, force, None); }
            finally { Gate.Release(); }
        }
        public UsbHeadsetProfile Profile => (UsbHeadsetProfile)State.Profile!;
        public byte[] this[RazerPaSetting setting] { get => _headset[setting]; set => _headset[setting] = value; }

        private Task<IReadOnlyDictionary<RazerPaSetting, byte[]>> ReadAsync(IReadOnlyList<RazerPaSetting> settings, CancellationToken ct)
        {
            Assert.Equal(0, Gate.CurrentCount); // only ever under the shared battery gate
            Reads++;
            if (Throw) throw new IOException("unplugged");
            return Task.FromResult<IReadOnlyDictionary<RazerPaSetting, byte[]>>(
                settings.Where(_headset.ContainsKey).ToDictionary(s => s, s => _headset[s]));
        }

        private Task<bool> WriteAsync(RazerPaSetting setting, byte[] values, CancellationToken ct)
        {
            Assert.Equal(0, Gate.CurrentCount);
            if (Throw) throw new IOException("unplugged");
            OnWrite?.Invoke();
            Writes.Add((setting, values));
            if (!Ignore) _headset[setting] = values;
            return Task.FromResult(!Ignore);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A 0550 receiver that acknowledges remote-mode changes and fails or stays silent on cue.</summary>
    private sealed class Receiver
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        public List<byte> Routes { get; } = [];
        public byte? ThrowOn { get; init; }
        public Action? OnGet { get; init; }
        public Task<byte[]> ReceiveAsync(CancellationToken ct) => _input.Reader.ReadAsync(ct).AsTask();

        public Task SendAsync(byte[] report, CancellationToken ct)
        {
            byte sequence = report[8], command = report[10];
            if (command == ThrowOn) throw new IOException("Barracuda replacement receiver stayed busy");
            if (command == 0xE1)
            {
                Routes.Add(report[11]);
                var frame = new byte[] { 0x50, 0x49, 1, 0, 0, 0, 0, 0, 3, 0, 0x0E, (byte)(sequence | 0x80), 0 };
                var input = new byte[64];
                input[0] = 2; input[1] = (byte)frame.Length;
                frame.CopyTo(input, 2);
                _input.Writer.TryWrite(input);
            }
            else OnGet?.Invoke();
            return Task.CompletedTask;
        }
    }
}

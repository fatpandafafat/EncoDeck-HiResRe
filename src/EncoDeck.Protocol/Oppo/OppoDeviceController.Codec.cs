// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Oppo;

public sealed partial class OppoDeviceController
{
    private Task? _codecPollTask;

    // Air5s 163 updates the queried cache from audio events and stream configuration. There is
    // no verified codec-change notification, so an initialization-only read can become stale.
    internal TimeSpan CodecPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    private bool IsAir5s => ProductId is 0x06C810 or 0x06E810;

    private void StartCodecPolling()
    {
        if (IsAir5s && !IsLeAudioTransport && _remoteCapabilities?.Allows(OppoCommand.QueryCodec) == true)
            _codecPollTask ??= PollCodecAsync(_keepAliveStop.Token);
    }

    private async Task PollCodecAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CodecPollInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && _session.IsOpen)
            {
                if (_store.Current.PendingCommands.Count > 0 || _store.Current.IsLeAudio || _store.Current.LinkViaBridge) continue;
                // One read, without the keep-alive retry: a codec observation must not force a
                // control-role handover on a transport that treats a repeated timeout as failure.
                var result = await RequestIfAllowedAsync(new OppoRequest(OppoCommand.QueryCodec, []), cancellationToken).ConfigureAwait(false);
                if (result?.Response is { } frame) ApplyCodec(frame, StateSource.QueryResponse);
                else if (result?.Status == RequestStatus.Timeout)
                    _store.TryUpdate(_identity.DeviceId, s => s with { Codec = null, ReportedCodecId = null });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OPPO codec polling stopped");
        }
    }

    private void ApplyCodec(OppoFrame frame, StateSource source)
    {
        var now = _time.GetUtcNow();
        var p = frame.Payload;
        Observed<byte>? reported = frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryCodec)
            && p.Length >= 2 && p[0] == OppoParsers.StatusOk ? Observed.From(p[1], now, source) : null;
        Observed<AudioCodec>? codec = OppoParsers.TryParseCodec(frame, out var parsed)
            && (!IsAir5s || parsed is AudioCodec.Sbc or AudioCodec.Aac) ? Observed.From(parsed, now, source) : null;
        // Air5s supports SBC/AAC. Its 163 firmware can translate another cached enum to 7/8;
        // neither that translation nor a PC-wide LHDC service proves an LHDC link to this model.
        // Keep the raw id for diagnostics; do not guess that an anomalous 7 means AAC.
        _store.TryUpdate(_identity.DeviceId, s => s with { Codec = codec, ReportedCodecId = reported });
    }
}

// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Holds discovered members until primary pairing and its LE control preparation finish.</summary>
internal sealed class PairingSetPreparation
{
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> WaitAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    public async Task CompleteAsync(bool primaryPaired, Func<CancellationToken, Task>? prepare, CancellationToken ct)
    {
        try
        {
            if (primaryPaired && prepare is not null) await prepare(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ready.TrySetResult(primaryPaired);
        }
        finally
        {
            // Failure or cancellation must release callbacks without starting member pairing.
            Stop();
        }
    }

    public void Stop() => _ready.TrySetResult(false);
}

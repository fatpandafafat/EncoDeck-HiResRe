// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>
/// Registers callback lifetime BEFORE touching WinRT event arguments. A native PairAsync completion
/// does not imply its set callbacks have completed (or even entered managed code yet).
/// </summary>
internal sealed class PairingSetDiscovery
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _firstCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closed;

    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_closed) return null;
            _active++;
            _firstCallback.TrySetResult();
            return new Callback(this);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            if (_active == 0) _drained.TrySetResult();
        }
    }

    public async Task<bool> WaitAndCloseAsync(TimeSpan discoveryTimeout, CancellationToken ct)
    {
        try { await _firstCallback.Task.WaitAsync(discoveryTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
        finally { Close(); }
        // Close the admission gate before taking the drain decision. A late callback cannot start
        // another native pairing after the caller begins its explicit-address fallback.
        await _drained.Task.WaitAsync(ct).ConfigureAwait(false);
        return _firstCallback.Task.IsCompletedSuccessfully;
    }

    private void Leave()
    {
        lock (_gate)
        {
            _active--;
            if (_closed && _active == 0) _drained.TrySetResult();
        }
    }

    private sealed class Callback(PairingSetDiscovery owner) : IDisposable
    {
        private PairingSetDiscovery? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}

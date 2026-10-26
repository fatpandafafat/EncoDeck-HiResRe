// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;

namespace EncoDeck.Tests.Bluetooth;

public class LeAudioSetPairingTests
{
    private const ulong Primary = 0x112233445566;
    private const ulong Secondary = 0x11223344A1B2;

    [Fact]
    public void NativeVectorWithoutIterable_IsReadWithoutRequestingAnEnumerator()
    {
        var nativeLike = new IndexOnlyVector<string>(["expected-peer", "other-member"]);
        Assert.Equal(["expected-peer", "other-member"], LeAudioSetPairing.SnapshotByIndex(nativeLike));
        Assert.Equal(0, nativeLike.EnumerationAttempts);
        Assert.Equal(2, nativeLike.IndexReads);
    }

    [Fact]
    public void EmptyNativeVector_DoesNotRequestEnumeratorOrReadAnElement()
    {
        var nativeLike = new IndexOnlyVector<string>([]);
        Assert.Empty(LeAudioSetPairing.SnapshotByIndex(nativeLike));
        Assert.Equal(0, nativeLike.EnumerationAttempts);
        Assert.Equal(0, nativeLike.IndexReads);
    }

    private sealed class IndexOnlyVector<T>(T[] values) : IReadOnlyList<T>
    {
        public int EnumerationAttempts { get; private set; }
        public int IndexReads { get; private set; }
        public int Count => values.Length;
        public T this[int index] { get { IndexReads++; return values[index]; } }
        public IEnumerator<T> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidCastException("Native vector exposes GetAt but not IIterable (E_NOINTERFACE).");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", true)]
    [InlineData("bluetoothle#bluetoothleaa:bb:cc:dd:ee:ff-11:22:33:44:A1:B2", true)]
    [InlineData("Bluetooth#Bluetoothaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2", false)]
    [InlineData("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:55:66", false)]
    [InlineData("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b3", false)]
    [InlineData("BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-aa:bb:cc:44:a1:b2", false)]
    [InlineData("OPPO Enco X4", false)]
    [InlineData("", false)]
    public void OnlyTheExactAdvertisedLePeerCanBePaired(string id, bool expected)
    {
        Assert.Equal(expected, LeAudioSetPairing.IsExpectedPeer(id, Primary, Secondary));
    }

    [Fact]
    public void InvalidPeerIdentityCannotAuthorizeSetPairing()
    {
        const string id = "BluetoothLE#BluetoothLEaa:bb:cc:dd:ee:ff-11:22:33:44:a1:b2";
        Assert.False(LeAudioSetPairing.IsExpectedPeer(id, Secondary, Secondary));
        Assert.False(LeAudioSetPairing.IsExpectedPeer(id, Primary, 0));
    }
}

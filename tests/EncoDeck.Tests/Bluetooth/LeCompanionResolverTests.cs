// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Bluetooth;

public sealed class LeCompanionResolverTests
{
    private const ulong Classic = 0x0011223344AA;
    private static readonly LeTarget Le = new(0xC0FFEE000001, true), Other = new(0xC0FFEE000002, true);
    private static readonly GattStreamEndpoint Bes = GattStreamEndpoints.Find(AkgBleUuids.BesService)!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<(LeTarget? Target, string? Name)> Calls(params (LeTarget? Target, string? Name)[] calls) => [.. calls];

    private static LeAdvertisement Ad(LeTarget target, string? name, params Guid[] services) => new(target, name, services);

    [Theory]
    [InlineData("AKG N400NC", "AKG N400NC", true)]
    [InlineData("AKG N400NC", "akg n400nc", true)]
    [InlineData("AKG N9 Hybrid", "AKG N9 Hybrid-LE", true)]
    [InlineData("AKG N9 Hybrid", "AKG N9 Hybrid LE", true)]
    [InlineData("AKG N9 Hybrid", "AKG N9 Hybrid-LE2", false)]
    [InlineData("AKG N400NC", "AKG N400", false)]
    [InlineData("AKG N400NC", null, false)]
    [InlineData("", "", false)]
    public void NameMatchingIsNormalizedAndAcceptsTheLeSuffixOnly(string classic, string? advertised, bool expected) =>
        Assert.Equal(expected, LeCompanionResolver.NameMatches(classic, advertised));

    [Fact]
    public void SelectionBindsOnlyASingleNameAndServiceMatch()
    {
        Guid service = AkgBleUuids.BesService;
        Assert.Equal(((LeTarget?)null, 0), LeCompanionResolver.Select("AKG N400NC", service, [Ad(Le, "JBL Live"), Ad(Other, null)]));
        // A matching name with a different advertised service list is not a candidate; an empty list is.
        Assert.Equal(((LeTarget?)Le, 1), LeCompanionResolver.Select("AKG N400NC", service, [Ad(Le, "AKG N400NC"), Ad(Other, "AKG N400NC", Guid.NewGuid())]));
        Assert.Equal(((LeTarget?)Le, 1), LeCompanionResolver.Select("AKG N400NC", service, [Ad(Le, "AKG N400NC-LE", service), Ad(Le, "AKG N400NC")]));
        Assert.Equal(((LeTarget?)null, 2), LeCompanionResolver.Select("AKG N400NC", service, [Ad(Le, "AKG N400NC", service), Ad(Other, "AKG N400NC-LE")]));
    }

    /// <summary>Records which targets were opened; the classic address and named targets succeed per test.</summary>
    private sealed class Opener
    {
        public bool ClassicOpens { get; set; }
        public HashSet<LeTarget> Reachable { get; } = [];
        public List<(LeTarget? Target, string? Name)> Calls { get; } = [];
        public int Scans { get; private set; }
        public IReadOnlyList<LeAdvertisement> Advertisements { get; set; } = [];

        public Task<ChannelOpenResult> Open(LeTarget? target, string? name, CancellationToken ct)
        {
            Calls.Add((target, name));
            bool ok = target is null ? ClassicOpens : Reachable.Contains(target);
            return Task.FromResult(ok ? new ChannelOpenResult(ChannelOpenStatus.Opened, new FakeByteChannel(), Bes.Service, null)
                : ChannelOpenResult.Failed(target is null ? ChannelOpenStatus.ServiceNotFound : ChannelOpenStatus.ControlEndpointNotFound, "missing"));
        }

        public Task<IReadOnlyList<LeAdvertisement>> Scan(CancellationToken ct) { Scans++; return Task.FromResult(Advertisements); }
    }

    private static Task<ChannelOpenResult> Resolve(Opener opener, LeCompanionCache cache, string? name = "AKG N400NC", GattStreamEndpoint? endpoint = null) =>
        LeCompanionResolver.OpenAsync(Classic, name, endpoint ?? Bes, cache, opener.Open, opener.Scan, NullLogger.Instance, Ct);

    [Fact]
    public async Task ClassicAddressWinsAndNeverScans()
    {
        var opener = new Opener { ClassicOpens = true };
        Assert.Equal(ChannelOpenStatus.Opened, (await Resolve(opener, new())).Status);
        Assert.Equal(0, opener.Scans);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ZeroOrSeveralCandidatesFailWithoutBinding(int candidates)
    {
        var opener = new Opener { Advertisements = candidates == 0 ? [Ad(Le, "Other")] : [Ad(Le, "AKG N400NC"), Ad(Other, "AKG N400NC-LE")] };
        opener.Reachable.UnionWith([Le, Other]);
        var cache = new LeCompanionCache();
        var result = await Resolve(opener, cache);
        Assert.Equal(ChannelOpenStatus.ServiceNotFound, result.Status);
        Assert.Contains($"{candidates} LE advertiser(s)", result.Detail);
        Assert.Equal(Calls((null, null)), opener.Calls);
        Assert.False(cache.TryGet(Classic, out _));
    }

    [Fact]
    public async Task SingleCandidateIsBoundCachedAndReusedUntilItFails()
    {
        var opener = new Opener { Advertisements = [Ad(Le, "AKG N400NC-LE", AkgBleUuids.BesService)] };
        opener.Reachable.Add(Le);
        var cache = new LeCompanionCache();
        Assert.Equal(ChannelOpenStatus.Opened, (await Resolve(opener, cache)).Status);
        Assert.Equal(Calls((null, null), (Le, "AKG N400NC")), opener.Calls);
        Assert.True(cache.TryGet(Classic, out var cached) && cached == Le);

        // Cache hit: straight to the companion, no classic attempt and no scan.
        opener.Calls.Clear();
        Assert.Equal(ChannelOpenStatus.Opened, (await Resolve(opener, cache)).Status);
        Assert.Equal(Calls((Le, "AKG N400NC")), opener.Calls);
        Assert.Equal(1, opener.Scans);

        // The companion stops answering (or reports another name): forget it and resolve again.
        opener.Reachable.Clear();
        opener.Reachable.Add(Other);
        opener.Advertisements = [Ad(Other, "AKG N400NC")];
        opener.Calls.Clear();
        Assert.Equal(ChannelOpenStatus.Opened, (await Resolve(opener, cache)).Status);
        Assert.Equal(Calls((Le, "AKG N400NC"), (null, null), (Other, "AKG N400NC")), opener.Calls);
        Assert.True(cache.TryGet(Classic, out cached) && cached == Other);

        opener.Reachable.Clear();
        opener.Advertisements = [];
        Assert.NotEqual(ChannelOpenStatus.Opened, (await Resolve(opener, cache)).Status);
        Assert.False(cache.TryGet(Classic, out _));
    }

    [Fact]
    public async Task SlowClassicAttemptIsCancelledAtTheCapAndTheScanPathIsTaken()
    {
        var cache = new LeCompanionCache();
        bool classicCancelled = false;
        async Task<ChannelOpenResult> Open(LeTarget? target, string? name, CancellationToken ct)
        {
            if (target is not null) return new ChannelOpenResult(ChannelOpenStatus.Opened, new FakeByteChannel(), Bes.Service, null);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { classicCancelled = true; throw; }
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "unreachable");
        }
        Task<IReadOnlyList<LeAdvertisement>> Scan(CancellationToken ct) => Task.FromResult<IReadOnlyList<LeAdvertisement>>([Ad(Le, "AKG N400NC-LE")]);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await LeCompanionResolver.OpenAsync(Classic, "AKG N400NC", Bes, cache, Open, Scan, NullLogger.Instance, Ct, TimeSpan.FromMilliseconds(50));
        Assert.Equal(ChannelOpenStatus.Opened, result.Status);
        Assert.True(classicCancelled);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(cache.TryGet(Classic, out var cached) && cached == Le);
        Assert.Equal(TimeSpan.FromSeconds(4), LeCompanionResolver.ClassicAttemptCap);

        // Cancelling the caller is still reported as cancellation, not as a missing endpoint.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LeCompanionResolver.OpenAsync(Classic, "AKG N400NC", Bes, new(), Open, Scan, NullLogger.Instance, cancelled.Token));
    }

    [Fact]
    public async Task EndpointsWithoutTheFallbackAndUnnamedDevicesUseTheClassicAddressOnly()
    {
        var opener = new Opener { Advertisements = [Ad(Le, "WH-1000XM6")] };
        opener.Reachable.Add(Le);
        var sony = new GattStreamEndpoint(SonyAkgProfiles.BleServiceUuid, SonyAkgProfiles.BleWriteUuid, SonyAkgProfiles.BleNotifyUuid, "Sony MDR GATT");
        Assert.False(sony.NameMatchedLeFallback);
        Assert.NotEqual(ChannelOpenStatus.Opened, (await Resolve(opener, new(), "WH-1000XM6", sony)).Status);
        Assert.NotEqual(ChannelOpenStatus.Opened, (await Resolve(opener, new(), null)).Status);
        Assert.Equal(0, opener.Scans);
        Assert.True(GattStreamEndpoints.Find(AkgBleUuids.RaceService)!.NameMatchedLeFallback);
    }
}

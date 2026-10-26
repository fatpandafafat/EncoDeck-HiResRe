// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using Microsoft.Extensions.Logging;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Oppo.Models;

/// <summary><see cref="IDeviceProfile"/> backed by an <see cref="OppoModelDefinition"/>.</summary>
public sealed class OppoDeviceProfile(OppoModelDefinition definition) : IControllableProfile
{
    private static readonly string[] GenericNameHints = ["OPPO", "Enco", "OnePlus Buds", "OnePlus Nord Buds", "realme Buds", "DIZO"];

    public OppoModelDefinition Definition { get; } = definition;

    public string ModelId => Definition.ModelId;

    public string Brand => "oppo";

    public bool IsGeneric => Definition.IsGeneric;

    public string DisplayName => Definition.DisplayName;

    public string ImageKey => Definition.ImageKey;

    public DeviceForm Form => Definition.Form;

    public IDeviceCapabilities Capabilities => Definition.Capabilities;

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; } = definition.NoiseModes.Select(m => m.Option).ToArray();

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; } = definition.EqualizerPresets.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();

    public IReadOnlyList<Guid> ControlServiceUuids => Definition.ServiceUuids;

    public int MatchScore(DeviceIdentity identity)
    {
        if (Definition.BluetoothNames.Any(n => string.Equals(n, identity.Name, StringComparison.OrdinalIgnoreCase))) return 100;
        if (Definition.IsGeneric && GenericNameHints.Any(h => identity.Name.Contains(h, StringComparison.OrdinalIgnoreCase))) return 1;
        return 0;
    }

    /// <summary>
    /// The profile to show for earbuds before (or while) they connect: the model of the product id
    /// they reported last time (falling back to the name match), adapted to the capabilities and
    /// firmware they reported last time, so the settings page looks the same on every connection.
    /// </summary>
    public static OppoDeviceProfile Remembered(OppoDeviceProfile matched, uint? productId, byte[]? capabilities, int? firmware)
    {
        var model = (productId is { } pid ? OppoModels.FindByProductId(pid) : null) ?? matched.Definition.Base ?? matched.Definition;
        var bitmap = capabilities is { Length: > 0 } bytes ? new OppoCapabilityBitmap(bytes) : null;
        if (bitmap is null && firmware is null && ReferenceEquals(model, matched.Definition)) return matched;
        return new OppoDeviceProfile(model.Adapt(bitmap, firmware));
    }

    public IControllableProfile Remember(DeviceState known) => Remembered(this, known.ProductId, known.ControlCapabilities, known.BudFirmware);

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context)
    {
        var session = new OppoSession(channel, context.Loggers.CreateLogger<OppoSession>(), context.Time, context.Trace);
        session.Start();
        return new OppoDeviceController(session, this, context.Identity, context.Store, context.AllowExperimental, context.Loggers.CreateLogger<OppoDeviceController>(), context.Time)
        {
            OwnsSession = true,
            KeepAliveInterval = channel.KeepAliveInterval,
        };
    }

    /// <summary>Profiles for every built-in model plus the read-only generic fallback.</summary>
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. OppoModels.All.Select(m => new OppoDeviceProfile(m)), new OppoDeviceProfile(OppoModels.GenericOppo)];
}

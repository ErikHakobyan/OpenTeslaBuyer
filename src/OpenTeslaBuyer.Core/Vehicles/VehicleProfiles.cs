using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>Turns a vehicle's CAN frames into <see cref="BatteryData"/>. One instance per connection: profiles keep decoding state.</summary>
public interface IVehicleProfile
{
    string Name { get; }

    /// <summary>The messages to listen for. May change once a profile has identified the car.</summary>
    IReadOnlyList<MonitoredId> MonitoredIds { get; }

    void Process(CanFrame frame, BatteryData data);
}

public static class VehicleProfiles
{
    public static IVehicleProfile Create(VehiclePlatform platform, EnergyLayout layout = EnergyLayout.Auto) => platform switch
    {
        VehiclePlatform.Model3Family => new Model3Profile(layout),
        VehiclePlatform.LegacyModelSX => new ModelSXProfile(layout),
        _ => new AutoDetectProfile(layout),
    };

    public static string Describe(VehiclePlatform platform) => platform switch
    {
        VehiclePlatform.Model3Family => "Model 3, Model Y, 2021+ Model S/X",
        VehiclePlatform.LegacyModelSX => "Model S/X (2012–2021)",
        _ => "Detect automatically",
    };

    /// <summary>The energy message's ID on each platform.</summary>
    public static uint EnergyMessageId(VehiclePlatform platform) =>
        platform == VehiclePlatform.LegacyModelSX ? ModelSXSignals.Energy : Model3Signals.Energy;

    /// <summary>Stores one page of a VIN message (bytes 1–7, padding ignored) and publishes the VIN once all pages are in.</summary>
    internal static void CollectVinPart(byte[] d, byte[] pages, string?[] parts, BatteryData data)
    {
        var part = Array.IndexOf(pages, d[0]);
        if (part < 0)
            return;

        parts[part] = new string(d.Skip(1).Select(b => (char)b).Where(char.IsAsciiLetterOrDigit).ToArray());
        if (parts.All(p => p is not null) && string.Concat(parts) is { Length: 17 } vin)
            data.Vin = vin;
    }
}

/// <summary>
/// Listens for both platforms' messages and commits to one once at least two of its signature messages, which
/// the other platform does not use, have been seen. Frames received before that are replayed into the chosen profile.
/// </summary>
public sealed class AutoDetectProfile(EnergyLayout layout = EnergyLayout.Auto) : IVehicleProfile
{
    private const int MaxBufferedFrames = 5000;
    private const int SignatureThreshold = 2;

    private static readonly (VehiclePlatform Platform, uint[] Ids)[] Signatures =
    [
        (VehiclePlatform.Model3Family, [Model3Signals.Energy, Model3Signals.SocStatus, Model3Signals.BrickVoltages, Model3Signals.Vin]),
        (VehiclePlatform.LegacyModelSX, [ModelSXSignals.Energy, ModelSXSignals.BrickData, ModelSXSignals.Vin, ModelSXSignals.Odometer]),
    ];

    private readonly Queue<CanFrame> _buffer = new();
    private readonly HashSet<uint> _seen = [];
    private readonly IReadOnlyList<MonitoredId> _allIds = new Model3Profile().MonitoredIds
        .Concat(new ModelSXProfile().MonitoredIds)
        .DistinctBy(id => id.Id)
        .ToList();

    private IVehicleProfile? _chosen;

    public string Name => _chosen?.Name ?? "Detecting the platform…";

    public IReadOnlyList<MonitoredId> MonitoredIds => _chosen?.MonitoredIds ?? _allIds;

    public void Process(CanFrame frame, BatteryData data)
    {
        if (_chosen is not null)
        {
            _chosen.Process(frame, data);
            return;
        }

        _buffer.Enqueue(frame);
        if (_buffer.Count > MaxBufferedFrames)
            _buffer.Dequeue();
        _seen.Add(frame.Id);

        var matches = Signatures.Where(s => s.Ids.Count(_seen.Contains) >= SignatureThreshold).ToList();
        if (matches.Count != 1)
            return;

        _chosen = VehicleProfiles.Create(matches[0].Platform, layout);
        while (_buffer.TryDequeue(out var buffered))
            _chosen.Process(buffered, data);
    }
}

using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using static OpenTeslaBuyer.Core.Vehicles.ModelSXSignals;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>
/// Model S 2012–2021 and Model X 2015–2021, read from the powertrain CAN bus. Unlike the Model 3 platform, these
/// cars' BMS does not broadcast the pack's capacity when new, its current scaling, or the rated consumption, so
/// original capacity has to be entered and range at 100% is not available.
/// </summary>
public sealed class ModelSXProfile(EnergyLayout forcedLayout = EnergyLayout.Auto) : IVehicleProfile
{
    private const int InvalidRaw = 0x3FFF;

    private readonly EnergyLayoutDetector _detector = new(EnergyLayout.Bits11, EnergyLayout.Bits10);
    private readonly string?[] _vinParts = new string?[VinPages.Length];
    private readonly SortedDictionary<int, double> _temperatures = [];
    private readonly HashSet<int> _voltagePagesSeen = [];
    private EnergyLayout _layout = forcedLayout;

    public string Name => VehicleProfiles.Describe(VehiclePlatform.LegacyModelSX);

    public IReadOnlyList<MonitoredId> MonitoredIds { get; } =
    [
        new(Energy, FramesPerVisit: 4),
        new(SocStatus, FramesPerVisit: 1),
        new(HvBusStatus, FramesPerVisit: 1),
        new(KwhCounter, FramesPerVisit: 1),
        new(Odometer, FramesPerVisit: 1),
        new(Vin, FramesPerVisit: 4),
        new(BrickData, FramesPerVisit: 40),
        new(DcDc, FramesPerVisit: 1, PollEvery: 2),
        new(Country, FramesPerVisit: 1, PollEvery: 8),
        new(BatteryOdometer, FramesPerVisit: 1, PollEvery: 4),
    ];

    public void Process(CanFrame frame, BatteryData data)
    {
        var d = frame.Data;
        data.Platform = VehiclePlatform.LegacyModelSX;
        switch (frame.Id)
        {
            case Energy:
                if (_layout == EnergyLayout.Auto)
                    _layout = _detector.Detect(d, data.SocUiPercent);
                data.EnergyLayout = _layout;
                EnergyStatus.Decode(_layout, d, data);
                break;

            case SocStatus:
                data.SocMinPercent = Model3Profile.Plausible(SocMin.Decode(d), 0, 100) ?? data.SocMinPercent;
                data.SocUiPercent = Model3Profile.Plausible(SocUi.Decode(d), 0, 100) ?? data.SocUiPercent;
                break;

            case HvBusStatus:
                data.PackVoltage = PackVoltage.Decode(d) ?? data.PackVoltage;
                break;

            case KwhCounter:
                data.DischargeTotalKWh = DischargeTotal.Decode(d) ?? data.DischargeTotalKWh;
                data.ChargeTotalKWh = ChargeTotal.Decode(d) ?? data.ChargeTotalKWh;
                break;

            case Odometer:
                if (OdometerMiles.Decode(d) is { } miles)
                    data.OdometerKm = miles * Units.KmPerMile;
                break;

            case BatteryOdometer:
                if (BatteryOdometerMiles.Decode(d) is { } batteryMiles)
                    data.BatteryOdometerKm = batteryMiles * Units.KmPerMile;
                break;

            case BrickData:
                ProcessBrickData(d, data);
                break;

            case DcDc:
                // No separate 12 V reading on these cars: the converter's output is the 12 V system while awake.
                data.DcDcVolts = Model3Profile.Plausible(DcDcOutputVoltage.Decode(d), 5, 20) ?? data.DcDcVolts;
                data.DcDcAmps = DcDcOutputCurrent.Decode(d) ?? data.DcDcAmps;
                break;

            case Vin:
                VehicleProfiles.CollectVinPart(d, VinPages, _vinParts, data);
                break;

            case Country:
                // OVMS reads the country from bytes 0-1, comma.ai's DBC from bytes 2-3; accept whichever holds letters.
                var country = (d.Length >= 2 ? CarInfo.Country(d[0], d[1]) : null)
                              ?? (d.Length >= 4 ? CarInfo.Country(d[2], d[3]) : null);
                if (country is not null)
                    data.CarInfo["country"] = country;
                break;
        }
    }

    private void ProcessBrickData(byte[] d, BatteryData data)
    {
        var page = (int)BrickPage.Raw(d);
        var voltages = page < VoltagePages;
        var slots = voltages ? BrickVoltageSlots : TemperatureSlots;
        if (voltages)
            _voltagePagesSeen.Add(page);

        for (var slot = 0; slot < slots.Length; slot++)
        {
            // OVMS treats all-zero and all-one fields as "not available".
            var raw = slots[slot].Raw(d);
            if (raw is 0 or InvalidRaw || slots[slot].Decode(d) is not { } value)
                continue;

            if (voltages)
            {
                if (value is >= 1.5 and <= 4.5)
                    data.BrickVoltages[page * slots.Length + slot] = value;
            }
            else if (value is >= -40 and <= 90)
            {
                _temperatures[(page - VoltagePages) * slots.Length + slot] = value;
            }
        }

        // Pages beyond the pack's last cell group carry no valid voltages, so after a full sweep the count is final.
        if (_voltagePagesSeen.Count == VoltagePages && data.BrickVoltages.Count > 0)
            data.CellGroupCount = data.BrickVoltages.Count;

        if (_temperatures.Count > 0)
        {
            data.TempMinC = _temperatures.Values.Min();
            data.TempMaxC = _temperatures.Values.Max();
        }
    }
}

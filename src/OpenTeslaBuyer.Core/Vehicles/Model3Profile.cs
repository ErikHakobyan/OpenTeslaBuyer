using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using static OpenTeslaBuyer.Core.Vehicles.Model3Signals;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>
/// The Model 3 platform's vehicle-CAN battery messages: Model 3 and Model Y built 2017–2023, which this was built
/// against, and per ScanMyTesla also the 2021+ Model S/X. Later Model 3/Y facelifts are untested.
/// </summary>
public sealed class Model3Profile(EnergyLayout forcedLayout = EnergyLayout.Auto) : IVehicleProfile
{
    private readonly EnergyLayoutDetector _detector = new(EnergyLayout.Multiplexed, EnergyLayout.Bits11, EnergyLayout.Bits10);
    private readonly string?[] _vinParts = new string?[VinPages.Length];
    private EnergyLayout _layout = forcedLayout;

    public string Name => VehicleProfiles.Describe(VehiclePlatform.Model3Family);

    /// <summary>Battery messages first, then the alert matrices, battery-related controllers before the rest.</summary>
    public IReadOnlyList<MonitoredId> MonitoredIds { get; } =
    [
        new(Energy, FramesPerVisit: 4),
        new(SocStatus, FramesPerVisit: 1),
        new(HvBusStatus, FramesPerVisit: 2),
        new(BrickMinMax, FramesPerVisit: 4),
        new(KwhCounter, FramesPerVisit: 1),
        new(RangeSoc, FramesPerVisit: 1),
        new(Odometer, FramesPerVisit: 1),
        new(Vin, FramesPerVisit: 6),
        new(BrickVoltages, FramesPerVisit: 64),
        new(BmsStatus, FramesPerVisit: 1, PollEvery: 2),
        new(KwhCountersMultiplexed, FramesPerVisit: 8, PollEvery: 4),
        new(ParkingBrakeLeft, FramesPerVisit: 1, PollEvery: 4),
        new(GatewayConfig.Id, FramesPerVisit: 6, PollEvery: 8),
        .. AlertCatalog.Model3Platform.Messages.Select(m =>
            new MonitoredId(m.Id, FramesPerVisit: m.PageCount, PollEvery: AlertDescriptions.Priority(m.Ecu) <= 1 ? 4 : 12)),
    ];

    public void Process(CanFrame frame, BatteryData data)
    {
        var d = frame.Data;
        data.Platform = VehiclePlatform.Model3Family;
        if (AlertCatalog.Model3Platform.TryGetMessage(frame.Id, out var alerts))
        {
            foreach (var (code, active) in alerts.Decode(d))
                data.CarAlerts[code] = active;
            return;
        }

        switch (frame.Id)
        {
            case Energy:
                if (_layout == EnergyLayout.Auto)
                    _layout = _detector.Detect(d, data.SocUiPercent);
                data.EnergyLayout = _layout;
                EnergyStatus.Decode(_layout, d, data);
                break;

            case SocStatus:
                data.SocMinPercent = SocMin.Decode(d) ?? data.SocMinPercent;
                data.SocUiPercent = SocUi.Decode(d) ?? data.SocUiPercent;
                data.SocMaxPercent = SocMax.Decode(d) ?? data.SocMaxPercent;
                data.SocAveragePercent = SocAverage.Decode(d) ?? data.SocAveragePercent;
                data.InitialFullPackKWh = Plausible(InitialFullPackEnergy.Decode(d), 20, 150) ?? data.InitialFullPackKWh;
                break;

            case HvBusStatus:
                data.PackVoltage = PackVoltage.Decode(d) ?? data.PackVoltage;
                data.PackCurrent = Plausible(PackCurrent.Decode(d), -1600, 1600) ?? data.PackCurrent; // raw -16384 is SNA
                break;

            case BrickMinMax:
                if (BrickMinMaxPage.Raw(d) == 0)
                {
                    data.TempMaxC = TempMax.Decode(d) ?? data.TempMaxC;
                    data.TempMinC = TempMin.Decode(d) ?? data.TempMinC;
                }
                else if (BrickMinMaxPage.Raw(d) == 1)
                {
                    data.BrickVoltageMax = BrickVoltageMax.Decode(d) ?? data.BrickVoltageMax;
                    data.BrickVoltageMin = BrickVoltageMin.Decode(d) ?? data.BrickVoltageMin;
                    data.BrickNumberMax = (int?)BrickNumberMax.Decode(d) ?? data.BrickNumberMax;
                    data.BrickNumberMin = (int?)BrickNumberMin.Decode(d) ?? data.BrickNumberMin;
                }

                break;

            case KwhCounter:
                data.DischargeTotalKWh = DischargeTotal.Decode(d) ?? data.DischargeTotalKWh;
                data.ChargeTotalKWh = ChargeTotal.Decode(d) ?? data.ChargeTotalKWh;
                break;

            case RangeSoc:
                if (Plausible(RatedWhPerMile.Decode(d), 50, 1000) is { } whPerMile)
                    data.RatedWhPerKm = whPerMile / Units.KmPerMile;
                break;

            case Odometer:
                data.OdometerKm = OdometerKm.Decode(d) ?? data.OdometerKm;
                break;

            case BrickVoltages:
                var page = (int)BrickPage.Raw(d);
                for (var slot = 0; slot < BrickSlots.Length; slot++)
                {
                    if (Plausible(BrickSlots[slot].Decode(d), 1.5, 4.5) is { } volts)
                        data.BrickVoltages[page * BrickSlots.Length + slot] = volts;
                }

                break;

            case Vin:
                VehicleProfiles.CollectVinPart(d, VinPages, _vinParts, data);
                break;

            case GatewayConfig.Id:
                GatewayConfig.Decode(d, data.CarInfo);
                break;

            case BmsStatus:
                data.IsolationResistanceKOhm = IsolationResistance.Decode(d) ?? data.IsolationResistanceKOhm;
                break;

            case KwhCountersMultiplexed:
                if (KwhCounterValue.Decode(d) is { } kwh)
                {
                    switch (KwhCounterPage.Raw(d))
                    {
                        case 0: data.AcChargeTotalKWh = kwh; break;
                        case 1: data.DcChargeTotalKWh = kwh; break;
                        case 2: data.RegenChargeTotalKWh = kwh; break;
                        case 3: data.DriveDischargeTotalKWh = kwh; break;
                    }
                }

                break;

            case ParkingBrakeLeft:
                data.TwelveVoltVolts = Plausible(TwelveVolt.Decode(d), 5, 20) ?? data.TwelveVoltVolts;
                break;
        }
    }

    internal static double? Plausible(double? value, double min, double max) =>
        value is { } v && v >= min && v <= max ? v : null;
}

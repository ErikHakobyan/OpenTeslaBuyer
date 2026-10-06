using System.Runtime.CompilerServices;
using System.Text;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;
using M3 = OpenTeslaBuyer.Core.Vehicles.Model3Signals;
using SX = OpenTeslaBuyer.Core.Vehicles.ModelSXSignals;

namespace OpenTeslaBuyer.Core.Adapters;

public enum SimulatedCar
{
    /// <summary>2018 Model 3 Long Range, newer firmware (multiplexed energy message).</summary>
    Model3,

    /// <summary>2015 Model S 85, original 10-bit energy message.</summary>
    ModelS,

    /// <summary>The Model 3 at a Supercharger: resting, charging at about 95 kW, then stopping. Runs four times faster than real time.</summary>
    Model3Charging,

    /// <summary>The Model S on an 11 kW home charger: resting, charging, then stopping. Runs four times faster than real time.</summary>
    ModelSCharging,
}

/// <summary>
/// Generates the battery-related CAN traffic of a parked car with a moderately worn pack, at roughly the real
/// message rates; the charging simulators add a charging session. Lets the UI be developed and demonstrated without a car.
/// </summary>
public sealed class SimulatorAdapter(SimulatedCar car = SimulatedCar.Model3, TimeSpan? tick = null) : ICanAdapter
{
    public static class Model3Values
    {
        public const double OriginalKWh = 80.5;
        public const double FullPackKWh = 71.6;
        public const double BufferKWh = 3.0;
        public const double SocPercent = 64.3;
        public const int BrickCount = 96;
        public const string VinTemplate = "5YJ3E1EB0JF100123";
        public const double ChargeTotalKWh = 41_234.567;
        public const double DischargeTotalKWh = 39_876.123;

        /// <summary>Active for the whole simulation.</summary>
        public const string PersistentAlert = "BMS_a064";

        /// <summary>Active for the first <see cref="TransientAlertTicks"/> ticks only, so it ends up in the history.</summary>
        public const string TransientAlert = "PCS_a019";

        public const int TransientAlertTicks = 200;

        /// <summary>Raw gateway configuration codes: a 2018 Long Range AWD, US maps, HW2.5, Enhanced Autopilot.</summary>
        public static readonly IReadOnlyDictionary<string, ulong> Configuration = new Dictionary<string, ulong>
        {
            ["drivetrain"] = 1,
            ["steering"] = 0,
            ["autopilotHardware"] = 3,
            ["autopilot"] = 2,
            ["supercharging"] = 1,
            ["exteriorColor"] = 3,
            ["mapRegion"] = 0,
            ["performance"] = 0,
            ["towPackage"] = 0,
            ["chassis"] = 2,
            ["connectivity"] = 1,
            ["audio"] = 1,
            ["pack"] = 1,
            ["softRange"] = 0,
            ["wheels"] = 0,
            ["immersiveAudio"] = 2,
            ["twelveVBattery"] = 0,
            ["birthday"] = 1_529_020_800, // 2018-06-15
        };
    }

    public static class ModelSValues
    {
        public const double FullPackKWh = 74.6;
        public const double BufferKWh = 3.3;
        public const double SocPercent = 71.5;
        public const int BrickCount = 96;
        public const int TemperatureSensors = 32;
        public const double OdometerMiles = 98_765.4;
        public const double ChargeTotalKWh = 66_890.2;
        public const double DischargeTotalKWh = 61_234.5;

        /// <summary>Less than the car's: the simulated car had its pack replaced, so the buyer check has something to find.</summary>
        public const double BatteryOdometerMiles = 41_200.0;
        public const string VinTemplate = "5YJSA1E20FF100456";
    }

    /// <summary>The charging simulators' electrical model and timeline (in simulated seconds).</summary>
    public static class ChargingValues
    {
        /// <summary>Simulated seconds per real second, so a whole test fits in under a minute.</summary>
        public const double TimeLapse = 4;

        /// <summary>The weak cell group (zero-based): it has extra resistance on top of being low at rest.</summary>
        public const int WeakGroup = 56;

        public const double Model3GroupMilliOhm = 0.48;
        public const double Model3WeakExtraMilliOhm = 0.20;
        public const double ModelSGroupMilliOhm = 0.61;
        public const double ModelSWeakExtraMilliOhm = 0.25;

        /// <summary>Busbars, contactors and fuse, in series with the cell groups.</summary>
        public const double ConnectionOhm = 0.015;

        public static double GroupMilliOhm(SimulatedCar car, int group)
        {
            var model3 = car == SimulatedCar.Model3Charging;
            var typical = model3 ? Model3GroupMilliOhm : ModelSGroupMilliOhm;
            var spread = typical * (1 + 0.03 * Math.Sin(1.7 * group)); // a few percent of normal variation
            return group == WeakGroup ? spread + (model3 ? Model3WeakExtraMilliOhm : ModelSWeakExtraMilliOhm) : spread;
        }
    }

    private readonly Random _random = new(42);

    public string Name => "Simulator";

    public static string SimulatedVin(SimulatedCar car = SimulatedCar.Model3)
    {
        var vin = new StringBuilder(IsModelS(car) ? ModelSValues.VinTemplate : Model3Values.VinTemplate);
        vin[8] = VinInfo.ComputeCheckDigit(vin.ToString());
        return vin.ToString();
    }

    public async IAsyncEnumerable<CanFrame> StreamAsync(Func<IReadOnlyList<MonitoredId>> ids, Action<string> log, [EnumeratorCancellation] CancellationToken ct)
    {
        log(car switch
        {
            SimulatedCar.ModelS => "Simulating a parked 2015 Model S 85. No hardware involved.",
            SimulatedCar.Model3Charging => "Simulating the 2018 Model 3 at a Supercharger, four times faster than real time: resting, charging, then stopping. No hardware involved.",
            SimulatedCar.ModelSCharging => "Simulating the 2015 Model S 85 on an 11 kW home charger, four times faster than real time: resting, charging, then stopping. No hardware involved.",
            _ => "Simulating a parked 2018 Model 3 Long Range. No hardware involved.",
        });
        var interval = tick ?? TimeSpan.FromMilliseconds(50);
        var modelS = IsModelS(car);
        var brickCount = modelS ? ModelSValues.BrickCount : Model3Values.BrickCount;
        var nominalVolts = modelS ? 3.982 : 3.861;
        var bricks = Enumerable.Range(0, brickCount).Select(_ => nominalVolts + (_random.NextDouble() - 0.5) * 0.006).ToArray();
        bricks[56] -= modelS ? 0.024 : 0.012; // one weak cell group; on the Model S enough for a tool warning
        var vin = SimulatedVin(car);
        var session = car is SimulatedCar.Model3Charging or SimulatedCar.ModelSCharging ? new ChargingSession(car, DateTimeOffset.UtcNow) : null;

        for (long t = 0; ; t++)
        {
            await Task.Delay(interval, ct);
            var live = session?.Advance(t, bricks);
            var frames = modelS ? ModelSFrames(t, bricks, vin, live) : Model3Frames(t, bricks, vin, live);
            foreach (var frame in frames)
                yield return frame;
        }
    }

    private static bool IsModelS(SimulatedCar car) => car is SimulatedCar.ModelS or SimulatedCar.ModelSCharging;

    private IEnumerable<CanFrame> Model3Frames(long t, double[] bricks, string vin, LiveState? live = null)
    {
        var now = live?.Time ?? DateTimeOffset.UtcNow;
        const double full = Model3Values.FullPackKWh, buffer = Model3Values.BufferKWh;
        var soc = live?.Soc ?? Model3Values.SocPercent;

        var page = (int)(t % 32);
        Jitter(bricks, page * 3, 3);
        var cells = live?.Groups ?? bricks;
        yield return Frame(M3.BrickVoltages, now, d =>
        {
            M3.BrickPage.EncodeRaw(d, (ulong)page);
            for (var slot = 0; slot < 3; slot++)
                M3.BrickSlots[slot].Encode(d, cells[page * 3 + slot]);
        });

        if (t % 4 == 1)
            yield return Frame(M3.BmsStatus, now, d => M3.IsolationResistance.Encode(d, 4_500));

        if (t % 2 == 0)
        {
            yield return Frame(M3.HvBusStatus, now, d =>
            {
                M3.PackVoltage.Encode(d, live?.PackVolts ?? bricks.Sum());
                M3.PackCurrent.Encode(d, (live?.Amps ?? -0.8) + (_random.NextDouble() - 0.5) * 0.4);
            });
        }

        if (live is not null && t % 5 == 0)
            yield return CounterFrame(M3.KwhCounter, now, live);

        if (t % 5 == 0)
        {
            var minMaxPage = t / 5 % 2;
            yield return Frame(M3.BrickMinMax, now, d =>
            {
                M3.BrickMinMaxPage.EncodeRaw(d, (ulong)minMaxPage);
                if (minMaxPage == 0)
                {
                    M3.TempMax.Encode(d, 23.0);
                    M3.TempMin.Encode(d, 21.5);
                }
                else
                {
                    M3.BrickVoltageMax.Encode(d, cells.Max());
                    M3.BrickVoltageMin.Encode(d, cells.Min());
                    M3.BrickNumberMax.Encode(d, Array.IndexOf(cells, cells.Max()) + 1);
                    M3.BrickNumberMin.Encode(d, Array.IndexOf(cells, cells.Min()) + 1);
                }
            }, length: 6);
        }

        if (t % 10 == 0)
        {
            var energyPage = t / 10 % 2;
            var remaining = buffer + soc / 100 * (full - buffer);
            yield return Frame(M3.Energy, now, d =>
            {
                EnergyStatus.Multiplexed.Page.EncodeRaw(d, (ulong)energyPage);
                if (energyPage == 0)
                {
                    EnergyStatus.Multiplexed.NominalFullPack.Encode(d, full);
                    EnergyStatus.Multiplexed.NominalRemaining.Encode(d, remaining);
                    EnergyStatus.Multiplexed.IdealRemaining.Encode(d, remaining * 1.01);
                }
                else
                {
                    EnergyStatus.Multiplexed.EnergyBuffer.Encode(d, buffer);
                    EnergyStatus.Multiplexed.ExpectedRemaining.Encode(d, remaining - 1.0);
                    EnergyStatus.Multiplexed.EnergyToChargeComplete.Encode(d, (0.9 - soc / 100) * (full - buffer));
                }
            });

            yield return VinFrame(M3.Vin, M3.VinPages, vin, (int)(t / 10 % 3), now);
        }

        if (t % 20 == 0)
        {
            yield return Frame(M3.SocStatus, now, d =>
            {
                M3.SocMin.Encode(d, soc - 0.4);
                M3.SocUi.Encode(d, soc);
                M3.SocMax.Encode(d, soc + 0.3);
                M3.SocAverage.Encode(d, soc);
                M3.InitialFullPackEnergy.Encode(d, Model3Values.OriginalKWh);
            });
            if (live is null)
            {
                yield return Frame(M3.KwhCounter, now, d =>
                {
                    M3.DischargeTotal.Encode(d, Model3Values.DischargeTotalKWh);
                    M3.ChargeTotal.Encode(d, Model3Values.ChargeTotalKWh);
                });
            }

            yield return Frame(M3.RangeSoc, now, d => M3.RatedWhPerMile.Encode(d, 245));
            yield return Frame(M3.KwhCountersMultiplexed, now, d =>
            {
                var page = t / 20 % 4;
                M3.KwhCounterPage.EncodeRaw(d, (ulong)page);
                M3.KwhCounterValue.Encode(d, page switch { 0 => 29_900.4, 1 => 11_334.2 + (live?.AddedKWh ?? 0), 2 => 5_210.8, _ => 21_480.6 });
            });
            yield return Frame(M3.ParkingBrakeLeft, now, d => M3.TwelveVolt.Encode(d, 13.9));
            yield return Frame(M3.Odometer, now, d => M3.OdometerKm.Encode(d, 112_345.678), length: 4);
        }

        // Gateway configuration, four pages, one every half second.
        if (t % 10 == 5)
        {
            var configPage = (int)(t / 10 % 4) + 1;
            yield return new CanFrame(GatewayConfig.Id, GatewayConfig.Encode(configPage, Model3Values.Configuration, "US"), now);
        }

        // Alert matrices, one page per tick in turn, about as often as the car sends them.
        var active = t < Model3Values.TransientAlertTicks
            ? new[] { Model3Values.PersistentAlert, Model3Values.TransientAlert }
            : [Model3Values.PersistentAlert];
        var matrices = AlertCatalog.Model3Platform.Messages;
        var pages = matrices.SelectMany(m => Enumerable.Range(0, m.PageCount).Select(p => (Message: m, Page: p))).ToList();
        if (t % 2 == 1)
        {
            var (message, alertPage) = pages[(int)(t / 2 % pages.Count)];
            yield return new CanFrame(message.Id, message.Encode(alertPage, active), now);
        }
    }

    private IEnumerable<CanFrame> ModelSFrames(long t, double[] bricks, string vin, LiveState? live = null)
    {
        var now = live?.Time ?? DateTimeOffset.UtcNow;
        const double full = ModelSValues.FullPackKWh, buffer = ModelSValues.BufferKWh;
        var soc = live?.Soc ?? ModelSValues.SocPercent;

        // 0x6F2: 24 voltage pages of four bricks, then 8 temperature pages of four sensors.
        var page = (int)(t % (SX.VoltagePages + ModelSValues.TemperatureSensors / 4));
        if (page < SX.VoltagePages)
            Jitter(bricks, page * 4, 4);
        var cells = live?.Groups ?? bricks;
        yield return Frame(SX.BrickData, now, d =>
        {
            SX.BrickPage.EncodeRaw(d, (ulong)page);
            for (var slot = 0; slot < 4; slot++)
            {
                if (page < SX.VoltagePages)
                    SX.BrickVoltageSlots[slot].Encode(d, cells[page * 4 + slot]);
                else
                    SX.TemperatureSlots[slot].Encode(d, 23.5 + (page - SX.VoltagePages) * 0.2 + slot * 0.1);
            }
        });

        if (t % 2 == 0)
            yield return Frame(SX.HvBusStatus, now, d => SX.PackVoltage.Encode(d, live?.PackVolts ?? bricks.Sum()));

        // The pack current is not decoded on these cars, so the charging simulator's counters tick every simulated second.
        if (live is not null && t % 5 == 0)
            yield return CounterFrame(SX.KwhCounter, now, live);

        if (t % 10 == 0)
        {
            var remaining = buffer + soc / 100 * (full - buffer);
            yield return Frame(SX.Energy, now, d =>
            {
                EnergyStatus.Bits10.NominalFullPack.Encode(d, full);
                EnergyStatus.Bits10.NominalRemaining.Encode(d, remaining);
                EnergyStatus.Bits10.ExpectedRemaining.Encode(d, remaining - 1.2);
                EnergyStatus.Bits10.IdealRemaining.Encode(d, remaining * 1.02);
                EnergyStatus.Bits10.EnergyToChargeComplete.Encode(d, (0.9 - soc / 100) * (full - buffer));
                EnergyStatus.Bits10.EnergyBuffer.Encode(d, buffer);
            });

            yield return VinFrame(SX.Vin, SX.VinPages, vin, (int)(t / 10 % 3), now);
        }

        if (t % 20 == 0)
        {
            yield return Frame(SX.SocStatus, now, d =>
            {
                SX.SocMin.Encode(d, soc - 0.5);
                SX.SocUi.Encode(d, soc);
            }, length: 3);
            if (live is null)
            {
                yield return Frame(SX.KwhCounter, now, d =>
                {
                    SX.DischargeTotal.Encode(d, ModelSValues.DischargeTotalKWh);
                    SX.ChargeTotal.Encode(d, ModelSValues.ChargeTotalKWh);
                });
            }

            yield return Frame(SX.Odometer, now, d => SX.OdometerMiles.Encode(d, ModelSValues.OdometerMiles), length: 4);
            yield return Frame(SX.Country, now, d => Encoding.ASCII.GetBytes("US").CopyTo(d, 0), length: 2);
            yield return Frame(SX.BatteryOdometer, now, d => SX.BatteryOdometerMiles.Encode(d, ModelSValues.BatteryOdometerMiles), length: 4);
        }
    }

    private void Jitter(double[] bricks, int start, int count)
    {
        for (var i = start; i < start + count && i < bricks.Length; i++)
            bricks[i] += (_random.NextDouble() - 0.5) * 0.0004;
    }

    /// <summary>0x3D2 lifetime energy counters; the same ID and layout on both platforms.</summary>
    private static CanFrame CounterFrame(uint id, DateTimeOffset timestamp, LiveState live) => Frame(id, timestamp, d =>
    {
        M3.DischargeTotal.Encode(d, live.DischargeTotalKWh);
        M3.ChargeTotal.Encode(d, live.ChargeTotalKWh);
    });

    /// <summary>What a charging simulator broadcasts at one moment. Amps are positive into the pack.</summary>
    private sealed record LiveState(
        DateTimeOffset Time, double Amps, double PackVolts, double[] Groups, double Soc, double ChargeTotalKWh, double DischargeTotalKWh, double AddedKWh);

    /// <summary>
    /// A charging simulator's session: rest, ramp up, charge, ramp down, rest. Every cell group's voltage is its resting
    /// voltage, shifted with the state of charge, plus the current times its resistance.
    /// </summary>
    private sealed class ChargingSession(SimulatedCar car, DateTimeOffset start)
    {
        private const double StartSeconds = 70;
        private const double StopSeconds = 160;
        private const double RampDownSeconds = 2;
        private const double VoltsPerSocPercent = 0.0072;

        private readonly bool _supercharger = car == SimulatedCar.Model3Charging;
        private readonly double _initialSoc = car == SimulatedCar.Model3Charging ? Model3Values.SocPercent : ModelSValues.SocPercent;
        private double[]? _ohms;
        private double _seconds;
        private double? _soc;
        private double? _charged;
        private double? _discharged;
        private double _added;

        private double RestAmps => _supercharger ? -1.5 : -1.2;

        private double ChargeAmps => _supercharger ? 250 : 29;

        private double RampUpSeconds => _supercharger ? 10 : 2;

        private double UsableKWh => _supercharger
            ? Model3Values.FullPackKWh - Model3Values.BufferKWh
            : ModelSValues.FullPackKWh - ModelSValues.BufferKWh;

        public LiveState Advance(long tick, double[] restingGroups)
        {
            var seconds = tick * 0.05 * ChargingValues.TimeLapse;
            var amps = AmpsAt(seconds);
            _ohms ??= Enumerable.Range(0, restingGroups.Length).Select(g => ChargingValues.GroupMilliOhm(car, g) / 1000).ToArray();
            var soc = _soc ?? _initialSoc;
            var shift = (soc - _initialSoc) * VoltsPerSocPercent;
            var groups = restingGroups.Select((volts, g) => volts + shift + amps * _ohms[g]).ToArray();
            var packVolts = groups.Sum() + amps * ChargingValues.ConnectionOhm;

            var kwh = packVolts * amps * (seconds - _seconds) / 3_600_000;
            var charged = _charged ?? (_supercharger ? Model3Values.ChargeTotalKWh : ModelSValues.ChargeTotalKWh);
            var discharged = _discharged ?? (_supercharger ? Model3Values.DischargeTotalKWh : ModelSValues.DischargeTotalKWh);
            if (kwh > 0)
            {
                charged += kwh;
                _added += kwh;
            }
            else
            {
                discharged -= kwh;
            }

            _charged = charged;
            _discharged = discharged;
            _soc = soc + kwh / UsableKWh * 100;
            _seconds = seconds;
            return new LiveState(start + TimeSpan.FromSeconds(seconds), amps, packVolts, groups, _soc.Value, charged, discharged, _added);
        }

        private double AmpsAt(double seconds)
        {
            var upEnd = StartSeconds + RampUpSeconds;
            var downEnd = StopSeconds + RampDownSeconds;
            if (seconds < StartSeconds || seconds >= downEnd)
                return RestAmps;
            if (seconds < upEnd)
                return RestAmps + (ChargeAmps - RestAmps) * (seconds - StartSeconds) / RampUpSeconds;
            if (seconds < StopSeconds)
                return ChargeAmps;
            return ChargeAmps + (RestAmps - ChargeAmps) * (seconds - StopSeconds) / RampDownSeconds;
        }
    }

    /// <summary>One VIN page: the Model 3 splits the VIN 3/7/7 across its pages, the Model S/X 7/7/3.</summary>
    private static CanFrame VinFrame(uint id, byte[] pages, string vin, int part, DateTimeOffset timestamp)
    {
        int[] lengths = pages[0] == 0 ? [7, 7, 3] : [3, 7, 7];
        var start = lengths.Take(part).Sum();
        return Frame(id, timestamp, d =>
        {
            d[0] = pages[part];
            Encoding.ASCII.GetBytes(vin.Substring(start, lengths[part])).CopyTo(d, 1);
        });
    }

    private static CanFrame Frame(uint id, DateTimeOffset timestamp, Action<byte[]> fill, int length = 8)
    {
        var data = new byte[length];
        fill(data);
        return new CanFrame(id, data, timestamp);
    }
}

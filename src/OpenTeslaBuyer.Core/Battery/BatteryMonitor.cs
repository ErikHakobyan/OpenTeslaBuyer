using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>Feeds frames from the adapter thread into a profile; the UI reads consistent snapshots.</summary>
public sealed class BatteryMonitor(IVehicleProfile profile)
{
    private readonly object _gate = new();
    private readonly BatteryData _data = new();
    private readonly ChargeTest _chargeTest = new();
    private readonly ChargerCheck _charger = new();
    private readonly TwelveVoltCheck _twelveVolt = new();

    public IVehicleProfile Profile => profile;

    public void Process(CanFrame frame)
    {
        lock (_gate)
        {
            _data.FrameCount++;
            _data.LastFrameAt = DateTimeOffset.UtcNow;
            _data.SeenIds.Add(frame.Id);
            profile.Process(frame, _data);
            _chargeTest.Observe(_data, frame.Timestamp);
            _charger.Observe(_data, frame.Timestamp, _chargeTest.Amps);
            _twelveVolt.Observe(_data, frame.Timestamp);
        }
    }

    public BatteryData Snapshot()
    {
        lock (_gate)
            return _data.Clone();
    }

    public ChargeTestStatus ChargeTestStatus()
    {
        lock (_gate)
            return _chargeTest.Status();
    }

    public ChargerReport ChargerReport()
    {
        lock (_gate)
            return _charger.Report(_data);
    }

    public TwelveVoltReport TwelveVoltReport()
    {
        lock (_gate)
            return _twelveVolt.Report(_data);
    }

    /// <summary>Discards the charging test's measurements, e.g. to repeat it without reconnecting.</summary>
    public void RestartChargeTest()
    {
        lock (_gate)
            _chargeTest.Reset();
    }
}

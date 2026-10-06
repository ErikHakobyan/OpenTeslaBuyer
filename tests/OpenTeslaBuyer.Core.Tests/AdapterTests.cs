using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class AdapterTests
{
    private static readonly string[] Bus =
    [
        "352 00 00 7C 0D 00 09 00 09",
        "132 C6 90 08 00 00 00 00 00",
        "7FF 01 02 03 04 05 06 07 08",
        "352 01 00 2C 01 00 09 00 09",
    ];

    private static readonly MonitoredId[] Ids = [new(0x352, FramesPerVisit: 2), new(0x132, FramesPerVisit: 1)];

    [Fact]
    public async Task Stn_adapter_sets_pass_filters_and_streams_only_wanted_ids()
    {
        var link = new FakeElmLink(stn: true, Bus);
        var adapter = new ElmAdapter(() => link) { BusCheckDuration = TimeSpan.FromMilliseconds(200) };
        var log = new List<string>();

        var frames = await Take(adapter.StreamAsync(() => Ids, log.Add, CancellationToken.None), count: Bus.Length + 3);

        Assert.Contains("STFPA352,7FF", link.Commands);
        Assert.Contains("STFPA132,7FF", link.Commands);
        Assert.Equal("STM", link.Commands[^1]);
        Assert.Equal([0x352u, 0x132u, 0x352u], frames.Skip(Bus.Length).Select(f => f.Id));
        Assert.Contains(log, l => l.StartsWith("Bus check: 3 message IDs seen, 2 of 2", StringComparison.Ordinal));
        Assert.False(link.Monitoring);
    }

    [Fact]
    public async Task Plain_elm_cycles_through_ids_one_at_a_time()
    {
        var link = new FakeElmLink(stn: false, Bus);
        var adapter = new ElmAdapter(() => link) { BusCheckDuration = TimeSpan.FromMilliseconds(200), PollWindow = TimeSpan.FromMilliseconds(200) };

        var frames = await Take(adapter.StreamAsync(() => Ids, _ => { }, CancellationToken.None), count: Bus.Length + 3);

        Assert.Contains("ATCRA352", link.Commands);
        Assert.Contains("ATCRA132", link.Commands);
        Assert.DoesNotContain(link.Commands, c => c.StartsWith("STFPA", StringComparison.Ordinal));
        Assert.Equal([0x352u, 0x352u, 0x132u], frames.Skip(Bus.Length).Select(f => f.Id));
        Assert.False(link.Monitoring);
    }

    [Theory]
    [InlineData("352 00 00 7C 0D 00 09 00 09", 0x352u, 8)]
    [InlineData("3520000", 0x352u, 2)]
    [InlineData("3B6 01 02 03 04", 0x3B6u, 4)]
    public void Parses_elm_monitor_lines(string line, uint id, int length)
    {
        Assert.True(ElmAdapter.TryParseMonitorLine(line, DateTimeOffset.UtcNow, out var frame));
        Assert.Equal(id, frame.Id);
        Assert.Equal(length, frame.Data.Length);
    }

    [Theory]
    [InlineData("BUFFER FULL")]
    [InlineData("OK")]
    [InlineData("18 DA F1 10 02 01 00")]
    [InlineData("")]
    public void Ignores_non_frame_elm_lines(string line)
    {
        Assert.False(ElmAdapter.TryParseMonitorLine(line, DateTimeOffset.UtcNow, out _));
    }

    [Theory]
    [InlineData("t35280000700D00900090", 0x352u, false, "0000700D00900090")]
    [InlineData("t1322C690", 0x132u, false, "C690")]
    [InlineData("T18DAF1102010212AB", 0x18DAF110u, true, "0102")]
    public void Parses_slcan_frames(string line, uint id, bool extended, string hex)
    {
        Assert.True(SlcanAdapter.TryParseFrame(line, DateTimeOffset.UtcNow, out var frame));
        Assert.Equal(id, frame.Id);
        Assert.Equal(extended, frame.Extended);
        Assert.Equal(hex, Convert.ToHexString(frame.Data));
    }

    [Theory]
    [InlineData("t35290000")]
    [InlineData("t3528000")]
    [InlineData("z")]
    public void Rejects_malformed_slcan_frames(string line)
    {
        Assert.False(SlcanAdapter.TryParseFrame(line, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public async Task Simulator_produces_a_complete_decodable_picture()
    {
        var profile = new Model3Profile();
        var monitor = new BatteryMonitor(profile);

        foreach (var frame in await Take(new SimulatorAdapter(SimulatedCar.Model3, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 1500))
            monitor.Process(frame);

        var data = monitor.Snapshot();
        var health = HealthCalculator.Evaluate(data);
        Assert.Equal(EnergyLayout.Multiplexed, data.EnergyLayout);
        Assert.Equal(SimulatorAdapter.SimulatedVin(), data.Vin);
        Assert.Equal(SimulatorAdapter.Model3Values.BrickCount, data.BrickVoltages.Count);
        Assert.Equal(SimulatorAdapter.Model3Values.FullPackKWh / SimulatorAdapter.Model3Values.OriginalKWh * 100, health.StateOfHealthPercent!.Value, 1);
        Assert.NotNull(health.FullRangeKm);
        Assert.NotNull(data.OdometerKm);
    }

    [Fact]
    public async Task Replay_plays_back_a_recorded_log()
    {
        var path = Path.GetTempFileName();
        try
        {
            var frames = new[]
            {
                new CanFrame(0x352, [1, 2, 3], DateTimeOffset.UnixEpoch.AddSeconds(100)),
                new CanFrame(0x132, [4, 5], DateTimeOffset.UnixEpoch.AddSeconds(100.01)),
            };
            using (var writer = new CandumpWriter(path))
            {
                foreach (var frame in frames)
                    writer.Write(frame);
            }

            var replayed = await Take(new ReplayAdapter(path, speed: 0).StreamAsync(() => [], _ => { }, CancellationToken.None), count: 10);

            Assert.Equal(frames.Select(f => f.ToString()), replayed.Select(f => f.ToString()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static async Task<List<CanFrame>> Take(IAsyncEnumerable<CanFrame> stream, int count)
    {
        var frames = new List<CanFrame>();
        await foreach (var frame in stream)
        {
            frames.Add(frame);
            if (frames.Count == count)
                break;
        }

        return frames;
    }
}

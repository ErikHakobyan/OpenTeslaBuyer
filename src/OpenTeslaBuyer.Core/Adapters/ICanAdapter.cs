using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Adapters;

/// <summary>
/// A CAN message the vehicle profile wants. Lists are ordered most important first: adapters with limited
/// filter memory (OBDLink) keep the head of the list when they run out.
/// </summary>
/// <param name="FramesPerVisit">
/// Only used by adapters that can listen to one ID at a time (plain ELM327): multiplexed messages
/// need several frames to cover all their pages.
/// </param>
/// <param name="PollEvery">Plain ELM327 only: visit this ID on every n-th pass through the list.</param>
public sealed record MonitoredId(uint Id, int FramesPerVisit = 2, int PollEvery = 1);

/// <summary>
/// A read-only source of CAN frames. Implementations only listen; none of them transmits on the vehicle bus.
/// </summary>
public interface ICanAdapter
{
    string Name { get; }

    /// <summary>
    /// Connects, configures the adapter and streams frames until <paramref name="ct"/> is cancelled
    /// or the source ends. Adapters may deliver IDs that were not asked for.
    /// </summary>
    /// <param name="ids">The wanted messages. Read again whenever an adapter needs the list, because it can
    /// shrink once the vehicle profile has identified the car.</param>
    IAsyncEnumerable<CanFrame> StreamAsync(Func<IReadOnlyList<MonitoredId>> ids, Action<string> log, CancellationToken ct);
}

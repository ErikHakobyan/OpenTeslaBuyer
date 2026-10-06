using System.Text;
using System.Threading.Channels;
using OpenTeslaBuyer.Core.Adapters;

namespace OpenTeslaBuyer.Core.Tests;

/// <summary>
/// Behaves like an ELM327 (optionally with an STN chip) on a bus that carries <paramref name="busLines"/>:
/// spaces in commands are ignored, any character stops a running monitor, monitors honour ATCRA / STFPA filters.
/// </summary>
internal sealed class FakeElmLink(bool stn, IReadOnlyList<string> busLines) : ISerialLink
{
    private const string OutOfMemory = "OUT OF MEMORY\r\r>";

    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly StringBuilder _input = new();
    private readonly HashSet<string> _passFilters = [];
    private string? _receiveAddress;

    public List<string> Commands { get; } = [];

    public bool Monitoring { get; private set; }

    /// <summary>How many pass filters fit before STFPA answers OUT OF MEMORY.</summary>
    public int MaxFilters { get; init; } = int.MaxValue;

    public string Description => "fake ELM327";

    public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

    public Task WriteAsync(string text, CancellationToken ct)
    {
        foreach (var c in text)
        {
            if (Monitoring)
            {
                Monitoring = false;
                Send("STOPPED\r\r>");
                continue;
            }

            if (c == '\r')
            {
                Execute(_input.ToString().Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
                _input.Clear();
            }
            else
            {
                _input.Append(c);
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask<string> ReadAsync(CancellationToken ct) => await _output.Reader.ReadAsync(ct);

    public void DiscardInput()
    {
        while (_output.Reader.TryRead(out _))
        {
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Execute(string command)
    {
        if (command.Length == 0)
            throw new InvalidOperationException("A bare CR would make a real ELM327 repeat its last command.");

        Commands.Add(command);
        switch (command)
        {
            case "ATZ":
                Send("\r\rELM327 v1.5\r\r>");
                break;
            case "STI":
                Send(stn ? "STN2255 v5.10.3\r\r>" : "?\r\r>");
                break;
            case "ATMA" or "STMA":
                StartMonitor(_ => true);
                break;
            case "STM":
                StartMonitor(id => _passFilters.Contains(id));
                break;
            default:
                if (command.StartsWith("STFPA", StringComparison.Ordinal) && _passFilters.Count >= MaxFilters)
                {
                    Send(OutOfMemory);
                    return;
                }

                if (command.StartsWith("STFPA", StringComparison.Ordinal))
                    _passFilters.Add(command[5..8]);
                else if (command.StartsWith("ATCRA", StringComparison.Ordinal))
                    _receiveAddress = command[5..];
                Send(command.StartsWith("ST", StringComparison.Ordinal) && !stn ? "?\r\r>" : "OK\r\r>");
                break;
        }
    }

    private void StartMonitor(Func<string, bool> pass)
    {
        Monitoring = true;
        foreach (var line in busLines)
        {
            var id = line[..3];
            if (pass(id) && (_receiveAddress is null || _receiveAddress == id))
                Send(line + "\r");
        }
    }

    private void Send(string text) => _output.Writer.TryWrite(text);
}

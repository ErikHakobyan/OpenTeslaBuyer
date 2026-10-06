using System.Text;

namespace OpenTeslaBuyer.Core.Adapters;

/// <summary>
/// Splits a serial stream into lines. Besides line breaks it understands the two single-character
/// replies adapters send without a line break: the ELM327 prompt <c>&gt;</c> and the SLCAN error bell.
/// </summary>
internal sealed class LineReader(ISerialLink link)
{
    public const string Prompt = ">";
    public const string Bell = "\a";

    private readonly Queue<string> _pending = new();
    private readonly StringBuilder _current = new();

    /// <summary>The next line ("" for an empty one), <see cref="Prompt"/>, <see cref="Bell"/>, or null when the link closed.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (_pending.Count == 0)
        {
            var chunk = await link.ReadAsync(ct);
            if (chunk.Length == 0)
                return null;

            foreach (var c in chunk)
            {
                switch (c)
                {
                    case '\r':
                        Emit();
                        break;
                    case '\n':
                        if (_current.Length > 0)
                            Emit();
                        break;
                    case '>':
                        if (_current.Length > 0)
                            Emit();
                        _pending.Enqueue(Prompt);
                        break;
                    case '\a':
                        if (_current.Length > 0)
                            Emit();
                        _pending.Enqueue(Bell);
                        break;
                    case '\0':
                        break;
                    default:
                        _current.Append(c);
                        break;
                }
            }
        }

        return _pending.Dequeue();
    }

    /// <summary>Like <see cref="ReadLineAsync"/> but returns null after <paramref name="timeout"/> without data.</summary>
    public async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);
        try
        {
            return await ReadLineAsync(window.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _current.Clear();
        link.DiscardInput();
    }

    private void Emit()
    {
        _pending.Enqueue(_current.ToString().Trim());
        _current.Clear();
    }
}

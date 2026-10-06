using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace OpenTeslaBuyer.Core.Adapters;

/// <summary>A text-oriented serial connection; abstracted so adapter protocols can be tested without hardware.</summary>
public interface ISerialLink : IAsyncDisposable
{
    string Description { get; }

    Task OpenAsync(CancellationToken ct);

    Task WriteAsync(string text, CancellationToken ct);

    /// <summary>The next chunk of received text, or an empty string once the link is closed.</summary>
    ValueTask<string> ReadAsync(CancellationToken ct);

    /// <summary>Drops anything received but not read yet.</summary>
    void DiscardInput();
}

/// <summary>
/// A COM port, including the virtual ones Windows creates for USB adapters and paired Bluetooth (SPP) adapters.
/// Reads run on a dedicated thread because <see cref="SerialPort"/>'s async reads ignore cancellation on Windows.
/// </summary>
public sealed class SerialPortLink(string portName, int baudRate) : ISerialLink
{
    private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private SerialPort? _port;
    private Thread? _readThread;
    private volatile bool _closing;

    public string Description => $"{portName} @ {baudRate} baud";

    public Task OpenAsync(CancellationToken ct)
    {
        _port = new SerialPort(portName, baudRate)
        {
            Encoding = Encoding.ASCII,
            ReadTimeout = 200,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"Serial reader {portName}" };
        _readThread.Start();
        return Task.CompletedTask;
    }

    public Task WriteAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        (_port ?? throw new InvalidOperationException("Port is not open.")).Write(text);
        return Task.CompletedTask;
    }

    public async ValueTask<string> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await _chunks.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException ex) when (ex.InnerException is not null)
        {
            throw new IOException($"Lost connection to {portName}: {ex.InnerException.Message}", ex.InnerException);
        }
        catch (ChannelClosedException)
        {
            return "";
        }
    }

    public void DiscardInput()
    {
        while (_chunks.Reader.TryRead(out _))
        {
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_closing)
            {
                int count;
                try
                {
                    count = _port!.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (count > 0)
                    _chunks.Writer.TryWrite(Encoding.ASCII.GetString(buffer, 0, count));
            }

            _chunks.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _chunks.Writer.TryComplete(_closing ? null : ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _closing = true;
        _readThread?.Join(TimeSpan.FromSeconds(1));
        _port?.Dispose();
        _chunks.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

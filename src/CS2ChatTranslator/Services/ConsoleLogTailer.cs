using System.Text;

namespace CS2ChatTranslator.Services;

public sealed class ConsoleLogTailer : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly bool _startFromBeginning;
    private readonly System.Threading.Timer _timer;
    private FileStream? _stream;
    private long _lastPosition;
    private string _partialLine = "";
    private int _inTick;
    private bool _disposed;

    public long LinesRead { get; private set; }

    public event EventHandler<string>? LineRead;
    public event EventHandler<Exception>? ErrorOccurred;

    public ConsoleLogTailer(string path, TimeSpan? pollInterval = null, bool startFromBeginning = false)
    {
        _path = path;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _startFromBeginning = startFromBeginning;
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        OpenAndSeek();
        _timer.Change(TimeSpan.Zero, _pollInterval);
    }

    private void OpenAndSeek()
    {
        _stream?.Dispose();
        _stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (_startFromBeginning)
        {
            _stream.Seek(0, SeekOrigin.Begin);
            _lastPosition = 0;
        }
        else
        {
            _stream.Seek(0, SeekOrigin.End);
            _lastPosition = _stream.Position;
        }
        _partialLine = "";
    }

    private void OnTick(object? _)
    {
        if (Interlocked.Exchange(ref _inTick, 1) == 1) return;
        try
        {
            ReadNewBytes();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            TryReopen();
        }
        finally
        {
            Interlocked.Exchange(ref _inTick, 0);
        }
    }

    private void ReadNewBytes()
    {
        if (_stream is null) return;

        var currentLength = _stream.Length;
        if (currentLength < _lastPosition)
        {
            _stream.Seek(0, SeekOrigin.Begin);
            _lastPosition = 0;
            _partialLine = "";
        }

        if (currentLength == _lastPosition) return;

        _stream.Seek(_lastPosition, SeekOrigin.Begin);
        var toRead = (int)Math.Min(currentLength - _lastPosition, 1 << 20);
        var buffer = new byte[toRead];
        var read = _stream.Read(buffer, 0, toRead);
        _lastPosition += read;

        var chunk = Encoding.UTF8.GetString(buffer, 0, read);
        var combined = _partialLine + chunk;

        var lastNewline = combined.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _partialLine = combined;
            return;
        }

        var complete = combined.Substring(0, lastNewline);
        _partialLine = combined.Substring(lastNewline + 1);

        foreach (var line in complete.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (clean.Length == 0) continue;
            LinesRead++;
            LineRead?.Invoke(this, clean);
        }
    }

    private void TryReopen()
    {
        try { OpenAndSeek(); }
        catch { /* next tick will retry */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
        _stream?.Dispose();
    }
}

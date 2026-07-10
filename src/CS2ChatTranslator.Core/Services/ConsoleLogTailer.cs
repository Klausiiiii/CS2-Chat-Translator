using System.Buffers;
using System.Text;

namespace CS2ChatTranslator.Services;

public sealed class ConsoleLogTailer : IDisposable
{
    private const int MaxPartialLine = 64 * 1024;

    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly bool _startFromBeginning;
    private readonly System.Threading.Timer _timer;
    // UTF-8 is stateful: a multibyte codepoint can straddle two reads (the 1 MiB read cap, or a
    // flush that lands mid-character). A shared Decoder retains the incomplete trailing bytes
    // between ticks so split codepoints decode correctly instead of becoming U+FFFD.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private FileStream? _stream;
    private long _lastPosition;
    private string _partialLine = "";
    private int _inTick;
    private volatile bool _disposed;

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
        _decoder.Reset(); // drop any half-codepoint buffered before this seek
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
            if (_disposed) return; // a race with Dispose is not a real read error
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
            _decoder.Reset(); // truncation (CS2 restart) discontinues the byte stream
        }

        if (currentLength == _lastPosition) return;

        _stream.Seek(_lastPosition, SeekOrigin.Begin);
        var toRead = (int)Math.Min(currentLength - _lastPosition, 1 << 20);
        var buffer = ArrayPool<byte>.Shared.Rent(toRead);
        char[]? charBuffer = null;
        int read;
        string chunk;
        try
        {
            read = _stream.Read(buffer, 0, toRead);
            _lastPosition += read;
            var charCount = _decoder.GetCharCount(buffer, 0, read, flush: false);
            charBuffer = ArrayPool<char>.Shared.Rent(charCount == 0 ? 1 : charCount);
            var produced = _decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: false);
            chunk = new string(charBuffer, 0, produced);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (charBuffer is not null) ArrayPool<char>.Shared.Return(charBuffer);
        }

        var combined = _partialLine + chunk;

        var lastNewline = combined.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            // Bound the accumulator: a newline-free stream (misconfigured/binary file) would
            // otherwise grow _partialLine without limit and make `combined = _partialLine + chunk`
            // O(N^2). Real CS2 chat lines are well under this cap; discard and resync on the next '\n'.
            _partialLine = combined.Length > MaxPartialLine ? "" : combined;
            return;
        }

        var start = 0;
        while (start <= lastNewline)
        {
            var nl = combined.IndexOf('\n', start);
            if (nl < 0) break;
            var lineLen = nl - start;
            if (lineLen > 0 && combined[nl - 1] == '\r') lineLen--;
            if (lineLen > 0)
            {
                LinesRead++;
                LineRead?.Invoke(this, combined.Substring(start, lineLen));
            }
            start = nl + 1;
        }
        _partialLine = combined.Substring(lastNewline + 1);
    }

    private void TryReopen()
    {
        if (_disposed) return; // never reopen a stream on a disposed instance (handle leak)
        try { OpenAndSeek(); }
        catch { /* next tick will retry */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        // Timer.Dispose(WaitHandle) signals only after any in-flight callback returns, so the
        // stream is never disposed underneath a running tick (no ObjectDisposedException, no
        // spurious 'Lesefehler', no reopen-after-dispose handle leak). Deadlock-safe: LineRead/
        // ErrorOccurred marshal to the UI thread via non-blocking BeginInvoke/Dispatcher.Post,
        // never a synchronous Invoke that would re-enter this thread.
        using var done = new ManualResetEvent(false);
        if (_timer.Dispose(done)) done.WaitOne();
        _stream?.Dispose();
    }
}

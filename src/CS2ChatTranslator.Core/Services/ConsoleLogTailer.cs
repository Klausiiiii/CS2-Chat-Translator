using System.Buffers;
using System.Text;

namespace CS2ChatTranslator.Services;

public sealed class ConsoleLogTailer : IDisposable
{
    private const int MaxPartialLine = 64 * 1024;
    public const long DefaultSeedTailBytes = 1 << 20;

    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly bool _startFromBeginning;
    private readonly long _seedTailBytes;
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

    /// <summary>Fired once during Start() with the complete lines of the tail window, in file order.</summary>
    public event EventHandler<IReadOnlyList<string>>? SeedRead;

    public ConsoleLogTailer(string path, TimeSpan? pollInterval = null, bool startFromBeginning = false, long seedTailBytes = 0)
    {
        _path = path;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _startFromBeginning = startFromBeginning;
        _seedTailBytes = seedTailBytes;
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        OpenAndSeek();
        if (_seedTailBytes > 0) EmitSeed();
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

        if (combined.LastIndexOf('\n') < 0)
        {
            // Bound the accumulator: a newline-free stream (misconfigured/binary file) would otherwise
            // grow _partialLine without limit and make `combined = _partialLine + chunk` O(N^2). Real
            // CS2 chat lines are well under this cap; discard and resync on the next '\n'.
            _partialLine = combined.Length > MaxPartialLine ? "" : combined;
            return;
        }

        _partialLine = EmitCompleteLines(combined, 0, line => LineRead?.Invoke(this, line));
    }

    // Emits each complete, non-empty (\r-trimmed) line in [startIdx, lastNewline] via onLine and
    // returns the trailing partial (substring after the last '\n'). If there is no '\n' at/after
    // startIdx, returns the whole remainder from startIdx unchanged. Shared by the live read path
    // and the seed path so the line-splitting rules stay identical.
    private string EmitCompleteLines(string text, int startIdx, Action<string> onLine)
    {
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < startIdx) return text.Substring(startIdx);

        var start = startIdx;
        while (start <= lastNewline)
        {
            var nl = text.IndexOf('\n', start);
            if (nl < 0) break;
            var lineLen = nl - start;
            if (lineLen > 0 && text[nl - 1] == '\r') lineLen--;
            if (lineLen > 0)
            {
                LinesRead++;
                onLine(text.Substring(start, lineLen));
            }
            start = nl + 1;
        }
        return text.Substring(lastNewline + 1);
    }

    private void EmitSeed()
    {
        if (_stream is null) return;

        var length = _stream.Length;
        var offset = Math.Max(0, length - _seedTailBytes);
        var toRead = (int)(length - offset);
        _stream.Seek(offset, SeekOrigin.Begin);
        _decoder.Reset(); // window start is a discontinuity
        _lastPosition = length;
        _partialLine = "";
        if (toRead <= 0) return;

        string text;
        var buffer = ArrayPool<byte>.Shared.Rent(toRead);
        char[]? charBuffer = null;
        try
        {
            var read = _stream.Read(buffer, 0, toRead);
            _lastPosition = offset + read;
            var charCount = _decoder.GetCharCount(buffer, 0, read, flush: false);
            charBuffer = ArrayPool<char>.Shared.Rent(charCount == 0 ? 1 : charCount);
            var produced = _decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: false);
            text = new string(charBuffer, 0, produced);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (charBuffer is not null) ArrayPool<char>.Shared.Return(charBuffer);
        }

        // A BOM at the true file start decodes as a literal U+FEFF (the raw Decoder, unlike
        // StreamReader, does not skip it). Only reachable when the window covers byte 0.
        if (offset == 0 && text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);

        // If we seeked into the middle of the file, the first (partial) line is garbage — resync past it.
        var startIdx = 0;
        if (offset > 0)
        {
            var firstNl = text.IndexOf('\n');
            if (firstNl < 0) { _partialLine = ""; return; } // no complete line in window
            startIdx = firstNl + 1;
        }

        var lines = new List<string>();
        _partialLine = EmitCompleteLines(text, startIdx, lines.Add);
        if (lines.Count > 0) SeedRead?.Invoke(this, lines);
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

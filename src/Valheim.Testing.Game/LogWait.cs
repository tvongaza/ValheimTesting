using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>A matched log line and how long its wait took.</summary>
public sealed record LogLine(string Text, Match Match, TimeSpan Elapsed);

// Follows one log file from a remembered byte offset, so nothing written before it can satisfy a wait. The file
// may not exist yet. Only complete lines match: a line still being written waits for its newline. A file that is
// truncated or replaced (shorter than the offset, or different bytes at its start or just before the offset) is
// read again from its beginning; a replacement byte-identical at both places is indistinguishable. Lines read but
// not yet matched stay queued for the next wait. One wait at a time.
public sealed class LogWait : IDisposable
{
    // FileSystemWatcher can miss events: network and container-mounted filesystems often raise none, and an
    // overflowing buffer drops them. A wait therefore also re-reads the file at this low frequency. It only bounds
    // how late a missed event is noticed; the watcher wakes a wait as soon as the file changes.
    public static readonly TimeSpan DefaultSafetyInterval = TimeSpan.FromSeconds(2);
    private const int Fingerprint = 256, Chunk = 64 * 1024;
    private readonly object _sync = new();
    // Never disposed: a watcher callback already in flight may still release it after Dispose.
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly FileSystemWatcher? _watcher;
    private readonly Queue<string> _lines = new();
    private readonly MemoryStream _partial = new();
    private readonly TimeSpan _safety = DefaultSafetyInterval;
    private byte[] _head = [], _tail = [];
    private long _offset;
    private int _waiting;
    private bool _disposed;
    private string? _last;
    public string LogPath { get; }
    public long Offset { get { lock (_sync) return _offset; } }
    /// <summary>The most recent complete line read from the file, matched or not.</summary>
    public string? LastLine { get { lock (_sync) return _last; } }
    /// <summary>Re-read interval when no watcher event arrives. <see cref="Timeout.InfiniteTimeSpan"/> relies on events alone.</summary>
    public TimeSpan SafetyInterval
    {
        get => _safety;
        init => _safety = value > TimeSpan.Zero || value == Timeout.InfiniteTimeSpan ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <param name="offset">Byte offset to read from. By default the file's current end, or its start if it does not exist yet.</param>
    public LogWait(string path, long? offset = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        LogPath = Path.GetFullPath(path);
        using (var stream = Open())
        {
            _offset = offset ?? stream?.Length ?? 0;
            // An offset past the end means the file was replaced since it was taken: the first read starts over.
            if (stream != null && _offset <= stream.Length) Remember(stream);
        }
        string directory = Path.GetDirectoryName(LogPath)!;
        // A missing directory has nothing to watch yet; the safety re-read still finds the file once it appears.
        if (!Directory.Exists(directory)) return;
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(LogPath))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime };
        _watcher.Changed += (_, _) => Wake(); _watcher.Created += (_, _) => Wake(); _watcher.Deleted += (_, _) => Wake();
        _watcher.Renamed += (_, _) => Wake(); _watcher.Error += (_, _) => Wake();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Waits for a complete line matching <paramref name="success"/>. A line matching any of <paramref name="failures"/>
    /// (checked first) ends the wait at once with <see cref="WaitFailedException"/>; expiry throws <see cref="WaitTimeoutException"/>.
    /// </summary>
    public async Task<LogLine> WaitAsync(Regex success, TimeSpan timeout, IReadOnlyList<Regex>? failures = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(success);
        WaitText.RequireTimeout(timeout);
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _waiting, 1) == 1) throw new InvalidOperationException("A LogWait serves one wait at a time.");
        try
        {
            var clock = Stopwatch.StartNew();
            string target = $"a line matching /{success}/ in {LogPath}";
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    ReadNew();
                    while (_lines.TryDequeue(out string? line))
                    {
                        foreach (var failure in failures ?? [])
                            if (failure.IsMatch(line)) throw new WaitFailedException(target, $"a line matched failure /{failure}/", clock.Elapsed, line);
                        var match = success.Match(line);
                        if (match.Success) return new LogLine(line, match, clock.Elapsed);
                    }
                }
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new WaitTimeoutException(target, clock.Elapsed, Refresh());
                await _changed.WaitAsync(_safety != Timeout.InfiniteTimeSpan && _safety < remaining ? _safety : remaining, cancellation).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _waiting, 0); }
    }
    /// <summary>Waits for a line containing <paramref name="text"/> (ordinal); a line containing any failure text ends it at once.</summary>
    public Task<LogLine> WaitAsync(string text, TimeSpan timeout, IReadOnlyList<string>? failures = null, CancellationToken cancellation = default) =>
        WaitAsync(Literal(text), timeout, failures?.Select(Literal).ToArray(), cancellation);
    public static Regex Literal(string text) => new(Regex.Escape(text), RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads what was appended since the last read without matching it (it stays queued for the next wait) and returns
    /// the last line seen, including a line still being written. For reports, for example after the writer exited.
    /// </summary>
    public string? Refresh()
    {
        lock (_sync)
        {
            if (!_disposed) ReadNew();
            return _partial.Length == 0 ? _last : Decode(_partial) + " [incomplete line]";
        }
    }

    // Callers hold _sync.
    private void ReadNew()
    {
        FileStream? stream;
        // Another process may briefly hold the file exclusively, or (Windows) it is pending deletion: try on the next wake.
        try { stream = Open(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        using (stream)
        {
            if (stream == null)
            {
                // Deleted or not created yet: whatever appears at this path next is a new file.
                if (_offset > 0 || _partial.Length > 0) StartOver();
                return;
            }
            if (!Unchanged(stream)) StartOver();
            stream.Position = _offset;
            var buffer = new byte[Chunk];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    _partial.Write(buffer, start, i - start);
                    _last = Decode(_partial); _partial.SetLength(0);
                    _lines.Enqueue(_last);
                    start = i + 1;
                }
                _partial.Write(buffer, start, read - start);
                _offset += read;
            }
            Remember(stream);
        }
    }
    private static string Decode(MemoryStream bytes) => Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimEnd('\r').TrimStart('﻿');
    private void StartOver() { _offset = 0; _partial.SetLength(0); _head = []; _tail = []; }
    private bool Unchanged(FileStream stream) => stream.Length >= _offset &&
        ReadAt(stream, 0, _head.Length).AsSpan().SequenceEqual(_head) && ReadAt(stream, _offset - _tail.Length, _tail.Length).AsSpan().SequenceEqual(_tail);
    private void Remember(FileStream stream)
    {
        _head = ReadAt(stream, 0, (int)Math.Min(Fingerprint, _offset));
        long from = Math.Max(0, _offset - Fingerprint);
        _tail = ReadAt(stream, from, (int)(_offset - from));
    }
    private static byte[] ReadAt(FileStream stream, long position, int count)
    {
        var buffer = new byte[count];
        stream.Position = position;
        int read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }
    private FileStream? Open()
    {
        try { return new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    private void Wake()
    {
        try { _changed.Release(); }
        catch (SemaphoreFullException) { /* A wake is already pending. */ }
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _watcher?.Dispose();
    }
}

using System.Text;

/// <summary>
/// Captures what one test writes to <see cref="Console.Error"/> while other test classes run at the same time. Console.Error
/// is process-global, so swapping it for a test would also catch an unrelated test's warning. Instead it is replaced once
/// by a router that sends each write to the capture of the execution flow that made it (an <see cref="AsyncLocal{T}"/>,
/// which flows into awaits, tasks and threads the captured action starts) and every other write to the real stderr.
/// </summary>
internal static class StderrCapture
{
    private static readonly AsyncLocal<Capture?> Current = new();
    private static readonly object Gate = new();
    private static bool _installed;

    public static string Of(Action action)
    {
        lock (Gate)
        {
            if (!_installed) { Console.SetError(new Router(Console.Error)); _installed = true; }
        }
        var capture = new Capture(); var previous = Current.Value;
        Current.Value = capture;
        try { action(); }
        finally { Current.Value = previous; capture.Close(); }
        return capture.ToString();
    }

    private sealed class Capture
    {
        private readonly StringBuilder _text = new();
        private bool _closed;
        // False once closed: a thread the action started that writes later goes to the real stderr.
        public bool TryWrite(string? value) { lock (_text) { if (_closed) return false; _text.Append(value); return true; } }
        public void Close() { lock (_text) _closed = true; }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    private sealed class Router(TextWriter stderr) : TextWriter
    {
        public override Encoding Encoding => stderr.Encoding;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
        public override void Write(string? value) { if (Current.Value?.TryWrite(value) != true) stderr.Write(value); }
        public override void WriteLine(string? value) => Write(value + NewLine);
        public override void Flush() => stderr.Flush();
    }
}

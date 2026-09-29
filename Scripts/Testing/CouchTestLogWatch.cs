#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Logging;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Subscribes to <see cref="Log.LogCallback"/> for the lifetime of one scenario (docs/design/testing-plan.md §6.5).
/// Any Error-level line, or any line matching <see cref="CouchTestLogPatterns"/> and not allowlisted, fails the
/// scenario; the first such line is recorded and every later one is ignored. Lines carrying the runner's own
/// <see cref="CouchTestLog.Marker"/> (diagnostic dumps, "scenario starting" lines, ...) are ignored so the runner
/// can never fail a scenario by describing its own failure.
/// </summary>
internal sealed class CouchTestLogWatch : System.IDisposable
{
    private bool _disposed;

    public CouchTestLogWatch()
    {
        Log.LogCallback += OnLog;
    }

    /// <summary>The first offending line, formatted for a scenario result's message field. Null while clean.</summary>
    public string? FirstFailure { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log.LogCallback -= OnLog;
    }

    private void OnLog(LogLevel level, string line, int skipFrames)
    {
        if (FirstFailure != null || line.Contains(CouchTestLog.Marker, System.StringComparison.Ordinal))
        {
            return;
        }

        if (CouchTestLogPatterns.IsAllowlisted(line))
        {
            return;
        }

        if (level == LogLevel.Error)
        {
            FirstFailure = $"[Error] {line}";
            return;
        }

        string? pattern = CouchTestLogPatterns.FirstMatch(line);
        if (pattern != null)
        {
            FirstFailure = $"[{level}] {line} (matched failure pattern \"{pattern}\")";
        }
    }
}
#endif

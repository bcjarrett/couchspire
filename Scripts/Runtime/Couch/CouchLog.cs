using Godot;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Logging for couch co-op code: the base mod's <c>[LocalMultiControl]</c> prefix plus <c>[Couch]</c>,
/// throttling for per-frame paths, and a short in-memory history for the debug overlay.
/// </summary>
internal static class CouchLog
{
    private const int HistorySize = 12;

    private static readonly Dictionary<string, ulong> _lastLogAtMs = new();

    private static readonly LinkedList<string> _history = new();

    public static IEnumerable<string> History => _history;

    public static void Info(string message)
    {
        LocalMultiControlLogger.Info($"[Couch] {message}");
    }

    public static void Warn(string message)
    {
        LocalMultiControlLogger.Warn($"[Couch] {message}");
    }

    public static void Probe(string message)
    {
        if (CouchConfig.ProbeEnabled)
        {
            LocalMultiControlLogger.Info($"[Couch][Probe] {message}");
        }
    }

    /// <summary>
    /// Logs at most once per <paramref name="intervalMs"/> for the given key.
    /// </summary>
    public static void Throttled(string key, string message, ulong intervalMs = 1000)
    {
        ulong now = Time.GetTicksMsec();
        if (_lastLogAtMs.TryGetValue(key, out ulong last) && now - last < intervalMs)
        {
            return;
        }

        _lastLogAtMs[key] = now;
        Info(message);
    }

    /// <summary>
    /// Records a routing event for the debug overlay.
    /// </summary>
    public static void Remember(string entry)
    {
        _history.AddFirst($"{Time.GetTicksMsec() / 1000.0,8:F1}s {entry}");
        while (_history.Count > HistorySize)
        {
            _history.RemoveLast();
        }
    }
}

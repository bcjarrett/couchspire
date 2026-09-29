#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Logging for the in-game test runner: the mod's "[CouchSpire]" prefix plus "[CouchTest]"
/// (docs/design/testing-plan.md §6.1, §6.5). <see cref="Marker"/> is the substring <see cref="CouchTestLogWatch"/>
/// uses to recognize (and ignore) the runner's own lines, so its diagnostics can never trip its own failure detector.
/// </summary>
internal static class CouchTestLog
{
    public const string Marker = "[CouchTest]";

    public static void Info(string message)
    {
        ModLog.Info($"{Marker} {message}");
    }

    public static void Warn(string message)
    {
        ModLog.Warn($"{Marker} {message}");
    }

    public static void Error(string message)
    {
        ModLog.Error($"{Marker} {message}");
    }
}
#endif

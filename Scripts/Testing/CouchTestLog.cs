#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Runtime;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// Logging for the in-game test runner: the mod's "[LocalMultiControl]" prefix plus "[CouchTest]"
/// (docs/design/testing-plan.md §6.1, §6.5). <see cref="Marker"/> is the substring <see cref="CouchTestLogWatch"/>
/// uses to recognize (and ignore) the runner's own lines, so its diagnostics can never trip its own failure detector.
/// </summary>
internal static class CouchTestLog
{
    public const string Marker = "[CouchTest]";

    public static void Info(string message)
    {
        LocalMultiControlLogger.Info($"{Marker} {message}");
    }

    public static void Warn(string message)
    {
        LocalMultiControlLogger.Warn($"{Marker} {message}");
    }

    public static void Error(string message)
    {
        LocalMultiControlLogger.Error($"{Marker} {message}");
    }
}
#endif

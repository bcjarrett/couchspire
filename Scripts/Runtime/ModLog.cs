using MegaCrit.Sts2.Core.Logging;

namespace CouchSpire.Scripts.Runtime;

internal static class ModLog
{
    private const string Prefix = "[CouchSpire]";

    public static void Info(string message)
    {
        Log.Info($"{Prefix} {message}");
    }

    public static void Warn(string message)
    {
        Log.Warn($"{Prefix} {message}");
    }

    public static void Error(string message)
    {
        Log.Error($"{Prefix} {message}");
    }
}

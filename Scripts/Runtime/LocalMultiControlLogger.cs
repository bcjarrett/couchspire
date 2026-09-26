using MegaCrit.Sts2.Core.Logging;

namespace LocalMultiControl.Scripts.Runtime;

internal static class LocalMultiControlLogger
{
    private const string Prefix = "[LocalMultiControl]";

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

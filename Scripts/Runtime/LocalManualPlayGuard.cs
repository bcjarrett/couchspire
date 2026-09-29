using Godot;

namespace CouchSpire.Scripts.Runtime;

internal static class LocalManualPlayGuard
{
    private static int _depth;
    private static ulong _holdUntilMs;

    public static bool IsActive
    {
        get
        {
            if (Volatile.Read(ref _depth) > 0)
            {
                return true;
            }

            return Time.GetTicksMsec() <= Volatile.Read(ref _holdUntilMs);
        }
    }

    public static void Enter(string source)
    {
        int nextDepth = Interlocked.Increment(ref _depth);
        if (nextDepth == 1)
        {
            ModLog.Info($"Entering manual card-play critical section: source={source}");
        }
    }

    public static void Exit(string source)
    {
        int nextDepth = Interlocked.Decrement(ref _depth);
        if (nextDepth <= 0)
        {
            Volatile.Write(ref _depth, 0);
            // There is a narrow async window between the end of target selection and the action actually being enqueued; add a brief hold period here to cover it.
            Volatile.Write(ref _holdUntilMs, Time.GetTicksMsec() + 180UL);
            ModLog.Info($"Exiting manual card-play critical section: source={source}");
        }
    }
}

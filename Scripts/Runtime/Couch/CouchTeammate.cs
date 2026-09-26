using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Simultaneous-combat rules: during combat the driver (<see cref="LocalContext.NetId"/>) keeps the main screen and
/// every other local character is a "teammate" that the game treats like an online player.
/// </summary>
internal static class CouchTeammate
{
    /// <summary>True during combat in a local multi-character run with simultaneous mode on.</summary>
    public static bool DriverKeepsScreen =>
        CouchConfig.SimultaneousEnabled
        && LocalSelfCoopContext.IsEnabled
        && RunManager.Instance.IsInProgress
        && CombatManager.Instance.IsInProgress;

    /// <summary>
    /// True if <paramref name="player"/> is a local character other than the driver while simultaneous mode is on,
    /// in or out of combat (used for the teammate's own rewards panel).
    /// </summary>
    public static bool IsSimultaneousTeammate(Player? player)
    {
        return player != null
            && CouchConfig.SimultaneousEnabled
            && LocalSelfCoopContext.IsEnabled
            && RunManager.Instance.IsInProgress
            && LocalContext.NetId != player.NetId
            && LocalSelfCoopContext.LocalPlayerIds.Contains(player.NetId);
    }

    /// <summary>
    /// The first living local character, in session order, who isn't the driver, while simultaneous mode is on (in or
    /// out of combat). Null otherwise.
    /// </summary>
    public static Player? FindTeammate()
    {
        if (!CouchConfig.SimultaneousEnabled || !LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress)
        {
            return null;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        return LocalMultiControlRuntime.SessionState.OrderedPlayerIds
            .Where((ulong id) => id != LocalContext.NetId)
            .Select((ulong id) => runState?.GetPlayer(id))
            .FirstOrDefault((Player? p) => p != null && p.Creature.IsAlive);
    }

    /// <summary>True if <paramref name="player"/> is a local character other than the driver, during simultaneous combat.</summary>
    public static bool IsTeammate(Player? player)
    {
        return player != null
            && DriverKeepsScreen
            && LocalContext.NetId != player.NetId
            && LocalSelfCoopContext.LocalPlayerIds.Contains(player.NetId);
    }
}

using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
internal static class CombatManagerPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, ref bool canBackOut, ref Func<Task>? actionDuringEnemyTurn)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        // Couch simultaneous combat: each player can un-end their own turn, as in vanilla co-op.
        if (CouchTeammate.DriverKeepsScreen)
        {
            return;
        }

        if (canBackOut)
        {
            canBackOut = false;
            ModLog.Info($"Local co-op mode disables turn rollback: player={player.NetId}");
        }
    }
}

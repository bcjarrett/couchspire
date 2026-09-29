using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(NEndTurnButton), "CallReleaseLogic")]
internal static class NEndTurnButtonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NEndTurnButton __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        // Couch simultaneous combat: each player ends (and un-ends) only their own turn, as in vanilla co-op. The
        // hotseat rules below would block "Undo End Turn" and could end the teammate's turn for them.
        if (CouchTeammate.DriverKeepsScreen)
        {
            return true;
        }

        CombatState? combatState = AccessTools.Field(typeof(NEndTurnButton), "_combatState")?.GetValue(__instance) as CombatState;
        if (combatState == null)
        {
            return true;
        }

        bool handled = LocalControlRuntime.TryManualEndTurnAutoCloseAllPlayers();
        if (handled)
        {
            ModLog.Info("End-turn click was handled by the \"all players have no playable cards\" rule; skipping the original end-turn logic.");
            return false;
        }

        Player? me = LocalContext.GetMe(combatState);
        if (me != null)
        {
            LocalControlRuntime.RecordManualEndTurnIntent(me.NetId, "end-turn-button");
        }

        if (me != null && CombatManager.Instance.IsPlayerReadyToEndTurn(me))
        {
            ModLog.Info($"Ignoring end-turn back-out click: player={me.NetId}");
            return false;
        }

        return true;
    }
}

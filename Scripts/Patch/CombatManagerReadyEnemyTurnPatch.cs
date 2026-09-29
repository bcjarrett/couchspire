using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// When one local character readies for the enemy turn, mirror that readiness onto every other local character so
/// the game's own all-players-ready check passes.
///
/// v0.107.1's <c>CombatManager</c> keeps <c>_playersReadyToBeginEnemyTurn</c> (a <c>HashSet&lt;Player&gt;</c>) and
/// <c>_playerReadyLock</c> directly as its own fields and calls <c>AfterAllPlayersReadyToBeginEnemyTurn</c> itself,
/// synchronously, once every player is in the set — no separate <c>CombatTurnState</c>/signal-source indirection
/// (that only exists in the v0.111.0 beta this mod previously targeted). So the prefix only needs to add the other
/// local players to that set before the original method's own count check runs; the original then drives the
/// transition itself, exactly as it would for a real second player.
/// </summary>
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToBeginEnemyTurn))]
internal static class CombatManagerReadyEnemyTurnPatch
{
    private static readonly AccessTools.FieldRef<CombatManager, HashSet<Player>> ReadySetRef =
        AccessTools.FieldRefAccess<CombatManager, HashSet<Player>>("_playersReadyToBeginEnemyTurn");

    private static readonly AccessTools.FieldRef<CombatManager, System.Threading.Lock> ReadyLockRef =
        AccessTools.FieldRefAccess<CombatManager, System.Threading.Lock>("_playerReadyLock");

    [HarmonyPrefix]
    private static void Prefix(CombatManager __instance, Player player)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        CombatState? state = __instance.DebugOnlyGetState();
        if (state == null || state.CurrentSide != CombatSide.Player || state.Players.Count < 2)
        {
            return;
        }

        HashSet<Player> readySet = ReadySetRef(__instance);
        System.Threading.Lock readyLock = ReadyLockRef(__instance);

        List<Player> pendingPlayers;
        using (readyLock.EnterScope())
        {
            pendingPlayers = state.Players
                .Where((candidate) => candidate.NetId != player.NetId)
                .Where((candidate) => !readySet.Contains(candidate))
                .ToList();
            foreach (Player pendingPlayer in pendingPlayers)
            {
                readySet.Add(pendingPlayer);
            }
        }

        if (pendingPlayers.Count > 0)
        {
            ModLog.Info(
                $"Local co-op auto-filled enemy-turn ready state: trigger={player.NetId}, mirrored={string.Join(",", pendingPlayers.Select((candidate) => candidate.NetId))}");
        }
    }
}

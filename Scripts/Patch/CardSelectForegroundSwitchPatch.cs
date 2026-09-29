using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

internal static class CardSelectForegroundSwitchPatch
{
    private static bool TryGetRejectReason(out string reason)
    {
        reason = string.Empty;
        if (!RunManager.Instance.IsInProgress || !CombatManager.Instance.IsInProgress)
        {
            reason = "combat-not-in-progress";
            return true;
        }

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            reason = "combat-ui-null";
            return true;
        }

        NPlayerHand hand = combatUi.Hand;
        if (hand.InCardPlay)
        {
            reason = "hand-in-card-play";
            return true;
        }

        if (hand.IsInCardSelection)
        {
            reason = "hand-in-card-selection";
            return true;
        }

        if (NTargetManager.Instance?.IsInSelection ?? false)
        {
            reason = "target-selecting";
            return true;
        }

        ActionSynchronizerCombatState syncState = RunManager.Instance.ActionQueueSynchronizer.CombatState;
        if (syncState != ActionSynchronizerCombatState.PlayPhase)
        {
            reason = $"sync-{syncState}";
            return true;
        }

        return false;
    }

    private static void EnsureForegroundForCombatChoice(Player player, string source)
    {
        // Couch simultaneous combat: the driver keeps the screen; the teammate answers its own choice.
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode || CouchTeammate.IsTeammate(player))
        {
            return;
        }

        if (!LocalSelfCoopContext.LocalPlayerIds.Contains(player.NetId))
        {
            return;
        }

        ulong currentPlayerId = LocalControlRuntime.SessionState.CurrentControlledPlayerId
            ?? LocalContext.NetId
            ?? LocalSelfCoopContext.PrimaryPlayerId;
        if (currentPlayerId == player.NetId)
        {
            return;
        }

        if (TryGetRejectReason(out string reason))
        {
            LocalControlRuntime.RecordFlowBlockSignal(
                "foreground_switch_rejected_due_to_state",
                reason,
                player.NetId,
                source,
                round: -1);
            ModLog.Info(
                $"Combat card-selection foreground switch skipped: source={source}, target={player.NetId}, reason={reason}");
            return;
        }

        ModLog.Info(
            $"Detected a combat card-selection request from a backgrounded player; preparing to defer a foreground switch for manual selection: source={source}, current={currentPlayerId}, target={player.NetId}");
        Callable.From(delegate
        {
            if (TryGetRejectReason(out string deferredReason))
            {
                LocalControlRuntime.RecordFlowBlockSignal(
                    "foreground_switch_rejected_due_to_state",
                    $"{deferredReason}-deferred",
                    player.NetId,
                    source,
                    round: -1);
                ModLog.Info(
                    $"Combat card-selection deferred foreground switch canceled: source={source}, target={player.NetId}, reason={deferredReason}");
                return;
            }

            LocalControlRuntime.SwitchControlledPlayerTo(player.NetId, $"combat-choice-{source}");
        }).CallDeferred();
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
    [HarmonyPrefix]
    private static void FromSimpleGridPrefix(Player player)
    {
        EnsureForegroundForCombatChoice(player, "FromSimpleGrid");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
    [HarmonyPrefix]
    private static void FromHandPrefix(Player player)
    {
        EnsureForegroundForCombatChoice(player, "FromHand");
    }
}

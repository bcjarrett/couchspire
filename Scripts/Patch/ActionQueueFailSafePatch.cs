using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(NCardPlayQueue), "OnActionEnqueued")]
internal static class NCardPlayQueueOnActionEnqueuedFailSafePatch
{
    private static ulong _lastLogAtMs;

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, GameAction action)
    {
        if (!LocalSelfCoopContext.IsEnabled || __exception is not NullReferenceException)
        {
            return __exception;
        }

        ulong now = Time.GetTicksMsec();
        if (now - _lastLogAtMs >= 600)
        {
            _lastLogAtMs = now;
            ModLog.Warn(
                $"Action queue UI enqueue triggered a null reference, intercepted to avoid blocking: action={action?.ToString() ?? "null"}, context={LocalContext.NetId?.ToString() ?? "null"}");
        }

        TryRecoverActionOwnerContext();
        return null;
    }

    private static void TryRecoverActionOwnerContext()
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return;
        }

        ulong playerId = LocalContext.NetId ?? LocalSelfCoopContext.PrimaryPlayerId;
        if (playerId == 0)
        {
            return;
        }

        LocalControlRuntime.AlignContextForActionOwner(playerId, "action-queue-ui-failsafe");
    }
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), "RequestEnqueue", new[] { typeof(GameAction) })]
internal static class ActionQueueSynchronizerRequestEnqueueFailSafePatch
{
    private static ulong _lastLogAtMs;

    [HarmonyPrefix]
    private static void Prefix(GameAction action)
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress || !CombatManager.Instance.IsInProgress)
        {
            return;
        }

        ActionSynchronizerCombatState syncState = RunManager.Instance.ActionQueueSynchronizer.CombatState;
        if (syncState == ActionSynchronizerCombatState.PlayPhase)
        {
            return;
        }

        if (action is not PlayCardAction && action is not EndPlayerTurnAction)
        {
            return;
        }

        int round = NCombatRoom.Instance?.Ui != null
            ? (AccessTools.Field(typeof(NEndTurnButton), "_combatState")?.GetValue(NCombatRoom.Instance.Ui.EndTurnButton) as CombatState)?.RoundNumber ?? -1
            : -1;
        ulong playerId = LocalContext.NetId ?? LocalSelfCoopContext.PrimaryPlayerId;
        LocalControlRuntime.RecordFlowBlockSignal(
            "deferred_play_detected_during_enemy_turn",
            syncState.ToString(),
            playerId,
            "RequestEnqueue",
            round);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, GameAction action)
    {
        if (!LocalSelfCoopContext.IsEnabled || __exception is not NullReferenceException)
        {
            return __exception;
        }

        ulong now = Time.GetTicksMsec();
        if (now - _lastLogAtMs >= 600)
        {
            _lastLogAtMs = now;
            ModLog.Warn(
                $"RequestEnqueue null reference intercepted, avoiding a block: action={action?.ToString() ?? "null"}, context={LocalContext.NetId?.ToString() ?? "null"}");
        }

        ulong playerId = LocalContext.NetId ?? LocalSelfCoopContext.PrimaryPlayerId;
        if (playerId != 0)
        {
            LocalControlRuntime.AlignContextForActionOwner(playerId, "request-enqueue-failsafe");
        }

        return null;
    }
}

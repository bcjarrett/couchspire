using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NEventRoom), "RefreshEventState")]
internal static class NEventRoomPatch
{
    [HarmonyPostfix]
    private static void Postfix(NEventRoom __instance, EventModel eventModel)
    {
        if (LocalSelfCoopContext.UseSingleEventFlow)
        {
            return;
        }

        if (TryAutoSwitchToNextPendingEvent(eventModel))
        {
            return;
        }

        if (!LocalSelfCoopContext.ShouldQueueEventAutoSwitchAfterEventState(eventModel))
        {
            return;
        }

        if (NOverlayStack.Instance?.ScreenCount > 0)
        {
            LocalMultiControlLogger.Info("Event flow complete; waiting for the reward/choice popup to close before auto-switching players.");
            return;
        }

        Callable.From(delegate
        {
            if (RunManager.Instance.IsInProgress)
            {
                LocalMultiControlRuntime.TryRunPendingEventAutoSwitch("event-auto-next");
            }
        }).CallDeferred();
    }

    private static bool TryAutoSwitchToNextPendingEvent(EventModel eventModel)
    {
        if (RunManager.Instance.EventSynchronizer.IsShared)
        {
            return false;
        }

        if (eventModel.Owner == null || !eventModel.IsFinished)
        {
            return false;
        }

        EventModel? pendingEvent = RunManager.Instance.EventSynchronizer.Events.FirstOrDefault((candidate) =>
            candidate.Owner != null &&
            candidate.Owner.NetId != eventModel.Owner.NetId &&
            !candidate.IsFinished);
        if (pendingEvent?.Owner == null)
        {
            return false;
        }

        // Couch simultaneous mode: the teammate plays their event in their own panel.
        if (CouchTeammateEvent.IsActive)
        {
            return false;
        }

        if (NOverlayStack.Instance?.ScreenCount > 0)
        {
            LocalMultiControlLogger.Info($"Event complete; waiting for the popup to close before auto-switching to the next player: {eventModel.Owner.NetId} -> {pendingEvent.Owner.NetId}");
            return false;
        }

        Callable.From(delegate
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return;
            }

            LocalMultiControlLogger.Info($"Event auto-switched to the next player pending selection: {eventModel.Owner.NetId} -> {pendingEvent.Owner.NetId}");
            LocalMultiControlRuntime.SwitchControlledPlayerTo(pendingEvent.Owner.NetId, "event-finished-next-player");
        }).CallDeferred();
        return true;
    }
}

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class NEventRoomOptionButtonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NEventRoom __instance, EventOption option)
    {
        // Verified in-game: this interception ensures that for Neow/non-shared events both players must finish before Proceed is allowed.
        // This is a stability-critical point in the early-game flow; if changes are needed later, do a log regression first to avoid regressing to "only one player can choose".
        if (!LocalSelfCoopContext.IsEnabled || !option.IsProceed || !RunManager.Instance.IsInProgress)
        {
            return true;
        }

        if (LocalSelfCoopContext.UseSingleEventFlow)
        {
            return true;
        }

        // Couch simultaneous mode: don't swap to the teammate's event; NEventRoom.Proceed waits for them instead.
        if (CouchTeammateEvent.IsActive)
        {
            return true;
        }

        if (RunManager.Instance.EventSynchronizer.IsShared)
        {
            return true;
        }

        EventModel? currentEvent = AccessTools.Field(typeof(NEventRoom), "_event")?.GetValue(__instance) as EventModel;
        if (currentEvent?.Owner == null || !currentEvent.IsFinished)
        {
            return true;
        }

        EventModel? pendingEvent = RunManager.Instance.EventSynchronizer.Events.FirstOrDefault((eventModel) =>
            eventModel.Owner != null &&
            eventModel.Owner.NetId != currentEvent.Owner.NetId &&
            !eventModel.IsFinished);
        if (pendingEvent?.Owner == null)
        {
            return true;
        }

        LocalMultiControlLogger.Info($"Detected another player has not yet finished the event; intercepted Proceed and switched to player={pendingEvent.Owner.NetId}");
        LocalMultiControlRuntime.SwitchControlledPlayerTo(pendingEvent.Owner.NetId, "event-proceed-next-player");
        return false;
    }
}

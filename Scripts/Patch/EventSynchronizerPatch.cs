using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
internal static class EventSynchronizerPatch
{
    private static bool _isChoosingSharedEventOption;

    private struct SenderState
    {
        internal bool IsPatched;
        internal ulong? PreviousContextNetId;
        internal ulong PreviousSenderId;
    }

    [HarmonyPrefix]
    private static void Prefix(EventSynchronizer __instance, int index, ref SenderState __state)
    {
        __state = default;
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleEventFlow)
        {
            return;
        }

        if (!__instance.IsShared)
        {
            return;
        }

        INetGameService? netService = AccessTools.Field(typeof(EventSynchronizer), "_netService")?.GetValue(__instance) as INetGameService;
        if (netService is not LocalLoopbackHostGameService loopbackService)
        {
            return;
        }

        __state.IsPatched = true;
        __state.PreviousContextNetId = LocalContext.NetId;
        __state.PreviousSenderId = loopbackService.NetId;

        LocalContext.NetId = LocalSelfCoopContext.PrimaryPlayerId;
        loopbackService.SetCurrentSenderId(LocalSelfCoopContext.PrimaryPlayerId);
    }

    [HarmonyPostfix]
    private static void Postfix(EventSynchronizer __instance, int index, SenderState __state)
    {
        try
        {
            TryAutoProxyEventChoice(__instance, index);
        }
        finally
        {
            if (__state.IsPatched)
            {
                INetGameService? netService = AccessTools.Field(typeof(EventSynchronizer), "_netService")?.GetValue(__instance) as INetGameService;
                if (netService is LocalLoopbackHostGameService loopbackService && loopbackService.NetId != __state.PreviousSenderId)
                {
                    loopbackService.SetCurrentSenderId(__state.PreviousSenderId);
                }

                LocalContext.NetId = __state.PreviousContextNetId;
            }
        }
    }

    private static void TryAutoProxyEventChoice(EventSynchronizer synchronizer, int index)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        // Couch simultaneous mode: the teammate votes and chooses in their own event panel, so don't vote for them
        // or switch the screen to their event.
        if (CouchTeammateEvent.IsActive)
        {
            return;
        }

        if (!synchronizer.IsShared)
        {
            // Couch simultaneous mode: every player plays their own event (the teammate in their panel), so the
            // driver keeps the screen even after the teammate has finished theirs.
            if (CouchConfig.SimultaneousEnabled)
            {
                return;
            }

            ulong currentPlayerId =
                AccessTools.Field(typeof(EventSynchronizer), "_localPlayerId")?.GetValue(synchronizer) as ulong?
                ?? LocalMultiControlRuntime.SessionState.CurrentControlledPlayerId
                ?? 0UL;
            if (currentPlayerId != 0)
            {
                LocalSelfCoopContext.RequestEventAutoSwitchAfterChoice(currentPlayerId);
            }

            return;
        }

        try
        {
            INetGameService? netService = AccessTools.Field(typeof(EventSynchronizer), "_netService")?.GetValue(synchronizer) as INetGameService;
            if (netService is not LocalLoopbackHostGameService)
            {
                return;
            }

            IPlayerCollection? playerCollection = AccessTools.Field(typeof(EventSynchronizer), "_playerCollection")?.GetValue(synchronizer) as IPlayerCollection;
            List<uint?>? votes = AccessTools.Field(typeof(EventSynchronizer), "_playerVotes")?.GetValue(synchronizer) as List<uint?>;
            if (playerCollection == null || votes == null)
            {
                return;
            }

            int sharedCount = Math.Min(playerCollection.Players.Count, votes.Count);
            if (sharedCount < 2)
            {
                return;
            }

            // If the shared event has already resolved and cleared the votes (e.g. the last vote triggered the base game's resolution), skip directly here
            // to avoid writing votes again, which would cause a duplicate trigger and mess up the control context.
            if (!votes.Take(sharedCount).Any((vote) => vote.HasValue))
            {
                return;
            }

            List<Player> players = playerCollection.Players.Take(sharedCount).ToList();
            ulong localPlayerId = LocalMultiControlRuntime.SessionState.CurrentControlledPlayerId
                ?? LocalContext.NetId
                ?? LocalSelfCoopContext.PrimaryPlayerId;

            int localSlot = players.FindIndex((player) => player.NetId == localPlayerId);
            if (localSlot < 0)
            {
                localSlot = players.FindIndex((player) => player.NetId == LocalSelfCoopContext.PrimaryPlayerId);
            }

            if (localSlot < 0)
            {
                return;
            }

            uint selectedOption = (uint)index;
            votes[localSlot] = selectedOption;
            int filledCount = 1;
            for (int i = 0; i < sharedCount; i++)
            {
                if (i == localSlot)
                {
                    continue;
                }

                if (!votes[i].HasValue || votes[i]!.Value != selectedOption)
                {
                    votes[i] = selectedOption;
                }

                filledCount++;
            }

            LocalMultiControlLogger.Info($"Shared event auto-filled votes: option={index}, filled={filledCount}/{sharedCount}");
            if (votes.Take(sharedCount).All((vote) => vote.HasValue) && netService.Type != NetGameType.Client)
            {
                TryChooseSharedEventOptionDeferred(synchronizer);
            }
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Shared event auto vote-fill failed: {exception.Message}");
        }
    }

    private static void TryChooseSharedEventOptionDeferred(EventSynchronizer synchronizer)
    {
        if (_isChoosingSharedEventOption)
        {
            LocalMultiControlLogger.Warn("Shared event resolution already in progress; skipping duplicate trigger.");
            return;
        }

        _isChoosingSharedEventOption = true;
        Callable.From(delegate
        {
            try
            {
                AccessTools.Method(typeof(EventSynchronizer), "ChooseSharedEventOption")?.Invoke(synchronizer, Array.Empty<object>());
                LocalMultiControlLogger.Info("Shared event auto vote-fill complete, triggered resolution.");
            }
            catch (Exception exception)
            {
                LocalMultiControlLogger.Warn($"Failed to trigger shared event resolution: {exception.Message}");
            }
            finally
            {
                _isChoosingSharedEventOption = false;
            }
        }).CallDeferred();
    }
}

[HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
internal static class EventSynchronizerChooseOptionForEventPatch
{
    private static readonly SemaphoreSlim SharedEventChoiceSemaphore = new(1, 1);

    [HarmonyPrefix]
    private static bool Prefix(EventSynchronizer __instance, Player player, int optionIndex)
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress || !__instance.IsShared)
        {
            return true;
        }

        if (RunManager.Instance.NetService is not LocalLoopbackHostGameService)
        {
            return true;
        }

        EventModel eventForPlayer = __instance.GetEventForPlayer(player);
        if (eventForPlayer.IsFinished)
        {
            throw new InvalidOperationException($"Option chosen for player {player} on {eventForPlayer}, but it is already finished!");
        }

        if (optionIndex < 0 || optionIndex >= eventForPlayer.CurrentOptions.Count)
        {
            throw new InvalidOperationException(
                $"Player {player.NetId} attempted to choose option index {optionIndex} in event {eventForPlayer.Id}, but there were only {eventForPlayer.CurrentOptions.Count} options available!");
        }

        EventOption eventOption = eventForPlayer.CurrentOptions[optionIndex];
        AccessTools.Method(typeof(EventSynchronizer), "SaveEventOptionToHistory")
            ?.Invoke(__instance, new object[] { player, eventOption });

        TaskHelper.RunSafely(ExecuteSharedOptionSeriallyAsync(player, eventOption));
        return false;
    }

    private static async Task ExecuteSharedOptionSeriallyAsync(Player player, EventOption eventOption)
    {
        await SharedEventChoiceSemaphore.WaitAsync();
        try
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return;
            }

            ulong? driverBefore = LocalContext.NetId;
            LocalMultiControlRuntime.SwitchControlledPlayerTo(player.NetId, "event-shared-serial-execution");
            LocalMultiControlLogger.Info($"Shared event starting to execute player option: player={player.NetId}, key={eventOption.TextKey}");
            await eventOption.Chosen();
            LocalMultiControlLogger.Info($"Shared event player option execution complete: player={player.NetId}, key={eventOption.TextKey}");

            // Couch simultaneous mode: give the screen back to the driver after running the teammate's part.
            if (CouchConfig.SimultaneousEnabled && driverBefore.HasValue && driverBefore.Value != player.NetId)
            {
                LocalMultiControlRuntime.SwitchControlledPlayerTo(driverBefore.Value, "event-shared-serial-restore-driver");
            }
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Shared event player option execution failed: player={player.NetId}, key={eventOption.TextKey}, error={exception.Message}");
        }
        finally
        {
            SharedEventChoiceSemaphore.Release();
        }
    }
}

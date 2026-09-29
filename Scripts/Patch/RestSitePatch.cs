using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Generate))]
internal static class RestSiteOptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref List<RestSiteOption> __result)
    {
        // Requirement change: rest site keeps the original multiplayer options; no longer trimmed.
    }
}

[HarmonyPatch(typeof(HealRestSiteOption), nameof(HealRestSiteOption.OnSelect))]
internal static class HealRestSiteOptionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(HealRestSiteOption __instance, ref Task<bool> __result)
    {
        // Requirement change: rest site healing resolves independently per player; no longer intercepted as an all-players heal.
        return true;
    }
}

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
internal static class RestSiteSynchronizerChooseLocalOptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(RestSiteSynchronizer __instance, int index, ref Task<bool> __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        __result = WrapChooseLocalOptionAsync(__instance, index, __result);
    }

    private static async Task<bool> WrapChooseLocalOptionAsync(RestSiteSynchronizer synchronizer, int optionIndex, Task<bool> originalTask)
    {
        ulong? localPlayerId = LocalContext.NetId;
        if (!localPlayerId.HasValue)
        {
            localPlayerId = ReadLocalPlayerIdFromSynchronizer(synchronizer);
        }

        List<RestSiteOption> sourceOptionsSnapshot = new List<RestSiteOption>();
        if (localPlayerId.HasValue)
        {
            sourceOptionsSnapshot = synchronizer.GetOptionsForPlayer(localPlayerId.Value).ToList();
        }

        bool success = await originalTask;
        if (!localPlayerId.HasValue)
        {
            ModLog.Warn($"Rest site upgrade switch failed: unable to identify the current local player, optionIndex={optionIndex}");
            return success;
        }

        if (!success)
        {
            ModLog.Warn(
                $"Rest site option execution failed; not triggering auto-switch: player={localPlayerId.Value}, optionIndex={optionIndex}, snapshot={DescribeOptions(sourceOptionsSnapshot)}");
            return success;
        }

        // Couch simultaneous mode: the teammate picks in their own rest site panel.
        if (CouchConfig.SimultaneousEnabled && CouchTeammate.FindTeammate() != null)
        {
            return success;
        }

        if (TryFindNextSelectablePlayer(synchronizer, localPlayerId.Value, out ulong nextPlayerId))
        {
            ModLog.Info(
                $"Rest site choice succeeded; queued a switch to the next player pending selection (not auto-choosing for them): {localPlayerId.Value} -> {nextPlayerId}, optionIndex={optionIndex}, snapshot={DescribeOptions(sourceOptionsSnapshot)}");
            Callable.From(delegate
            {
                RestSiteAutoSwitchUtil.SwitchToPlayerAndEnsureOptions(nextPlayerId, "rest-site-next-player-choice");
            }).CallDeferred();
        }
        else
        {
            ModLog.Info(
                $"Rest site choice complete: all eligible players have chosen. player={localPlayerId.Value}, optionIndex={optionIndex}");
            Callable.From(delegate
            {
                RestSiteAutoSwitchUtil.ShowAllPlayersSelectedNotice();
            }).CallDeferred();
        }

        return success;
    }

    private static bool TryFindNextSelectablePlayer(RestSiteSynchronizer synchronizer, ulong currentPlayerId, out ulong nextPlayerId)
    {
        IReadOnlyList<ulong> orderedPlayerIds = LocalControlRuntime.SessionState.OrderedPlayerIds;
        if (orderedPlayerIds.Count < 2)
        {
            nextPlayerId = 0;
            return false;
        }

        int currentIndex = IndexOfPlayer(orderedPlayerIds, currentPlayerId);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        for (int step = 1; step < orderedPlayerIds.Count; step++)
        {
            int index = (currentIndex + step) % orderedPlayerIds.Count;
            ulong candidatePlayerId = orderedPlayerIds[index];
            if (candidatePlayerId == currentPlayerId)
            {
                continue;
            }

            IReadOnlyList<RestSiteOption> candidateOptions = synchronizer.GetOptionsForPlayer(candidatePlayerId);
            if (candidateOptions.Count > 0)
            {
                nextPlayerId = candidatePlayerId;
                return true;
            }
        }

        nextPlayerId = 0;
        return false;
    }

    private static int IndexOfPlayer(IReadOnlyList<ulong> orderedPlayerIds, ulong playerId)
    {
        for (int i = 0; i < orderedPlayerIds.Count; i++)
        {
            if (orderedPlayerIds[i] == playerId)
            {
                return i;
            }
        }

        return -1;
    }

    private static ulong? ReadLocalPlayerIdFromSynchronizer(RestSiteSynchronizer synchronizer)
    {
        object? fieldValue = AccessTools.Field(typeof(RestSiteSynchronizer), "_localPlayerId")?.GetValue(synchronizer);
        if (fieldValue is ulong fieldPlayerId)
        {
            return fieldPlayerId;
        }

        return null;
    }

    private static string DescribeOptions(IReadOnlyList<RestSiteOption> options)
    {
        if (options.Count == 0)
        {
            return "[]";
        }

        return "[" + string.Join(", ", options.Select((option, index) => $"{index}:{option.GetType().Name}")) + "]";
    }
}

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.Dispose))]
internal static class RestSiteSynchronizerDisposePatch
{
    [HarmonyPrefix]
    private static void Prefix(RestSiteSynchronizer __instance)
    {
        // Task.Dispose() throws "A task may only be disposed if it is in a completion state" if the hover-message
        // task (a fire-and-forget Task.Delay/Task.Yield started by QueueHoverMessage) is still pending when Dispose
        // runs - e.g. abandoning a run right after hovering a rest site option. The base game's Dispose() doesn't
        // guard this. Detach it here so the original method's `_hoverMessageTask?.Dispose()` becomes a no-op; the
        // orphaned task still completes on its own and needs no explicit disposal.
        ref Task? hoverMessageTask = ref AccessTools.FieldRefAccess<RestSiteSynchronizer, Task?>(__instance, "_hoverMessageTask");
        if (hoverMessageTask != null && !hoverMessageTask.IsCompleted)
        {
            ModLog.Info("Rest site disposed while its hover-message task was still pending; detached it instead of disposing.");
            hoverMessageTask = null;
        }
    }
}

[HarmonyPatch(typeof(NRestSiteRoom), "AfterSelectingOptionAsync")]
internal static class NRestSiteRoomAfterSelectingOptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        // Auto-switch after selection is handled centrally by RestSiteSynchronizerChooseLocalOptionPatch.
    }
}

[HarmonyPatch(typeof(NRestSiteRoom), "OnPlayerChangedHoveredRestSiteOption")]
internal static class NRestSiteRoomHoverGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NRestSiteRoom __instance, ulong playerId)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return true;
        }

        IRunState? runState = AccessTools.Field(typeof(NRestSiteRoom), "_runState")?.GetValue(__instance) as IRunState;
        if (runState == null || runState.Players.Count <= 1)
        {
            return false;
        }

        NRestSiteCharacter? character = __instance.Characters.FirstOrDefault((candidate) => candidate.Player.NetId == playerId);
        if (character == null)
        {
            return false;
        }

        int? hoveredOptionIndex = RunManager.Instance.RestSiteSynchronizer.GetHoveredOptionIndex(playerId);
        RestSiteOption? option = null;
        if (hoveredOptionIndex.HasValue)
        {
            IReadOnlyList<RestSiteOption> options = RunManager.Instance.RestSiteSynchronizer.GetOptionsForPlayer(playerId);
            if (hoveredOptionIndex.Value >= 0 && hoveredOptionIndex.Value < options.Count)
            {
                option = options[hoveredOptionIndex.Value];
            }
            else
            {
                ModLog.Warn($"Rest site hover index out of range; ignored: player={playerId}, index={hoveredOptionIndex.Value}, options={options.Count}");
            }
        }

        character.ShowHoveredRestSiteOption(option);
        return false;
    }
}

[HarmonyPatch(typeof(NRestSiteButton), "SelectOption")]
internal static class NRestSiteButtonSelectGuardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(RestSiteOption option, ref Task __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return true;
        }

        NRestSiteRoom? room = NRestSiteRoom.Instance;
        if (room == null)
        {
            return true;
        }

        if (FindOptionIndex(room.Options, option) >= 0)
        {
            return true;
        }

        RestSiteUiRefreshUtil.TryRefresh("button-option-mismatch");
        room = NRestSiteRoom.Instance;
        if (room != null && FindOptionIndex(room.Options, option) >= 0)
        {
            return true;
        }

        RunManager.Instance.RestSiteSynchronizer.LocalOptionHovered(null);
        ModLog.Warn("Rest site button does not match the current option list; rejected this click and refreshed.");
        __result = Task.CompletedTask;
        return false;
    }

    private static int FindOptionIndex(IReadOnlyList<RestSiteOption> options, RestSiteOption target)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i] == target)
            {
                return i;
            }
        }

        return -1;
    }
}

[HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
internal static class NRestSiteRoomReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NRestSiteRoom __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        NextFrame(() => EnsurePrimaryPlayerOptionsVisible(__instance, attempt: 0, loadingSettledFramesLeft: 2, switchedToPrimary: false, loadingFramesWaited: 0));
    }

    // Retries wait a real frame. A CallDeferred queued from inside a deferred call runs in the same flush, so a
    // "wait until loading ends" loop built on it never lets a frame pass, loading never ends, and the game freezes
    // with the message queue full (found by the rest_site test scenario).
    private static void NextFrame(Action action)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        void OnFrame()
        {
            tree.ProcessFrame -= OnFrame;
            action();
        }

        tree.ProcessFrame += OnFrame;
    }

    private const int MaxLoadingWaitFrames = 300;

    private static void EnsurePrimaryPlayerOptionsVisible(
        NRestSiteRoom room,
        int attempt,
        int loadingSettledFramesLeft,
        bool switchedToPrimary,
        int loadingFramesWaited)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode || !RunManager.Instance.IsInProgress)
        {
            return;
        }

        if (NRestSiteRoom.Instance != room)
        {
            return;
        }

        bool isLoading = LocalSelfCoopContext.NetService?.IsGameLoading ?? false;
        if (isLoading && loadingFramesWaited < MaxLoadingWaitFrames)
        {
            NextFrame(() => EnsurePrimaryPlayerOptionsVisible(room, attempt, loadingSettledFramesLeft: 2, switchedToPrimary, loadingFramesWaited + 1));
            return;
        }

        if (isLoading)
        {
            ModLog.Warn($"Rest site entry: the game still reports loading after {MaxLoadingWaitFrames} frames; checking options anyway.");
        }
        else if (loadingSettledFramesLeft > 0)
        {
            NextFrame(() => EnsurePrimaryPlayerOptionsVisible(room, attempt, loadingSettledFramesLeft - 1, switchedToPrimary, loadingFramesWaited));
            return;
        }

        if (!switchedToPrimary)
        {
            LocalControlRuntime.SwitchControlledPlayerTo(LocalSelfCoopContext.PrimaryPlayerId, $"rest-site-enter-primary-{attempt}");
            switchedToPrimary = true;
        }

        RestSiteUiRefreshUtil.TryRefresh($"rest-site-enter-primary-{attempt}");

        int optionCount = room.Options.Count;
        int localOptionCount = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions().Count;
        if (optionCount > 0 || localOptionCount > 0 || attempt >= 6)
        {
            ModLog.Info(
                $"Rest site post-entry option check: attempt={attempt}, options={optionCount}, localOptions={localOptionCount}, switchedToPrimary={switchedToPrimary}");
            return;
        }

        NextFrame(() => EnsurePrimaryPlayerOptionsVisible(room, attempt + 1, loadingSettledFramesLeft: 1, switchedToPrimary, loadingFramesWaited));
    }
}

internal static class RestSiteAutoSwitchUtil
{
    private const int MaxRefreshAttempts = 5;

    internal static void SwitchToPlayerAndEnsureOptions(ulong targetPlayerId, string source)
    {
        EnsureOptionsAfterSwitch(targetPlayerId, source, attempt: 0, switched: false);
    }

    internal static void ShowAllPlayersSelectedNotice()
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create(LocalModText.RestSiteAllChosen));
        ModLog.Info("Rest site hint text shown: all eligible players have chosen.");
    }

    private static void EnsureOptionsAfterSwitch(ulong targetPlayerId, string source, int attempt, bool switched)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode || !RunManager.Instance.IsInProgress)
        {
            return;
        }

        NRestSiteRoom? room = NRestSiteRoom.Instance;
        if (room == null)
        {
            return;
        }

        if (!switched)
        {
            LocalControlRuntime.SwitchControlledPlayerTo(targetPlayerId, source);
            switched = true;
        }

        RestSiteUiRefreshUtil.TryRefresh($"{source}-attempt-{attempt}");

        int targetOptionCount = RunManager.Instance.RestSiteSynchronizer.GetOptionsForPlayer(targetPlayerId).Count;
        int localOptionCount = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions().Count;
        if (targetOptionCount > 0 && localOptionCount > 0)
        {
            ModLog.Info(
                $"Rest site auto-switched to the next player pending selection and refreshed successfully: target={targetPlayerId}, attempt={attempt}, targetOptions={targetOptionCount}, localOptions={localOptionCount}");
            return;
        }

        if (attempt >= MaxRefreshAttempts)
        {
            ModLog.Warn(
                $"Rest site option display still not restored after auto-switch: target={targetPlayerId}, attempts={attempt + 1}, targetOptions={targetOptionCount}, localOptions={localOptionCount}");
            return;
        }

        Callable.From(delegate
        {
            EnsureOptionsAfterSwitch(targetPlayerId, source, attempt + 1, switched);
        }).CallDeferred();
    }
}

internal static class RestSiteUiRefreshUtil
{
    internal static bool TryRefresh(string source)
    {
        NRestSiteRoom? room = NRestSiteRoom.Instance;
        if (room == null)
        {
            return false;
        }

        try
        {
            RunManager.Instance.RestSiteSynchronizer.LocalOptionHovered(null);
            AccessTools.Field(typeof(NRestSiteRoom), "_lastFocused")?.SetValue(room, null);
            AccessTools.Method(typeof(NRestSiteRoom), "UpdateRestSiteOptions")?.Invoke(room, null);
            EnsureChoicesVisibleForLocalPlayer(room, source);
            ModLog.Info($"Rest site options refreshed: source={source}, player={LocalContext.NetId?.ToString() ?? "null"}");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to refresh rest site options: source={source}, error={exception.Message}");
            return false;
        }
    }

    internal static void EnsureChoicesVisibleForLocalPlayer(NRestSiteRoom room, string source)
    {
        try
        {
            int localOptionCount = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions().Count;
            if (localOptionCount <= 0)
            {
                return;
            }

            Control? choicesScreen = AccessTools.Field(typeof(NRestSiteRoom), "_choicesScreen")?.GetValue(room) as Control;
            if (choicesScreen != null)
            {
                Color modulate = choicesScreen.Modulate;
                if (modulate.A < 0.99f)
                {
                    modulate.A = 1f;
                    choicesScreen.Modulate = modulate;
                }
            }

            AccessTools.Method(typeof(NRestSiteRoom), "EnableOptions")?.Invoke(room, null);
            AccessTools.Method(typeof(NRestSiteRoom), "AnimateDescriptionUp")?.Invoke(room, null);
            EnsureControllerFocus(room, source);
            ModLog.Info($"Rest site option visibility restored: source={source}, options={localOptionCount}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to restore rest site option visibility: source={source}, error={exception.Message}");
        }
    }

    private static void EnsureControllerFocus(NRestSiteRoom room, string source)
    {
        if (!(NControllerManager.Instance?.InputType == InputType.Controller))
        {
            return;
        }

        try
        {
            Control? focusTarget = FindFirstFocusableRestSiteButton(room) ?? FindFirstFocusableControl(room);
            if (focusTarget == null)
            {
                ModLog.Warn($"Rest site controller focus restore failed: no focusable control found. source={source}");
                return;
            }

            focusTarget.GrabFocus();
            ModLog.Info($"Rest site controller focus restored: source={source}, target={focusTarget.Name}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to restore rest site controller focus: source={source}, error={exception.Message}");
        }
    }

    private static Control? FindFirstFocusableRestSiteButton(Node root)
    {
        foreach (Node node in EnumerateDescendants(root))
        {
            if (node is NRestSiteButton button &&
                button.Visible &&
                button.FocusMode != Control.FocusModeEnum.None)
            {
                return button;
            }
        }

        return null;
    }

    private static Control? FindFirstFocusableControl(Node root)
    {
        foreach (Node node in EnumerateDescendants(root))
        {
            if (node is Control control &&
                control.Visible &&
                control.FocusMode != Control.FocusModeEnum.None)
            {
                return control;
            }
        }

        return null;
    }

    private static IEnumerable<Node> EnumerateDescendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node nested in EnumerateDescendants(child))
            {
                yield return nested;
            }
        }
    }
}

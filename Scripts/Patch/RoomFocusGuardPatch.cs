using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
internal static class NRestSiteRoomReadyGuardPatch
{
    private const int MaxRecoveryAttempts = 6;

    [HarmonyFinalizer]
    private static Exception? Finalizer(NRestSiteRoom __instance, Exception? __exception)
    {
        if (__exception is ArgumentOutOfRangeException)
        {
            int playerCount = ReadPlayerCount(__instance);
            int roomOptions = SafeCountRoomOptions(__instance);
            int localOptions = SafeCountLocalOptions();
            bool isLoading = LocalSelfCoopContext.NetService?.IsGameLoading ?? false;
            string controlledPlayer = LocalContext.NetId?.ToString() ?? "null";
            ModLog.Warn(
                $"Rest site initialization went out of range; intercepted and continuing the flow: error={__exception.Message}, players={playerCount}, controlled={controlledPlayer}, roomOptions={roomOptions}, localOptions={localOptions}, loading={isLoading}");

            if (playerCount > 4)
            {
                NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create(LocalModText.RestSiteFocusHint));
                ModLog.Warn($"[needs fix] Rest site with 5+ players may not show options on the first frame; showed a manual-switch hint. players={playerCount}");
            }

            Callable.From(delegate
            {
                TryRecoverRestSiteAfterReadyOutOfRange(__instance, attempt: 0, loadingSettledFramesLeft: 2, switchedToPrimary: false);
            }).CallDeferred();

            return null;
        }

        return __exception;
    }

    private static void TryRecoverRestSiteAfterReadyOutOfRange(
        NRestSiteRoom room,
        int attempt,
        int loadingSettledFramesLeft,
        bool switchedToPrimary)
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
        if (isLoading)
        {
            if (attempt >= MaxRecoveryAttempts)
            {
                ModLog.Warn(
                    $"Rest site out-of-range recovery failed: loading state never settled, manual player switch required. attempts={attempt + 1}, localOptions={SafeCountLocalOptions()}");
                return;
            }

            Callable.From(delegate
            {
                TryRecoverRestSiteAfterReadyOutOfRange(room, attempt + 1, loadingSettledFramesLeft: 2, switchedToPrimary);
            }).CallDeferred();
            return;
        }

        if (loadingSettledFramesLeft > 0)
        {
            Callable.From(delegate
            {
                TryRecoverRestSiteAfterReadyOutOfRange(room, attempt, loadingSettledFramesLeft - 1, switchedToPrimary);
            }).CallDeferred();
            return;
        }

        if (!switchedToPrimary)
        {
            LocalControlRuntime.SwitchControlledPlayerTo(LocalSelfCoopContext.PrimaryPlayerId, $"rest-site-finalizer-recover-{attempt}");
            switchedToPrimary = true;
        }

        RestSiteUiRefreshUtil.TryRefresh($"rest-site-finalizer-recover-{attempt}");

        int roomOptions = SafeCountRoomOptions(room);
        int localOptions = SafeCountLocalOptions();
        if (localOptions > 0)
        {
            ModLog.Info(
                $"Rest site out-of-range recovery succeeded: options are now visible. attempt={attempt}, roomOptions={roomOptions}, localOptions={localOptions}, controlled={LocalContext.NetId?.ToString() ?? "null"}");
            return;
        }

        if (attempt >= MaxRecoveryAttempts)
        {
            ModLog.Warn(
                $"Rest site out-of-range recovery ended: options still not shown, please switch players manually. attempts={attempt + 1}, roomOptions={roomOptions}, localOptions={localOptions}, controlled={LocalContext.NetId?.ToString() ?? "null"}");
            return;
        }

        Callable.From(delegate
        {
            TryRecoverRestSiteAfterReadyOutOfRange(room, attempt + 1, loadingSettledFramesLeft: 1, switchedToPrimary);
        }).CallDeferred();
    }

    private static int ReadPlayerCount(NRestSiteRoom room)
    {
        IRunState? runState = AccessTools.Field(typeof(NRestSiteRoom), "_runState")?.GetValue(room) as IRunState;
        return runState?.Players.Count ?? 0;
    }

    private static int SafeCountRoomOptions(NRestSiteRoom room)
    {
        try
        {
            return room.Options.Count;
        }
        catch
        {
            return -1;
        }
    }

    private static int SafeCountLocalOptions()
    {
        try
        {
            return RunManager.Instance.RestSiteSynchronizer.GetLocalOptions().Count;
        }
        catch
        {
            return -1;
        }
    }
}

[HarmonyPatch(typeof(NTreasureRoomRelicCollection), "get_DefaultFocusedControl")]
internal static class NTreasureRoomRelicCollectionFocusGuardPatch
{
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception is ArgumentOutOfRangeException)
        {
            ModLog.Warn($"Treasure room focus control went out of range; intercepted and skipped focus for this frame: {__exception.Message}");
            return null;
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnFocus")]
internal static class NTreasureRoomRelicHolderFocusGuardPatch
{
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception is InvalidOperationException exception &&
            exception.Message.Contains("Model was accessed before it was set", StringComparison.Ordinal))
        {
            ModLog.Warn("Treasure room relic model was not ready when focus arrived; skipped Focus for this frame.");
            return null;
        }

        return __exception;
    }
}

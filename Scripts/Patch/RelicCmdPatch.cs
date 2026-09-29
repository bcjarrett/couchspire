using HarmonyLib;
using CouchSpire.Scripts.Rewards;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), new[] { typeof(RelicModel), typeof(Player), typeof(int) })]
internal static class RelicCmdObtainPatch
{
    private static readonly HashSet<RelicModel> NonSharedChainRelics = new(ReferenceEqualityComparer.Instance);

    internal static bool TryConsumeNonSharedChainRelic(RelicModel relic)
    {
        return NonSharedChainRelics.Remove(relic);
    }

    [HarmonyPrefix]
    private static void Prefix()
    {
        GoldMirrorSuppressionContext.EnterSuppression();
    }

    [HarmonyPostfix]
    private static void Postfix(Player player, ref Task<RelicModel> __result)
    {
        // Start the wrapped task while suppression is active (its async flow captures it), then exit here,
        // synchronously: an AsyncLocal write inside an async method never flows back to the caller.
        __result = MirrorObtainForOtherLocalPlayersAsync(player, __result);
        GoldMirrorSuppressionContext.ExitSuppressionOnce();
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception != null)
        {
            GoldMirrorSuppressionContext.ExitSuppressionOnce();
        }

        return __exception;
    }

    private static async Task<RelicModel> MirrorObtainForOtherLocalPlayersAsync(Player player, Task<RelicModel> originalTask)
    {
        RelicModel obtainedRelic = await originalTask;
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return obtainedRelic;
        }

        if (player.RunState?.Players == null || player.RunState.Players.Count <= 1)
        {
            return obtainedRelic;
        }

        // In the combat-reward-merge flow, each player has already independently generated their reward, so no mirroring is needed
        if (CombatRewardMergeContext.IsActive)
        {
            ModLog.Info($"Skipping relic mirror during the combat-reward-merge flow: relic={obtainedRelic.Id.Entry}, owner={player.NetId}");
            return obtainedRelic;
        }

        bool isCombatRewardContext = player.RunState.CurrentRoom is CombatRoom && !CombatManager.Instance.IsInProgress;
        bool isCrystalSphereContext = CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext(player);
        if (!isCombatRewardContext && !isCrystalSphereContext)
        {
            return obtainedRelic;
        }

        if (PaelsWingPatch.TryConsumePendingOwner(player.NetId))
        {
            ModLog.Info($"Relic produced by a Pael's Wing sacrifice is not shared-mirrored: relic={obtainedRelic.Id.Entry}, owner={player.NetId}");
            return obtainedRelic;
        }

        bool skipChainMirror = ShouldSkipChainMirror(obtainedRelic);
        if (skipChainMirror)
        {
            NonSharedChainRelics.Add(obtainedRelic);
            ModLog.Info($"Chain relic handled as a special case, not shared-mirrored: relic={obtainedRelic.Id.Entry}, owner={player.NetId}");
            return obtainedRelic;
        }

        foreach (Player otherPlayer in player.RunState.Players.Where((candidate) => candidate.NetId != player.NetId))
        {
            if (!obtainedRelic.IsStackable && otherPlayer.GetRelicById(obtainedRelic.Id) != null)
            {
                continue;
            }

            try
            {
                RelicModel mirroredRelic = RelicModel.FromSerializable(obtainedRelic.ToSerializable());
                otherPlayer.AddRelicInternal(mirroredRelic);
                await mirroredRelic.AfterObtained();
                ModLog.Info($"Local co-op shared relic sync: {obtainedRelic.Id.Entry}, {player.NetId} -> {otherPlayer.NetId}");
            }
            catch (Exception exception)
            {
                ModLog.Warn($"Shared relic sync failed (obtain): target={otherPlayer.NetId}, error={exception.Message}");
            }
        }

        return obtainedRelic;
    }

    private static bool ShouldSkipChainMirror(RelicModel relic)
    {
        if (relic.IsWax)
        {
            return true;
        }

        string relicId = relic.Id.Entry;
        return relicId == "LARGE_CAPSULE" || relicId == "TOY_BOX";
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Remove))]
internal static class RelicCmdRemovePatch
{
    [HarmonyPostfix]
    private static void Postfix(RelicModel relic, ref Task __result)
    {
        __result = MirrorRemoveForOtherLocalPlayersAsync(relic, __result);
    }

    private static async Task MirrorRemoveForOtherLocalPlayersAsync(RelicModel removedRelic, Task originalTask)
    {
        await originalTask;
        if (RelicCmdObtainPatch.TryConsumeNonSharedChainRelic(removedRelic))
        {
            return;
        }

        if (!LocalSelfCoopContext.IsEnabled || removedRelic.Owner?.RunState == null)
        {
            return;
        }

        // Skip mirroring during the combat-reward-merge flow
        if (CombatRewardMergeContext.IsActive)
        {
            return;
        }

        bool isCombatRewardContext = removedRelic.Owner.RunState.CurrentRoom is CombatRoom && !CombatManager.Instance.IsInProgress;
        bool isCrystalSphereContext = CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext(removedRelic.Owner);
        if (!isCombatRewardContext && !isCrystalSphereContext)
        {
            return;
        }

        IRunState runState = removedRelic.Owner.RunState;
        if (runState.Players.Count <= 1)
        {
            return;
        }

        foreach (Player otherPlayer in runState.Players.Where((candidate) => candidate.NetId != removedRelic.Owner.NetId))
        {
            RelicModel? mirroredRelic = otherPlayer.GetRelicById(removedRelic.Id);
            if (mirroredRelic == null)
            {
                continue;
            }

            try
            {
                otherPlayer.RemoveRelicInternal(mirroredRelic);
                await mirroredRelic.AfterRemoved();
                ModLog.Info($"Local co-op shared relic sync removal: {removedRelic.Id.Entry}, owner={otherPlayer.NetId}");
            }
            catch (Exception exception)
            {
                ModLog.Warn($"Shared relic sync failed (remove): target={otherPlayer.NetId}, error={exception.Message}");
            }
        }
    }
}

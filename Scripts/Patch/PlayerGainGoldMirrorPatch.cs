using HarmonyLib;
using LocalMultiControl.Scripts.Rewards;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Patch;

internal static class GoldMirrorSuppressionContext
{
    private static readonly AsyncLocal<int> SuppressDepth = new();

    internal static bool ShouldSuppressGoldMirror => SuppressDepth.Value > 0;

    internal static void EnterSuppression()
    {
        SuppressDepth.Value++;
    }

    internal static void ExitSuppressionOnce()
    {
        if (SuppressDepth.Value > 0)
        {
            SuppressDepth.Value--;
        }
    }
}

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainGold))]
internal static class PlayerGainGoldMirrorPatch
{
    private static readonly AsyncLocal<bool> IsMirroring = new();

    [HarmonyPostfix]
    private static void Postfix(decimal amount, Player player, bool wasStolenBack, ref Task __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        if (amount <= 0m || IsMirroring.Value)
        {
            return;
        }

        if (GoldMirrorSuppressionContext.ShouldSuppressGoldMirror)
        {
            LocalMultiControlLogger.Info($"Skipping gold mirror for relic-flow gold: amount={amount}, owner={player.NetId}");
            return;
        }

        // In the combat-reward-merge flow, each player has already independently generated their gold reward, so no mirroring is needed
        if (CombatRewardMergeContext.IsActive)
        {
            return;
        }

        bool isCombatRewardContext = player.RunState.CurrentRoom is CombatRoom && !CombatManager.Instance.IsInProgress;
        bool isCrystalSphereContext = CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext(player);
        if (!isCombatRewardContext && !isCrystalSphereContext)
        {
            return;
        }

        __result = MirrorGoldToOtherPlayersAsync(amount, player, wasStolenBack, __result);
    }

    private static async Task MirrorGoldToOtherPlayersAsync(decimal amount, Player sourcePlayer, bool wasStolenBack, Task originalTask)
    {
        await originalTask;

        var otherPlayers = sourcePlayer.RunState.Players.Where((candidate) => candidate.NetId != sourcePlayer.NetId).ToList();
        if (otherPlayers.Count == 0)
        {
            return;
        }

        IsMirroring.Value = true;
        try
        {
            foreach (Player otherPlayer in otherPlayers)
            {
                await PlayerCmd.GainGold(amount, otherPlayer, wasStolenBack);
            }

            LocalMultiControlLogger.Info(
                $"Event/flow gold synced to the other players: amount={amount}, owner={sourcePlayer.NetId}, mirrored={string.Join(",", otherPlayers.Select((player) => player.NetId))}");
        }
        finally
        {
            IsMirroring.Value = false;
        }
    }
}

using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Rewards;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace LocalMultiControl.Scripts.Patch;

// NOTE: since the Silken Tress fix, the PRIMARY merge point for post-combat rewards is
// CombatRoomOfferRewardsPatch (CombatRoom.OfferRoomEndRewards), which generates each player's
// set exactly once. The merged branch below is a BACKSTOP for other callers of Offer; it still
// regenerates per player, so one-shot generation-time relics would misbehave if it ever ran for
// a combat room — TryMarkRoomMerged normally prevents that.
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
internal static class RewardsSetPatch
{
    [HarmonyPrefix]
    private static bool Prefix(RewardsSet __instance, ref Task __result)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        if (LocalSelfCoopContext.UseSingleAdventureMode
            && __instance.Room is CombatRoom combatRoom
            && __instance.Player.RunState.Players.Count > 1)
        {
            if (!CombatRewardMergeContext.TryMarkRoomMerged(combatRoom))
            {
                LocalMultiControlLogger.Info($"Detected a duplicate post-combat reward Offer call; ignored: player={__instance.Player.NetId}");
                __result = Task.CompletedTask;
                return false;
            }

            __result = OfferMergedCombatRewards(combatRoom);
            return false;
        }

        __result = OfferLocalSelfCoop(__instance);
        return false;
    }

    private static async Task OfferMergedCombatRewards(CombatRoom combatRoom)
    {
        List<Player> allPlayers = combatRoom.CombatState.RunState.Players.ToList();
        if (allPlayers.Count == 0)
        {
            return;
        }

        CombatRewardMergeContext.Enter();
        try
        {
            List<Reward> mergedRewards = new();
            bool shouldGiveRewards = combatRoom.Encounter == null || combatRoom.Encounter.ShouldGiveRewards;
            foreach (Player player in allPlayers)
            {
                if (player.Creature?.IsDead == true)
                {
                    continue;
                }

                RewardsSet perPlayerSet = shouldGiveRewards
                    ? new RewardsSet(player).WithRewardsFromRoom(combatRoom)
                    : new RewardsSet(player).EmptyForRoom(combatRoom);
                await perPlayerSet.GenerateWithoutOffering();

                foreach (Reward reward in perPlayerSet.Rewards)
                {
                    RewardPlayerLabelRegistry.Register(reward, player.NetId);
                }

                mergedRewards.AddRange(perPlayerSet.Rewards);
                LocalMultiControlLogger.Info($"Per-player independent reward generated (Offer): player={player.NetId}, rewardCount={perPlayerSet.Rewards.Count}");
            }

            Player displayPlayer = allPlayers.FirstOrDefault((p) => p.Creature?.IsDead != true) ?? allPlayers[0];
            LocalMultiControlRuntime.SwitchControlledPlayerTo(displayPlayer.NetId, "merged-rewards-offer-from-rewardsset");
            RewardsSet displaySet = new RewardsSet(displayPlayer).WithCustomRewards(mergedRewards);

            if (TestMode.IsOn)
            {
                foreach (Reward reward in mergedRewards)
                {
                    await reward.SelectUnsynchronized();
                }

                return;
            }

            NRewardsScreen rewardScreen = NRewardsScreen.ShowScreen(displaySet, isTerminal: true, displayPlayer.RunState);
            await CombatRewardMergeContext.WaitForRewardsScreenDoneAsync(rewardScreen);
        }
        finally
        {
            CombatRewardMergeContext.Exit();
        }
    }

    private static async Task OfferLocalSelfCoop(RewardsSet rewardsSet)
    {
        if (rewardsSet.Player.Creature.IsDead)
        {
            return;
        }

        await rewardsSet.GenerateWithoutOffering();
        bool isTerminal = rewardsSet.Room is CombatRoom;
        bool allowEmptyRewards = (bool)(AccessTools.Field(typeof(RewardsSet), "_allowEmptyRewards")?.GetValue(rewardsSet) ?? false);
        if (rewardsSet.Rewards.Count <= 0 && !isTerminal && !allowEmptyRewards)
        {
            return;
        }

        if (!rewardsSet.Rewards.All((reward) => reward.IsPopulated) && rewardsSet.Rewards.Any((reward) => reward.IsPopulated))
        {
            Log.Warn("Some rewards are populated and others are not when calling RewardsCmd.Offer! This might lead to hooks getting called twice");
        }

        // Couch simultaneous mode: rewards for the teammate (from events, relics...) go to their own panel instead of
        // switching the whole screen to them. The panel claims through the synchronizer, which completes this set.
        if (CouchTeammateRewards.ShouldTakeRewards(rewardsSet.Player))
        {
            Node? host = (Node?)NRun.Instance?.GlobalUi ?? NGame.Instance;
            if (host != null)
            {
                Task teammateSetTask = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewardsSet);
                CouchTeammateRewards.Open(host, rewardsSet, synchronized: true);
                await teammateSetTask;
                return;
            }

            LocalMultiControlLogger.Warn("No UI host for the teammate rewards panel; falling back to the main screen.");
        }

        LocalMultiControlRuntime.SwitchControlledPlayerTo(rewardsSet.Player.NetId, "rewards-offer");
        LocalMultiControlLogger.Info($"Opening reward screen: player={rewardsSet.Player.NetId}, count={rewardsSet.Rewards.Count}");
        Task rewardsSetTask = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewardsSet);

        if (TestMode.IsOn)
        {
            foreach (Reward reward in rewardsSet.Rewards)
            {
                await RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward);
            }

            await rewardsSetTask;
            return;
        }

        NRewardsScreen.ShowScreen(rewardsSet, isTerminal, rewardsSet.Player.RunState);
        await rewardsSetTask;
    }
}

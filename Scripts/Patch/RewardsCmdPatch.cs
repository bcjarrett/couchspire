using HarmonyLib;
using CouchSpire.Scripts.Rewards;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Post-combat reward rework: independently generates rewards for each player (using the base game's RewardsSet logic),
/// then merges all players' rewards into a single list for display, with each reward tagged to its owning player.
/// This lets effects like relic doubling, the Nice Stuff multiplier, and Ranger hunting apply correctly on a per-player basis.
/// </summary>
// NOTE: since the Silken Tress fix, the PRIMARY merge point for post-combat rewards is
// CombatRoomOfferRewardsPatch (CombatRoom.OfferRoomEndRewards). This patch remains as a
// backstop for direct OfferForRoomEnd callers; TryMarkRoomMerged dedupes between the two.
[HarmonyPatch(typeof(RewardsCmd), nameof(RewardsCmd.OfferForRoomEnd))]
internal static class RewardsCmdPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player player, AbstractRoom room, ref Task __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || room is not CombatRoom combatRoom || player.RunState.Players.Count <= 1)
        {
            return true;
        }

        if (!CombatRewardMergeContext.TryMarkRoomMerged(room))
        {
            ModLog.Info($"Detected a duplicate post-combat reward call; ignored: player={player.NetId}, room={room.RoomType}");
            __result = Task.CompletedTask;
            return false;
        }

        __result = OfferMergedRewardsForAllPlayers(combatRoom);
        return false;
    }

    private static async Task OfferMergedRewardsForAllPlayers(CombatRoom combatRoom)
    {
        IRunState runState = combatRoom.CombatState.RunState;
        List<Player> allPlayers = runState.Players.ToList();
        if (allPlayers.Count == 0)
        {
            return;
        }

        // Mark entry into the combat-reward-merge flow to suppress mirror copying of relics/potions/gold
        CombatRewardMergeContext.Enter();
        try
        {
            await OfferMergedRewardsCore(combatRoom, allPlayers);
        }
        finally
        {
            CombatRewardMergeContext.Exit();
        }
    }

    private static async Task OfferMergedRewardsCore(CombatRoom combatRoom, List<Player> allPlayers)
    {
        // Independently generate rewards for each player (without displaying them), collected into a merged list
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

            // Call GenerateWithoutOffering to trigger Populate + Hook.ModifyRewards
            await perPlayerSet.GenerateWithoutOffering();

            // Register a player label for each reward
            foreach (Reward reward in perPlayerSet.Rewards)
            {
                RewardPlayerLabelRegistry.Register(reward, player.NetId);
            }

            mergedRewards.AddRange(perPlayerSet.Rewards);
            ModLog.Info(
                $"Per-player independent reward generated: player={player.NetId}, rewardCount={perPlayerSet.Rewards.Count}");
        }

        // Switch to the first surviving player's control context to display the reward screen
        Player? displayPlayer = allPlayers.FirstOrDefault((p) => p.Creature?.IsDead != true) ?? allPlayers[0];
        LocalControlRuntime.SwitchControlledPlayerTo(displayPlayer.NetId, "merged-rewards-offer");
        RewardsSet displaySet = new RewardsSet(displayPlayer).WithCustomRewards(mergedRewards);

        if (TestMode.IsOn)
        {
            foreach (Reward reward in mergedRewards)
            {
                await reward.SelectUnsynchronized();
            }

            return;
        }

        bool isTerminal = true; // CombatRoom's reward screen is always terminal
        NRewardsScreen rewardScreen = NRewardsScreen.ShowScreen(displaySet, isTerminal, displayPlayer.RunState);
        await CombatRewardMergeContext.WaitForRewardsScreenDoneAsync(rewardScreen);
    }
}

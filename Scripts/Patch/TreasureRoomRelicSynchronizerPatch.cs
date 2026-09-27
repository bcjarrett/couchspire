using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.OnPicked))]
internal static class TreasureRoomRelicSynchronizerPatch
{
    internal sealed class OverflowCopyPlan
    {
        public Player PrimaryPlayer { get; init; } = null!;

        public List<Player> Followers { get; } = new();
    }

    private static readonly Dictionary<TreasureRoomRelicSynchronizer, OverflowCopyPlan> OverflowPlans = new();
    private static readonly HashSet<TreasureRoomRelicSynchronizer> SkipAutoSwitchOnce = new();

    [HarmonyPostfix]
    private static void Postfix(TreasureRoomRelicSynchronizer __instance, Player player)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        if (SkipAutoSwitchOnce.Remove(__instance))
        {
            return;
        }

        TryAutoSwitchToNextUnpickedPlayer(__instance, player.NetId, "treasure-picked");
    }

    [HarmonyPrefix]
    private static bool Prefix(TreasureRoomRelicSynchronizer __instance, Player player, int? index)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return true;
        }

        if (!TryGetOverflowPlan(__instance, out OverflowCopyPlan? plan) || plan == null)
        {
            return true;
        }

        if (!index.HasValue)
        {
            return true;
        }

        try
        {
            List<TreasureRoomRelicSynchronizer.PlayerVote>? votes =
                AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes")?.GetValue(__instance)
                    as List<TreasureRoomRelicSynchronizer.PlayerVote>;
            IPlayerCollection? players = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_playerCollection")?.GetValue(__instance) as IPlayerCollection;
            IReadOnlyList<RelicModel>? currentRelics = __instance.CurrentRelics;
            if (votes == null || players == null || currentRelics == null || currentRelics.Count == 0)
            {
                return false;
            }

            int selectedIndex = index.Value;
            if (selectedIndex < 0 || selectedIndex >= currentRelics.Count)
            {
                LocalMultiControlLogger.Warn($"Treasure room vote index out of range; ignored: index={selectedIndex}, relicCount={currentRelics.Count}");
                return false;
            }

            int sharedCount = Math.Min(votes.Count, players.Players.Count);
            for (int i = 0; i < sharedCount; i++)
            {
                votes[i].index = selectedIndex;
                votes[i].voteReceived = true;
            }

            InvokeVotesChanged(__instance);

            RelicModel selectedRelic = currentRelics[selectedIndex];
            List<RelicPickingResult> results = BuildOverflowResults(plan, player, selectedRelic);

            InvokeRelicsAwarded(__instance, results);
            AccessTools.Method(typeof(TreasureRoomRelicSynchronizer), "EndRelicVoting")?.Invoke(__instance, null);
            SkipAutoSwitchOnce.Add(__instance);
            RemoveOverflowPlan(__instance);
            LocalMultiControlLogger.Info(
                $"Treasure room fast resolution for 5+ players: player={player.NetId}, relic={selectedRelic.Id.Entry}, awarded per the resolution result and ended the room.");
            return false;
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Treasure room overflow vote takeover failed; falling back to the original flow: {exception.Message}");
            RemoveOverflowPlan(__instance);
            return true;
        }
    }

    private static List<RelicPickingResult> BuildOverflowResults(OverflowCopyPlan plan, Player sourcePlayer, RelicModel selectedRelic)
    {
        List<Player> orderedPlayers = new();
        HashSet<ulong> seenPlayerIds = new();

        void AddPlayer(Player candidate)
        {
            if (seenPlayerIds.Add(candidate.NetId))
            {
                orderedPlayers.Add(candidate);
            }
        }

        AddPlayer(sourcePlayer);
        AddPlayer(plan.PrimaryPlayer);
        foreach (Player follower in plan.Followers)
        {
            AddPlayer(follower);
        }

        List<RelicPickingResult> results = new(orderedPlayers.Count);
        foreach (Player participant in orderedPlayers)
        {
            results.Add(new RelicPickingResult
            {
                type = RelicPickingResultType.OnlyOnePlayerVoted,
                relic = selectedRelic,
                player = participant
            });
        }

        return results;
    }

    private static void InvokeVotesChanged(TreasureRoomRelicSynchronizer synchronizer)
    {
        Action? callback = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "VotesChanged")?.GetValue(synchronizer) as Action;
        callback?.Invoke();
    }

    private static void InvokeRelicsAwarded(TreasureRoomRelicSynchronizer synchronizer, List<RelicPickingResult> results)
    {
        Action<List<RelicPickingResult>>? callback =
            AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "RelicsAwarded")?.GetValue(synchronizer) as Action<List<RelicPickingResult>>;
        callback?.Invoke(results);
    }

    internal static void SetOverflowPlan(TreasureRoomRelicSynchronizer synchronizer, OverflowCopyPlan plan)
    {
        OverflowPlans[synchronizer] = plan;
    }

    private static bool TryGetOverflowPlan(TreasureRoomRelicSynchronizer synchronizer, out OverflowCopyPlan? plan)
    {
        return OverflowPlans.TryGetValue(synchronizer, out plan);
    }

    internal static void RemoveOverflowPlan(TreasureRoomRelicSynchronizer synchronizer)
    {
        OverflowPlans.Remove(synchronizer);
    }

    internal static bool TryAutoSwitchToNextUnpickedPlayer(TreasureRoomRelicSynchronizer synchronizer, ulong currentPlayerId, string source)
    {
        // Couch simultaneous mode: the teammate picks in their own treasure panel.
        if (CouchConfig.SimultaneousEnabled && CouchTeammate.FindTeammate() != null)
        {
            return false;
        }

        try
        {
            List<TreasureRoomRelicSynchronizer.PlayerVote>? votes =
                AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes")?.GetValue(synchronizer)
                    as List<TreasureRoomRelicSynchronizer.PlayerVote>;
            IPlayerCollection? players = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_playerCollection")?.GetValue(synchronizer) as IPlayerCollection;
            if (votes == null || players == null)
            {
                return false;
            }

            int sharedCount = Math.Min(votes.Count, players.Players.Count);
            if (sharedCount < 2)
            {
                return false;
            }

            int currentSlot = players.Players.Take(sharedCount).ToList().FindIndex((candidate) => candidate.NetId == currentPlayerId);
            if (currentSlot < 0)
            {
                return false;
            }

            if (!votes[currentSlot].voteReceived)
            {
                return false;
            }

            int nextSlot = -1;
            for (int step = 1; step < sharedCount; step++)
            {
                int slot = (currentSlot + step) % sharedCount;
                if (!votes[slot].voteReceived)
                {
                    nextSlot = slot;
                    break;
                }
            }

            if (nextSlot < 0)
            {
                return false;
            }

            ulong nextPlayerId = players.Players[nextSlot].NetId;
            Callable.From(delegate
            {
                LocalMultiControlRuntime.SwitchControlledPlayerTo(nextPlayerId, source);
            }).CallDeferred();

            LocalMultiControlLogger.Info($"Treasure room auto-switched to the next unselected player after selection completed: {currentPlayerId} -> {nextPlayerId}");
            return true;
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Treasure room auto-switch to an unselected player failed: {exception.Message}");
            return false;
        }
    }
}

[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
internal static class TreasureRoomRelicSynchronizerBeginPatch
{
    private const int MAX_MANUAL_RELIC_OPTIONS = 4;

    [HarmonyPostfix]
    private static void Postfix(TreasureRoomRelicSynchronizer __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        try
        {
            List<TreasureRoomRelicSynchronizer.PlayerVote>? votes =
                AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes")?.GetValue(__instance)
                    as List<TreasureRoomRelicSynchronizer.PlayerVote>;
            IReadOnlyList<Player>? players = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_playerCollection")?.GetValue(__instance) is IPlayerCollection playerCollection
                ? playerCollection.Players
                : null;
            IReadOnlyList<RelicModel>? currentRelics = __instance.CurrentRelics;
            if (votes == null || players == null || votes.Count <= 1 || currentRelics == null || currentRelics.Count <= 1)
            {
                return;
            }

            int sharedCount = Math.Min(votes.Count, players.Count);
            if (sharedCount <= MAX_MANUAL_RELIC_OPTIONS)
            {
                for (int i = 0; i < sharedCount; i++)
                {
                    votes[i].index = null;
                    votes[i].voteReceived = false;
                }

                TreasureRoomRelicSynchronizerPatch.RemoveOverflowPlan(__instance);
                LocalMultiControlLogger.Info("Treasure room disabled auto-vote-on-behalf; switched to per-player manual selection.");
                return;
            }

            Player primaryPlayer = players[0];
            TreasureRoomRelicSynchronizerPatch.OverflowCopyPlan plan = new()
            {
                PrimaryPlayer = primaryPlayer
            };

            for (int i = 0; i < sharedCount; i++)
            {
                votes[i].index = null;
                votes[i].voteReceived = false;
                if (i > 0)
                {
                    plan.Followers.Add(players[i]);
                }
            }

            TreasureRoomRelicSynchronizerPatch.SetOverflowPlan(__instance, plan);
            LocalMultiControlLogger.Info($"Treasure room special-case for 5+ players enabled: only slot 1 participates in the event, the other {plan.Followers.Count} players will directly copy slot 1's relic.");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Failed to disable treasure room auto-vote-on-behalf: {exception.Message}");
        }
    }
}

using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(SpoilsMap), nameof(SpoilsMap.OnQuestComplete))]
internal static class SpoilsMapPatch
{
    private static readonly HashSet<SpoilsMap> SuppressedCards = new(ReferenceEqualityComparer.Instance);

    [HarmonyPostfix]
    private static void Postfix(SpoilsMap __instance, ref Task<int> __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        if (SuppressedCards.Contains(__instance))
        {
            return;
        }

        __result = CompleteForOtherLocalPlayersAsync(__instance, __result);
    }

    private static async Task<int> CompleteForOtherLocalPlayersAsync(SpoilsMap sourceMap, Task<int> originalTask)
    {
        int sourceGold = await originalTask;

        Player? sourceOwner = sourceMap.Owner;
        IRunState? runState = sourceOwner?.RunState;
        if (runState == null || runState.Players.Count <= 1)
        {
            return sourceGold;
        }

        if (!SuppressedCards.Add(sourceMap))
        {
            return sourceGold;
        }

        try
        {
            foreach (Player otherPlayer in runState.Players.Where((candidate) => sourceOwner != null && candidate.NetId != sourceOwner.NetId))
            {
                CardPile deckPile = PileType.Deck.GetPile(otherPlayer);
                List<SpoilsMap> pendingMaps = deckPile.Cards
                    .OfType<SpoilsMap>()
                    .Where((candidate) => candidate.SpoilsActIndex == runState.CurrentActIndex)
                    .ToList();
                if (pendingMaps.Count == 0)
                {
                    continue;
                }

                foreach (SpoilsMap pendingMap in pendingMaps)
                {
                    if (!SuppressedCards.Add(pendingMap))
                    {
                        continue;
                    }

                    try
                    {
                        await PlayerCmd.GainGold(pendingMap.DynamicVars.Gold.BaseValue, otherPlayer);
                        PlayerCmd.CompleteQuest(pendingMap);
                        await CardPileCmd.RemoveFromDeck(pendingMap);
                        ModLog.Info(
                            $"Spoils map resolution synced to the local player: owner={sourceOwner?.NetId ?? 0}, target={otherPlayer.NetId}, gold={pendingMap.DynamicVars.Gold.IntValue}");
                    }
                    finally
                    {
                        SuppressedCards.Remove(pendingMap);
                    }
                }
            }
        }
        finally
        {
            SuppressedCards.Remove(sourceMap);
        }

        return sourceGold;
    }
}

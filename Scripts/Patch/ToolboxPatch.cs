using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Relics;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(Toolbox), nameof(Toolbox.BeforeHandDraw))]
internal static class ToolboxPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        Toolbox __instance,
        Player player,
        PlayerChoiceContext choiceContext,
        ICombatState combatState,
        ref Task __result)
    {
        if (!ShouldAutoPickFirstCard(__instance, player, out string reason))
        {
            return true;
        }

        LocalMultiControlLogger.Info($"Toolbox auto-takeover triggered: player={player.NetId}, reason={reason}");
        __result = AutoPickFirstCardAsync(__instance, player);
        return false;
    }

    private static bool ShouldAutoPickFirstCard(Toolbox relic, Player player, out string reason)
    {
        reason = string.Empty;
        if (!LocalSelfCoopContext.IsEnabled || player != relic.Owner)
        {
            return false;
        }

        ICombatState? combatState = player.Creature.CombatState;
        if (combatState == null || combatState.RoundNumber != 1)
        {
            return false;
        }

        if (LocalContext.IsMe(player))
        {
            return false;
        }

        reason = "background-player";
        return true;
    }

    private static async Task AutoPickFirstCardAsync(
        Toolbox relic,
        Player player)
    {
        if (player != relic.Owner || player.Creature.CombatState == null || player.Creature.CombatState.RoundNumber != 1)
        {
            return;
        }

        relic.Flash();
        List<CardModel> cards = CardFactory.GetDistinctForCombat(
                relic.Owner,
                ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint),
                relic.DynamicVars.Cards.IntValue,
                relic.Owner.RunState.Rng.CombatCardGeneration)
            .ToList();

        CardModel? pickedCard = cards.FirstOrDefault();
        if (pickedCard != null)
        {
            await CardPileCmd.AddGeneratedCardToCombat(pickedCard, PileType.Hand, relic.Owner);
            LocalMultiControlLogger.Info($"Toolbox auto-picked the first card: player={player.NetId}, card={pickedCard.Id.Entry}");
        }
    }
}

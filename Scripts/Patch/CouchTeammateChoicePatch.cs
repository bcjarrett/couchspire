using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace LocalMultiControl.Scripts.Patch;

/// <summary>
/// Couch co-op: record what a teammate is asked to choose during simultaneous combat, so the answer can be sent back
/// as that teammate. Each prefix mirrors how the selection method builds its option list and decodes the answer.
/// </summary>
[HarmonyPatch]
internal static class CouchTeammateChoicePatch
{
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
    [HarmonyPrefix]
    private static void PrefixFromHand(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        if (!CouchTeammate.IsTeammate(player))
        {
            return;
        }

        IEnumerable<CardModel> options = PileType.Hand.GetPile(player).Cards.Where(filter ?? ((CardModel _) => true));
        CouchTeammateChoices.Request(player, CouchChoiceAnswerKind.CombatCards, options, prefs.MinSelect, prefs.MaxSelect, prefs.Prompt.GetFormattedText(), "hand");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade))]
    [HarmonyPrefix]
    private static void PrefixFromHandForUpgrade(Player player)
    {
        if (!CouchTeammate.IsTeammate(player))
        {
            return;
        }

        IEnumerable<CardModel> options = PileType.Hand.GetPile(player).Cards.Where((CardModel c) => c.IsUpgradable);
        CouchTeammateChoices.Request(player, CouchChoiceAnswerKind.CombatCards, options, 1, 1, CardSelectorPrefs.UpgradeSelectionPrompt.GetFormattedText(), "hand upgrade");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile), new[] { typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>) })]
    [HarmonyPrefix]
    private static void PrefixFromCombatPile(CardPile pile, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        if (!CouchTeammate.IsTeammate(player))
        {
            return;
        }

        IEnumerable<CardModel> options = filter == null ? pile.Cards : pile.Cards.Where(filter);
        CouchTeammateChoices.Request(player, CouchChoiceAnswerKind.CombatCards, options, prefs.MinSelect, prefs.MaxSelect, prefs.Prompt.GetFormattedText(), $"{pile.Type} pile");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    [HarmonyPrefix]
    private static void PrefixFromChooseACardScreen(IReadOnlyList<CardModel> cards, Player player, bool canSkip)
    {
        if (CouchTeammate.IsTeammate(player))
        {
            CouchTeammateChoices.Request(player, CouchChoiceAnswerKind.Index, cards, canSkip ? 0 : 1, 1, "Choose a card", "choose-a-card screen");
            return;
        }

        // Outside combat (e.g. Neow's Massive Scroll): the teammate's card picker.
        RequestOutOfCombat(player, CouchChoiceAnswerKind.Index, cards, canSkip ? 0 : 1, 1, canSkip, "Choose a card", "choose-a-card screen");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
    [HarmonyPrefix]
    private static void PrefixFromSimpleGrid(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs)
    {
        if (CouchTeammate.IsTeammate(player))
        {
            CouchTeammateChoices.Request(player, CouchChoiceAnswerKind.Indexes, cardsIn, prefs.MinSelect, prefs.MaxSelect, prefs.Prompt.GetFormattedText(), "grid");
            return;
        }

        RequestOutOfCombat(player, CouchChoiceAnswerKind.Indexes, cardsIn, prefs.MinSelect, prefs.MaxSelect, prefs.Cancelable, prefs.Prompt.GetFormattedText(), "grid");
    }

    /// <summary>
    /// A teammate's pick outside combat goes to their card picker. Single picks that may be skipped get a Skip row.
    /// </summary>
    private static void RequestOutOfCombat(Player player, CouchChoiceAnswerKind kind, IReadOnlyList<CardModel> cards, int minSelect, int maxSelect, bool canClose, string prompt, string source)
    {
        if (!CouchTeammate.IsSimultaneousTeammate(player) || CombatManager.Instance.IsInProgress)
        {
            return;
        }

        bool skipRow = maxSelect == 1 && (minSelect == 0 || canClose);
        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = player,
            Kind = kind,
            Options = cards.ToList(),
            MinSelect = minSelect,
            MaxSelect = maxSelect,
            CanClose = canClose,
            IsCombatChoice = false,
            ExtraOptions = skipRow ? new[] { "Skip" } : Array.Empty<string>(),
            ExtraOptionsSkip = skipRow,
            Prompt = CouchText.Plain(prompt),
            Source = source
        });
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade))]
    [HarmonyPrefix]
    private static void PrefixFromDeckForUpgrade(Player player, CardSelectorPrefs prefs)
    {
        RequestDeckChoice(player, PileType.Deck.GetPile(player).Cards.Where((CardModel c) => c.IsUpgradable), prefs, "deck upgrade");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation))]
    [HarmonyPrefix]
    private static void PrefixFromDeckForTransformation(Player player, CardSelectorPrefs prefs)
    {
        RequestDeckChoice(player, PileType.Deck.GetPile(player).Cards.Where((CardModel c) => c.Type != CardType.Quest && c.IsTransformable), prefs, "deck transform");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForEnchantment), new[] { typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) })]
    [HarmonyPrefix]
    private static void PrefixFromDeckForEnchantment(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs)
    {
        if (cards.Count == 0)
        {
            return;
        }

        Player owner = cards[0].Owner;
        List<CardModel> deck = PileType.Deck.GetPile(owner).Cards.ToList();
        RequestDeckChoice(owner, cards.OrderBy((CardModel c) => deck.IndexOf(c)), prefs, "deck enchant");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckGeneric))]
    [HarmonyPrefix]
    private static void PrefixFromDeckGeneric(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, Func<CardModel, int>? sortingOrder)
    {
        IEnumerable<CardModel> options = PileType.Deck.GetPile(player).Cards.Where(filter ?? ((CardModel _) => true));
        if (sortingOrder != null)
        {
            options = options.OrderBy(sortingOrder);
        }

        RequestDeckChoice(player, options, prefs, "deck");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards))]
    [HarmonyPrefix]
    private static void PrefixFromSimpleGridForRewards(List<CardCreationResult> cards, Player player, CardSelectorPrefs prefs)
    {
        if (!CouchTeammate.IsSimultaneousTeammate(player))
        {
            return;
        }

        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = player,
            Kind = CouchChoiceAnswerKind.Indexes,
            Options = cards.Select((CardCreationResult c) => c.Card).ToList(),
            MinSelect = prefs.MinSelect,
            MaxSelect = prefs.MaxSelect,
            CanClose = prefs.Cancelable,
            IsCombatChoice = CombatManager.Instance.IsInProgress,
            Prompt = CouchText.Plain(prefs.Prompt.GetFormattedText()),
            Source = "reward grid"
        });
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseABundleScreen))]
    [HarmonyPrefix]
    private static void PrefixFromChooseABundleScreen(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        if (!CouchTeammate.IsSimultaneousTeammate(player) || bundles.Count == 0)
        {
            return;
        }

        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = player,
            Kind = CouchChoiceAnswerKind.Index,
            Options = new List<CardModel>(),
            ExtraOptions = bundles.Select((IReadOnlyList<CardModel> b, int i) => $"Pack {i + 1}: {string.Join(", ", b.Select((CardModel c) => c.Title))}").ToList(),
            MinSelect = 1,
            MaxSelect = 1,
            IsCombatChoice = CombatManager.Instance.IsInProgress,
            Prompt = "Choose a pack",
            Source = "bundle"
        });
    }

    [HarmonyPatch(typeof(RelicSelectCmd), nameof(RelicSelectCmd.FromChooseARelicScreen))]
    [HarmonyPrefix]
    private static void PrefixFromChooseARelicScreen(Player player, IReadOnlyList<RelicModel> relics)
    {
        if (!CouchTeammate.IsSimultaneousTeammate(player) || relics.Count == 0)
        {
            return;
        }

        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = player,
            Kind = CouchChoiceAnswerKind.Index,
            Options = new List<CardModel>(),
            ExtraOptions = relics.Select((RelicModel r) =>
                $"{CouchText.Plain(r.Title.GetFormattedText())}: {CouchText.Plain(r.DynamicDescription.GetFormattedText())}").ToList(),
            MinSelect = 1,
            MaxSelect = 1,
            CanClose = true,
            IsCombatChoice = CombatManager.Instance.IsInProgress,
            Prompt = "Choose a relic",
            Source = "relic"
        });
    }

    /// <summary>Mend (rest site): the teammate picks who to heal; the game waits for their answer as a player id.</summary>
    [HarmonyPatch(typeof(MendRestSiteOption), nameof(MendRestSiteOption.OnSelect))]
    [HarmonyPrefix]
    private static void PrefixMendOnSelect(MendRestSiteOption __instance)
    {
        Player? owner = AccessTools.Property(typeof(RestSiteOption), "Owner")?.GetValue(__instance) as Player;
        if (owner == null || !CouchTeammate.IsSimultaneousTeammate(owner))
        {
            return;
        }

        List<Player> targets = owner.RunState.Players.Where((Player p) => p != owner && p.Creature.IsAlive).ToList();
        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = owner,
            Kind = CouchChoiceAnswerKind.PlayerId,
            Options = new List<CardModel>(),
            ExtraOptions = targets.Select((Player p) =>
                $"{CouchSeats.FindByPlayer(p.NetId)?.Label ?? "?"} · {p.Character.Title.GetFormattedText()}    HP {p.Creature.CurrentHp}/{p.Creature.MaxHp}").ToList(),
            OptionPlayerIds = targets.Select((Player p) => p.NetId).ToList(),
            MinSelect = 1,
            MaxSelect = 1,
            CanClose = true,
            IsCombatChoice = false,
            Prompt = "Mend: choose who to heal",
            Source = "mend"
        });
    }

    /// <summary>A selection from the teammate's deck, answered with the chosen deck cards.</summary>
    private static void RequestDeckChoice(Player player, IEnumerable<CardModel> options, CardSelectorPrefs prefs, string source)
    {
        if (!CouchTeammate.IsSimultaneousTeammate(player))
        {
            return;
        }

        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = player,
            Kind = CouchChoiceAnswerKind.DeckCards,
            Options = options.ToList(),
            MinSelect = prefs.MinSelect,
            MaxSelect = prefs.MaxSelect,
            CanClose = prefs.Cancelable,
            IsCombatChoice = CombatManager.Instance.IsInProgress,
            Prompt = CouchText.Plain(prefs.Prompt.GetFormattedText()),
            Source = source
        });
    }

    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))]
    [HarmonyPrefix]
    private static void PrefixWaitForRemoteChoice(Player player, uint choiceId)
    {
        CouchTeammateRewards.PrepareChoiceRequest(player);
        CouchTeammateChoices.OnWaitingForRemoteChoice(player, choiceId);
    }
}

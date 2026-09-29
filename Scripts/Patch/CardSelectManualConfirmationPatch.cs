using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

internal static class CardSelectManualConfirmationPatch
{
    private static void ForceManualIfNeeded(ref CardSelectorPrefs prefs, string source)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        if (RunManager.Instance.NetService is not LocalLoopbackHostGameService)
        {
            return;
        }

        if (prefs.RequireManualConfirmation)
        {
            return;
        }

        CardSelectorPrefs patchedPrefs = new CardSelectorPrefs(prefs.Prompt, prefs.MinSelect, prefs.MaxSelect)
        {
            RequireManualConfirmation = true,
            Cancelable = prefs.Cancelable,
            UnpoweredPreviews = prefs.UnpoweredPreviews,
            PretendCardsCanBePlayed = prefs.PretendCardsCanBePlayed
        };
        patchedPrefs.ShouldGlowGold = prefs.ShouldGlowGold;
        prefs = patchedPrefs;

        ModLog.Info(
            $"Local co-op forces deck card selection to pop up the inventory: source={source}, min={prefs.MinSelect}, max={prefs.MaxSelect}");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade), new[] { typeof(Player), typeof(CardSelectorPrefs) })]
    [HarmonyPrefix]
    private static void FromDeckForUpgradePrefix(ref CardSelectorPrefs prefs)
    {
        ForceManualIfNeeded(ref prefs, "FromDeckForUpgrade");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation), new[] { typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, CardTransformation>) })]
    [HarmonyPrefix]
    private static void FromDeckForTransformationPrefix(ref CardSelectorPrefs prefs)
    {
        ForceManualIfNeeded(ref prefs, "FromDeckForTransformation");
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckGeneric), new[] { typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(Func<CardModel, int>) })]
    [HarmonyPrefix]
    private static void FromDeckGenericPrefix(ref CardSelectorPrefs prefs)
    {
        ForceManualIfNeeded(ref prefs, "FromDeckGeneric");
    }
}

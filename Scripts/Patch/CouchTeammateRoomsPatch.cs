using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op, rooms where the teammate has their own panel: keep the driver in the rest site or shop until the
/// teammate is done, and give the teammate their own treasure chest gold.
/// </summary>
[HarmonyPatch]
internal static class CouchTeammateRoomsPatch
{
    [HarmonyPatch(typeof(NRestSiteRoom), "OnProceedButtonReleased")]
    [HarmonyPrefix]
    private static bool PrefixRestSiteProceed()
    {
        if (!CouchTeammateRestSite.BlocksProceed)
        {
            return true;
        }

        CouchTeammateRestSite.NotifyProceedBlocked();
        return false;
    }

    [HarmonyPatch(typeof(NMerchantRoom), "HideScreen")]
    [HarmonyPrefix]
    private static bool PrefixMerchantLeave()
    {
        if (!CouchTeammateShop.BlocksProceed)
        {
            return true;
        }

        CouchTeammateShop.NotifyProceedBlocked();
        return false;
    }

    /// <summary>
    /// Opening the chest gives each player their own gold; the game only runs it for the player who opened the chest
    /// and waits for the others' "chest opened" messages. Send the teammate's, as their client would.
    /// </summary>
    [HarmonyPatch(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalTreasureRoomRewards))]
    [HarmonyPostfix]
    private static void PostfixTreasureOpened()
    {
        Player? teammate = CouchTeammate.FindTeammate();
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (teammate == null || runState == null)
        {
            return;
        }

        // The base mod already settles Spoils Map quests for every player here; the chest message would settle it twice.
        if (teammate.Deck.Cards.OfType<SpoilsMap>().Any((SpoilsMap map) => map.SpoilsActIndex == runState.CurrentActIndex))
        {
            CouchLog.Info($"Teammate {teammate.NetId} has a Spoils Map for this act; skipping their separate chest gold.");
            return;
        }

        CouchLog.Info($"Teammate {teammate.NetId} opens the chest too (their own gold).");
        CouchRemotePlay.DispatchAs(teammate, new TreasureChestOpenedMessage { Location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation });
    }
}

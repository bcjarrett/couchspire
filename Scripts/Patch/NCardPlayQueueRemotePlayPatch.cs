using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: a teammate's card play (sent as a remote request) would be shown waiting in the play queue by
/// flying it out of the teammate's intent UI. The base mod disables that UI in local mode
/// (<see cref="NMultiplayerPlayerIntentHandlerPatch"/>), so the game's code would dereference null. Skip the queue
/// preview instead; the card still flies out of the teammate's character when it resolves
/// (<c>CardPileCmd</c> creates the node for non-local owners), and cancellation tolerates the missing queue entry.
/// </summary>
[HarmonyPatch(typeof(NCardPlayQueue), "OnActionEnqueued")]
internal static class NCardPlayQueueRemotePlayPatch
{
    [HarmonyPrefix]
    private static bool PrefixOnActionEnqueued(GameAction action)
    {
        if (!LocalSelfCoopContext.IsEnabled || action is not PlayCardAction playCardAction || LocalContext.IsMe(playCardAction.Player))
        {
            return true;
        }

        NCreature? creatureNode = NCombatRoom.Instance?.GetCreatureNode(playCardAction.Player.Creature);
        return creatureNode?.PlayerIntentHandler != null;
    }
}

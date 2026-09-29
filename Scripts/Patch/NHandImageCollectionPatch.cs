using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(NHandImageCollection), "UpdateHandVisibility")]
internal static class NHandImageCollectionUpdateVisibilityPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NHandImageCollection __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return true;
        }

        PeerInputSynchronizer? synchronizer =
            AccessTools.Field(typeof(NHandImageCollection), "_synchronizer")?.GetValue(__instance) as PeerInputSynchronizer;
        List<NHandImage>? hands =
            AccessTools.Field(typeof(NHandImageCollection), "_hands")?.GetValue(__instance) as List<NHandImage>;
        if (synchronizer == null || hands == null)
        {
            return false;
        }

        bool hasLocalScreen = TryGetScreenType(synchronizer, LocalContext.NetId ?? 0UL, out NetScreenType localScreenType);
        foreach (NHandImage hand in hands)
        {
            NetScreenType handScreenType;
            bool hasHandScreen;
            if (hand.Player.NetId == LocalContext.NetId)
            {
                handScreenType = localScreenType;
                hasHandScreen = hasLocalScreen;
            }
            else
            {
                // Every hand here belongs to one of our own local players (couch co-op is always fake multiplayer:
                // see LocalSelfCoopContext.LocalPlayerIds), matching vanilla's now-removed IsSinglePlayerOrFakeMultiplayer
                // branch, which always showed teammates' hands instead of asking the synchronizer. Querying it here for a
                // player who hasn't sent any real input yet (GetScreenType lazily creates their PeerInputState) fires
                // StateAdded synchronously during Initialize(), after AddHand already ran for every player - "twice".
                handScreenType = NetScreenType.SharedRelicPicking;
                hasHandScreen = true;
            }

            if (!hasHandScreen)
            {
                if (!hasLocalScreen)
                {
                    hand.Visible = false;
                    continue;
                }

                handScreenType = localScreenType;
            }

            bool shouldShow = hasLocalScreen &&
                handScreenType == NetScreenType.SharedRelicPicking &&
                localScreenType == NetScreenType.SharedRelicPicking;
            if (!hand.Visible && shouldShow)
            {
                hand.AnimateIn();
            }

            hand.Visible = shouldShow;
        }

        bool cursorShown = !hasLocalScreen || localScreenType != NetScreenType.SharedRelicPicking;
        NGame.Instance?.CursorManager.SetCursorShown(cursorShown);
        return false;
    }

    private static bool TryGetScreenType(PeerInputSynchronizer synchronizer, ulong playerId, out NetScreenType screenType)
    {
        try
        {
            screenType = synchronizer.GetScreenType(playerId);
            return true;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("PeerInputState for non-existent player"))
        {
            ModLog.Warn($"Treasure room gesture layer skipping player with missing input state: player={playerId}");
            screenType = default;
            return false;
        }
    }
}

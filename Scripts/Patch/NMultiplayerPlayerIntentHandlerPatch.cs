using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NMultiplayerPlayerIntentHandler), nameof(NMultiplayerPlayerIntentHandler.Create))]
internal static class NMultiplayerPlayerIntentHandlerPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player player, ref NMultiplayerPlayerIntentHandler? __result)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        // Risk: the remote intent UI subscribes to InputSynchronizer/Targeting events,
        // and switching players in local co-op mode easily conflicts with the lifecycle of NMouseCardPlay and remote targeting-line nodes,
        // manifesting as frequent ObjectDisposedException and card-play queue errors.
        // The current choice is to disable this UI outright; restoring it later requires a stability regression pass first.
        __result = null;
        LocalMultiControlLogger.Info($"Local co-op mode has disabled the remote intent UI: player={player.NetId}");
        return false;
    }
}

using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.IsAboutToBeginGame))]
internal static class StartRunLobbyReadyFixPatch
{
    [HarmonyPostfix]
    private static void Postfix(StartRunLobby __instance, ref bool __result)
    {
        if (__result || __instance.NetService is not LocalLoopbackHostGameService)
        {
            return;
        }

        if (__instance.Players.Count >= 2 && __instance.Players.All((player) => player.isReady))
        {
            __result = true;
            LocalMultiControlLogger.Info($"Local multi-control lobby meets the ready condition; forcibly allowing the game to start: players={__instance.Players.Count}");
        }
    }
}

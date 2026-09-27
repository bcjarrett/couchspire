using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.ShouldAllowRunToBegin))]
internal static class NMultiplayerLoadGameScreenPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMultiplayerLoadGameScreen __instance, ref Task<bool> __result)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        LoadRunLobby? runLobby = AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby")?.GetValue(__instance) as LoadRunLobby;
        if (runLobby?.NetService is not LocalLoopbackHostGameService)
        {
            return true;
        }

        __result = Task.FromResult(true);
        LocalMultiControlLogger.Info("Local co-op save load skips the \"not all players present\" popup and allows continuing directly.");
        return false;
    }
}

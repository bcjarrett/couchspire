using System.Reflection;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.SetReady))]
internal static class StartRunLobbySetReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(StartRunLobby __instance, bool ready)
    {
        if (!ready || __instance.NetService is not LocalLoopbackHostGameService)
        {
            return;
        }

        bool beginningRun = AccessTools.Field(typeof(StartRunLobby), "_isBeginningRun")?.GetValue(__instance) as bool? ?? false;
        if (!beginningRun)
        {
            MethodInfo? beginRunIfReadyMethod =
                AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")
                ?? AccessTools.Method(typeof(StartRunLobby), "BeginRunIfAllPlayersReady");
            beginRunIfReadyMethod?.Invoke(__instance, Array.Empty<object>());
        }
    }
}

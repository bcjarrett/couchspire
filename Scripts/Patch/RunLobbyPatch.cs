using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(RunLobby), nameof(RunLobby.AbandonRun))]
internal static class RunLobbyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(RunLobby __instance)
    {
        bool shouldHandleLocally = LocalSelfCoopContext.IsEnabled || RunManager.Instance.NetService is LocalLoopbackHostGameService;
        if (!shouldHandleLocally)
        {
            return true;
        }

        ModLog.Info("Local co-op taking over RunLobby.AbandonRun, executing the full run-abandon flow directly.");
        ((IRunLobbyListener)RunManager.Instance).RunAbandoned();
        return false;
    }
}

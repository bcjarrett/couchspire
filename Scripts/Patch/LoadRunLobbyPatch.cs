using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.SetReady))]
internal static class LoadRunLobbyPatch
{
    [HarmonyPostfix]
    private static void Postfix(LoadRunLobby __instance, bool ready)
    {
        if (__instance.NetService is not LocalLoopbackHostGameService || !LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        List<ulong> localPlayerIdsInRun = __instance.Run.Players
            .Select((player) => player.NetId)
            .Where((id) => LocalSelfCoopContext.LocalPlayerIds.Contains(id))
            .Distinct()
            .ToList();
        if (localPlayerIdsInRun.Count <= 1)
        {
            return;
        }

        // Other local co-op characters never go through the lobby's own join flow (they share this
        // process with the host), so they never appear in ConnectedPlayerIds. BeginRunForAllPlayersIfAllReady's
        // readiness gate only checks ConnectedPlayerIds, which contains just the host, and
        // NMultiplayerLoadGameScreenPatch already skips the "not everyone is here" popup - so the run
        // begins as soon as the host readies, with no mirroring needed here.
        if (ready)
        {
            ModLog.Info($"Local co-op save load auto-ready: players={string.Join(",", localPlayerIdsInRun)}");
        }
    }
}

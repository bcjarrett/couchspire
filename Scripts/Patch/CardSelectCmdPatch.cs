using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(CardSelectCmd), "ShouldSelectLocalCard")]
internal static class CardSelectCmdPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player player, ref bool __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return true;
        }

        // Couch simultaneous mode: a teammate's selection that their own UI can answer takes the game's remote path
        // and is answered as that teammate (CouchTeammateChoices). Forcing it local would show it on the driver's screen.
        if (CouchTeammate.IsSimultaneousTeammate(player) && CouchTeammateChoices.HasRequest(player))
        {
            return true;
        }

        if (RunManager.Instance.NetService is not LocalLoopbackHostGameService)
        {
            return true;
        }

        if (!LocalSelfCoopContext.LocalPlayerIds.Contains(player.NetId))
        {
            return true;
        }

        // Under local multi-control, all local players are treated as "local manual selection" to avoid event card removal/transformation falling into remote-wait-then-random-or-stuck behavior.
        __result = RunManager.Instance.NetService.Type != NetGameType.Replay;
        return false;
    }

    [HarmonyPostfix]
    private static void Postfix(Player player, ref bool __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode
            || (CouchTeammate.IsSimultaneousTeammate(player) && CouchTeammateChoices.HasRequest(player)))
        {
            return;
        }

        if (RunManager.Instance.NetService is not LocalLoopbackHostGameService)
        {
            return;
        }

        if (!LocalSelfCoopContext.LocalPlayerIds.Contains(player.NetId))
        {
            return;
        }

        if (!__result)
        {
            LocalMultiControlLogger.Info($"Deck card selection forced to local manual selection: player={player.NetId}");
        }

        __result = true;
    }
}

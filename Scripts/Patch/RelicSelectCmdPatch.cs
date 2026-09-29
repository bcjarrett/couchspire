using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(RelicSelectCmd), "ShouldSelectLocalRelic")]
internal static class RelicSelectCmdPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player player, ref bool __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return true;
        }

        if (RunManager.Instance.NetService is not LocalLoopbackHostGameService)
        {
            return true;
        }

        // Couch simultaneous mode: the teammate picks this relic in their own card picker (remote path).
        if (CouchTeammate.IsSimultaneousTeammate(player) && CouchTeammateChoices.HasRequest(player))
        {
            return true;
        }

        __result = true;
        ModLog.Info($"Local co-op mode forces local handling of relic selection: player={player.NetId}");
        return false;
    }
}

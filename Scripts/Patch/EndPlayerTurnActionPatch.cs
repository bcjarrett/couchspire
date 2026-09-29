using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.GameActions;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
internal static class EndPlayerTurnActionPatch
{
    [HarmonyPostfix]
    private static void Postfix(EndPlayerTurnAction __instance)
    {
        LocalControlRuntime.TryAutoSwitchAfterEndTurn(__instance.OwnerId);
    }
}

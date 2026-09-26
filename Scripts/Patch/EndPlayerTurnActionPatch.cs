using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.GameActions;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
internal static class EndPlayerTurnActionPatch
{
    [HarmonyPostfix]
    private static void Postfix(EndPlayerTurnAction __instance)
    {
        LocalMultiControlRuntime.TryAutoSwitchAfterEndTurn(__instance.OwnerId);
    }
}

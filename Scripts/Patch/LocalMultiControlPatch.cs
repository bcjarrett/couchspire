using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch]
internal static class LocalMultiControlPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    private static void PostfixRunManagerLaunch(RunState __result)
    {
        LocalMultiControlRuntime.OnRunLaunched(__result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    private static void PrefixRunManagerCleanUp(bool graceful)
    {
        LocalMultiControlLogger.Info($"检测到 RunManager.CleanUp(graceful={graceful})，准备清理本地多控会话。");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    private static void PostfixRunManagerCleanUp()
    {
        LocalMultiControlRuntime.OnRunCleanup();
    }
}

using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch]
internal static class RunManagerLifecyclePatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    private static void PostfixRunManagerLaunch(RunState __result)
    {
        LocalControlRuntime.OnRunLaunched(__result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    private static void PrefixRunManagerCleanUp(bool graceful)
    {
        ModLog.Info($"Detected RunManager.CleanUp(graceful={graceful}), preparing to clean up the local co-op session.");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    private static void PostfixRunManagerCleanUp()
    {
        LocalControlRuntime.OnRunCleanup();
    }
}

using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.ControllerInput;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: poll every Steam Input controller instead of only the first one.
/// </summary>
[HarmonyPatch(typeof(SteamControllerInputStrategy), nameof(SteamControllerInputStrategy.ProcessInput))]
internal static class SteamControllerInputStrategyPatch
{
    [HarmonyPrefix]
    private static bool PrefixProcessInput(SteamControllerInputStrategy __instance)
    {
        return CouchSteamPoller.ProcessInput(__instance);
    }
}

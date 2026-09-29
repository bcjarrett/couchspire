using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.ControllerInput;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: turn each joypad's sticks and triggers into actions separately instead of merging all joypads.
/// </summary>
[HarmonyPatch(typeof(GodotControllerInputStrategy), nameof(GodotControllerInputStrategy.ProcessInput))]
internal static class GodotControllerInputStrategyPatch
{
    [HarmonyPrefix]
    private static bool PrefixProcessInput(GodotControllerInputStrategy __instance)
    {
        return CouchGodotPadPoller.ProcessInput(__instance);
    }
}

using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Relics;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(WhisperingEarring), nameof(WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate))]
internal static class WhisperingEarringPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        // Keep the base game's Whispering Earring behavior to avoid it interfering with the Toolbox relic's local multi-control handling and causing a continuous takeover.
        return true;
    }
}

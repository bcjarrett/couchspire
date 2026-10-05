using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: the game only runs the Crystal Sphere minigame for the local player, so a teammate who picks either
/// option paid for the event and got no grid and no rewards. Run the teammate's minigame in their own panel instead.
/// </summary>
[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.PlayMinigame))]
internal static class CouchTeammateCrystalSpherePatch
{
    private static readonly AccessTools.FieldRef<CrystalSphereMinigame, Player> OwnerRef =
        AccessTools.FieldRefAccess<CrystalSphereMinigame, Player>("_owner");

    [HarmonyPrefix]
    private static bool PrefixPlayMinigame(CrystalSphereMinigame __instance, ref Task __result)
    {
        Player owner = OwnerRef(__instance);
        if (!CouchTeammate.IsSimultaneousTeammate(owner))
        {
            return true;
        }

        __result = CouchTeammateCrystalSphere.PlayForTeammate(__instance, owner);
        return false;
    }
}

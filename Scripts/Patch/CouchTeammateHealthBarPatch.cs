using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: the game hides the health/block bar under teammates' characters (online, teammates are shown in the
/// top-left panel and the bar appears on hover). Every character on the couch belongs to someone sitting there, so
/// local characters and their pets keep their bar visible like the local player's own.
/// </summary>
[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
internal static class CouchTeammateHealthBarPatch
{
    private static readonly AccessTools.FieldRef<NCreature, bool> IsRemotePlayerOrPetRef =
        AccessTools.FieldRefAccess<NCreature, bool>("_isRemotePlayerOrPet");

    private static readonly AccessTools.FieldRef<NCreature, NCreatureStateDisplay> StateDisplayRef =
        AccessTools.FieldRefAccess<NCreature, NCreatureStateDisplay>("_stateDisplay");

    [HarmonyPostfix]
    private static void PostfixReady(NCreature __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled || !IsRemotePlayerOrPetRef(__instance))
        {
            return;
        }

        Player? owner = __instance.Entity.IsPlayer ? __instance.Entity.Player : __instance.Entity.PetOwner;
        if (owner == null || !LocalSelfCoopContext.LocalPlayerIds.Contains(owner.NetId))
        {
            return;
        }

        IsRemotePlayerOrPetRef(__instance) = false;
        StateDisplayRef(__instance).AnimateIn(HealthBarAnimMode.SpawnedAtCombatStart);
    }
}

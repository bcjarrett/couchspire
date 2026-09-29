using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(NCombatRoom), "OnCombatSetUp")]
internal static class NCombatRoomPatch
{
    [HarmonyPostfix]
    private static void Postfix(CombatState state)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        LocalControlRuntime.SwitchControlledPlayerTo(LocalSelfCoopContext.PrimaryPlayerId, "combat-setup");
        LocalControlRuntime.RefreshSharedTopBarForCombat("combat-setup");
        LocalControlRuntime.RefreshCombatEnergyForCurrentPlayer("combat-setup");
        Callable.From(delegate
        {
            LocalControlRuntime.RefreshSharedTopBarForCombat("combat-setup-deferred");
            LocalControlRuntime.RefreshCombatEnergyForCurrentPlayer("combat-setup-deferred");

            Callable.From(delegate
            {
                LocalControlRuntime.RefreshCombatEnergyForCurrentPlayer("combat-setup-deferred-2");
            }).CallDeferred();
        }).CallDeferred();
    }
}

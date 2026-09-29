using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(ThievingHopper), "ThieveryMove")]
internal static class ThievingHopperPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref IReadOnlyList<Creature> targets)
    {
        if (!LocalSelfCoopContext.IsEnabled || targets.Count <= 1)
        {
            return;
        }

        Creature? selectedTarget = LocalContext.GetMe(targets);
        if (selectedTarget == null)
        {
            ulong? currentPlayerId = LocalControlRuntime.SessionState.CurrentControlledPlayerId;
            if (currentPlayerId.HasValue)
            {
                selectedTarget = targets.FirstOrDefault(
                    (creature) => creature.Player?.NetId == currentPlayerId.Value || creature.PetOwner?.NetId == currentPlayerId.Value);
            }
        }

        selectedTarget ??= targets.FirstOrDefault((creature) => creature.IsAlive) ?? targets[0];
        targets = new List<Creature> { selectedTarget };
        ModLog.Info($"Thieving Hopper card-steal target narrowed to a single player: target={selectedTarget.Player?.NetId ?? selectedTarget.PetOwner?.NetId ?? 0UL}");
    }
}

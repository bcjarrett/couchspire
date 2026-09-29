using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Kill), new[] { typeof(IReadOnlyCollection<Creature>), typeof(bool) })]
internal static class CreatureCmdKillWinCheckPatch
{
    [HarmonyPostfix]
    private static void Postfix(IReadOnlyCollection<Creature> creatures, ref Task __result)
    {
        if (!LocalSelfCoopContext.IsEnabled || creatures.Count == 0)
        {
            return;
        }

        bool hasEnemy = creatures.Any((creature) => creature != null && creature.IsEnemy);
        if (!hasEnemy)
        {
            return;
        }

        __result = WrapWithImmediateWinCheck(__result);
    }

    private static async Task WrapWithImmediateWinCheck(Task originalTask)
    {
        await originalTask;
        if (!CombatManager.Instance.IsInProgress)
        {
            return;
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || state.CurrentSide != CombatSide.Player)
        {
            return;
        }

        if (state.Enemies.Any((enemy) => enemy != null && enemy.IsAlive && enemy.IsPrimaryEnemy))
        {
            return;
        }

        ModLog.Info("Detected that all enemies are dead; immediately triggering combat victory resolution.");
        await CombatManager.Instance.CheckWinCondition();
    }
}

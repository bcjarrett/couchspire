using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime;

internal static class LocalControlSwitchGuard
{
    public static bool TrySwitchTo(ulong playerId, string source)
    {
        if (!CanSwitchNow(source))
        {
            return false;
        }

        LocalControlRuntime.SwitchControlledPlayerTo(playerId, source);
        return true;
    }

    private static bool CanSwitchNow(string source)
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress)
        {
            return false;
        }

        if (!CombatManager.Instance.IsInProgress)
        {
            return true;
        }

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            return false;
        }

        if (combatUi.Hand.InCardPlay || combatUi.Hand.IsInCardSelection || (NTargetManager.Instance?.IsInSelection ?? false))
        {
            ModLog.Info($"Switch ignored ({source}): a card play or selection is in progress.");
            return false;
        }

        if (RunManager.Instance.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase)
        {
            ModLog.Info(
                $"Switch ignored ({source}): combat sync phase is {RunManager.Instance.ActionQueueSynchronizer.CombatState}.");
            return false;
        }

        return true;
    }
}

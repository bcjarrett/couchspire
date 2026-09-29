#if COUCHSPIRE_TESTS
using System.Text;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// The timeout diagnostic dump from docs/design/testing-plan.md §6.5: overlay stack, current room, driver, action
/// queue state, and both players' pending choices and visible mod panels. Logged in full (prefix
/// "[CouchSpire] [CouchTest]") and returned truncated for the scenario result's message field.
/// </summary>
internal static class CouchTestDiagnostics
{
    private const int MaxMessageLength = 2000;

    public static string DumpOnTimeout(string scenarioName)
    {
        string dump = Build(scenarioName);
        CouchTestLog.Warn(dump);
        return dump.Length <= MaxMessageLength ? dump : dump[..MaxMessageLength] + "... (truncated)";
    }

    private static string Build(string scenarioName)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Timeout diagnostics for scenario '{scenarioName}':");

        IOverlayScreen? top = NOverlayStack.Instance?.Peek();
        sb.AppendLine($"  Overlay stack: top={top?.GetType().Name ?? "none"}, count={NOverlayStack.Instance?.ScreenCount ?? 0}");

        bool inProgress = RunManager.Instance.IsInProgress;
        RunState? runState = inProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        sb.AppendLine($"  Current room: {runState?.CurrentRoom?.RoomType.ToString() ?? "none"}");
        sb.AppendLine($"  Driver (LocalContext.NetId): {LocalContext.NetId?.ToString() ?? "none"}");

        bool queueEmpty = inProgress && RunManager.Instance.ActionQueueSet.IsEmpty;
        bool executorRunning = inProgress && RunManager.Instance.ActionExecutor.IsRunning;
        sb.AppendLine($"  Action queue: inProgress={inProgress}, empty={queueEmpty}, executorRunning={executorRunning}");

        if (CouchTeammateChoices.Pending.Count == 0)
        {
            sb.AppendLine("  Pending teammate choices: none");
        }
        else
        {
            foreach (CouchTeammateChoice choice in CouchTeammateChoices.Pending)
            {
                sb.AppendLine($"  Pending teammate choice: player={choice.Player.NetId}, {choice.Describe()}");
            }
        }

        sb.AppendLine(
            $"  Visible mod panels: info={CouchTeammateInfo.IsActive}, choicePanel={CouchTeammateChoicePanel.IsActive}, " +
            $"rewards={CouchTeammateRewards.IsActive}, event={CouchTeammateEvent.IsActive}, restSite={CouchTeammateRestSite.IsActive}, " +
            $"treasure={CouchTeammateTreasure.IsActive}, shop={CouchTeammateShop.IsActive}, hud={CouchTeammateHud.IsActive}");

        return sb.ToString();
    }
}
#endif

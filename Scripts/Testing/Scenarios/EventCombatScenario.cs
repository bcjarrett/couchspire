#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// A shared event that starts a fight: Punch Off (src/Core/Models/Events/PunchOff.cs), "I can take them" then "Fight".
/// It has a Combat layout, so the fight runs inside the event room and the event is never finished. Once the fight
/// starts, P2's event panel must be gone and P2's input must reach their combat HUD. Guaranteed by
/// <c>CouchTeammateEvent</c> hiding during an event combat; without it the panel kept showing the "Fight" page and
/// swallowed every P2 press, so P2 couldn't play the combat (playtest report).
///
/// Runs at FastMode.Fast, not the harness's Instant: Punch Off's idle loop (<c>PunchOff.PunchEachOther</c>) only
/// yields through <c>Cmd.Wait</c>, which Instant skips, so the loop never yields and hangs the game.
/// </summary>
internal sealed class EventCombatScenario : CouchTestScenarioBase
{
    private const int TakeThemOption = 1;

    public override string Name => "event_combat";

    public override async Task RunAsync(CouchTestContext context)
    {
        FastModeType fastModeBefore = SaveManager.Instance.PrefsSave.FastMode;
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
        try
        {
            await RunEventCombatAsync(context);
        }
        finally
        {
            SaveManager.Instance.PrefsSave.FastMode = fastModeBefore;
        }
    }

    private static async Task RunEventCombatAsync(CouchTestContext context)
    {
        await context.EnterEvent("PUNCH_OFF");

        EventSynchronizer synchronizer = RunManager.Instance.EventSynchronizer;
        context.Expect(synchronizer.IsShared, "Expected Punch Off to be a shared event.");
        await context.WaitUntil(() => CouchTeammateEvent.IsActive, "P2's event panel to open");
        await context.Checkpoint("event-initial");

        // Page 1: both vote "I can take them" (P2's cursor starts on option 0).
        synchronizer.ChooseLocalOption(TakeThemOption);
        await context.P2Press(CouchHudCommand.Down);
        await context.P2Press(CouchHudCommand.Accept);
        EventModel p2Event = synchronizer.GetEventForPlayer(context.P2);
        await context.WaitUntil(
            () => p2Event.CurrentOptions.Count == 1 && synchronizer.GetEventForPlayer(context.P1).CurrentOptions.Count == 1,
            "both players to reach the Fight page");
        await context.Settle();

        // Page 2: both vote "Fight".
        synchronizer.ChooseLocalOption(0);
        await context.P2Press(CouchHudCommand.Accept);
        await context.WaitUntil(() => CombatManager.Instance.IsInProgress, "the event's combat to start");
        await context.Settle();
        await context.Checkpoint("event-combat");

        context.Expect(
            !CouchTeammateEvent.IsActive,
            "Expected P2's event panel to close once the event's fight started, but it is still up (it would swallow P2's combat input).");

        // P2's input must now drive their combat HUD: end their turn through it.
        await context.P2EndTurnThroughHud();
        await context.Settle();
        context.Expect(
            CombatManager.Instance.IsPlayerReadyToEndTurn(context.P2),
            "Expected P2's End Turn press to reach their combat HUD during the event's fight.");
    }
}
#endif

#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// An Ancient event (Darv, src/Core/Models/Events/Darv.cs) offers relics whose text names cards and keywords. The
/// game shows those as hover tips on the focused option; P2's event panel used to show only the text, so P2 couldn't
/// tell what an offered relic's card or keyword was (playtest report). Now the option under P2's cursor opens its tips
/// in P2's panel, below the options (as floating tips they covered P1's screen).
/// </summary>
internal sealed class EventHoverTipScenario : EventHoverTipScenarioBase
{
    public override string Name => "event_hover_tips";

    protected override string EventId => "DARV";
}

/// <summary>
/// Sunken Treasury's second chest gives Greed, a curse shown as a card preview on the option. P2 found the preview
/// partly hidden under their event panel when it was a floating tip (playtest report); it's now drawn in the panel.
/// </summary>
internal sealed class EventCurseHoverTipScenario : EventHoverTipScenarioBase
{
    public override string Name => "event_hover_tips_curse";

    protected override string EventId => "SUNKEN_TREASURY";
}

/// <summary>Enters <see cref="EventId"/>, moves P2's cursor to the first option with hover tips, and checks them.</summary>
internal abstract class EventHoverTipScenarioBase : CouchTestScenarioBase
{
    protected abstract string EventId { get; }

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterEvent(EventId);
        await context.WaitUntil(() => CouchTeammateEvent.IsActive, "P2's event panel to open");

        EventModel p2Event = RunManager.Instance.EventSynchronizer.GetEventForPlayer(context.P2);
        IReadOnlyList<EventOption> options = p2Event.CurrentOptions;
        int withTips = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (!options[i].IsLocked && options[i].HoverTips.Any())
            {
                withTips = i;
                break;
            }
        }

        context.Expect(withTips >= 0, $"Expected one of {EventId}'s {options.Count} options to have hover tips (the offer is random).");
        if (withTips < 0)
        {
            return;
        }

        // P2's cursor starts on option 0.
        for (int i = 0; i < withTips; i++)
        {
            await context.P2Press(CouchHudCommand.Down);
        }

        await context.Settle();
        await context.Checkpoint("p2-option-tips");
        context.Expect(
            CouchTeammateEvent.TipOptionIndex == withTips,
            $"Expected P2's focused option {withTips} ({options[withTips].TextKey}) to show its hover tips, found {CouchTeammateEvent.TipOptionIndex?.ToString() ?? "none"}.");
        int cardTips = options[withTips].HoverTips.OfType<CardHoverTip>().Count();
        context.Expect(
            CouchTeammateEvent.TipCardCount == cardTips,
            $"Expected P2's event panel to preview the option's {cardTips} card(s), found {CouchTeammateEvent.TipCardCount}.");
    }
}
#endif

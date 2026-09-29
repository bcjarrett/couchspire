#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Combat helpers for <c>combat</c> and <c>choice</c> (docs/testing.md). Everything here drives P2
/// exactly the way their pad/keyboard would (<see cref="CouchTeammateHud"/> via <see cref="CouchTestContext.P2Press"/>),
/// or P1 through the real UI (<see cref="CouchTestContext.ClickAsync"/>) — never through <see cref="CouchRemotePlay"/>
/// directly, since the HUD path exercises the input-handling code the scenarios exist to protect.
/// </summary>
internal sealed partial class CouchTestContext
{
    /// <summary>
    /// Finds P2's copy of a no-target card by ID and plays it through P2's HUD, sending the same commands their pad
    /// would (docs/testing.md row `combat`): moves the hand cursor from its starting position (index
    /// 0, true for a <see cref="CouchTeammateHud"/> freshly attached to this combat, since nothing has moved it yet)
    /// onto the card, then presses Accept twice — the first picks the card up ("Holding", since it needs no target),
    /// the second confirms the play. Only cards for which <see cref="CouchRemotePlay.NeedsTarget"/> is false (e.g.
    /// Backflip, Survivor: both <c>TargetType.Self</c>) go through this "Holding" path; a card that needs a target
    /// would instead enter "Targeting" mode after the first Accept.
    /// </summary>
    public async Task<CardModel> P2PlayNoTargetCardThroughHud(string cardIdEntry)
    {
        ThrowIfCancelled();
        List<CardModel> hand = P2.PlayerCombatState!.Hand.Cards.ToList();
        int targetIndex = hand.FindIndex((CardModel candidate) => candidate.Id.Entry == cardIdEntry);
        if (targetIndex < 0)
        {
            throw new CouchTestExpectationFailedException(
                $"P2's hand does not contain {cardIdEntry}: [{string.Join(", ", hand.Select((CardModel candidate) => candidate.Id.Entry))}].");
        }

        CardModel card = hand[targetIndex];
        if (CouchRemotePlay.NeedsTarget(card))
        {
            throw new CouchTestExpectationFailedException(
                $"{cardIdEntry} needs a target; P2PlayNoTargetCardThroughHud only drives the no-target ('Holding') HUD path.");
        }

        for (int i = 0; i < targetIndex; i++)
        {
            await P2Press(CouchHudCommand.Right);
        }

        await P2Press(CouchHudCommand.Accept);
        await P2Press(CouchHudCommand.Accept);
        return card;
    }

    /// <summary>Ends P2's turn exactly as their pad's End Turn press would (<see cref="CouchTeammateHud"/>'s Hand mode).</summary>
    public Task P2EndTurnThroughHud()
    {
        return P2Press(CouchHudCommand.EndTurn);
    }

    /// <summary>
    /// Clicks P1's real End Turn button (<see cref="NEndTurnButton"/>) exactly as the driver would with a mouse,
    /// via <see cref="ClickAsync"/> (<see cref="UiHelper.Click"/> → <c>ForceClick</c>).
    /// </summary>
    public async Task P1EndTurnThroughRealButton()
    {
        ThrowIfCancelled();
        NEndTurnButton? button = null;
        await WaitUntil(
            () => (button = UiHelper.FindFirst<NEndTurnButton>(_tree.Root)) != null && button.IsVisibleInTree(),
            "P1's End Turn button to appear");
        await ClickAsync(button!);
    }

    /// <summary>
    /// Answers P2's pending teammate choice through their HUD (docs/testing.md row `choice`): moves
    /// the choice cursor from its starting position (index 0, reset whenever the HUD switches into
    /// <c>HudMode.Choice</c>) onto the option whose card ID matches <paramref name="optionCardIdEntry"/>, then
    /// presses Accept. Only works for single-pick choices (<c>MaxSelect == 1</c>, true for Survivor's discard), where
    /// Accept answers immediately instead of toggling a multi-select pick.
    /// </summary>
    public async Task P2AnswerChoiceByCardIdThroughHud(string optionCardIdEntry)
    {
        ThrowIfCancelled();
        CouchTeammateChoice? choice = CouchTeammateChoices.Pending.FirstOrDefault((CouchTeammateChoice candidate) => candidate.Player == P2);
        if (choice == null)
        {
            throw new CouchTestExpectationFailedException("No teammate choice is pending for P2.");
        }

        if (choice.MaxSelect != 1)
        {
            throw new CouchTestExpectationFailedException(
                $"P2AnswerChoiceByCardIdThroughHud only drives single-pick choices (MaxSelect == 1), found MaxSelect={choice.MaxSelect}.");
        }

        int optionIndex = choice.Options.ToList().FindIndex((CardModel candidate) => candidate.Id.Entry == optionCardIdEntry);
        if (optionIndex < 0)
        {
            throw new CouchTestExpectationFailedException(
                $"P2's pending choice does not offer {optionCardIdEntry}: [{string.Join(", ", choice.Options.Select((CardModel candidate) => candidate.Id.Entry))}].");
        }

        // The choice can become pending synchronously, inside the same P2Press call that played the card (the
        // loopback dispatch resolves in-process, with no frame boundary in between) — before the HUD's own
        // _Process() has run its UpdateMode() and switched _mode to Choice. Wait for that switch explicitly, or
        // these presses would still be routed by whatever mode the HUD was last in.
        await WaitUntil(() => CouchTeammateHud.ModeName == "Choice", "the teammate HUD to enter Choice mode for P2's pending choice");

        for (int i = 0; i < optionIndex; i++)
        {
            await P2Press(CouchHudCommand.Right);
        }

        await P2Press(CouchHudCommand.Accept);
    }
}
#endif

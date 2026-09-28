#if COUCHSPIRE_TESTS
using Godot;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// New scenario for the approved column-expand spec: both players smith at the same time, P1 through the real
/// full-screen upgrade grid (<see cref="NDeckUpgradeSelectScreen"/>) and P2 through <see cref="CouchTeammateChoicePanel"/>
/// in the shared column, at once. This is exactly the case the maintainer's live screenshots showed going wrong:
/// P1's grid spans the screen with its confirm (✓) / cancel buttons bottom-right/bottom-left, and P2's column used
/// to stretch down over them. Guards:
/// <list type="bullet">
/// <item>the column floor now measured live off P1's card-select buttons, not a fixed clearance
/// (<see cref="CouchColumnFloor"/>, <see cref="CouchPanel.ColumnMaxY"/>);</item>
/// <item>P2's panel staying compact instead of stretching to the floor while P1's picker is open
/// (<see cref="CouchPanel.RowsBudgetMaxY"/>);</item>
/// <item>the generalized "no P2 panel overlaps any visible P1 action button" layout rule
/// (<see cref="CouchTestLayout"/>'s use of <see cref="CouchColumnFloor.FindActionButtons"/>), checked automatically
/// at every checkpoint below, including the one while both pickers are open.</item>
/// </list>
/// Aspects: 16:9 and 16:10, like the other layout-sensitive rest-site coverage.
/// </summary>
internal sealed class SimultaneousSmithScenario : CouchTestScenarioBase
{
    public override string Name => "simultaneous_smith";

    public override IReadOnlyList<string> Aspects { get; } = CouchTestLayout.KnownAspects;

    public override async Task RunAsync(CouchTestContext context)
    {
        // Give P2 an upgradable card at a known, reproducible deck position (last, by CardPileCmd.Add's default
        // Bottom placement), so scrolling P2's Smith list all the way down lands on it deterministically - the same
        // technique the rest_site scenario already uses.
        await context.Console(context.P2, "card NEUTRALIZE deck");
        await context.Settle();

        CardModel p2SeededCard = context.P2.Deck.Cards.Last();
        context.Expect(
            p2SeededCard.Id.Entry == "NEUTRALIZE",
            $"Expected the card just added to P2's deck to be NEUTRALIZE, found {p2SeededCard.Id.Entry}.");
        context.Expect(
            p2SeededCard.IsUpgradable && !p2SeededCard.IsUpgraded,
            "Expected the seeded NEUTRALIZE card to start unupgraded and upgradable.");

        await context.EnterRoom(RoomType.RestSite);
        await context.Checkpoint("rest-site-open");
        context.Expect(CouchTeammateRestSite.IsActive, "Expected P2's rest site panel to be visible after entering the rest site.");

        List<CardModel> p1CardsBefore = context.P1.Deck.Cards.ToList();
        List<bool> p1UpgradedBefore = p1CardsBefore.Select((card) => card.IsUpgraded).ToList();
        List<CardModel> p2CardsBefore = context.P2.Deck.Cards.ToList();
        List<bool> p2UpgradedBefore = p2CardsBefore.Select((card) => card.IsUpgraded).ToList();

        // P1 opens Smith through the real UI: the click starts SmithRestSiteOption's flow, which pushes the real
        // NDeckUpgradeSelectScreen overlay and waits for P1's pick.
        NClickableControl p1SmithButton = context.P1RestSiteOptionButton("SMITH");
        await context.ClickAsync(p1SmithButton);
        await context.Settle();

        NCardGridSelectionScreen p1Screen = context.P1CardSelectScreen();
        CardModel p1PickedCard = context.P1CardSelectFirstOption(p1Screen);

        // P1 clicks a card: for a single-select screen this immediately opens the preview (its own confirm/cancel
        // buttons - the ✓/back pair from the maintainer's screenshot), without finishing the pick yet.
        await context.P1ClickCardInSelectScreen(p1Screen, p1PickedCard);
        await context.Checkpoint("p1-smith-grid-open");

        // Now P2 smiths through their own panel while P1's grid (and its preview confirm/cancel) is still up.
        CouchTeammateRestSite restSitePanel = context.TeammateRestSitePanel();
        await context.P2ChooseRestSiteOption(restSitePanel, "SMITH");

        CouchTeammateChoice p2UpgradeChoice = await context.WaitForPendingTeammateChoice(CouchTestContext.RestSiteUpgradeChoiceSource);
        await context.Checkpoint("both-smithing-simultaneously");
        context.LogChoicePanelPreviewCardRect("both-smithing-simultaneously");

        int seededCardOptionIndex = p2UpgradeChoice.Options.ToList().FindIndex((card) => ReferenceEquals(card, p2SeededCard));
        context.Expect(seededCardOptionIndex >= 0, "Expected the seeded NEUTRALIZE card to be offered in P2's Smith upgrade choice.");
        context.Expect(
            seededCardOptionIndex >= 3,
            $"Expected the seeded card to sit well down P2's Smith list (a real scroll, not just the first row), found index {seededCardOptionIndex}.");

        // Scrolls P2's list with P2Press to the far-down seeded card and picks it - CouchTeammateChoicePanel's own
        // scrolled row list, which must keep the cursor row visible while P1's grid is open at the same time.
        CardModel p2PickedCard = await context.P2AnswerSingleCardTeammateChoice(p2UpgradeChoice, seededCardOptionIndex);
        context.Expect(ReferenceEquals(p2PickedCard, p2SeededCard), "Expected P2's Smith pick to be the seeded NEUTRALIZE card.");
        context.Expect(!CouchTeammateChoicePanel.IsActive, "Expected P2's card-choice panel to be closed once P2's Smith pick is answered.");

        // P1's card-select overlay must still be exactly where P2 left it: untouched by P2's pick.
        context.Expect(
            context.P1CardSelectScreen() == p1Screen,
            "Expected P1's card-select overlay to be unaffected by P2 smithing simultaneously.");

        // Only P2's seeded card changed; P1's deck is completely untouched while P2 smiths (P1 hasn't confirmed yet).
        List<CardModel> p1CardsAfterP2 = context.P1.Deck.Cards.ToList();
        context.Expect(
            p1CardsAfterP2.Count == p1CardsBefore.Count && p1CardsAfterP2.Select((card) => card.IsUpgraded).SequenceEqual(p1UpgradedBefore),
            "Expected P1's deck upgrade state to be completely unchanged while P2 smiths (P1 hasn't confirmed their own pick yet).");

        List<CardModel> p2CardsAfter = context.P2.Deck.Cards.ToList();
        context.Expect(p2CardsAfter.Count == p2CardsBefore.Count, $"Expected P2's deck size to stay at {p2CardsBefore.Count} cards, found {p2CardsAfter.Count}.");
        for (int i = 0; i < p2CardsBefore.Count; i++)
        {
            CardModel card = p2CardsBefore[i];
            bool wasUpgraded = p2UpgradedBefore[i];
            bool isUpgradedNow = card.IsUpgraded;
            if (ReferenceEquals(card, p2SeededCard))
            {
                context.Expect(!wasUpgraded && isUpgradedNow, $"Expected the seeded card to go from not-upgraded to upgraded, found {wasUpgraded} -> {isUpgradedNow}.");
            }
            else
            {
                context.Expect(
                    wasUpgraded == isUpgradedNow,
                    $"Expected only the seeded card's upgrade state to change; P2's deck card at index {i} ({card.Id.Entry}) changed from {wasUpgraded} to {isUpgradedNow}.");
            }
        }

        // Now P1 finishes their own upgrade through the real UI: confirm the preview, closing the overlay.
        await context.P1ConfirmCardSelectPreview(p1Screen);
        CouchTestLog.Info(
            $"After P1's confirm click: overlay top={NOverlayStack.Instance?.Peek()?.GetType().Name ?? "none"}, " +
            $"p1Screen still valid={GodotObject.IsInstanceValid(p1Screen)}, p1PickedCard.IsUpgraded={p1PickedCard.IsUpgraded}.");

        // The confirm click resolves NDeckUpgradeSelectScreen's own completion source, but SmithRestSiteOption.OnSelect
        // then still awaits CardCmd.Upgrade/Hook.AfterRestSiteSmith beyond what Settle()'s screen-stability heuristics
        // watch (no overlay/pending-choice change marks that in-flight), so wait for the actual deck mutation directly
        // rather than assuming Settle() alone caught up to it.
        await context.WaitUntil(() => p1PickedCard.IsUpgraded, "P1's picked card to become upgraded after confirming");
        await context.Checkpoint("p1-smith-confirmed");

        context.Expect(context.P1.Deck.Cards.First((card) => ReferenceEquals(card, p1PickedCard)).IsUpgraded, "Expected P1's picked card to be upgraded after confirming.");

        List<CardModel> p1CardsFinal = context.P1.Deck.Cards.ToList();
        context.Expect(p1CardsFinal.Count == p1CardsBefore.Count, $"Expected P1's deck size to stay at {p1CardsBefore.Count} cards, found {p1CardsFinal.Count}.");
        for (int i = 0; i < p1CardsBefore.Count; i++)
        {
            CardModel card = p1CardsBefore[i];
            bool wasUpgraded = p1UpgradedBefore[i];
            bool isUpgradedNow = card.IsUpgraded;
            if (ReferenceEquals(card, p1PickedCard))
            {
                context.Expect(!wasUpgraded && isUpgradedNow, $"Expected P1's picked card to go from not-upgraded to upgraded, found {wasUpgraded} -> {isUpgradedNow}.");
            }
            else
            {
                context.Expect(
                    wasUpgraded == isUpgradedNow,
                    $"Expected only P1's picked card's upgrade state to change; P1's deck card at index {i} ({card.Id.Entry}) changed from {wasUpgraded} to {isUpgradedNow}.");
            }
        }

        context.Expect(!CouchTeammateRestSite.IsActive, "Expected P2's rest site panel to already be closed (Smith completes the whole rest site).");
    }
}
#endif

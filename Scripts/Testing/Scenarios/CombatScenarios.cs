#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §6.9, row `combat`: P2 plays a no-target card through their HUD — it leaves P2's hand
/// and spends P2's energy, while P1's hand and energy are unaffected. P2 ends their turn (through the HUD), P1 ends
/// theirs (through the real End Turn button), the enemy turn runs, and control returns to a new player round.
/// <c>win</c> then opens the rewards screen. Guaranteed by <c>CouchRemotePlay.cs</c> (the loopback send under the
/// HUD) and, chiefly, <c>CombatManagerReadyEnemyTurnPatch.cs</c>: the game only ever auto-enqueues the "ready to
/// begin enemy turn" follow-up for <c>LocalContext.GetMe</c> (the driver, P1); without the patch's mirroring, P2's
/// own readiness never reaches <c>CombatManager</c>, so once both players end their turn (phase 1) the enemy turn
/// never begins (phase 2 stalls) — a soft-lock. <c>CombatManagerPatch.cs</c> only applies when
/// <c>CouchTeammate.DriverKeepsScreen</c> is false (a different combat mode than the simultaneous default this
/// scenario runs under), so it isn't exercised here.
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes for this scenario once the aspect mechanism (WP4, landing in
/// parallel) is available; the runner currently only supports a single fixed 16:9 pass.
/// </summary>
internal sealed class CombatScenario : CouchTestScenarioBase
{
    /// <summary>Silent skill, TargetType.Self, no player choice (src/Core/Models/Cards/Backflip.cs): gain block, draw 2.</summary>
    private const string NoTargetCardId = "BACKFLIP";

    public override string Name => "combat";

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterRoom(RoomType.Monster);

        await context.Console(context.P2, $"card {NoTargetCardId}");
        await context.Settle();

        List<string> p1HandBefore = context.P1.PlayerCombatState!.Hand.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        int p1EnergyBefore = context.P1.PlayerCombatState!.Energy;
        int p2EnergyBefore = context.P2.PlayerCombatState!.Energy;
        CardModel cardBeforePlay = context.P2.PlayerCombatState!.Hand.Cards.First((CardModel c) => c.Id.Entry == NoTargetCardId);
        int cardCost = cardBeforePlay.EnergyCost.GetAmountToSpend();

        CardModel playedCard = await context.P2PlayNoTargetCardThroughHud(NoTargetCardId);
        await context.Settle();

        context.Expect(
            !context.P2.PlayerCombatState!.Hand.Cards.Contains(playedCard),
            $"Expected {NoTargetCardId} to leave P2's hand after being played through the HUD.");
        context.Expect(
            playedCard.Pile?.Type is PileType.Discard or PileType.Exhaust,
            $"Expected {NoTargetCardId} to land in P2's discard or exhaust pile, found {playedCard.Pile?.Type.ToString() ?? "no pile"}.");
        context.Expect(
            context.P2.PlayerCombatState!.Energy == p2EnergyBefore - cardCost,
            $"Expected P2's energy to drop by {NoTargetCardId}'s cost ({cardCost}): {p2EnergyBefore} -> expected {p2EnergyBefore - cardCost}, found {context.P2.PlayerCombatState!.Energy}.");

        List<string> p1HandAfter = context.P1.PlayerCombatState!.Hand.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        context.Expect(
            p1HandAfter.SequenceEqual(p1HandBefore),
            $"Expected P1's hand to be unaffected by P2's play: before=[{string.Join(",", p1HandBefore)}], after=[{string.Join(",", p1HandAfter)}].");
        context.Expect(
            context.P1.PlayerCombatState!.Energy == p1EnergyBefore,
            $"Expected P1's energy to be unaffected by P2's play: before={p1EnergyBefore}, found {context.P1.PlayerCombatState!.Energy}.");

        await context.Checkpoint("p2-played-card");

        CombatState combatStateBeforeEndTurns = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new CouchTestExpectationFailedException("No combat state while ending turns.");
        int roundBefore = combatStateBeforeEndTurns.RoundNumber;

        await context.P2EndTurnThroughHud();
        await context.Settle();
        context.Expect(
            CombatManager.Instance.IsPlayerReadyToEndTurn(context.P2),
            "Expected P2 to be ready to end their turn after pressing End Turn through the HUD.");

        await context.P1EndTurnThroughRealButton();
        await context.Settle();

        await context.WaitUntil(
            () =>
            {
                CombatState? state = CombatManager.Instance.DebugOnlyGetState();
                return state != null && state.CurrentSide == CombatSide.Player && state.RoundNumber > roundBefore;
            },
            "the round to advance and control to return to a new player turn",
            TimeSpan.FromSeconds(30));

        CombatState combatStateAfterEnemyTurn = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new CouchTestExpectationFailedException("No combat state after the enemy turn.");
        context.Expect(
            combatStateAfterEnemyTurn.RoundNumber > roundBefore,
            $"Expected the round number to increase past {roundBefore}, found {combatStateAfterEnemyTurn.RoundNumber}.");
        context.Expect(
            combatStateAfterEnemyTurn.CurrentSide == CombatSide.Player,
            $"Expected control to return to the player side, found {combatStateAfterEnemyTurn.CurrentSide}.");

        await context.Checkpoint("new-player-round");

        await context.Console(context.P1, "win");
        await context.WaitUntil(
            () => NOverlayStack.Instance?.Peek() is NRewardsScreen,
            "the rewards screen to open after winning");
        context.Expect(
            NOverlayStack.Instance?.Peek() is NRewardsScreen,
            $"Expected the rewards screen to be open after 'win', found {NOverlayStack.Instance?.Peek()?.GetType().Name ?? "none"}.");

        await context.Checkpoint("rewards-open");
        await context.Settle();
    }
}

/// <summary>
/// docs/design/testing-plan.md §6.9, row `choice`: P2 plays Survivor through their HUD, which asks them to discard a
/// card from their hand. A teammate choice is pending for P2 and P1's screen is unaffected (no overlay change, no
/// card-select screen for P1); P2 answers through the HUD and the discarded card comes from P2's hand, not P1's.
/// Guaranteed by <c>CouchTeammateChoicePatch.cs</c> (records the pending choice so <c>CouchTeammateChoices</c> and the
/// HUD know about it — without it, P2's own choice is invisible to the mod even though the underlying game correctly
/// waits for P2's remote answer) and <c>CouchTeammateChoices.cs:155-231</c> (routes the HUD's answer back as P2 over
/// the loopback).
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes for this scenario once the aspect mechanism (WP4, landing in
/// parallel) is available; the runner currently only supports a single fixed 16:9 pass.
/// </summary>
internal sealed class ChoiceScenario : CouchTestScenarioBase
{
    /// <summary>Silent skill, TargetType.Self (src/Core/Models/Cards/Survivor.cs): gain block, then discard 1 card from hand.</summary>
    private const string ChoiceCardId = "SURVIVOR";

    /// <summary>
    /// The known discard target. Not one of Silent's 12 starting-deck cards (5x StrikeSilent, 5x DefendSilent,
    /// Neutralize, Survivor — src/Core/Models/Characters/Silent.cs), so adding it with the console can't collide with
    /// a copy already drawn into the opening hand, and finding it by ID in the choice's options is unambiguous.
    /// </summary>
    private const string DiscardTargetCardId = "SUCKER_PUNCH";

    public override string Name => "choice";

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterRoom(RoomType.Monster);

        await context.Console(context.P2, $"card {ChoiceCardId}");
        await context.Settle();
        await context.Console(context.P2, $"card {DiscardTargetCardId}");
        await context.Settle();

        List<string> p1HandBefore = context.P1.PlayerCombatState!.Hand.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        List<string> p1DiscardBefore = context.P1.PlayerCombatState!.DiscardPile.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        List<string> p2DiscardBefore = context.P2.PlayerCombatState!.DiscardPile.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        IOverlayScreen? overlayTopBefore = NOverlayStack.Instance?.Peek();
        int overlayCountBefore = NOverlayStack.Instance?.ScreenCount ?? 0;

        // Survivor's own OnPlay awaits the discard choice, so it doesn't finish (and the action queue doesn't go
        // idle) until the choice is answered below — Settle() must wait, not run, until after P2AnswerChoiceByCardIdThroughHud.
        await context.P2PlayNoTargetCardThroughHud(ChoiceCardId);

        await context.WaitUntil(
            () => CouchTeammateChoices.Pending.Any((CouchTeammateChoice c) => c.Player == context.P2),
            "a teammate choice to be pending for P2 after Survivor is played");

        CouchTeammateChoice pending = CouchTeammateChoices.Pending.First((CouchTeammateChoice c) => c.Player == context.P2);
        context.Expect(
            pending.MinSelect == 1 && pending.MaxSelect == 1,
            $"Expected Survivor's discard choice to ask for exactly 1 card, found {pending.MinSelect}-{pending.MaxSelect}.");

        context.Expect(
            ReferenceEquals(NOverlayStack.Instance?.Peek(), overlayTopBefore) && (NOverlayStack.Instance?.ScreenCount ?? 0) == overlayCountBefore,
            "Expected P1's overlay stack to be unchanged while P2's teammate choice is pending.");
        context.Expect(
            NPlayerHand.Instance?.IsInCardSelection != true,
            "Expected no card-select screen to have opened for P1 while P2's teammate choice is pending.");

        await context.P2AnswerChoiceByCardIdThroughHud(DiscardTargetCardId);
        await context.Settle();

        context.Expect(
            CouchTeammateChoices.Pending.All((CouchTeammateChoice c) => c.Player != context.P2),
            "Expected no teammate choice to remain pending for P2 after answering.");

        List<string> p2HandAfter = context.P2.PlayerCombatState!.Hand.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        context.Expect(
            !p2HandAfter.Contains(DiscardTargetCardId),
            $"Expected {DiscardTargetCardId} to leave P2's hand, found it still there: [{string.Join(",", p2HandAfter)}].");

        List<string> p2DiscardAfter = context.P2.PlayerCombatState!.DiscardPile.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        context.Expect(
            p2DiscardAfter.Count((string id) => id == DiscardTargetCardId) == p2DiscardBefore.Count((string id) => id == DiscardTargetCardId) + 1,
            $"Expected {DiscardTargetCardId} to land in P2's discard pile: before=[{string.Join(",", p2DiscardBefore)}], after=[{string.Join(",", p2DiscardAfter)}].");

        List<string> p1HandAfter = context.P1.PlayerCombatState!.Hand.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        List<string> p1DiscardAfter = context.P1.PlayerCombatState!.DiscardPile.Cards.Select((CardModel c) => c.Id.Entry).ToList();
        context.Expect(
            p1HandAfter.SequenceEqual(p1HandBefore),
            $"Expected P1's hand to be unaffected by P2's choice: before=[{string.Join(",", p1HandBefore)}], after=[{string.Join(",", p1HandAfter)}].");
        context.Expect(
            p1DiscardAfter.SequenceEqual(p1DiscardBefore),
            $"Expected P1's discard pile to be unaffected by P2's choice: before=[{string.Join(",", p1DiscardBefore)}], after=[{string.Join(",", p1DiscardAfter)}].");

        await context.Checkpoint("p2-answered-choice");
        await context.Settle();
    }
}
#endif

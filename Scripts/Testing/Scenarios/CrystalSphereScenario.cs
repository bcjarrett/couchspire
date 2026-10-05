#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// P2 plays the Crystal Sphere ("divination") event (src/Core/Models/Events/CrystalSphere.cs). The game only opens the
/// minigame for the local player, so P2 used to pay for "Uncover the future" and get no grid and no rewards
/// (playtest report). Now P2's pick opens <see cref="CouchTeammateCrystalSphere"/>: P2 spends the three divinations
/// there, the revealed rewards go to P2's rewards panel, and the event finishes. Also checks the base mod's
/// single-adventure mirroring stays off: P2's cost and finds must not reach P1.
/// </summary>
internal sealed class CrystalSphereScenario : CouchTestScenarioBase
{
    public override string Name => "crystal_sphere";

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterEvent("CRYSTAL_SPHERE");
        await context.Console(context.P2, "gold 200");
        await context.Settle();
        await context.WaitUntil(() => CouchTeammateEvent.IsActive, "P2's event panel to open");

        EventSynchronizer synchronizer = RunManager.Instance.EventSynchronizer;
        EventModel p2Event = synchronizer.GetEventForPlayer(context.P2);
        int cost = (int)p2Event.DynamicVars["UncoverFutureCost"].BaseValue;
        int p1GoldBefore = context.P1.Gold;
        int p1DeckBefore = context.P1.Deck.Cards.Count;
        int p2GoldBefore = context.P2.Gold;

        // P2's cursor starts on option 0, "Uncover the future".
        await context.P2Press(CouchHudCommand.Accept);
        await context.WaitUntil(() => CouchTeammateCrystalSphere.IsActive, "P2's Crystal Sphere panel to open");
        await context.Settle();
        await context.Checkpoint("p2-sphere-open");

        context.Expect(!CouchTeammateEvent.IsActive, "Expected P2's event panel to hide while P2 plays the Crystal Sphere minigame.");
        context.Expect(CouchTeammateEvent.BlocksProceed, "Expected P1 to be kept in the room while P2 plays the minigame.");
        context.Expect(context.P2.Gold == p2GoldBefore - cost, $"Expected P2 to pay {cost} gold: {p2GoldBefore} -> {context.P2.Gold}.");
        context.Expect(context.P1.Gold == p1GoldBefore, $"Expected P1's gold to be untouched by P2's payment: {p1GoldBefore} -> {context.P1.Gold}.");
        context.Expect(CouchTeammateCrystalSphere.DivinationsLeft == 3, $"Expected 3 divinations, found {CouchTeammateCrystalSphere.DivinationsLeft}.");

        // Three big (3×3) divinations at the centre, then left and right of it, so no area overlaps.
        await DivineAsync(context, expectedLeft: 2);
        await context.Checkpoint("p2-sphere-divined");
        await MoveAsync(context, CouchHudCommand.Left, 3);
        await DivineAsync(context, expectedLeft: 1);
        await MoveAsync(context, CouchHudCommand.Right, 6);
        await DivineAsync(context, expectedLeft: 0);

        await context.WaitUntil(() => !CouchTeammateCrystalSphere.IsActive, "P2's Crystal Sphere panel to close once the divinations are spent");
        await context.WaitUntil(() => p2Event.IsFinished || CouchTeammateRewards.IsActive, "P2's rewards or the event's finish page");
        if (CouchTeammateRewards.IsActive)
        {
            await context.Checkpoint("p2-sphere-rewards");
            await context.P2Press(CouchHudCommand.SubmitOrEndTurn);
            await context.Settle();
            await context.P2Press(CouchHudCommand.Accept);
            await context.Settle();
        }

        await context.WaitUntil(() => p2Event.IsFinished, "P2's Crystal Sphere event to finish");
        await context.WaitUntil(() => CouchTeammateEvent.IsActive, "P2's event panel to come back with the finish page");
        await context.P2Press(CouchHudCommand.Accept);
        await context.Settle();

        context.Expect(!CouchTeammateEvent.BlocksProceed, "Expected P1 to be free to leave once P2 is done with the event.");
        context.Expect(context.P1.Gold == p1GoldBefore, $"Expected P1's gold to be untouched by P2's sphere: {p1GoldBefore} -> {context.P1.Gold}.");
        context.Expect(
            context.P1.Deck.Cards.Count == p1DeckBefore,
            $"Expected P1's deck to be untouched by P2's sphere: {p1DeckBefore} -> {context.P1.Deck.Cards.Count} cards.");
    }

    private static async Task MoveAsync(CouchTestContext context, CouchHudCommand direction, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            await context.P2Press(direction);
        }
    }

    private static async Task DivineAsync(CouchTestContext context, int expectedLeft)
    {
        await context.P2Press(CouchHudCommand.Accept);
        await context.WaitUntil(
            () => CouchTeammateCrystalSphere.DivinationsLeft is null || CouchTeammateCrystalSphere.DivinationsLeft == expectedLeft,
            $"P2's divination to land ({expectedLeft} left)");
        await context.Settle();
    }
}
#endif

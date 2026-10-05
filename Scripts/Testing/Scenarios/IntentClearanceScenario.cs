#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Rooms;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// P2's hand stops short of enemy intents (<c>CouchTeammateHud.ClearIntents</c>): a boss fight, whose tall enemy
/// lifts its intent up into P2's card band, with P2's hand filled so it would otherwise fan out across the screen.
/// The layout rules check every mod panel against the intents (<see cref="CouchTestLayout"/>'s EnemyIntent anchors).
/// </summary>
internal sealed class IntentClearanceScenario : CouchTestScenarioBase
{
    /// <summary>Silent skill (src/Core/Models/Cards/Backflip.cs); any card does, it only fills the hand.</summary>
    private const string FillerCardId = "BACKFLIP";

    private const int FullHand = 10;

    public override string Name => "intent_clearance";

    public override IReadOnlyList<string> Aspects { get; } = new[] { "16:9", "16:10" };

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterRoom(RoomType.Boss);
        await context.Settle();

        for (int i = context.P2.PlayerCombatState!.Hand.Cards.Count; i < FullHand; i++)
        {
            await context.Console(context.P2, $"card {FillerCardId}");
        }

        context.Expect(
            context.P2.PlayerCombatState!.Hand.Cards.Count == FullHand,
            $"Expected P2's hand to be full ({FullHand}), found {context.P2.PlayerCombatState!.Hand.Cards.Count}.");

        await context.Settle();
        await context.Checkpoint("full-hand");
    }
}
#endif

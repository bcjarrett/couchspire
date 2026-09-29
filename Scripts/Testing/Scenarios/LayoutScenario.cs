#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Rooms;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// The vehicle scenario for the layout-checking machinery in
/// <see cref="CouchTestLayout"/> (see docs/testing.md) — two checkpoints ("map", before entering combat, and
/// "combat", after), run at both 16:9 and 16:10 so the aspect-pass mechanism has something to exercise. The
/// `combat` scenario adds richer combat checkpoints; this one only needs to prove the layout machinery itself
/// works, at both aspects.
/// </summary>
internal sealed class LayoutScenario : CouchTestScenarioBase
{
    public override string Name => "layout";

    public override IReadOnlyList<string> Aspects { get; } = new[] { "16:9", "16:10" };

    public override async Task RunAsync(CouchTestContext context)
    {
        // Before combat: the map screen. Whatever mod UI is visible here (the teammate's top bar is shown all run)
        // must already be inside the viewport, non-overlapping and on-baseline.
        await context.Checkpoint("map");

        await context.EnterRoom(RoomType.Monster);
        await context.Settle();

        // After combat starts: CouchTeammateHud and CouchTeammateRelicBar's combat layout are up, which is where the
        // anchoring rules (relic bar centered on the HUD status line, HUD band vs. the players list) actually bite.
        await context.Checkpoint("combat");
    }
}
#endif

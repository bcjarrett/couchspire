#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §8 WP4: the vehicle scenario for the layout-checking machinery in
/// <see cref="CouchTestLayout"/> — two checkpoints ("map", before entering combat, and "combat", after), run at both
/// 16:9 and 16:10 so the aspect-pass mechanism (§6.5.3) has something to exercise. The `combat` scenario written in
/// parallel (WP5a) adds richer combat checkpoints later; this one only needs to prove the layout machinery itself
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

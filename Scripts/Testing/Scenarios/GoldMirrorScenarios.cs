#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Rewards;
using CouchSpire.Scripts.Runtime;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// docs/testing.md WP7: guards the gold-mirror suppression leak fixed in commit 06b185a.
///
/// The bug: <c>RelicCmdObtainPatch</c> suppresses gold mirroring for the duration of a relic pickup
/// (<c>GoldMirrorSuppressionContext</c>, an <c>AsyncLocal</c>), so a relic's own on-pickup gold effect isn't
/// double-mirrored. Before the fix, the matching "turn suppression back off" ran inside a nested async method's
/// <c>finally</c> block, whose <c>AsyncLocal</c> write never flowed back to the method that called
/// <c>RelicCmd.Obtain</c> (that's normal, correct <c>AsyncLocal</c>/<c>ExecutionContext</c> behavior — writes made
/// inside a called async method don't leak out to the caller). So suppression stayed stuck "on" for the rest of
/// that caller's own flow, and a later <c>PlayerCmd.GainGold</c> in the *same* flow wrongly skipped mirroring.
///
/// No shipped event, relic, reward or rest-site option chains "obtain a relic" then "gain gold" as two awaits in
/// one method (checked against every source file touching both <c>RelicCmd.Obtain</c> and
/// <c>PlayerCmd.GainGold</c>); every one of them picks either a relic branch or a gold branch, never both in one
/// flow. A relic's own on-pickup gold (e.g. <c>OldCoin</c>) doesn't reproduce it either, because that
/// <c>GainGold</c> call happens nested *inside* <c>RelicCmd.Obtain</c>, before the suppression handoff the bug is
/// about. So this scenario drives <see cref="GoldMirrorFlowConsoleCmd"/>, a test-only console command
/// (<c>goldmirrorflow</c>) that calls the same two patched commands, in the same shape a real flow would, so it
/// exercises the real <c>RelicCmdObtainPatch</c>/<c>PlayerGainGoldMirrorPatch</c> code path rather than a mock.
///
/// Reaching a mirroring context: gold only mirrors in <c>isCombatRewardContext</c> (a <c>CombatRoom</c> with no
/// combat in progress) or the Crystal Sphere event context (<c>PlayerGainGoldMirrorPatch.cs</c>). The combat path
/// is a trap for this scenario: <c>win</c> on a Monster room enters <c>CombatRoomOfferRewardsPatch</c>, which holds
/// <c>CombatRewardMergeContext.IsActive</c> true for the *entire* time the rewards screen is open (it only exits in
/// a <c>finally</c> after the screen's <c>Completed</c> signal, which needs every one of P1's own reward buttons
/// individually claimed/skipped — not just a Proceed click, since <c>RunManager.ProceedFromTerminalRewardsScreen</c>
/// only opens the map and does not clear reward buttons or pop the room). So right after `win` + Settle, and even
/// after the map opens, <c>CombatRewardMergeContext.IsActive</c> is still true and <c>PlayerGainGoldMirrorPatch</c>
/// returns early on it regardless of the bug — a false negative either way.
///
/// This scenario instead uses the Crystal Sphere event (<c>event CRYSTAL_SPHERE</c>), which needs no combat and no
/// reward screen at all: entering an <see cref="MegaCrit.Sts2.Core.Rooms.EventRoom"/> via the debug <c>event</c>
/// command runs <c>EventSynchronizer.BeginEvent</c> synchronously for every player in the run (it loops
/// <c>_playerCollection.Players</c>), so both P1 and P2 immediately have their own <c>CrystalSphere</c> instance;
/// <c>CrystalSphere</c> doesn't override <c>EventModel.IsShared</c> (defaults to <c>false</c>), and its own event
/// options are never touched — the scenario just checks the room state, never the event's own UI. The scenario
/// asserts the precondition explicitly (<see cref="CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext"/> and
/// <c>!CombatRewardMergeContext.IsActive</c>) before running the flow, so a failure to reach a mirroring context at
/// all reads differently from the suppression bug regressing.
///
/// To see this scenario fail without the fix: temporarily revert 06b185a's code change (restore
/// <c>ExitSuppressionWhenCompleteAsync</c> in <c>Scripts/Patch/PlayerGainGoldMirrorPatch.cs</c> and have
/// <c>RelicCmdObtainPatch.Postfix</c> call it again instead of decrementing synchronously), rebuild, and rerun
/// <c>./deploy.sh test gold_mirror</c>. P2's gold assertion below should then fail because suppression is still
/// active when <c>goldmirrorflow</c>'s <c>GainGold</c> call runs. Revert the revert afterwards.
/// </summary>
internal sealed class GoldMirrorScenarios : CouchTestScenarioBase
{
    public override string Name => "gold_mirror";

    public override async Task RunAsync(CouchTestContext context)
    {
        // Jump straight to the Crystal Sphere event. This bypasses IsAllowed (100+ gold, act > 0) the same way the
        // `room`/`event` debug commands always do, and needs no combat or reward screen: BeginEvent gives every
        // player (P1 and P2, since they share one RunState) their own CrystalSphere instance synchronously.
        await context.Console(context.P1, "event CRYSTAL_SPHERE");
        await context.Settle();
        await context.Checkpoint("crystal-sphere-entered");

        // Assert the mirroring precondition explicitly, before running the flow: if this fails, the scenario never
        // reached a context where PlayerGainGoldMirrorPatch would mirror at all, which is a different problem from
        // the suppression handoff regressing below.
        bool reachedMirroringContext =
            !CombatRewardMergeContext.IsActive
            && CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext(context.P1);
        context.Expect(
            reachedMirroringContext,
            "Expected to be in a gold-mirroring context (Crystal Sphere event, not merged-rewards) before running " +
            "goldmirrorflow, found CombatRewardMergeContext.IsActive=" + CombatRewardMergeContext.IsActive +
            ", IsInCrystalSphereEventContext=" + CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext(context.P1) +
            ". The scenario's setup needs fixing, independent of the WP7 bug this scenario guards.");

        int p1GoldBefore = context.P1.Gold;
        int p2GoldBefore = context.P2.Gold;
        const int grantedGold = 77;

        // P1 runs the relic-then-gold flow. If the suppression handoff between RelicCmdObtainPatch and
        // PlayerGainGoldMirrorPatch has regressed, P1's own gold still rises (GainGold always applies to its
        // target), but P2 never gets the mirrored copy. The relic itself (Akabeko) is also mirrored to P2 by
        // RelicCmdObtainPatch, but Akabeko has no on-pickup effect (no AfterObtained override; the base
        // implementation is a no-op) and isn't chain-special-cased, so it can't itself change P2's gold — the only
        // gold this flow grants is the explicit GainGold call below.
        await context.Console(context.P1, $"goldmirrorflow {grantedGold}");
        await context.Settle();
        await context.Checkpoint("post-flow");

        context.Expect(
            context.P1.Gold == p1GoldBefore + grantedGold,
            $"Expected P1's gold to rise by the flow's own amount ({grantedGold}): {p1GoldBefore} -> {context.P1.Gold}.");
        context.Expect(
            context.P2.Gold == p2GoldBefore + grantedGold,
            $"Expected P2's gold to be mirrored by {grantedGold} once the relic-obtain suppression correctly turns " +
            $"back off before the same flow's GainGold call (06b185a): {p2GoldBefore} -> {context.P2.Gold}. If this " +
            "fails (and the precondition above passed), the gold-mirror suppression handoff between " +
            "RelicCmdObtainPatch and PlayerGainGoldMirrorPatch has regressed (a stuck-on AsyncLocal, e.g. an " +
            "async-method finally-block decrement instead of a synchronous one in the postfix).");
    }
}
#endif

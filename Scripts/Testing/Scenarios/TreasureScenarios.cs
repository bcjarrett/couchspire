#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// docs/testing.md, row `treasure`. Entering the room already begins relic picking
/// (<c>TreasureRoom.EnterInternal</c> calls <c>TreasureRoomRelicSynchronizer.BeginRelicPicking</c> immediately, one
/// relic per living player), but the real chest UI only reveals it once P1 opens the chest
/// (<c>NTreasureRoom.OpenChest</c>), which also grants each player their own chest gold
/// (<see cref="CouchTeammateRoomsPatch"/>'s postfix on <c>OneOffSynchronizer.DoLocalTreasureRoomRewards</c> re-sends
/// P2's own <c>TreasureChestOpenedMessage</c>, exactly as an online teammate's client would). Relic picking itself is
/// a vote: for &lt;=4 players, <c>TreasureRoomRelicSynchronizerBeginPatch</c> disables the base game's
/// auto-vote-on-behalf (which would otherwise silently roll a random pick for every non-local player in
/// single-adventure mode), so P2 must vote for themselves in their own panel
/// (<see cref="CouchTeammateTreasure"/>). P1 and P2 pick different relics here, so both resolve as
/// <c>RelicPickingResultType.OnlyOnePlayerVoted</c> instead of a non-deterministic rock-paper-scissors fight over the
/// same one (<see cref="TreasureRoomRelicSynchronizerPatch"/>/<c>TreasureRoomRelicSynchronizer.AwardRelics</c>).
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes here once the aspect mechanism (WP4) lands, as in
/// RewardsScenarios/ShopScenarios/RestSiteScenarios.
/// </summary>
internal sealed class TreasureScenarios : CouchTestScenarioBase
{
    public override string Name => "treasure";

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterRoom(RoomType.Treasure);
        await context.Checkpoint("treasure-open");

        TreasureRoomRelicSynchronizer synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new CouchTestExpectationFailedException("Expected a run state while in the treasure room.");

        // BeginRelicPicking runs at room entry, before the chest UI opens, so the vote/relic state already exists
        // here even though nothing is visible on screen yet.
        context.Expect(
            synchronizer.CurrentRelics != null && synchronizer.CurrentRelics.Count == runState.Players.Count,
            $"Expected one relic per living player ({runState.Players.Count}), found {synchronizer.CurrentRelics?.Count.ToString() ?? "none"}.");

        // Snapshot the relic instances now: TreasureRoomRelicSynchronizer.EndRelicVoting() nulls CurrentRelics once
        // both players have picked, so this is the only safe time to capture which relic each player will go for.
        List<RelicModel> offeredRelics = synchronizer.CurrentRelics!.ToList();
        RelicModel p1TargetRelic = offeredRelics[0];
        RelicModel p2TargetRelic = offeredRelics[1];
        context.Expect(
            !ReferenceEquals(p1TargetRelic, p2TargetRelic),
            "Expected the two offered relics to be distinct instances (one per player).");

        int p1GoldBefore = context.P1.Gold;
        int p2GoldBefore = context.P2.Gold;
        int p1RelicsBefore = context.P1.Relics.Count;
        int p2RelicsBefore = context.P2.Relics.Count;

        // P1 opens the chest through the real UI.
        NTreasureRoom room = context.TreasureRoomNode();
        await context.ClickAsync(context.TreasureChestButton(room));
        await context.WaitUntil(() => context.TreasureRelicCollectionOpen(room), "the treasure chest to open and reveal the relic-picking UI");
        await context.Settle();
        await context.Checkpoint("chest-open");

        // Each player got their own chest gold. P1's amount is read off the particle count the game itself set from
        // the reward call's return value, rather than hardcoding the 42-53 seeded-gold formula.
        int p1GoldReward = context.TreasureChestGoldParticleAmount(room);
        context.Expect(
            p1GoldReward > 0 && context.P1.Gold == p1GoldBefore + p1GoldReward,
            $"Expected P1's gold to rise by exactly the chest's own (displayed) gold amount ({p1GoldReward}): {p1GoldBefore} -> expected {p1GoldBefore + p1GoldReward}, found {context.P1.Gold}.");
        context.Expect(
            context.P2.Gold > p2GoldBefore,
            $"Expected P2 to also get their own chest gold when P1 opens the chest (CouchTeammateRoomsPatch.PostfixTreasureOpened), found {p2GoldBefore} -> {context.P2.Gold}.");

        context.Expect(CouchTeammateTreasure.IsActive, "Expected P2's CouchTeammateTreasure panel to be visible once the relics are on offer.");
        CouchTeammateTreasure teammatePanel = context.TeammateTreasurePanel()
            ?? throw new CouchTestExpectationFailedException("CouchTeammateTreasure.IsActive was true but no panel instance was found.");

        // P1 picks their own relic through the real relic-holder button.
        NTreasureRoomRelicCollection relicCollection = context.TreasureRelicCollection(room);
        NTreasureRoomRelicHolder p1Holder = context.TreasureHolderFor(relicCollection, p1TargetRelic);
        await context.ClickAsync(p1Holder);
        await context.WaitUntil(() => synchronizer.GetPlayerVote(context.P1).voteReceived, "P1's relic vote to register");

        // P2 picks the *other* relic through their own panel: a different index than P1, so both resolve as
        // OnlyOnePlayerVoted instead of a non-deterministic rock-paper-scissors fight over the same relic.
        await context.P2PickTreasureRelic(teammatePanel, p2TargetRelic);
        await context.WaitUntil(() => synchronizer.GetPlayerVote(context.P2).voteReceived, "P2's relic vote to register");

        // Voting is only half the story: TreasureRoomRelicSynchronizer.AwardRelics (run synchronously once the last
        // vote comes in) just fires a RelicsAwarded event; NTreasureRoomRelicCollection.AnimateRelicAwards is what
        // actually calls RelicCmd.Obtain per player, at the end of a real, wall-clock-timed "grab" animation
        // (Cmd.Wait delays outside the action queue, so Settle()'s queue-idle check never sees it). Wait for the
        // relic counts themselves instead of Settle(), or this reads the inventories before the animation grants
        // anything (confirmed on a run: both relics were still un-awarded when Settle() alone said things were idle).
        await context.WaitUntil(
            () => context.P1.Relics.Count > p1RelicsBefore && context.P2.Relics.Count > p2RelicsBefore,
            "both players' picked relics to be awarded (the grab animation to finish)");

        await context.Settle();
        await context.Checkpoint("relics-picked");

        context.Expect(!CouchTeammateTreasure.IsActive, "Expected P2's treasure panel to close once P2 has picked.");

        // Each player ends up with exactly one new relic: their own pick, no duplicates between them.
        context.Expect(
            context.P1.Relics.Count == p1RelicsBefore + 1,
            $"Expected P1's relics to grow by exactly 1, found {p1RelicsBefore} -> {context.P1.Relics.Count}.");
        context.Expect(
            context.P1.Relics.Any((RelicModel r) => r.Id.Entry == p1TargetRelic.Id.Entry),
            $"Expected P1 to have obtained the relic they picked ({p1TargetRelic.Id.Entry}).");
        context.Expect(
            context.P2.Relics.Count == p2RelicsBefore + 1,
            $"Expected P2's relics to grow by exactly 1, found {p2RelicsBefore} -> {context.P2.Relics.Count}.");
        context.Expect(
            context.P2.Relics.Any((RelicModel r) => r.Id.Entry == p2TargetRelic.Id.Entry),
            $"Expected P2 to have obtained the relic they picked ({p2TargetRelic.Id.Entry}).");
        context.Expect(
            !context.P1.Relics.Any((RelicModel r) => r.Id.Entry == p2TargetRelic.Id.Entry),
            $"Expected P1 to NOT also have P2's relic ({p2TargetRelic.Id.Entry}).");
        context.Expect(
            !context.P2.Relics.Any((RelicModel r) => r.Id.Entry == p1TargetRelic.Id.Entry),
            $"Expected P2 to NOT also have P1's relic ({p1TargetRelic.Id.Entry}).");

        // Treasure has no proceed-block like rest_site/shop (nothing in CouchTeammateRoomsPatch gates
        // NTreasureRoom's own Proceed button): both players pick independently, so P1 can leave right away.
        await context.ClickAsync(room.ProceedButton);
        await context.WaitUntil(() => NMapScreen.Instance?.IsOpen ?? false, "the map to open after P1's Proceed once both players have picked");
        await context.Checkpoint("map-open");
    }
}
#endif

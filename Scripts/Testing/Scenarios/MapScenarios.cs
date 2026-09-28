#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §6.9, row `map`. A fresh couch run starts directly on the map
/// (<c>MapRoom.EnterInternal</c> -&gt; <c>NMapRoom._Ready</c> opens <c>NMapScreen</c> and enables travel), so this
/// scenario needs no <c>EnterRoom</c> setup. P1 votes for a node through the real map UI (<c>NMapPoint.OnRelease</c>
/// -&gt; <c>NMapScreen.OnMapPointSelectedLocally</c>, which enqueues a <c>VoteForMapCoordAction</c>). The base game's
/// own <c>MapSelectionSynchronizer.PlayerVotedForMapCoord</c> only moves once every player's vote is in; the mod's
/// <c>MapSelectionSynchronizerPatch</c> postfix auto-fills every other local player's vote with the driver's own
/// destination the moment it lands, then triggers the move itself if the (now-complete) vote set wasn't enough to
/// trigger it already. So P2 never casts a real, separate vote here — the mod's auto-follow is what makes a couch
/// run advance on a single click instead of soft-locking, waiting forever for a second local input that the couch
/// map UI has no route for P2 to give. That auto-follow is exactly what this scenario asserts.
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes here once the aspect mechanism (WP4) lands, as in the other
/// scenarios.
/// </summary>
internal sealed class MapScenarios : CouchTestScenarioBase
{
    public override string Name => "map";

    public override async Task RunAsync(CouchTestContext context)
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new CouchTestExpectationFailedException("Expected a run state at the start of the map scenario.");

        // What `start` leaves a fresh run in: the map room, at the bottom (no coord chosen yet).
        context.Expect(
            runState.CurrentRoom?.RoomType == RoomType.Map,
            $"Expected to start on the map room, found {runState.CurrentRoom?.RoomType.ToString() ?? "none"}.");
        context.Expect(
            runState.CurrentMapCoord == null,
            $"Expected no map coord chosen yet at the start of a fresh run, found {runState.CurrentMapCoord}.");

        await context.WaitUntil(() => NMapScreen.Instance?.IsOpen ?? false, "the map screen to open on a fresh run");
        NMapScreen mapScreen = context.MapScreen();
        context.Expect(mapScreen.IsTravelEnabled, "Expected NMapScreen.Instance.IsTravelEnabled to be true on a fresh run's map room.");
        await context.Checkpoint("map-open");

        int floorBefore = runState.TotalFloor;
        MapCoord targetCoord = context.LeftmostTravelableMapPoint(mapScreen).Point.coord;

        // P1 votes through the real map UI. Retried: seen flaky on a --repeat run (the map screen can still be
        // settling its own reveal animation right after the "map-open" checkpoint, which only waits for the mod's
        // panels to stabilize, not the game's own NMapScreen; a click during that can miss). Re-fetch the point
        // fresh each attempt rather than reusing the node, in case the first click's target got recreated.
        int attempt = 0;
        while (true)
        {
            attempt++;
            NMapPoint targetPoint = context.LeftmostTravelableMapPoint(mapScreen);
            await context.ClickAsync(targetPoint);
            try
            {
                await context.WaitUntil(
                    () => RunManager.Instance.MapSelectionSynchronizer.GetVote(context.P1)?.coord == targetCoord,
                    "P1's map vote to register",
                    TimeSpan.FromSeconds(5));
                break;
            }
            catch (CouchTestExpectationFailedException) when (attempt < 3)
            {
                CouchTestLog.Info($"P1's map click didn't register a vote on attempt {attempt}; retrying.");
            }
        }

        // The mod auto-votes P2 to the same destination and moves as soon as both are in
        // (MapSelectionSynchronizerPatch); P2 never gets, or needs, a separate real vote for this.
        await context.Settle();
        await context.WaitUntil(
            () => runState.CurrentRoom?.RoomType != RoomType.Map,
            "the room to be entered after both players' map votes complete");
        await context.Settle();

        context.Expect(
            runState.TotalFloor == floorBefore + 1,
            $"Expected TotalFloor to rise by 1 after moving to a map node, found {floorBefore} -> {runState.TotalFloor}.");
        context.Expect(
            runState.CurrentMapCoord == targetCoord,
            $"Expected the run to land on the picked coord {targetCoord}, found {runState.CurrentMapCoord}.");
        context.Expect(
            context.P1.RunState.CurrentMapCoord == context.P2.RunState.CurrentMapCoord,
            "Expected P1's and P2's map coord to match (they share one RunState in couch mode; a regression here would mean a mirrored/desynced copy crept in).");
        context.Expect(
            runState.CurrentRoom != null && runState.CurrentRoom.RoomType != RoomType.Map && runState.CurrentRoom.RoomType != RoomType.Unassigned,
            $"Expected a real room to be entered after the move, found {runState.CurrentRoom?.RoomType.ToString() ?? "none"}.");

        await context.Checkpoint("room-entered");
    }
}
#endif

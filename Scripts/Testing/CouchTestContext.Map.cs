#if COUCHSPIRE_TESTS
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// Map-screen helpers for the <c>map</c> scenario (docs/design/testing-plan.md §6.9). A fresh couch run starts
/// directly on the map (<c>MapRoom.EnterInternal</c> -&gt; <c>NMapRoom._Ready</c> opens <see cref="NMapScreen"/> and
/// enables travel). P1 votes for a node through the real map UI (<c>NMapPoint.OnRelease</c> -&gt;
/// <c>NMapScreen.OnMapPointSelectedLocally</c>); the mod's <c>MapSelectionSynchronizerPatch</c> then auto-fills P2's
/// vote with the same destination and triggers the move once both are in. <c>NMapScreen._mapPointDictionary</c> is
/// private, so this reads it via <c>AccessTools</c>, the same way other <c>CouchTestContext.&lt;Area&gt;.cs</c>
/// partials already cross private boundaries (<see cref="CouchTestContext.RequireField"/>, declared in
/// <c>CouchTestContext.Rewards.cs</c>, is reused here).
/// </summary>
internal sealed partial class CouchTestContext
{
    public NMapScreen MapScreen()
    {
        ThrowIfCancelled();
        return NMapScreen.Instance
            ?? throw new CouchTestExpectationFailedException("Expected NMapScreen.Instance to exist.");
    }

    private static Dictionary<MapCoord, NMapPoint> MapPoints(NMapScreen screen)
    {
        return (Dictionary<MapCoord, NMapPoint>)RequireField(
            AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary"), "NMapScreen._mapPointDictionary").GetValue(screen)!;
    }

    /// <summary>
    /// The currently travelable node with the lowest (col, row) coordinate: a stable, deterministic pick that
    /// doesn't depend on the map's random layout or on dictionary enumeration order, unlike picking "the first"
    /// travelable point found.
    /// </summary>
    public NMapPoint LeftmostTravelableMapPoint(NMapScreen screen)
    {
        ThrowIfCancelled();
        return MapPoints(screen).Values
            .Where((NMapPoint point) => point.State == MapPointState.Travelable)
            .OrderBy((NMapPoint point) => point.Point.coord.col)
            .ThenBy((NMapPoint point) => point.Point.coord.row)
            .FirstOrDefault()
            ?? throw new CouchTestExpectationFailedException("Expected at least one travelable map point.");
    }
}
#endif

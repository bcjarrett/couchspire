#if COUCHSPIRE_TESTS
using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Treasure-room helpers for the <c>treasure</c> scenario (docs/testing.md). Relic picking is a
/// two-player vote (<c>TreasureRoomRelicSynchronizer</c>): the chest is opened once, by P1's real UI
/// (<see cref="NTreasureRoom"/>), which generates one relic per living player and each player's own chest gold; P1
/// then picks their own relic through the real relic-holder buttons (<see cref="NTreasureRoomRelicCollection"/>),
/// while P2 picks in their own panel (<see cref="CouchTeammateTreasure"/>). Several fields this file needs
/// (<c>NTreasureRoom._chestButton/_isRelicCollectionOpen/_relicCollection/_goldParticles</c>,
/// <c>NTreasureRoomRelicCollection._holdersInUse</c>, <c>CouchTeammateTreasure._instance/_shownRelics/_cursor</c>)
/// are private, so this reads them via <c>AccessTools</c>, the same way <c>CouchTestContext.Rewards.cs</c> already
/// crosses those boundaries. <see cref="CouchTestContext.RequireField"/> (declared there) is reused here.
/// </summary>
internal sealed partial class CouchTestContext
{
    public NTreasureRoom TreasureRoomNode()
    {
        ThrowIfCancelled();
        return NRun.Instance?.TreasureRoom
            ?? throw new CouchTestExpectationFailedException("Expected NRun.Instance.TreasureRoom to be set while in the treasure room.");
    }

    /// <summary>The real chest button P1 clicks to open the treasure chest.</summary>
    public NClickableControl TreasureChestButton(NTreasureRoom room)
    {
        ThrowIfCancelled();
        return (NClickableControl)RequireField(AccessTools.Field(typeof(NTreasureRoom), "_chestButton"), "NTreasureRoom._chestButton").GetValue(room)!;
    }

    /// <summary>True once the chest has finished opening and the relic-picking UI is up.</summary>
    public bool TreasureRelicCollectionOpen(NTreasureRoom room)
    {
        ThrowIfCancelled();
        return (bool)RequireField(AccessTools.Field(typeof(NTreasureRoom), "_isRelicCollectionOpen"), "NTreasureRoom._isRelicCollectionOpen").GetValue(room)!;
    }

    public NTreasureRoomRelicCollection TreasureRelicCollection(NTreasureRoom room)
    {
        ThrowIfCancelled();
        return (NTreasureRoomRelicCollection)RequireField(AccessTools.Field(typeof(NTreasureRoom), "_relicCollection"), "NTreasureRoom._relicCollection").GetValue(room)!;
    }

    /// <summary>
    /// P1's own chest gold, read from the particle count <c>NTreasureRoom.OpenChest</c> sets from
    /// <c>TreasureRoom.DoNormalRewards()</c>'s own (seeded, per-player) return value, instead of hardcoding the
    /// reward formula (<c>OneOffSynchronizer.DoTreasureRoomRewards</c> rolls <c>PlayerRng.Rewards.NextInt(42, 53)</c>
    /// independently per player). Zero (not emitting) if the chest gave no gold.
    /// </summary>
    public int TreasureChestGoldParticleAmount(NTreasureRoom room)
    {
        ThrowIfCancelled();
        Godot.GpuParticles2D particles = (Godot.GpuParticles2D)RequireField(
            AccessTools.Field(typeof(NTreasureRoom), "_goldParticles"), "NTreasureRoom._goldParticles").GetValue(room)!;
        return particles.Emitting ? particles.Amount : 0;
    }

    private static List<NTreasureRoomRelicHolder> TreasureRelicHolders(NTreasureRoomRelicCollection collection)
    {
        return (List<NTreasureRoomRelicHolder>)RequireField(
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse"), "NTreasureRoomRelicCollection._holdersInUse").GetValue(collection)!;
    }

    /// <summary>
    /// The real relic-holder button showing <paramref name="relic"/>, for <see cref="ClickAsync"/>. Holder order
    /// matches <c>TreasureRoomRelicSynchronizer.CurrentRelics</c> order (<c>InitializeRelics</c> assigns each holder
    /// <c>currentRelics[i]</c> 1:1), so this looks the holder up by relic instance instead of assuming an index.
    /// </summary>
    public NTreasureRoomRelicHolder TreasureHolderFor(NTreasureRoomRelicCollection collection, RelicModel relic)
    {
        ThrowIfCancelled();
        return TreasureRelicHolders(collection).FirstOrDefault((NTreasureRoomRelicHolder holder) => ReferenceEquals(holder.Relic.Model, relic))
            ?? throw new CouchTestExpectationFailedException($"No treasure relic holder found showing {relic.Id.Entry}.");
    }

    private static List<RelicModel> TreasureTeammateShownRelics(CouchTeammateTreasure panel)
    {
        return (List<RelicModel>)RequireField(
            AccessTools.Field(typeof(CouchTeammateTreasure), "_shownRelics"), "CouchTeammateTreasure._shownRelics").GetValue(panel)!;
    }

    private static int TreasureTeammateCursor(CouchTeammateTreasure panel)
    {
        return (int)RequireField(AccessTools.Field(typeof(CouchTeammateTreasure), "_cursor"), "CouchTeammateTreasure._cursor").GetValue(panel)!;
    }

    /// <summary>P2's own treasure panel, or null if it isn't open.</summary>
    public CouchTeammateTreasure? TeammateTreasurePanel()
    {
        ThrowIfCancelled();
        return RequireField(AccessTools.Field(typeof(CouchTeammateTreasure), "_instance"), "CouchTeammateTreasure._instance").GetValue(null) as CouchTeammateTreasure;
    }

    /// <summary>
    /// Moves P2's relic-row cursor onto <paramref name="target"/> with repeated Right presses (rows are the shown
    /// relics in order, then a trailing Skip row; see <see cref="CouchTeammateTreasure"/>), settling after each
    /// press. Bounded so a stuck cursor fails the scenario instead of looping forever.
    /// </summary>
    public async Task P2MoveTreasureCursorTo(CouchTeammateTreasure panel, RelicModel target)
    {
        ThrowIfCancelled();
        List<RelicModel> shown = TreasureTeammateShownRelics(panel);
        int targetIndex = shown.IndexOf(target);
        if (targetIndex < 0)
        {
            throw new CouchTestExpectationFailedException($"Relic {target.Id.Entry} is not part of the teammate's own shown relics.");
        }

        int rowCount = shown.Count + 1; // +1 for the trailing Skip row.
        int guard = rowCount + 2;
        while (TreasureTeammateCursor(panel) != targetIndex)
        {
            if (guard-- <= 0)
            {
                throw new CouchTestExpectationFailedException(
                    $"Could not move the teammate treasure cursor to index {targetIndex} ({target.Id.Entry}); stuck at {TreasureTeammateCursor(panel)}.");
            }

            await P2Press(CouchHudCommand.Right);
            await Settle();
        }
    }

    /// <summary>P2 picks <paramref name="target"/> in their own treasure panel, then settles.</summary>
    public async Task P2PickTreasureRelic(CouchTeammateTreasure panel, RelicModel target)
    {
        ThrowIfCancelled();
        await P2MoveTreasureCursorTo(panel, target);
        await P2Press(CouchHudCommand.Accept);
        await Settle();
    }
}
#endif

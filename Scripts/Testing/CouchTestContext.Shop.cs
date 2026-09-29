#if COUCHSPIRE_TESTS
using System.Reflection;
using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Shop-area helpers for the <c>shop</c> scenario (docs/testing.md). Each player has their own
/// <see cref="MerchantInventory"/> (<see cref="LocalMerchantInventoryRuntime"/>/<see cref="NMerchantInventoryPatch"/>);
/// P2 buys from theirs through <see cref="CouchTeammateShop"/>'s own panel, and the driver (P1) can't leave the room
/// until P2 is done (<c>CouchTeammateRoomsPatch.PrefixMerchantLeave</c>). <see cref="CouchTeammateShop"/>'s
/// <c>_instance</c>, <c>_cursor</c> and <c>_busy</c> fields are private, so this reads them via <c>AccessTools</c> the
/// same way the mod's own reward/rest-site test helpers already cross those boundaries.
/// </summary>
internal sealed partial class CouchTestContext
{
    // Looked up on use, not in static initializers: a static initializer that throws would break every scenario
    // (this is a partial of the shared context), while this fails only the scenario that needs the field. Keep the
    // names literal so Layer A (Tests/CouchSpire.Tests) checks them offline.
    private static FieldInfo RequireShopField(FieldInfo? field, string what)
    {
        return field ?? throw new CouchTestExpectationFailedException($"API drift: {what} not found (run dotnet test Tests/CouchSpire.Tests).");
    }

    /// <summary>The real merchant room node, for its real Proceed/leave button.</summary>
    public NMerchantRoom MerchantRoomNode()
    {
        ThrowIfCancelled();
        return NMerchantRoom.Instance
            ?? throw new CouchTestExpectationFailedException("Expected NMerchantRoom.Instance to exist while in the shop.");
    }

    /// <summary>The real Proceed/leave button on the merchant room, for <see cref="CouchTestContext.ClickAsync"/>.</summary>
    public NClickableControl MerchantProceedButton()
    {
        ThrowIfCancelled();
        return MerchantRoomNode().ProceedButton;
    }

    /// <summary><paramref name="player"/>'s own <see cref="MerchantInventory"/> for the current shop room.</summary>
    public MerchantInventory PlayerShopInventory(Player player)
    {
        ThrowIfCancelled();
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new CouchTestExpectationFailedException("No run state while reading a shop inventory.");
        if (runState.CurrentRoom is not MerchantRoom merchantRoom)
        {
            throw new CouchTestExpectationFailedException($"Expected the current room to be a shop, found {runState.CurrentRoom?.RoomType.ToString() ?? "none"}.");
        }

        int slot = runState.GetPlayerSlotIndex(player);
        if (slot < 0 || slot >= merchantRoom.Inventories.Count)
        {
            throw new CouchTestExpectationFailedException(
                $"Player {player.NetId}'s shop slot index ({slot}) is out of range (inventoryCount={merchantRoom.Inventories.Count}).");
        }

        return merchantRoom.Inventories[slot];
    }

    /// <summary>
    /// The panel's own row order (character cards, colorless cards, relics, potions, then card removal if present) —
    /// exactly <c>CouchTeammateShop.Entries</c>, built here from <see cref="MerchantInventory"/>'s public lists so a
    /// scenario can pick a purchase target by a stable row order without reflecting into the panel itself.
    /// </summary>
    public IReadOnlyList<MerchantEntry> ShopPanelOrder(MerchantInventory inventory)
    {
        ThrowIfCancelled();
        return CouchTeammateShop.Entries(inventory);
    }

    /// <summary>P2's teammate shop panel, or null if it isn't open (e.g. P2 already finished shopping).</summary>
    public CouchTeammateShop? TeammateShopPanel()
    {
        ThrowIfCancelled();
        return RequireShopField(AccessTools.Field(typeof(CouchTeammateShop), "_instance"), "CouchTeammateShop._instance").GetValue(null) as CouchTeammateShop;
    }

    private int TeammateShopCursor(CouchTeammateShop panel)
    {
        return (int)RequireShopField(AccessTools.Field(typeof(CouchTeammateShop), "_cursor"), "CouchTeammateShop._cursor").GetValue(panel)!;
    }

    private bool TeammateShopBusy(CouchTeammateShop panel)
    {
        return (bool)RequireShopField(AccessTools.Field(typeof(CouchTeammateShop), "_busy"), "CouchTeammateShop._busy").GetValue(panel)!;
    }

    /// <summary>
    /// Moves P2's shop-row cursor onto <paramref name="targetIndex"/> (an index into <see cref="ShopPanelOrder"/>,
    /// or the entry count itself for the trailing "Done shopping" row) with repeated Right presses, settling after
    /// each. Bounded so a stuck cursor fails the scenario instead of looping forever.
    /// </summary>
    public async Task P2MoveShopCursorTo(CouchTeammateShop panel, int targetIndex)
    {
        ThrowIfCancelled();
        int rowCount = ShopPanelOrder(PlayerShopInventory(P2)).Count + 1; // + the trailing "Done shopping" row.
        int guard = rowCount + 2;
        while (TeammateShopCursor(panel) != targetIndex)
        {
            if (guard-- <= 0)
            {
                throw new CouchTestExpectationFailedException(
                    $"Could not move P2's shop cursor to row {targetIndex}; stuck at {TeammateShopCursor(panel)}.");
            }

            await P2Press(CouchHudCommand.Right);
            await Settle();
        }
    }

    /// <summary>
    /// Buys the shop row at <paramref name="targetIndex"/>: moves the cursor there (asserting it landed before
    /// pressing Accept), presses Accept, then waits for the panel's own purchase task
    /// (<c>CouchTeammateShop.BuyAsync</c>, fire-and-forget behind <c>_busy</c>) to finish.
    /// </summary>
    public async Task P2BuyShopEntryAt(CouchTeammateShop panel, int targetIndex)
    {
        ThrowIfCancelled();
        await P2MoveShopCursorTo(panel, targetIndex);
        Expect(
            TeammateShopCursor(panel) == targetIndex,
            $"Expected P2's shop cursor to land on row {targetIndex} before buying, found {TeammateShopCursor(panel)}.");

        await P2Press(CouchHudCommand.Accept);
        await WaitUntil(() => !TeammateShopBusy(panel), "P2's shop purchase to finish");
        await Settle();
    }

    /// <summary>Moves to the trailing "Done shopping" row and presses Accept, exactly as P2's pad/keyboard would.</summary>
    public async Task P2FinishShopping(CouchTeammateShop panel)
    {
        ThrowIfCancelled();
        int doneRowIndex = ShopPanelOrder(PlayerShopInventory(P2)).Count;
        await P2MoveShopCursorTo(panel, doneRowIndex);
        await P2Press(CouchHudCommand.Accept);
        await Settle();
    }
}
#endif

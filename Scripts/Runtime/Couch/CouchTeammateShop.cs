using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's own shop. Every player has their own merchant inventory (<see cref="MerchantRoom.Inventories"/>); the
/// teammate buys from theirs here. Cards, relics and potions are bought with the entry's own purchase logic, which
/// applies to the entry's player. Card removal goes through <see cref="OneOffSynchronizer"/>'s remote-player removal (the
/// entry's own path always removes from the local player's deck) and opens the teammate's card picker. The driver
/// can't leave until the teammate is done shopping.
/// </summary>
internal sealed partial class CouchTeammateShop : CouchPanel
{
    private const float CardScale = 0.32f;

    private static readonly MethodInfo? DoMerchantCardRemovalMethod =
        AccessTools.Method(typeof(OneOffSynchronizer), "DoMerchantCardRemoval", new[] { typeof(Player), typeof(int), typeof(bool) });

    private static readonly MethodInfo? ClearAfterPurchaseMethod =
        AccessTools.Method(typeof(MerchantCardRemovalEntry), "ClearAfterPurchase");

    private static CouchTeammateShop? _instance;

    /// <summary>The shop room the teammate said they're done with.</summary>
    private static MerchantRoom? _doneRoom;

    private readonly List<RowView> _rows = new();

    private List<MerchantEntry> _entries = new();

    private MerchantInventory? _inventory;

    private Player? _teammate;

    private NCard? _preview;

    private CardModel? _previewCard;

    private bool _busy;

    private int _cursor;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    public static bool BlocksProceed => IsActive;

    /// <summary>Lives in the shared P2 column (<see cref="CouchPanel.ColumnWidth"/>).</summary>
    protected override float PanelWidth => ColumnWidth;

    protected override float RowIconSize => 26f;

    protected override int RowFontSize => 16;

    /// <summary>The shop lists a lot; rows are tight so it fits under the relic bar.</summary>
    protected override float RowPadding => 6f;

    protected override float RowSpacing => 3f;

    public static void NotifyProceedBlocked()
    {
        if (IsActive)
        {
            AnnounceWaiting(_instance!._teammate!, "shopping");
            _instance.Flash("The other player is waiting for you");
        }
    }

    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        if (!IsActive || (playerId.HasValue && _instance!._teammate?.NetId != playerId.Value))
        {
            return false;
        }

        _instance!.LastInputFromController = playerId.HasValue;
        _instance.OnCommand(command);
        return true;
    }

    public override void _Ready()
    {
        base._Ready();
        _instance = this;
    }

    public override void _ExitTree()
    {
        SetPreview(null);
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        MerchantInventory? inventory = TeammateInventory();
        if (inventory == null)
        {
            Visible = false;
            SetPreview(null);
            return;
        }

        // The card picker (card removal) opens on top of the shop; both live in the same column, so let it take over.
        if (CouchTeammateChoicePanel.IsActiveFor(_teammate!.NetId))
        {
            Visible = false;
            return;
        }

        Visible = true;
        List<MerchantEntry> entries = Entries(inventory);
        if (inventory != _inventory || !entries.SequenceEqual(_entries))
        {
            Rebuild(inventory, entries);
        }

        PlaceInColumn(top: 110f, margin: 10f);
        UpdatePotionDiscard();
        foreach (RowView row in _rows)
        {
            row.Root.Visible = !IsDiscardingPotion;
        }

        if (IsDiscardingPotion)
        {
            SetPreview(null);
            SetTitle($"{SeatLabel(_teammate!)} · Potion belt full");
            FinishLayout(LayoutPotionDiscard(ContentTop), "Discard one to make room", $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} choose · {Keys("K", "B")} back");
            return;
        }

        SetTitle($"{SeatLabel(_teammate!)} · Shop    Gold {_teammate!.Gold}");
        float y = ContentTop;

        MerchantEntry? current = _cursor < _entries.Count ? _entries[_cursor] : null;
        SetPreview((current as MerchantCardEntry)?.CreationResult?.Card);
        if (_preview != null)
        {
            Vector2 cardSize = NCard.defaultSize * CardScale;
            _preview.Position = new Vector2(PanelWidth * 0.5f - 10f, y + cardSize.Y * 0.5f + 4f);
            y += cardSize.Y + 14f;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            bool isDoneRow = i >= _entries.Count;
            MerchantEntry? entry = isDoneRow ? null : _entries[i];
            _rows[i].Label.Text = isDoneRow ? "Done shopping" : EntryText(entry!);
            bool available = entry == null || (entry.IsStocked && entry.EnoughGold && !IsUsedRemoval(entry));
            StyleRow(_rows[i], i == _cursor, dimmed: !available || _busy);
        }

        string keys = $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} buy";
        // Fold priority (senior review): the description is shown in full, wrapped, unless even the row list
        // collapsed to just the cursor row wouldn't leave room for it above the floor - only then does it shrink.
        string about = ShrinkFooterToFit(AboutText(current), keys, y, ColumnMaxY, MinRowHeightEstimate);
        float footer = MeasureFooterHeight(about, keys);
        y = LayoutRowsScrolled(_rows, y, RowsBudgetMaxY(y) - footer, _cursor);
        FinishLayout(y, about, keys);
    }

    private MerchantInventory? TeammateInventory()
    {
        _teammate = CouchTeammate.FindTeammate();
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (_teammate == null || runState?.CurrentRoom is not MerchantRoom room || NMerchantRoom.Instance == null || room == _doneRoom)
        {
            return null;
        }

        int slot = runState.GetPlayerSlotIndex(_teammate);
        return slot >= 0 && slot < room.Inventories.Count ? room.Inventories[slot] : null;
    }

    /// <summary>The panel's row order (the test runner reads it too, so the two can't drift).</summary>
    internal static List<MerchantEntry> Entries(MerchantInventory inventory)
    {
        List<MerchantEntry> entries = inventory.CharacterCardEntries.Cast<MerchantEntry>()
            .Concat(inventory.ColorlessCardEntries)
            .Concat(inventory.RelicEntries)
            .Concat(inventory.PotionEntries)
            .ToList();
        if (inventory.CardRemovalEntry != null)
        {
            entries.Add(inventory.CardRemovalEntry);
        }

        return entries;
    }

    private void Rebuild(MerchantInventory inventory, List<MerchantEntry> entries)
    {
        FreeRows(_rows);
        _inventory = inventory;
        _entries = entries;
        foreach (MerchantEntry entry in entries)
        {
            _rows.Add(CreateRow(IconFor(entry)));
        }

        _rows.Add(CreateRow(null));
        _cursor = Mathf.Clamp(_cursor, 0, _rows.Count - 1);
    }

    private void OnCommand(CouchHudCommand command)
    {
        if (IsDiscardingPotion)
        {
            OnPotionDiscardCommand(command);
            return;
        }

        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                _cursor = (_cursor - 1 + _rows.Count) % _rows.Count;
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _cursor = (_cursor + 1) % _rows.Count;
                break;
            case CouchHudCommand.Accept:
                if (_busy)
                {
                    return;
                }

                if (_cursor >= _entries.Count)
                {
                    _doneRoom = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom as MerchantRoom;
                    CouchLog.Info($"Teammate {_teammate?.NetId} is done shopping.");
                    return;
                }

                TaskHelper.RunSafely(BuyAsync(_entries[_cursor]));
                break;
        }
    }

    private async Task BuyAsync(MerchantEntry entry)
    {
        if (!entry.IsStocked || IsUsedRemoval(entry))
        {
            Flash("Sold out");
            return;
        }

        if (!entry.EnoughGold)
        {
            CouchSfx.MerchantNo();
            Flash("Not enough gold", denied: false);
            return;
        }

        // Belt full: let the teammate throw out a potion first, then buy.
        if (entry is MerchantPotionEntry && _teammate != null && !_teammate.HasOpenPotionSlots)
        {
            OpenPotionDiscard(_teammate, () => TaskHelper.RunSafely(BuyAsync(entry)));
            return;
        }

        _busy = true;
        PurchaseStatus? failure = null;
        void OnFailed(PurchaseStatus status) => failure = status;
        entry.PurchaseFailed += OnFailed;
        try
        {
            string name = EntryName(entry);
            bool bought = entry is MerchantCardRemovalEntry removal
                ? await RemoveCardAsync(removal)
                : await entry.OnTryPurchaseWrapper(_inventory);
            CouchLog.Info($"Teammate {_teammate?.NetId} {(bought ? "bought" : "didn't buy")} {name}.");
            if (bought)
            {
                CouchSfx.MerchantThanks();
            }
            else if (failure.HasValue)
            {
                CouchSfx.MerchantNo();
            }

            Flash(bought ? $"Bought {name}" : failure switch
            {
                PurchaseStatus.FailureGold => "Not enough gold",
                PurchaseStatus.FailureSpace => "No room for that (potion slots full?)",
                PurchaseStatus.FailureOutOfStock => "Sold out",
                PurchaseStatus.FailureForbidden => "Can't buy that",
                _ => "Didn't buy it"
            }, denied: false);
        }
        catch (Exception ex)
        {
            CouchLog.Warn($"Teammate purchase failed: {ex.Message}");
            Flash("Something went wrong");
        }
        finally
        {
            entry.PurchaseFailed -= OnFailed;
            _busy = false;
        }
    }

    /// <summary>
    /// The shop's card removal for the teammate: what the game runs when a remote player's removal message arrives
    /// (pick a card, pay, remove), then the entry is marked used as its own purchase path would.
    /// </summary>
    private async Task<bool> RemoveCardAsync(MerchantCardRemovalEntry removal)
    {
        if (DoMerchantCardRemovalMethod == null || _teammate == null)
        {
            return false;
        }

        bool removed = await (Task<bool>)DoMerchantCardRemovalMethod.Invoke(RunManager.Instance.OneOffSynchronizer, new object[] { _teammate, removal.Cost, true })!;
        if (removed)
        {
            ClearAfterPurchaseMethod?.Invoke(removal, null);
            removal.InvokePurchaseCompleted(removal);
        }

        return removed;
    }

    private void SetPreview(CardModel? card)
    {
        if (card == _previewCard)
        {
            return;
        }

        CouchCards.Free(_preview);
        _preview = null;
        _previewCard = card;
        if (card == null)
        {
            return;
        }

        _preview = CouchCards.Create(card, this);
        _preview.Scale = new Vector2(CardScale, CardScale);
        _preview.ZIndex = 3;
    }

    private static bool IsUsedRemoval(MerchantEntry entry)
    {
        return entry is MerchantCardRemovalEntry removal && removal.Used;
    }

    private static string EntryText(MerchantEntry entry)
    {
        string status = IsUsedRemoval(entry) || !entry.IsStocked ? "sold" : $"{entry.Cost} gold";
        return $"{EntryName(entry)} — {status}";
    }

    private static string EntryName(MerchantEntry entry)
    {
        return entry switch
        {
            MerchantCardEntry card => card.CreationResult?.Card.Title ?? "Card",
            MerchantRelicEntry relic => relic.Model != null ? CouchText.Plain(relic.Model.Title.GetFormattedText()) : "Relic",
            MerchantPotionEntry potion => potion.Model != null ? CouchText.Plain(potion.Model.Title.GetFormattedText()) : "Potion",
            MerchantCardRemovalEntry => "Remove a card from your deck",
            _ => entry.GetType().Name
        };
    }

    private static string AboutText(MerchantEntry? entry)
    {
        return entry switch
        {
            MerchantRelicEntry { Model: not null } relic => CouchText.Plain(relic.Model.DynamicDescription.GetFormattedText()),
            MerchantPotionEntry { Model: not null } potion => CouchText.Plain(potion.Model.DynamicDescription.GetFormattedText()),
            _ => ""
        };
    }

    private static Texture2D? IconFor(MerchantEntry entry)
    {
        try
        {
            return entry switch
            {
                MerchantRelicEntry { Model: not null } relic => relic.Model.Icon,
                MerchantPotionEntry { Model: not null } potion => potion.Model.Image,
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}

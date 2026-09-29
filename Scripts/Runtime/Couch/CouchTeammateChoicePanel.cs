using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's card picker for choices outside combat: picking cards from their deck to upgrade, transform, remove or
/// enchant (rest site smith, shop removal, events, relics), picking from a grid of reward cards, or picking a pack. Every
/// source this panel handles is out of combat (<c>CouchTeammateChoicePatch</c> only tags a choice <c>IsCombatChoice =
/// false</c> outside combat, and <see cref="Pending"/> below filters combat choices out anyway), so it always lives in
/// the shared P2 column (<see cref="CouchPanel.PlaceInColumn"/>), never the driver-blocking side used mid-fight.
/// The game waits for these as the teammate's remote choice; this panel answers through <see cref="CouchTeammateChoices"/>.
/// Options are a vertical list of card names (<see cref="CouchPanel.LayoutRowsScrolled"/>) with one focused preview above
/// it, rather than a horizontal strip of cards: that only fit in the column's narrower width once. For upgrade picks
/// (Smith and the like) the preview is the card before and after upgrading, side by side with a right-pointing arrow
/// between (<see cref="LayoutUpgradePreview"/>) - side by side rather than stacked specifically so the preview can stay
/// at a readable scale within the column's live floor (a stacked pair needs roughly twice the vertical room).
/// </summary>
internal sealed partial class CouchTeammateChoicePanel : CouchPanel
{
    /// <summary>
    /// The preview's normal, readable scale (senior review: individual cards were "tiny, about the old size" at the
    /// dynamic 0.22-0.5 range this replaces). <see cref="ChooseCardScale"/> uses this unless even the row list
    /// collapsed to just the cursor row wouldn't leave room for it - fold priority puts rows first, the preview
    /// last, not the other way around. An upgrade pick's before/after pair is laid out <b>side by side</b>, not
    /// stacked (<see cref="LayoutUpgradePreview"/>): two 300x422 cards (<see cref="NCard.defaultSize"/>) side by
    /// side need roughly half the vertical room that stacking them does, which is what makes a readable scale reach
    /// even in the column's typical floor-constrained budget - stacked, this same scale would need about twice the
    /// preview's own height in room, on top of the row list.
    /// </summary>
    private const float ReadableCardScale = 0.46f;

    /// <summary>Absolute last-resort floor below <see cref="ReadableCardScale"/>, only reached if the column floor
    /// is so tight that even the cursor row can't fit next to a readable preview.</summary>
    private const float MinPreviewScale = 0.30f;

    /// <summary>Gap between the before/after cards in the side-by-side upgrade preview.</summary>
    private const float SideBySideGap = 10f;

    /// <summary>This choice's current preview scale, recomputed once per <see cref="Layout"/> call.</summary>
    private float _cardScale = ReadableCardScale;

    private const string UpgradeArrowPath = "res://images/ui/cards/upgrade_preview/upgrade_arrow.png";

    /// <summary>Source tag of deck upgrade picks (Smith and the like), which get a before/after preview.</summary>
    private const string UpgradeSource = "deck upgrade";

    private static CouchTeammateChoicePanel? _instance;

    /// <summary>One row per offered card (<see cref="CouchTeammateChoice.Options"/>), in the same order.</summary>
    private readonly List<RowView> _rows = new();

    private readonly List<RowView> _extraRows = new();

    private readonly HashSet<int> _picked = new();

    private CouchTeammateChoice? _choice;

    /// <summary>Upgraded copies for the preview, made once per card while this choice is up.</summary>
    private readonly Dictionary<CardModel, CardModel> _upgradedCopies = new();

    private NCard? _focusedPreview;

    private CardModel? _focusedFor;

    private NCard? _before;

    private NCard? _after;

    private TextureRect? _downArrow;

    private CardModel? _previewFor;

    private int _cursor;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible && _instance._choice != null;

    /// <summary>True while this panel is showing a choice for <paramref name="playerId"/> — used by rest site/shop to
    /// step aside instead of stacking under it in the shared column while the teammate picks a card.</summary>
    public static bool IsActiveFor(ulong playerId) => IsActive && _instance!._choice!.Player.NetId == playerId;

    protected override float PanelWidth => ColumnWidth;

    /// <summary>Rows are plain card/option names with no icon (<see cref="CreateRow"/> is always called with
    /// <c>null</c> here), so the base class's icon-sized row floor is wasted height; shrink it to fit more of the
    /// column above the preview and P1's Proceed button.</summary>
    protected override float RowIconSize => 0f;

    protected override float RowPadding => 10f;

    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        if (!IsActive || (playerId.HasValue && _instance!._choice!.Player.NetId != playerId.Value))
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
        Show(null);
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        CouchTeammateChoice? choice = CouchTeammateChoices.Pending.FirstOrDefault((CouchTeammateChoice c) =>
            !c.IsCombatChoice && c.Source != CouchTeammateRewards.CardRewardSource);
        if (choice != _choice)
        {
            Show(choice);
        }

        if (choice == null)
        {
            Visible = false;
            return;
        }

        Visible = true;
        PlaceInColumn(top: 130f);
        Layout(choice);
    }

    private void Show(CouchTeammateChoice? choice)
    {
        FreeRows(_rows);
        FreeRows(_extraRows);
        SetFocusedPreview(null);
        SetUpgradePreview(null);
        _upgradedCopies.Clear();
        _picked.Clear();
        _cursor = 0;
        _choice = choice;
        if (choice == null)
        {
            return;
        }

        foreach (CardModel card in choice.Options)
        {
            RowView row = CreateRow(null);
            row.Label.Text = card.Title;
            _rows.Add(row);
        }

        foreach (string extra in choice.ExtraOptions)
        {
            RowView row = CreateRow(null);
            row.Label.Text = extra;
            _extraRows.Add(row);
        }

        CouchLog.Info($"Teammate {choice.Player.NetId} picks in their card picker: {choice.Describe()}");
    }

    private void Layout(CouchTeammateChoice choice)
    {
        SetTitle($"{SeatLabel(choice.Player)} · {choice.Prompt}");
        bool isUpgrade = choice.Source == UpgradeSource;
        CardModel? focused = _cursor < choice.Options.Count ? choice.Options[_cursor] : null;

        for (int i = 0; i < _rows.Count; i++)
        {
            StyleRow(_rows[i], i == _cursor, dimmed: false, picked: _picked.Contains(i));
        }

        for (int i = 0; i < _extraRows.Count; i++)
        {
            StyleRow(_extraRows[i], _rows.Count + i == _cursor, dimmed: false);
        }

        // The hint/counter text is fully known before layout (it only depends on cursor/pick state above), so the
        // footer's real height can be measured and reserved before the preview and rows claim their space (fold-
        // priority rule: title, cursor row and hint are never hidden).
        string counter = _rows.Count > 0 && _cursor < _rows.Count ? $"Card {_cursor + 1} of {_rows.Count}" : "";
        string picks = choice.MaxSelect > 1 ? $"{_picked.Count} picked ({choice.MinSelect}-{choice.MaxSelect})" : "";
        string keys = choice.MaxSelect > 1
            ? $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} toggle · {Keys("O", "Y")} confirm · {Keys("K", "B")} {(choice.CanClose || choice.MinSelect == 0 ? "cancel" : "clear")}"
            : $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick{(choice.CanClose || choice.MinSelect == 0 ? $" · {Keys("K", "B")} cancel" : "")}";
        string line1 = string.Join("    ", new[] { counter, picks }.Where((string s) => s.Length > 0));
        float footer = MeasureFooterHeight(line1, keys);
        float budgetBottom = RowsBudgetMaxY(ContentTop) - footer;

        // Fold priority (senior review): the preview stays at its normal, readable scale; only the row list gives
        // up space (scrolling down to just the cursor row) when the column is tight. The preview shrinks below that
        // only as a last resort, if even the cursor row alone wouldn't otherwise fit beside it.
        _cardScale = ChooseCardScale(isUpgrade, ContentTop, budgetBottom);
        float y = isUpgrade ? LayoutUpgradePreview(focused, ContentTop) : LayoutFocusedPreview(focused, ContentTop);

        List<RowView> allRows = new(_rows.Count + _extraRows.Count);
        allRows.AddRange(_rows);
        allRows.AddRange(_extraRows);
        y = LayoutRowsScrolled(allRows, y, budgetBottom, _cursor);

        FinishLayout(y, line1, keys);
    }

    /// <summary><see cref="ReadableCardScale"/>, unless even the cursor row wouldn't fit beside it in
    /// <paramref name="budgetBottom"/> (the row list's own floor, already net of the footer) - then the largest
    /// scale down to <see cref="MinPreviewScale"/> that does. Mirrors the heights
    /// <see cref="LayoutFocusedPreview"/>/<see cref="LayoutUpgradePreview"/> actually produce.</summary>
    private static float ChooseCardScale(bool isUpgrade, float y, float budgetBottom)
    {
        float scale = ReadableCardScale;
        while (scale > MinPreviewScale)
        {
            float previewHeight = isUpgrade ? UpgradePreviewHeight(scale) : FocusedPreviewHeight(scale);
            if (y + previewHeight + MinRowHeightEstimate <= budgetBottom)
            {
                return scale;
            }

            scale -= 0.02f;
        }

        return MinPreviewScale;
    }

    private static float FocusedPreviewHeight(float scale) => NCard.defaultSize.Y * scale + 14f;

    /// <summary>Side by side (see <see cref="LayoutUpgradePreview"/>), so the preview's total height is just one
    /// card's height plus a small pad - not two stacked - which is what lets <see cref="ReadableCardScale"/> fit.</summary>
    private static float UpgradePreviewHeight(float scale) => NCard.defaultSize.Y * scale + 14f;

    /// <summary>The card under the cursor, alone, for picks that aren't an upgrade (transform, remove, enchant, reward grid...).</summary>
    private float LayoutFocusedPreview(CardModel? card, float y)
    {
        SetUpgradePreview(null);
        SetFocusedPreview(card);
        if (_focusedPreview == null)
        {
            return y;
        }

        _focusedPreview.Scale = new Vector2(_cardScale, _cardScale);
        Vector2 cardSize = NCard.defaultSize * _cardScale;
        _focusedPreview.Position = new Vector2(PanelWidth * 0.5f - 10f, y + cardSize.Y * 0.5f + 4f);
        return y + cardSize.Y + 14f;
    }

    /// <summary>
    /// For upgrade picks, the card under the cursor before and after upgrading, side by side with a right-pointing
    /// arrow between, as the driver's smith screen (<c>NUpgradePreview</c>) originally laid it out before the panel
    /// briefly stacked them vertically to fit a narrower column. Side by side needs only one card's height of room
    /// (see <see cref="UpgradePreviewHeight"/>), which is what lets <see cref="ReadableCardScale"/> actually fit
    /// within the column's live floor (senior review regression: stacked, the same scale needed roughly twice the
    /// vertical room and was unreachable in the typical floor-constrained budget).
    /// </summary>
    private float LayoutUpgradePreview(CardModel? card, float y)
    {
        SetFocusedPreview(null);
        SetUpgradePreview(card);
        if (_before == null || _after == null)
        {
            return y;
        }

        _before.Scale = new Vector2(_cardScale, _cardScale);
        _after.Scale = new Vector2(_cardScale, _cardScale);
        Vector2 cardSize = NCard.defaultSize * _cardScale;

        // NCard.Position is its rendered footprint's CENTER, not a left edge (senior review round 4, settled by
        // decompiled source, not just pixels this time): card holders set CardNode.Position = Vector2.Zero at the
        // holder's own center (NCardHolder.ConnectSignals, NGridCardHolder.OnReturnedFromPool), which only makes
        // sense if NCard draws its art centered on its own origin - i.e. from -size/2 to +size/2 in its own local
        // space, PivotOffset notwithstanding (PivotOffset affects rotation/scale pivoting, not where the art itself
        // sits relative to Position). Round 3's "left edge" conclusion rested on a test rule
        // (NCardGlobalBounds) that made the same wrong top-left assumption, so it happened to agree with the buggy
        // placement instead of catching it; both are fixed together here. Both cards are centered as a pair on the
        // panel's own horizontal middle, giving equal side margins.
        float centerX = PanelWidth * 0.5f;
        float half = cardSize.X * 0.5f + SideBySideGap * 0.5f;
        float leftCenterX = centerX - half;
        float rightCenterX = centerX + half;
        float centerY = y + cardSize.Y * 0.5f;
        _before.Position = new Vector2(leftCenterX, centerY);
        _after.Position = new Vector2(rightCenterX, centerY);
        if (_downArrow != null)
        {
            _downArrow.Position = new Vector2(centerX - 11f, centerY - 11f);
        }

        return y + cardSize.Y + 14f;
    }

    private void SetFocusedPreview(CardModel? card)
    {
        if (card == _focusedFor)
        {
            return;
        }

        CouchCards.Free(_focusedPreview);
        _focusedPreview = null;
        _focusedFor = card;
        if (card == null)
        {
            return;
        }

        _focusedPreview = CouchCards.Create(card, this);
        _focusedPreview.Scale = new Vector2(_cardScale, _cardScale);
        _focusedPreview.ZIndex = 3;
    }

    private void SetUpgradePreview(CardModel? card)
    {
        if (card == _previewFor)
        {
            return;
        }

        CouchCards.Free(_before);
        CouchCards.Free(_after);
        _before = null;
        _after = null;
        _downArrow?.QueueFree();
        _downArrow = null;
        _previewFor = card;
        if (card == null || card.CardScope == null)
        {
            return;
        }

        if (!_upgradedCopies.TryGetValue(card, out CardModel? upgraded))
        {
            upgraded = card.CardScope.CloneCard(card);
            upgraded.UpgradeInternal();
            upgraded.UpgradePreviewType = CardUpgradePreviewType.Deck;
            _upgradedCopies[card] = upgraded;
        }

        _before = CouchCards.Create(card, this);
        _after = CouchCards.Create(upgraded, this);
        _after.ShowUpgradePreview();
        foreach (NCard node in new[] { _before, _after })
        {
            node.Scale = new Vector2(_cardScale, _cardScale);
            node.ZIndex = 2;
        }

        Texture2D? arrowTexture = CouchStyle.Load<Texture2D>(UpgradeArrowPath);
        if (arrowTexture != null)
        {
            _downArrow = new TextureRect
            {
                Texture = arrowTexture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Size = new Vector2(22f, 22f),
                // Side by side now (see LayoutUpgradePreview), so the arrow points right, its unrotated default -
                // the 90° rotation only made sense for the previous stacked (before-above-after) layout.
                PivotOffset = new Vector2(11f, 11f),
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = 2
            };
            AddChild(_downArrow);
        }
    }

    private void OnCommand(CouchHudCommand command)
    {
        CouchTeammateChoice choice = _choice!;
        int optionCount = _rows.Count + _extraRows.Count;
        if (optionCount == 0)
        {
            return;
        }

        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                _cursor = (_cursor - 1 + optionCount) % optionCount;
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _cursor = (_cursor + 1) % optionCount;
                break;
            case CouchHudCommand.Accept:
                if (_cursor >= _rows.Count && choice.ExtraOptionsSkip)
                {
                    Skip(choice);
                }
                else if (choice.Kind is CouchChoiceAnswerKind.Index or CouchChoiceAnswerKind.PlayerId)
                {
                    CouchTeammateChoices.AnswerIndex(choice, _cursor);
                }
                else if (choice.MaxSelect <= 1)
                {
                    CouchTeammateChoices.Answer(choice, new[] { _cursor });
                }
                else if (!_picked.Remove(_cursor))
                {
                    if (_picked.Count >= choice.MaxSelect)
                    {
                        Flash($"At most {choice.MaxSelect}");
                    }
                    else
                    {
                        _picked.Add(_cursor);
                    }
                }

                break;
            case CouchHudCommand.Submit:
            case CouchHudCommand.SubmitOrEndTurn:
                if (choice.MaxSelect <= 1)
                {
                    break;
                }

                if (_picked.Count < choice.MinSelect || _picked.Count > choice.MaxSelect)
                {
                    Flash(choice.MinSelect == choice.MaxSelect ? $"Pick exactly {choice.MinSelect}" : $"Pick {choice.MinSelect} to {choice.MaxSelect}");
                    break;
                }

                CouchTeammateChoices.Answer(choice, _picked.OrderBy((int i) => i).ToList());
                break;
            case CouchHudCommand.Back:
                if (_picked.Count > 0)
                {
                    _picked.Clear();
                }
                else if (choice.CanClose || choice.MinSelect == 0)
                {
                    Skip(choice);
                }

                break;
        }
    }

    /// <summary>Answers with no card (skip / cancel).</summary>
    private static void Skip(CouchTeammateChoice choice)
    {
        if (choice.Kind is CouchChoiceAnswerKind.Index or CouchChoiceAnswerKind.PlayerId)
        {
            CouchTeammateChoices.AnswerIndex(choice, null);
        }
        else
        {
            CouchTeammateChoices.Answer(choice, new List<int>());
        }
    }
}

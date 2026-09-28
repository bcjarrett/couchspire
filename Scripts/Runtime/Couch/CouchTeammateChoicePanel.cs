using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's card picker for choices outside combat: picking cards from their deck to upgrade, transform, remove or
/// enchant (rest site smith, shop removal, events, relics), picking from a grid of reward cards, or picking a pack. Every
/// source this panel handles is out of combat (<c>CouchTeammateChoicePatch</c> only tags a choice <c>IsCombatChoice =
/// false</c> outside combat, and <see cref="Pending"/> below filters combat choices out anyway), so it always lives in
/// the shared P2 column (<see cref="CouchPanel.PlaceInColumn"/>), never the driver-blocking side used mid-fight.
/// The game waits for these as the teammate's remote choice; this panel answers through <see cref="CouchTeammateChoices"/>.
/// Options are a vertical list of card names (<see cref="CouchPanel.LayoutRowsScrolled"/>) with one focused preview above
/// it, rather than a horizontal strip of cards: that only fit in the column's narrower width once. For upgrade picks
/// (Smith and the like) the preview is the card before and after upgrading, stacked vertically with a down arrow between,
/// in place of the old side-by-side pair that needed a much wider panel.
/// </summary>
internal sealed partial class CouchTeammateChoicePanel : CouchPanel
{
    /// <summary>
    /// Scale for the focused preview card(s). Smaller than the shop's 0.32 (a single card): this panel can show two
    /// cards stacked (an upgrade's before/after) above a scrolled row list, all above P1's Proceed button, so the
    /// preview needs to be more compact to leave the rows any room.
    /// </summary>
    private const float CardScale = 0.28f;

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
        float y = ContentTop;
        bool isUpgrade = choice.Source == UpgradeSource;
        CardModel? focused = _cursor < choice.Options.Count ? choice.Options[_cursor] : null;
        y = isUpgrade ? LayoutUpgradePreview(focused, y) : LayoutFocusedPreview(focused, y);

        for (int i = 0; i < _rows.Count; i++)
        {
            StyleRow(_rows[i], i == _cursor, dimmed: false, picked: _picked.Contains(i));
        }

        for (int i = 0; i < _extraRows.Count; i++)
        {
            StyleRow(_extraRows[i], _rows.Count + i == _cursor, dimmed: false);
        }

        List<RowView> allRows = new(_rows.Count + _extraRows.Count);
        allRows.AddRange(_rows);
        allRows.AddRange(_extraRows);
        y = LayoutRowsScrolled(allRows, y, ColumnMaxY, _cursor);

        string counter = _rows.Count > 0 && _cursor < _rows.Count ? $"Card {_cursor + 1} of {_rows.Count}" : "";
        string picks = choice.MaxSelect > 1 ? $"{_picked.Count} picked ({choice.MinSelect}-{choice.MaxSelect})" : "";
        string keys = choice.MaxSelect > 1
            ? $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} toggle · {Keys("O", "Y")} confirm · {Keys("K", "B")} {(choice.CanClose || choice.MinSelect == 0 ? "cancel" : "clear")}"
            : $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick{(choice.CanClose || choice.MinSelect == 0 ? $" · {Keys("K", "B")} cancel" : "")}";
        FinishLayout(y, string.Join("    ", new[] { counter, picks }.Where((string s) => s.Length > 0)), keys);
    }

    /// <summary>The card under the cursor, alone, for picks that aren't an upgrade (transform, remove, enchant, reward grid...).</summary>
    private float LayoutFocusedPreview(CardModel? card, float y)
    {
        SetUpgradePreview(null);
        SetFocusedPreview(card);
        if (_focusedPreview == null)
        {
            return y;
        }

        Vector2 cardSize = NCard.defaultSize * CardScale;
        _focusedPreview.Position = new Vector2(PanelWidth * 0.5f - 10f, y + cardSize.Y * 0.5f + 4f);
        return y + cardSize.Y + 14f;
    }

    /// <summary>
    /// For upgrade picks, the card under the cursor before and after upgrading, stacked vertically with a down arrow
    /// between, as on the driver's smith screen (<c>NUpgradePreview</c>) but rotated for the column's width.
    /// </summary>
    private float LayoutUpgradePreview(CardModel? card, float y)
    {
        SetFocusedPreview(null);
        SetUpgradePreview(card);
        if (_before == null || _after == null)
        {
            return y;
        }

        Vector2 cardSize = NCard.defaultSize * CardScale;
        float centerX = PanelWidth * 0.5f - 10f;
        _before.Position = new Vector2(centerX, y + cardSize.Y * 0.5f);
        float arrowY = y + cardSize.Y + 2f;
        if (_downArrow != null)
        {
            _downArrow.Position = new Vector2(centerX - 11f, arrowY);
        }

        float afterTop = arrowY + 22f + 2f;
        _after.Position = new Vector2(centerX, afterTop + cardSize.Y * 0.5f);
        return afterTop + cardSize.Y + 8f;
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
        _focusedPreview.Scale = new Vector2(CardScale, CardScale);
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
            node.Scale = new Vector2(CardScale, CardScale);
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
                RotationDegrees = 90f,
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

using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's card picker for choices outside combat: picking cards from their deck to upgrade, transform, remove or
/// enchant (rest site smith, shop removal, events, relics), picking from a grid of reward cards, or picking a pack.
/// The game waits for these as the teammate's remote choice; this panel answers through <see cref="CouchTeammateChoices"/>.
/// Shows a scrolling row of cards around the cursor.
/// </summary>
internal sealed partial class CouchTeammateChoicePanel : CouchPanel
{
    private const float CardScale = 0.42f;

    private const int VisibleCards = 4;

    private const string UpgradeArrowPath = "res://images/ui/cards/upgrade_preview/upgrade_arrow.png";

    /// <summary>Source tag of deck upgrade picks (Smith and the like), which get a before/after preview.</summary>
    private const string UpgradeSource = "deck upgrade";

    private static CouchTeammateChoicePanel? _instance;

    private readonly List<NCard> _cards = new();

    private readonly List<RowView> _extraRows = new();

    private readonly HashSet<int> _picked = new();

    private CouchTeammateChoice? _choice;

    /// <summary>Upgraded copies for the preview, made once per card while this choice is up.</summary>
    private readonly Dictionary<CardModel, CardModel> _upgradedCopies = new();

    private readonly List<TextureRect> _arrows = new();

    private NCard? _before;

    private NCard? _after;

    private CardModel? _previewFor;

    private int _cursor;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible && _instance._choice != null;

    protected override float PanelWidth => 600f;

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
        PlaceOnSide(left: !CouchConfig.EventPanelOnLeft, top: 130f);
        Layout(choice);
    }

    private void Show(CouchTeammateChoice? choice)
    {
        foreach (NCard node in _cards)
        {
            CouchCards.Free(node);
        }

        _cards.Clear();
        FreeRows(_extraRows);
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
            _cards.Add(CouchCards.Create(card, this));
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
        Vector2 cardSize = NCard.defaultSize * CardScale;
        if (_cards.Count > 0)
        {
            int windowStart = Mathf.Clamp(_cursor - 1, 0, Mathf.Max(0, _cards.Count - VisibleCards));
            float spacing = cardSize.X + 12f;
            for (int i = 0; i < _cards.Count; i++)
            {
                NCard node = _cards[i];
                bool shown = i >= windowStart && i < windowStart + VisibleCards;
                node.Visible = shown;
                if (!shown)
                {
                    continue;
                }

                bool isCursor = i == _cursor;
                bool picked = _picked.Contains(i);
                float lift = (isCursor ? 10f : 0f) + (picked ? 14f : 0f);
                Vector2 spot = new(CouchFrame.PadLeft + cardSize.X * 0.5f + (i - windowStart) * spacing, y + 14f + cardSize.Y * 0.5f - lift);
                CouchCards.Glide(node, spot, CardScale * (isCursor ? 1.1f : 1f));
                node.ZIndex = isCursor ? 3 : 2;
                CouchCards.SetGlow(node, picked ? NCardHighlight.gold : isCursor ? NCardHighlight.playableColor : null);
            }

            y += cardSize.Y + 40f;
        }

        y = LayoutUpgradePreview(choice, y);

        for (int i = 0; i < _extraRows.Count; i++)
        {
            StyleRow(_extraRows[i], _cards.Count + i == _cursor, dimmed: false);
        }

        y = LayoutRows(_extraRows, y);

        string counter = _cards.Count > 0 && _cursor < _cards.Count ? $"Card {_cursor + 1} of {_cards.Count}" : "";
        string picks = choice.MaxSelect > 1 ? $"{_picked.Count} picked ({choice.MinSelect}-{choice.MaxSelect})" : "";
        string keys = choice.MaxSelect > 1
            ? $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} toggle · {Keys("O", "Y")} confirm · {Keys("K", "B")} {(choice.CanClose || choice.MinSelect == 0 ? "cancel" : "clear")}"
            : $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick{(choice.CanClose || choice.MinSelect == 0 ? $" · {Keys("K", "B")} cancel" : "")}";
        FinishLayout(y, string.Join("    ", new[] { counter, picks }.Where((string s) => s.Length > 0)), keys);
    }

    /// <summary>
    /// For upgrade picks, the card under the cursor before and after upgrading, with the game's upgrade arrows between,
    /// as on the driver's smith screen (<c>NUpgradePreview</c>).
    /// </summary>
    private float LayoutUpgradePreview(CouchTeammateChoice choice, float y)
    {
        CardModel? card = choice.Source == UpgradeSource && _cursor < _cards.Count ? choice.Options[_cursor] : null;
        SetUpgradePreview(card);
        if (_before == null || _after == null)
        {
            return y;
        }

        Vector2 cardSize = NCard.defaultSize * CardScale;
        float centerY = y + cardSize.Y * 0.5f;
        _before.Position = new Vector2(PanelWidth * 0.5f - cardSize.X * 0.5f - 70f, centerY);
        _after.Position = new Vector2(PanelWidth * 0.5f + cardSize.X * 0.5f + 70f, centerY);
        for (int i = 0; i < _arrows.Count; i++)
        {
            _arrows[i].Position = new Vector2(PanelWidth * 0.5f - 42f + i * 28f, centerY - 14f);
        }

        return y + cardSize.Y + 30f;
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
        foreach (TextureRect arrow in _arrows)
        {
            arrow.QueueFree();
        }

        _arrows.Clear();
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
        for (int i = 0; i < 3 && arrowTexture != null; i++)
        {
            TextureRect arrow = new()
            {
                Texture = arrowTexture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Size = new Vector2(28f, 28f),
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = 2
            };
            AddChild(arrow);
            _arrows.Add(arrow);
        }
    }

    private void OnCommand(CouchHudCommand command)
    {
        CouchTeammateChoice choice = _choice!;
        int optionCount = _cards.Count + _extraRows.Count;
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
                if (_cursor >= _cards.Count && choice.ExtraOptionsSkip)
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

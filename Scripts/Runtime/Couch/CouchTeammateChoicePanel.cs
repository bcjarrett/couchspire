using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;

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

    private static CouchTeammateChoicePanel? _instance;

    private readonly List<NCard> _cards = new();

    private readonly List<RowView> _extraRows = new();

    private readonly HashSet<int> _picked = new();

    private CouchTeammateChoice? _choice;

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
                node.Position = new Vector2(16f + cardSize.X * 0.5f + (i - windowStart) * spacing, y + 10f + cardSize.Y * 0.5f - lift);
                float scale = CardScale * (isCursor ? 1.08f : 1f);
                node.Scale = new Vector2(scale, scale);
                node.ZIndex = isCursor ? 3 : 2;
                node.Modulate = picked ? new Color(1f, 0.86f, 0.45f) : isCursor ? Colors.White : new Color(0.78f, 0.78f, 0.78f);
            }

            y += cardSize.Y + 34f;
        }

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
                if (choice.Kind is CouchChoiceAnswerKind.Index or CouchChoiceAnswerKind.PlayerId)
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
                    if (choice.Kind is CouchChoiceAnswerKind.Index or CouchChoiceAnswerKind.PlayerId)
                    {
                        CouchTeammateChoices.AnswerIndex(choice, null);
                    }
                    else
                    {
                        CouchTeammateChoices.Answer(choice, new List<int>());
                    }
                }

                break;
        }
    }
}

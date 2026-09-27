using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's look at their own run: their deck (scrollable cards) and their relics (with descriptions), plus HP,
/// gold and potions. The top bar only ever shows the driver's. Toggled with V on the keyboard or View/Back on the
/// teammate's controller; read-only.
/// </summary>
internal sealed partial class CouchTeammateInfo : CouchPanel
{
    private const float CardScale = 0.42f;

    private const int VisibleCards = 4;

    private const int VisibleRelics = 6;

    private static CouchTeammateInfo? _instance;

    private readonly List<NCard> _cards = new();

    private readonly List<RowView> _relicRows = new();

    private List<CardModel> _shownDeck = new();

    private List<RelicModel> _shownRelics = new();

    private Player? _teammate;

    private bool _open;

    private bool _relicTab;

    private int _cursor;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    protected override float PanelWidth => 600f;

    /// <summary>Opens or closes the view for the teammate (only for <paramref name="playerId"/>, when given).</summary>
    public static void Toggle(ulong? playerId, bool fromController)
    {
        Player? teammate = CouchTeammate.FindTeammate();
        if (_instance == null || !IsInstanceValid(_instance) || teammate == null || (playerId.HasValue && teammate.NetId != playerId.Value))
        {
            return;
        }

        _instance._open = !_instance._open;
        _instance.LastInputFromController = fromController;
        _instance._cursor = 0;
        if (_instance._open)
        {
            CouchSfx.Accept();
        }
        else
        {
            CouchSfx.Back();
        }
    }

    /// <summary>Opens the view on the deck or relics tab (controller LB / RB).</summary>
    public static void Open(ulong? playerId, bool relics)
    {
        Player? teammate = CouchTeammate.FindTeammate();
        if (_instance == null || !IsInstanceValid(_instance) || teammate == null || (playerId.HasValue && teammate.NetId != playerId.Value))
        {
            return;
        }

        _instance._open = true;
        _instance._relicTab = relics;
        _instance._cursor = 0;
        _instance.LastInputFromController = playerId.HasValue;
        CouchSfx.Accept();
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
        ClearCards();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        _teammate = _open ? CouchTeammate.FindTeammate() : null;
        if (_teammate == null)
        {
            _open = false;
            if (Visible)
            {
                Visible = false;
                ClearCards();
                FreeRows(_relicRows);
                _shownRelics = new List<RelicModel>();
            }

            return;
        }

        Visible = true;
        PlaceOnSide(left: !CouchConfig.EventPanelOnLeft, top: 110f);
        List<CardModel> deck = PileType.Deck.GetPile(_teammate).Cards.ToList();
        List<RelicModel> relics = _teammate.Relics.ToList();
        if (!deck.SequenceEqual(_shownDeck))
        {
            RebuildDeck(deck);
        }

        if (!relics.SequenceEqual(_shownRelics))
        {
            RebuildRelics(relics);
        }

        int potions = _teammate.Potions.Count();
        SetTitle($"{SeatLabel(_teammate)} · {_teammate.Character.Title.GetFormattedText()}    HP {_teammate.Creature.CurrentHp}/{_teammate.Creature.MaxHp}    Gold {_teammate.Gold}    Potions {potions}/{_teammate.MaxPotionCount}\n"
            + (_relicTab ? $"Deck ({deck.Count})   [Relics ({relics.Count})]" : $"[Deck ({deck.Count})]   Relics ({relics.Count})"));

        float y = ContentTop;
        y = _relicTab ? LayoutRelics(y) : LayoutDeck(y);
        string where = _relicTab
            ? (_relicRows.Count > 0 ? $"Relic {_cursor + 1} of {_relicRows.Count}" : "No relics")
            : (_cards.Count > 0 ? $"Card {_cursor + 1} of {_cards.Count}" : "No cards");
        FinishLayout(y, where, $"{Keys("J/L", "D-pad")} scroll · {Keys("U", "LB/RB")} deck/relics · {Keys("V or K", "B")} close");
    }

    private float LayoutDeck(float y)
    {
        foreach (RowView row in _relicRows)
        {
            row.Root.Visible = false;
        }

        Vector2 cardSize = NCard.defaultSize * CardScale;
        int windowStart = Mathf.Clamp(_cursor - 1, 0, Mathf.Max(0, _cards.Count - VisibleCards));
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
            Vector2 spot = new(CouchFrame.PadLeft + cardSize.X * 0.5f + (i - windowStart) * (cardSize.X + 12f), y + 14f + cardSize.Y * 0.5f - (isCursor ? 10f : 0f));
            CouchCards.Glide(node, spot, CardScale * (isCursor ? 1.1f : 1f));
            node.ZIndex = isCursor ? 3 : 2;
        }

        return _cards.Count > 0 ? y + cardSize.Y + 40f : y;
    }

    private float LayoutRelics(float y)
    {
        foreach (NCard node in _cards)
        {
            node.Visible = false;
        }

        int windowStart = Mathf.Clamp(_cursor - 2, 0, Mathf.Max(0, _relicRows.Count - VisibleRelics));
        List<RowView> shown = new();
        for (int i = 0; i < _relicRows.Count; i++)
        {
            bool visible = i >= windowStart && i < windowStart + VisibleRelics;
            _relicRows[i].Root.Visible = visible;
            if (visible)
            {
                StyleRow(_relicRows[i], i == _cursor, dimmed: false);
                shown.Add(_relicRows[i]);
            }
        }

        return LayoutRows(shown, y);
    }

    private void OnCommand(CouchHudCommand command)
    {
        int count = _relicTab ? _relicRows.Count : _cards.Count;
        switch (command)
        {
            case CouchHudCommand.Left:
                _cursor = count == 0 ? 0 : (_cursor - 1 + count) % count;
                break;
            case CouchHudCommand.Right:
                _cursor = count == 0 ? 0 : (_cursor + 1) % count;
                break;
            case CouchHudCommand.Up:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _relicTab = !_relicTab;
                _cursor = 0;
                break;
            case CouchHudCommand.TabLeft:
            case CouchHudCommand.TabRight:
                _relicTab = command == CouchHudCommand.TabRight;
                _cursor = 0;
                break;
            case CouchHudCommand.Back:
            case CouchHudCommand.Info:
                _open = false;
                break;
        }
    }

    private void RebuildDeck(List<CardModel> deck)
    {
        ClearCards();
        _shownDeck = deck;
        foreach (CardModel card in deck)
        {
            NCard node = CouchCards.Create(card, this);
            node.Visible = false;
            _cards.Add(node);
        }

        _cursor = Mathf.Clamp(_cursor, 0, Mathf.Max(0, _cards.Count - 1));
    }

    private void RebuildRelics(List<RelicModel> relics)
    {
        FreeRows(_relicRows);
        _shownRelics = relics;
        foreach (RelicModel relic in relics)
        {
            Texture2D? icon = null;
            try
            {
                icon = relic.Icon;
            }
            catch (System.Exception)
            {
                icon = null;
            }

            RowView row = CreateRow(icon);
            row.Label.Text = $"{CouchText.Plain(relic.Title.GetFormattedText())}\n{CouchText.Plain(relic.DynamicDescription.GetFormattedText())}";
            _relicRows.Add(row);
        }
    }

    private void ClearCards()
    {
        foreach (NCard node in _cards)
        {
            CouchCards.Free(node);
        }

        _cards.Clear();
        _shownDeck = new List<CardModel>();
    }
}

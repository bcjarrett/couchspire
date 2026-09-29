using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's part of the top bar, in its empty middle: their portrait, HP, gold and potions, drawn with the
/// driver's top bar art (portrait backdrop, heart and gold icons, the HP and gold text styles, potion holders). Shown
/// all run, in and out of combat. It sits between the driver's floor/boss icons and the timer/buttons on the right, and
/// shrinks to fit when that gap narrows (more potion slots on either side). The combat HUD drives the potion slots
/// (cursor, targeting from a potion).
/// </summary>
internal sealed partial class CouchTeammateTopBar : Control
{
    private const string BackdropPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_char_backdrop.tres";

    private const string HeartPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres";

    private const string GoldPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_gold.tres";

    private const string HpStylePath = "res://themes/top_bar_hp.tres";

    private const string GoldStylePath = "res://themes/top_bar_gold.tres";

    private const float PortraitSize = 46f;

    private const float IconSize = 32f;

    private const float SlotSize = 38f;

    private const int FontSize = 22;

    /// <summary>Everything is laid out in here at full size, then scaled to fit the gap in the top bar.</summary>
    private Control? _row;

    private static CouchTeammateTopBar? _instance;

    private readonly List<CouchPotionSlot> _slots = new();

    private Player? _teammate;

    private TextureRect? _backdrop;

    private TextureRect? _portrait;

    private TextureRect? _heart;

    private TextureRect? _goldIcon;

    private Label? _seat;

    private Label? _hp;

    private Label? _gold;

    private int? _cursor;

    private CouchPotionSlot? _tipOwner;

    public static bool IsShown => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    /// <summary>The teammate's potion slot node, for aiming from it.</summary>
    public static Control? Slot(int index)
    {
        return IsShown && index >= 0 && index < _instance!._slots.Count ? _instance._slots[index] : null;
    }

    /// <summary>Puts the teammate's potion cursor on a slot (the slot's reticle and the potion's tooltip), or clears it.</summary>
    public static void SetCursor(int? index)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            _instance._cursor = index;
        }
    }

    public override void _Ready()
    {
        _instance = this;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _row = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_row);
        _backdrop = Icon(BackdropPath);
        _portrait = Icon(null);
        _heart = Icon(HeartPath);
        _goldIcon = Icon(GoldPath);
        _seat = CouchStyle.CreateLabel(_row, 20, bold: true, color: CouchStyle.Gold, outline: 8);
        _hp = StyledLabel(HpStylePath, new Color(1f, 0.333f, 0.333f));
        _gold = StyledLabel(GoldStylePath, CouchStyle.Gold);
        SetProcess(true);
    }

    public override void _ExitTree()
    {
        ClearTip();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        Player? teammate = CouchTeammate.FindTeammate();
        NTopBar? topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (teammate == null || topBar == null || !topBar.IsVisibleInTree() || CouchLayout.DriverRelicsHidden())
        {
            Visible = false;
            ClearTip();
            return;
        }

        if (teammate != _teammate)
        {
            _teammate = teammate;
            _portrait!.Texture = SafeIcon(teammate);
        }

        Visible = true;
        _seat!.Text = CouchSeats.FindByPlayer(teammate.NetId)?.Label ?? "P2";
        _hp!.Text = $"{teammate.Creature.CurrentHp}/{teammate.Creature.MaxHp}";
        _gold!.Text = teammate.Gold.ToString();
        SyncSlots(teammate);
        Layout(topBar);
        UpdateTip();
    }

    private void SyncSlots(Player teammate)
    {
        IReadOnlyList<PotionModel?> potions = teammate.PotionSlots;
        while (_slots.Count < potions.Count)
        {
            CouchPotionSlot slot = new() { Size = new Vector2(SlotSize, SlotSize) };
            _row!.AddChild(slot);
            _slots.Add(slot);
        }

        while (_slots.Count > potions.Count)
        {
            _slots[^1].QueueFree();
            _slots.RemoveAt(_slots.Count - 1);
        }

        for (int i = 0; i < potions.Count; i++)
        {
            _slots[i].Potion = potions[i];
            _slots[i].Highlighted = _cursor == i;
        }
    }

    /// <summary>
    /// After the driver's floor and boss icons, before the timer and buttons, vertically centered on the driver's HP;
    /// scaled down if it doesn't fit.
    /// </summary>
    private void Layout(NTopBar topBar)
    {
        float left = new Control?[] { topBar.RoomIcon, topBar.FloorIcon, topBar.BossIcon }
            .Where((Control? c) => c != null && c.IsVisibleInTree())
            .Select((Control? c) => c!.GetGlobalRect().End.X)
            .DefaultIfEmpty(880f)
            .Max() + 36f;
        float right = new Control?[] { topBar.Timer, topBar.Map, topBar.Deck, topBar.Pause }
            .Where((Control? c) => c != null && c.IsVisibleInTree())
            .Select((Control? c) => c!.GetGlobalRect().Position.X)
            .DefaultIfEmpty(GetViewportRect().Size.X)
            .Min() - 36f;
        float centerY = PortraitSize * 0.5f;
        float x = 0f;
        Place(_seat!, ref x, centerY, 6f);
        _backdrop!.Size = new Vector2(PortraitSize, PortraitSize);
        _backdrop.Position = new Vector2(x, centerY - PortraitSize * 0.5f);
        _portrait!.Size = new Vector2(PortraitSize, PortraitSize) * 0.86f;
        _portrait.Position = _backdrop.Position + new Vector2(PortraitSize, PortraitSize) * 0.07f;
        x += PortraitSize + 14f;
        PlaceIcon(_heart!, ref x, centerY);
        Place(_hp!, ref x, centerY, 18f);
        PlaceIcon(_goldIcon!, ref x, centerY);
        Place(_gold!, ref x, centerY, 18f);
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].Position = new Vector2(x + i * (SlotSize + 2f), centerY - SlotSize * 0.5f);
        }

        float width = x + _slots.Count * (SlotSize + 2f);
        float scale = Mathf.Clamp((right - left) / Mathf.Max(width, 1f), 0.5f, 1f);
        _row!.Scale = new Vector2(scale, scale);
        _row.Position = new Vector2(left, topBar.Hp.GetGlobalRect().GetCenter().Y - centerY * scale);
    }

    private static void Place(Label label, ref float x, float centerY, float gap)
    {
        Vector2 size = label.GetMinimumSize();
        label.Position = new Vector2(x, centerY - size.Y * 0.5f);
        x += size.X + gap;
    }

    private static void PlaceIcon(TextureRect icon, ref float x, float centerY)
    {
        icon.Size = new Vector2(IconSize, IconSize);
        icon.Position = new Vector2(x, centerY - IconSize * 0.5f);
        x += IconSize + 2f;
    }

    /// <summary>The potion under the teammate's cursor shows its tooltip, as the driver's does on controller focus.</summary>
    private void UpdateTip()
    {
        CouchPotionSlot? owner = _cursor is int index && index < _slots.Count && _slots[index].Potion != null ? _slots[index] : null;
        if (owner == _tipOwner)
        {
            return;
        }

        ClearTip();
        if (owner?.Potion == null)
        {
            return;
        }

        _tipOwner = owner;
        NHoverTipSet.CreateAndShow(owner, owner.Potion.HoverTips, HoverTipAlignment.Right);
    }

    private void ClearTip()
    {
        if (_tipOwner != null && IsInstanceValid(_tipOwner))
        {
            NHoverTipSet.Remove(_tipOwner);
        }

        _tipOwner = null;
    }

    private TextureRect Icon(string? path)
    {
        TextureRect rect = new()
        {
            Texture = path != null ? CouchStyle.Load<Texture2D>(path) : null,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _row!.AddChild(rect);
        return rect;
    }

    /// <summary>The driver's top bar text style, a little smaller.</summary>
    private Label StyledLabel(string stylePath, Color fallback)
    {
        Label label = new() { MouseFilter = MouseFilterEnum.Ignore };
        if (CouchStyle.Load<LabelSettings>(stylePath)?.Duplicate() is LabelSettings settings)
        {
            settings.FontSize = FontSize;
            settings.OutlineSize = 9;
            label.LabelSettings = settings;
        }
        else
        {
            CouchStyle.Text(label, FontSize, bold: true, color: fallback, outline: 9);
        }

        _row!.AddChild(label);
        return label;
    }

    private static Texture2D? SafeIcon(Player player)
    {
        try
        {
            return player.Character.IconTexture;
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}

using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's relics on the main screen, right-aligned across from the driver's own relic row (which the top bar
/// only ever shows for the driver). Uses the game's relic holders, so counters, disabled states, the flash when a relic
/// triggers and the hover tooltip all work as they do for the driver's relics; clicking one opens the relic inspector.
/// New relics pop in with the game's "obtained" animation and sound. It also plays the gold sound when the teammate's
/// gold goes up, which the game only does for the local player.
/// In combat the bar sits at the end of the teammate HUD's top line; elsewhere, at the same height. Panels on the right
/// start below it (<see cref="TopBelowBar"/>).
/// </summary>
internal sealed partial class CouchTeammateRelicBar : Control
{
    private const float MaxIconSize = 48f;

    private const float MinIconSize = 30f;

    private const float EdgeMargin = 24f;

    private const float Padding = 6f;

    private static CouchTeammateRelicBar? _instance;

    /// <summary>The relic the teammate's controller cursor is on (from their combat HUD), if any.</summary>
    private int? _focusRequest;

    private NRelicInventoryHolder? _focused;

    /// <summary>Owner of the focused relic's tooltip (the holders' own tooltips belong to the driver's mouse).</summary>
    private Control? _tipAnchor;

    private Tween? _focusTween;

    private readonly List<NRelicInventoryHolder> _holders = new();

    private List<RelicModel> _shownRelics = new();

    private Player? _teammate;

    private int _lastGold;

    /// <summary>False until the current teammate's relics are first shown (those don't animate in).</summary>
    private bool _filled;

    private Label? _label;

    private float _bottom;

    /// <summary>
    /// <paramref name="top"/>, or just below the relic bar if that's lower. For panels placed against the right edge.
    /// </summary>
    public static int RelicCount => _instance != null && IsInstanceValid(_instance) && _instance.Visible ? _instance._holders.Count : 0;

    /// <summary>Puts the teammate's cursor on a relic (tooltip and a bigger icon, as on controller focus), or clears it.</summary>
    public static void Focus(int? index)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            _instance._focusRequest = index;
        }
    }

    /// <summary>The focused relic's name, for the HUD's hint line.</summary>
    public static string? FocusedName => _instance?._focused?.Relic?.Model is RelicModel relic ? CouchText.Plain(relic.Title.GetFormattedText()) : null;

    public static float TopBelowBar(float top)
    {
        return _instance != null && IsInstanceValid(_instance) && _instance.Visible ? Mathf.Max(top, _instance._bottom + 10f) : top;
    }

    public override void _Ready()
    {
        _instance = this;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        // Like the driver's relic row: just the relics, plus the seat label.
        _label = CouchStyle.CreateLabel(this, 22, bold: true, outline: 8);
        _tipAnchor = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_tipAnchor);
        SetProcess(true);
    }

    public override void _ExitTree()
    {
        Clear();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        Player? teammate = CouchTeammate.FindTeammate();
        if (teammate != _teammate)
        {
            Clear();
            _teammate = teammate;
            _lastGold = teammate?.Gold ?? 0;
        }

        if (teammate == null || NRun.Instance?.GlobalUi == null)
        {
            Visible = false;
            return;
        }

        PlayGoldSound(teammate);
        List<RelicModel> relics = teammate.Relics.ToList();
        if (!relics.SequenceEqual(_shownRelics))
        {
            Sync(teammate, relics);
        }

        bool wasVisible = Visible;
        Visible = !CouchLayout.DriverRelicsHidden() && _holders.Count > 0;
        if (Visible)
        {
            Layout(teammate);
            Modulate = new Color(1f, 1f, 1f, CouchLayout.DriverReadingTopUi() ? 0.15f : 1f);
        }

        ApplyFocus();

        if (Visible != wasVisible)
        {
            CouchLog.Info(Visible
                ? $"Relic bar shown for {teammate.NetId}: {_holders.Count} relics, top {CouchTeammateHud.BandTop()}, bottom {_bottom}, in combat: {CouchTeammateHud.HeaderRight.HasValue}."
                : "Relic bar hidden.");
        }
    }

    private void PlayGoldSound(Player teammate)
    {
        int gold = teammate.Gold;
        if (gold > _lastGold && !LocalContext.IsMe(teammate))
        {
            CouchSfx.Gold(gold - _lastGold);
        }

        _lastGold = gold;
    }

    /// <summary>Adds holders for new relics (animated, with a sound, unless this is the first fill) and drops removed ones.</summary>
    private void Sync(Player teammate, List<RelicModel> relics)
    {
        foreach (NRelicInventoryHolder holder in _holders.Where((NRelicInventoryHolder h) => !relics.Contains(h.Relic.Model)).ToList())
        {
            _holders.Remove(holder);
            holder.QueueFreeSafely();
        }

        List<NRelicInventoryHolder> ordered = new();
        foreach (RelicModel relic in relics)
        {
            NRelicInventoryHolder? holder = _holders.FirstOrDefault((NRelicInventoryHolder h) => h.Relic.Model == relic);
            if (holder == null)
            {
                holder = CreateHolder(relic, relics);
                if (holder == null)
                {
                    continue;
                }

                if (_filled)
                {
                    TaskHelper.RunSafely(holder.PlayNewlyAcquiredAnimation(null, null));
                    if (!LocalContext.IsMe(teammate))
                    {
                        CouchSfx.RelicGained();
                    }
                }
            }

            ordered.Add(holder);
        }

        _holders.Clear();
        _holders.AddRange(ordered);
        _shownRelics = relics;
        _filled = true;
    }

    private NRelicInventoryHolder? CreateHolder(RelicModel relic, List<RelicModel> relics)
    {
        NRelicInventoryHolder? holder = NRelicInventoryHolder.Create(relic);
        if (holder == null)
        {
            return null;
        }

        // Mouse hover shows the tooltip; keep it out of controller focus so the driver's navigation never lands here.
        holder.FocusMode = FocusModeEnum.None;
        AddChild(holder);
        holder.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
            NGame.Instance?.GetInspectRelicScreen().Open(_teammate?.Relics.ToList() ?? relics, relic)));
        return holder;
    }

    /// <summary>
    /// In combat: right after the teammate HUD's status line, left-aligned, so the row stays over the teammate's band and
    /// clear of the enemies on the right. Elsewhere: right-aligned under the top bar, across from the driver's relics,
    /// with the seat label.
    /// </summary>
    private void Layout(Player teammate)
    {
        Vector2 viewport = GetViewportRect().Size;
        // Level with the teammate HUD's status line (and the players list on the left), below the driver's relic row.
        float top = CouchTeammateHud.BandTop() + 4f;
        bool inCombat = CouchTeammateHud.HeaderRight.HasValue;
        float left = CouchTeammateHud.HeaderRight is float headerRight
            ? headerRight + 30f
            : Mathf.Max(viewport.X * 0.45f, DriverRelicsRight() + 32f);
        float right = inCombat ? Mathf.Min(viewport.X - EdgeMargin, left + viewport.X * 0.3f) : viewport.X - EdgeMargin;

        _label!.Visible = !inCombat;
        _label.Text = CouchSeats.FindByPlayer(teammate.NetId)?.Label ?? "P2";
        Vector2 labelSize = inCombat ? Vector2.Zero : _label.GetMinimumSize();
        float available = Mathf.Max(MinIconSize, right - left - (inCombat ? 0f : labelSize.X + 12f));
        int count = _holders.Count;
        float size = Mathf.Clamp(available / count, MinIconSize, inCombat ? 36f : MaxIconSize);
        int perRow = Mathf.Max(1, Mathf.FloorToInt(available / size));
        int rows = (count + perRow - 1) / perRow;
        if (inCombat && CouchTeammateHud.HeaderMiddle is float headerMiddle)
        {
            // Centered on the HUD's status line (energy, draw and discard), which is taller than the icons.
            top = headerMiddle - size * 0.5f;
        }

        for (int i = 0; i < count; i++)
        {
            NRelicInventoryHolder holder = _holders[i];
            int row = i / perRow;
            int inRow = Mathf.Min(perRow, count - row * perRow);
            int column = i % perRow;
            Vector2 native = holder.Size.X > 1f ? holder.Size : new Vector2(68f, 68f);
            float scale = size / Mathf.Max(native.X, native.Y);
            holder.Scale = new Vector2(scale, scale);
            float x = inCombat ? left + column * size : right - (inRow - column) * size;
            // The holder scales around its center (pivot 34,34), so shift it back to keep the icon's top-left here.
            holder.Position = new Vector2(x, top + row * size) - holder.PivotOffset * (1f - scale);
        }

        float iconsLeft = right - Mathf.Min(perRow, count) * size;
        _label.Position = new Vector2(iconsLeft - labelSize.X - 8f, top + (size - labelSize.Y) * 0.5f);
        _bottom = top + rows * size + Padding;
    }

    /// <summary>Right edge of the driver's relic row, so the two rows don't run into each other.</summary>
    private static float DriverRelicsRight()
    {
        IReadOnlyList<NRelicInventoryHolder>? nodes = NRun.Instance?.GlobalUi?.RelicInventory?.RelicNodes;
        if (nodes == null || nodes.Count == 0)
        {
            return 0f;
        }

        return nodes.Where(IsInstanceValid).Select((NRelicInventoryHolder n) => n.GlobalPosition.X + n.Size.X).DefaultIfEmpty(0f).Max();
    }

    /// <summary><c>NRelicInventoryHolder.OnFocus</c>: the icon grows and the relic's tooltip opens.</summary>
    private void ApplyFocus()
    {
        NRelicInventoryHolder? holder = Visible && _focusRequest is int index && index >= 0 && index < _holders.Count ? _holders[index] : null;
        if (holder == _focused)
        {
            return;
        }

        if (_focused != null && IsInstanceValid(_focused))
        {
            _focusTween?.Kill();
            _focusTween = _focused.CreateTween();
            _focusTween.TweenProperty(_focused.Relic.Icon, "scale", Vector2.One, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }

        if (_tipAnchor != null)
        {
            NHoverTipSet.Remove(_tipAnchor);
        }

        _focused = holder;
        if (holder == null || _tipAnchor == null)
        {
            return;
        }

        _focusTween?.Kill();
        _focusTween = holder.CreateTween();
        _focusTween.TweenProperty(holder.Relic.Icon, "scale", Vector2.One * 1.25f, 0.05);
        _tipAnchor.Position = holder.Position + holder.PivotOffset * (Vector2.One - holder.Scale);
        _tipAnchor.Size = holder.Size * holder.Scale;
        NHoverTipSet.CreateAndShow(_tipAnchor, holder.Relic.Model.HoverTips)?.SetAlignmentForRelic(holder.Relic);
    }

    private void Clear()
    {
        foreach (NRelicInventoryHolder holder in _holders)
        {
            holder.QueueFreeSafely();
        }

        _holders.Clear();
        _shownRelics = new List<RelicModel>();
        _filled = false;
    }
}

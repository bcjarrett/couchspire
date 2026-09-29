using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Shared look and plumbing for the teammate's side panels (rest site, treasure, shop, card picker, info): the game's
/// hover tip frame with a gold title, rows drawn and animated like the game's event options (see <see cref="CouchButton"/>),
/// and a hint line that shows keyboard or controller keys depending on what the teammate last used. Panels slide in
/// from their screen edge when they open.
/// </summary>
internal abstract partial class CouchPanel : Control
{
    protected const float RowGap = 6f;

    /// <summary>Rows sit inside the frame's padding.</summary>
    protected const float RowLeft = CouchFrame.PadLeft - 6f;

    /// <summary>
    /// Shared width for the one P2 column: every out-of-combat teammate panel (shop, rest site, treasure, rewards,
    /// and the out-of-combat card picker) lives here, pinned to the right edge below the teammate's relic bar
    /// (<see cref="PlaceInColumn"/>). Widened from the shop's original 320 px to fit the Smith upgrade preview
    /// (<c>CouchTeammateChoicePanel.CardScale</c>) clearly larger without clipping the before/after arrow or the
    /// card name below it; 360 is comfortably under the ~380 px ceiling the maintainer approved, and every other
    /// panel (shop, rest site, treasure, rewards) just gets a little more breathing room from the same change.
    /// </summary>
    protected const float ColumnWidth = 360f;

    private const double FlashSeconds = 3.0;

    /// <summary>How far a panel slides in from its screen edge when it opens.</summary>
    private const float SlideDistance = 60f;

    private string _flash = "";

    private double _flashUntil;

    /// <summary>0 when the panel has just opened, 1 once it has slid into place.</summary>
    private float _appear = 1f;

    private bool _onLeft;

    private Tween? _appearTween;

    private readonly List<RowView> _discardRows = new();

    private List<PotionModel> _discardPotions = new();

    private Player? _discardOwner;

    private Action? _afterDiscard;

    private int _discardCursor;

    private bool _discardSent;

    /// <summary>Height of each "▲ N more / ▼ N more" scroll indicator row (see <see cref="LayoutRowsScrolled"/>).</summary>
    private const float ScrollIndicatorHeight = 20f;

    /// <summary>
    /// Compact row-list cap while P1 has a card-select overlay open (<see cref="CouchColumnFloor.IsP1CardSelectOpen"/>):
    /// P2's panel should stay at its natural, small size then instead of stretching to <see cref="ColumnMaxY"/>, so it
    /// covers as little of P1's grid as possible (approved spec problem 2, "Compact while P1 picks"). Sized to leave
    /// room for a readable Smith preview (<see cref="CouchTeammateChoicePanel.ReadableCardScale"/>) plus the cursor
    /// row even in compact mode - per senior review, the preview must stay readable and rows give way first, not
    /// the other way around.
    /// </summary>
    protected const float CompactRowsBudget = 340f;

    /// <summary>Conservative one-line row height, used to check whether at least the cursor row still has room once
    /// something else (a preview, a footer) has claimed its space - the real row heights are measured properly by
    /// <see cref="LayoutRowsScrolled"/> right after.</summary>
    protected const float MinRowHeightEstimate = 46f;

    /// <summary>Gap between the last row/preview and the hint line, matching <see cref="FinishLayout"/>'s own offset.</summary>
    private const float FooterGap = 4f;

    private Label? _scrollUpIndicator;

    private Label? _scrollDownIndicator;

    protected CouchFrame? Background { get; private set; }

    protected Label? Title { get; private set; }

    protected Label? Hint { get; private set; }

    protected bool LastInputFromController { get; set; }

    protected abstract float PanelWidth { get; }

    /// <summary>Row icon size; compact panels use smaller rows.</summary>
    protected virtual float RowIconSize => 44f;

    protected virtual int RowFontSize => 20;

    /// <summary>Space around a row's icon or text; compact panels use less.</summary>
    protected virtual float RowPadding => 16f;

    /// <summary>Gap between rows.</summary>
    protected virtual float RowSpacing => RowGap;

    /// <summary>Width available to rows.</summary>
    protected float RowWidth => PanelWidth - RowLeft - CouchFrame.PadRight + 12f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        Background = new CouchFrame();
        AddChild(Background);
        Title = CouchStyle.CreateLabel(this, 24, bold: true, color: CouchStyle.Gold, wrapWidth: PanelWidth - CouchFrame.PadLeft - CouchFrame.PadRight);
        Hint = CouchStyle.CreateLabel(this, 18, color: CouchStyle.Muted, wrapWidth: PanelWidth - CouchFrame.PadLeft - CouchFrame.PadRight);
        _scrollUpIndicator = CouchStyle.CreateLabel(this, 16, color: CouchStyle.Muted, wrapWidth: RowWidth);
        _scrollUpIndicator.Visible = false;
        _scrollDownIndicator = CouchStyle.CreateLabel(this, 16, color: CouchStyle.Muted, wrapWidth: RowWidth);
        _scrollDownIndicator.Visible = false;
        VisibilityChanged += OnVisibilityChanged;
        SetProcess(true);
    }

    /// <summary>
    /// Places the panel against the left or right edge of the screen, sliding in from that edge when it opens. On the
    /// right it starts below the teammate's relic bar; on the left, below the players list (names and health bars).
    /// </summary>
    protected void PlaceOnSide(bool left, float top, float margin = 36f)
    {
        _onLeft = left;
        Vector2 viewport = GetViewportRect().Size;
        float slide = (1f - _appear) * SlideDistance * (left ? -1f : 1f);
        Position = new Vector2((left ? margin : viewport.X - PanelWidth - margin) + slide, left ? CouchLayout.BelowPlayersList(top) : CouchTeammateRelicBar.TopBelowBar(top));
    }

    /// <summary>
    /// Places the panel in the shared right-hand P2 column (see <see cref="ColumnWidth"/>): pinned to the right
    /// edge, below the teammate's relic bar. Every out-of-combat teammate panel uses this instead of picking its own
    /// side, so they all line up. Equivalent to <c>PlaceOnSide(left: false, ...)</c>; kept as its own name so each
    /// panel states its intent instead of repeating "false".
    /// </summary>
    protected void PlaceInColumn(float top, float margin = 20f)
    {
        PlaceOnSide(left: false, top, margin);
    }

    /// <summary>
    /// The panel-local y below which the column must not draw: a small margin above the topmost visible P1 action
    /// button whose x-range reaches the column (Proceed, or a card-select overlay's confirm/cancel — see
    /// <see cref="CouchColumnFloor"/>), or a margin above the viewport bottom when none is up. Replaces the old
    /// hand-tuned <c>ProceedButtonClearance</c> constant with a live measurement, so the column always uses exactly
    /// the room it actually has instead of guessing. Rows are laid out in the panel's own local coordinates
    /// (relative to <see cref="Control.Position"/>, which <see cref="PlaceInColumn"/> sets to the panel's screen
    /// position), so this subtracts the panel's own top back out of the viewport-space floor line — comparing a
    /// local <c>y</c> straight against a viewport-space limit silently allowed rows to run past the bottom of the
    /// screen by however far the panel itself sat down the column (caught by a --review screenshot and the layout
    /// test's Proceed-button rule while building the original fixed-constant version of this).
    /// </summary>
    protected float ColumnMaxY => CouchColumnFloor.ComputeFloorY(GetViewportRect(), Position.X) - Position.Y;

    /// <summary>
    /// The row-list budget for the current frame, in panel-local y: <see cref="ColumnMaxY"/> normally, or a small
    /// compact cap below <paramref name="y"/> while P1 has a card-select overlay open
    /// (<see cref="CouchColumnFloor.IsP1CardSelectOpen"/>), so P2's own panel stays at its natural size instead of
    /// stretching down over P1's grid while both are picking a card at once (approved spec problem 2).
    /// </summary>
    protected float RowsBudgetMaxY(float y)
    {
        float floor = ColumnMaxY;
        return CouchColumnFloor.IsP1CardSelectOpen ? Mathf.Min(floor, y + CompactRowsBudget) : floor;
    }

    /// <summary>The game's event options ease in from below and fade up; the panels do the same from their side.</summary>
    private void OnVisibilityChanged()
    {
        _appearTween?.Kill();
        if (!Visible)
        {
            _appear = 1f;
            return;
        }

        _appear = 0f;
        Modulate = new Color(1f, 1f, 1f, 0f);
        _appearTween = CreateTween().SetParallel();
        _appearTween.TweenMethod(Callable.From<float>((float v) => _appear = v), 0f, 1f, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        _appearTween.TweenProperty(this, "modulate:a", 1f, 0.25);
    }

    protected void SetTitle(string text)
    {
        Title!.Text = text;
        Title.Position = new Vector2(CouchFrame.PadLeft, CouchFrame.PadTop);
        // A Control outside a Container only ever grows to satisfy its minimum size automatically; it never shrinks
        // back down on its own once the text (and so the minimum size) gets shorter again. Reset Size to the live
        // minimum every time so a stale, taller Size from an earlier, longer title/row visit this same session can't
        // make a bounding-box check (e.g. the cursor-row/hint-in-view layout rule) see a rect bigger than what's
        // actually drawn right now.
        Title.Size = Title.GetMinimumSize();
    }

    /// <summary>Title bottom, where content starts.</summary>
    protected float ContentTop => Title!.Position.Y + Title.GetMinimumSize().Y + 10f;

    /// <summary>Lays rows out top to bottom starting at <paramref name="y"/>; returns the y after the last row.</summary>
    protected float LayoutRows(IReadOnlyList<RowView> rows, float y)
    {
        foreach (RowView row in rows)
        {
            float textWidth = RowWidth - row.TextLeft - 24f;
            row.Label.CustomMinimumSize = new Vector2(textWidth, 0f);
            float height = Mathf.Max(RowIconSize + RowPadding, row.Label.GetMinimumSize().Y + RowPadding + 4f);
            row.Root.Position = new Vector2(RowLeft, y);
            row.Root.Size = new Vector2(RowWidth, height);
            row.Label.Position = new Vector2(row.TextLeft, 0f);
            row.Label.Size = new Vector2(textWidth, height);
            if (row.Icon != null)
            {
                row.Icon.Position = new Vector2(16f, (height - RowIconSize) * 0.5f);
            }

            y += height + RowSpacing;
        }

        return y;
    }

    /// <summary>
    /// Measures the footer's real height (the hint line(s) — a key hint plus, for some panels, a flash message or
    /// an about-text description of unbounded length, e.g. the shop's relic/potion description — and the frame's
    /// bottom padding) by actually setting <see cref="Hint"/>'s text and reading its wrapped size, instead of
    /// reserving a fixed worst-case allowance. Callers know their hint/footer text before laying out rows (it only
    /// depends on state fixed earlier in the frame, e.g. the row under the cursor), so they can measure the real
    /// footer once, subtract it from the row budget passed to <see cref="LayoutRowsScrolled"/>, and then pass the
    /// same text to <see cref="FinishLayout"/> to actually place it (idempotent: setting the same text twice is
    /// free). <paramref name="lines"/> must be the exact lines that will later be passed to
    /// <see cref="FinishLayout"/>.
    /// </summary>
    protected float MeasureFooterHeight(params string[] lines)
    {
        Hint!.Text = HintText(lines);
        return FooterGap + Hint.GetMinimumSize().Y + CouchFrame.PadBottom;
    }

    /// <summary>
    /// Fold-priority last resort for an unbounded-length hint/footer string (e.g. the shop's relic/potion
    /// description): shown in full, wrapped, as long as the panel still fits above <paramref name="floor"/> once
    /// the row list has already given up everything down to just the cursor row (<paramref name="minRowsReserve"/>).
    /// Only if even that doesn't fit does this progressively shorten <paramref name="primary"/> (trimming whole
    /// words, then hard-cutting) until it does. Per senior review: rows must scroll away before this description
    /// ever gets shortened, not the other way around - the old version capped the description to a fixed length
    /// unconditionally, which dropped information P2 needed to decide what to buy even when the column had room.
    /// </summary>
    protected string ShrinkFooterToFit(string primary, string keys, float y, float floor, float minRowsReserve)
    {
        string candidate = primary;
        while (true)
        {
            float footer = MeasureFooterHeight(candidate, keys);
            if (string.IsNullOrEmpty(candidate) || y + minRowsReserve + footer <= floor)
            {
                return candidate;
            }

            int keep = Math.Max(0, candidate.Length - 8);
            int cut = candidate.LastIndexOf(' ', Math.Min(keep, candidate.Length - 1));
            candidate = cut > 0 ? candidate[..cut].TrimEnd() + "…" : candidate[..keep] + "…";
        }
    }

    private string HintText(string[] lines)
    {
        bool flashing = Time.GetTicksMsec() / 1000.0 < _flashUntil;
        IEnumerable<string> all = new[] { flashing ? _flash : "" }.Concat(lines);
        return string.Join("\n", all.Where((string s) => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// Like <see cref="LayoutRows"/>, but when the full list would extend past <paramref name="maxY"/>, shows only
    /// a window of consecutive rows around <paramref name="cursorIndex"/> — as many as fit — and hides the rest, so
    /// the row under the cursor is always visible and the column never grows into whatever sits below it (e.g. P1's
    /// Proceed button; see <see cref="ColumnMaxY"/>). <paramref name="maxY"/> is the actual limit for rows: callers
    /// reserve room for anything they'll still add below (the footer — see <see cref="MeasureFooterHeight"/>)
    /// themselves before calling this, rather than a fixed constant baked in here. When rows are hidden, a
    /// "▲ N more" / "▼ N more" indicator marks whichever end has more, each taking its own small slice of the
    /// budget so it never itself pushes content past <paramref name="maxY"/>. Scroll follows the cursor: called
    /// every frame, the window is recomputed from the current cursor each time. Mirrors the windowed row of cards
    /// <c>CouchTeammateChoicePanel</c> already used for its card strip before this helper existed.
    /// </summary>
    protected float LayoutRowsScrolled(IReadOnlyList<RowView> rows, float y, float maxY, int cursorIndex)
    {
        if (rows.Count == 0)
        {
            _scrollUpIndicator!.Visible = false;
            _scrollDownIndicator!.Visible = false;
            return y;
        }

        float[] heights = new float[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            RowView row = rows[i];
            float textWidth = RowWidth - row.TextLeft - 24f;
            row.Label.CustomMinimumSize = new Vector2(textWidth, 0f);
            heights[i] = Mathf.Max(RowIconSize + RowPadding, row.Label.GetMinimumSize().Y + RowPadding + 4f);
        }

        float budget = Mathf.Max(0f, maxY - y);
        cursorIndex = Mathf.Clamp(cursorIndex, 0, rows.Count - 1);
        (int start, int end) = WindowFor(heights, budget, cursorIndex);

        // If that window had to cut rows, re-run it against a slightly smaller budget that leaves room for whichever
        // "N more" indicator(s) will now be shown, so the indicator itself never pushes past maxY. Cutting the
        // budget can only shrink the window further (monotonic), so start/end still land inside the first window.
        float reserve = (start > 0 ? ScrollIndicatorHeight : 0f) + (end < rows.Count ? ScrollIndicatorHeight : 0f);
        if (reserve > 0f)
        {
            (start, end) = WindowFor(heights, Mathf.Max(0f, budget - reserve), cursorIndex);
        }

        float rowY = y;
        if (start > 0)
        {
            _scrollUpIndicator!.Text = $"▲ {start} more";
            _scrollUpIndicator.Position = new Vector2(RowLeft, rowY);
            _scrollUpIndicator.Size = new Vector2(RowWidth, ScrollIndicatorHeight);
            _scrollUpIndicator.HorizontalAlignment = HorizontalAlignment.Center;
            _scrollUpIndicator.Visible = true;
            rowY += ScrollIndicatorHeight;
        }
        else
        {
            _scrollUpIndicator!.Visible = false;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            RowView row = rows[i];
            bool shown = i >= start && i < end;
            row.Root.Visible = shown;
            if (!shown)
            {
                continue;
            }

            float textWidth = RowWidth - row.TextLeft - 24f;
            row.Root.Position = new Vector2(RowLeft, rowY);
            row.Root.Size = new Vector2(RowWidth, heights[i]);
            row.Label.Position = new Vector2(row.TextLeft, 0f);
            row.Label.Size = new Vector2(textWidth, heights[i]);
            if (row.Icon != null)
            {
                row.Icon.Position = new Vector2(16f, (heights[i] - RowIconSize) * 0.5f);
            }

            rowY += heights[i] + RowSpacing;
        }

        if (end < rows.Count)
        {
            _scrollDownIndicator!.Text = $"▼ {rows.Count - end} more";
            _scrollDownIndicator.Position = new Vector2(RowLeft, rowY);
            _scrollDownIndicator.Size = new Vector2(RowWidth, ScrollIndicatorHeight);
            _scrollDownIndicator.HorizontalAlignment = HorizontalAlignment.Center;
            _scrollDownIndicator.Visible = true;
            rowY += ScrollIndicatorHeight;
        }
        else
        {
            _scrollDownIndicator!.Visible = false;
        }

        return rowY;
    }

    /// <summary>The widest contiguous [start, end) window around <paramref name="cursorIndex"/> that fits in
    /// <paramref name="budget"/>, growing outward row by row from the cursor.</summary>
    private (int Start, int End) WindowFor(float[] heights, float budget, int cursorIndex)
    {
        float total = heights.Sum() + RowSpacing * Mathf.Max(0, heights.Length - 1);
        if (total <= budget)
        {
            return (0, heights.Length);
        }

        int start = cursorIndex;
        int end = cursorIndex + 1;
        float used = heights[cursorIndex];
        bool grew = true;
        while (grew)
        {
            grew = false;
            if (end < heights.Length && used + RowSpacing + heights[end] <= budget)
            {
                used += RowSpacing + heights[end];
                end++;
                grew = true;
            }

            if (start > 0 && used + RowSpacing + heights[start - 1] <= budget)
            {
                used += RowSpacing + heights[start - 1];
                start--;
                grew = true;
            }
        }

        return (start, end);
    }

    /// <summary>Writes the hint (with any flash message) below <paramref name="y"/> and sizes the background.</summary>
    protected void FinishLayout(float y, params string[] lines)
    {
        Hint!.Text = HintText(lines);
        Hint.Position = new Vector2(CouchFrame.PadLeft, y + FooterGap);
        // See SetTitle: reset Size to the live minimum so a stale, taller Size from an earlier (longer) hint this
        // session can't make the hint's measured rect bigger than the frame it's actually drawn inside of.
        Hint.Size = Hint.GetMinimumSize();
        Background!.Size = new Vector2(PanelWidth, Hint.Position.Y + Hint.Size.Y + CouchFrame.PadBottom);
    }

    protected RowView CreateRow(Texture2D? icon, CouchButtonKind kind = CouchButtonKind.Event)
    {
        CouchButton root = new() { ZIndex = 1, Kind = kind };
        AddChild(root);
        TextureRect? iconRect = null;
        float textLeft = 26f;
        if (icon != null)
        {
            iconRect = new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Size = new Vector2(RowIconSize, RowIconSize),
                MouseFilter = MouseFilterEnum.Ignore
            };
            root.AddChild(iconRect);
            textLeft = RowIconSize + 26f;
        }

        Label label = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        if (kind == CouchButtonKind.Reward)
        {
            CouchStyle.Text(label, RowFontSize + 2, outline: 8);
            label.AddThemeColorOverride("font_outline_color", StsColors.rewardLabelOutline);
        }
        else
        {
            CouchStyle.Text(label, RowFontSize);
        }

        root.AddChild(label);
        root.Label = label;
        return new RowView(root, label, iconRect, textLeft);
    }

    protected static void StyleRow(RowView row, bool isCursor, bool dimmed, bool picked = false)
    {
        row.Root.SetState(isCursor, dimmed, picked);
    }

    protected static void FreeRows(List<RowView> rows)
    {
        foreach (RowView row in rows)
        {
            row.Root.QueueFree();
        }

        rows.Clear();
    }

    /// <summary>Shows a short message above the hint; most are refusals, which also get the "no" sound.</summary>
    protected void Flash(string message, bool denied = true)
    {
        _flash = message;
        _flashUntil = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        if (denied)
        {
            CouchSfx.Deny();
        }
    }

    protected string Keys(string keyboard, string controller)
    {
        return LastInputFromController ? controller : keyboard;
    }

    protected static string SeatLabel(Player player)
    {
        return CouchSeats.FindByPlayer(player.NetId)?.Label ?? "P2";
    }

    /// <summary>Tells the driver, with a big banner, that the teammate still has something to finish here.</summary>
    protected static void AnnounceWaiting(Player teammate, string what)
    {
        NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create($"Waiting for {SeatLabel(teammate)} to finish {what}"));
    }

    /// <summary>True while the teammate is picking a potion to throw out to make room.</summary>
    protected bool IsDiscardingPotion => _discardOwner != null;

    /// <summary>
    /// The teammate's belt is full and they want another potion: list their potions to discard one (as the driver can
    /// from the top bar), plus "Keep my potions". Once a slot is free, <paramref name="afterRoomMade"/> runs (take the
    /// reward, buy the potion).
    /// </summary>
    protected void OpenPotionDiscard(Player owner, Action afterRoomMade)
    {
        ClosePotionDiscard();
        _discardOwner = owner;
        _afterDiscard = afterRoomMade;
        _discardPotions = owner.PotionSlots.OfType<PotionModel>().ToList();
        foreach (PotionModel potion in _discardPotions)
        {
            RowView row = CreateRow(SafePotionImage(potion));
            string description = CouchText.Plain(potion.DynamicDescription.GetFormattedText());
            row.Label.Text = $"Discard {CouchText.Plain(potion.Title.GetFormattedText())}{(description.Length > 0 ? "\n" + description : "")}";
            _discardRows.Add(row);
        }

        RowView keep = CreateRow(null);
        keep.Label.Text = "Keep my potions";
        _discardRows.Add(keep);
        _discardCursor = 0;
        _discardSent = false;
    }

    protected void ClosePotionDiscard()
    {
        FreeRows(_discardRows);
        _discardPotions = new List<PotionModel>();
        _discardOwner = null;
        _afterDiscard = null;
        _discardSent = false;
    }

    /// <summary>Call every frame: once the discard has freed a slot, closes the list and runs the follow-up.</summary>
    protected void UpdatePotionDiscard()
    {
        if (_discardOwner != null && _discardSent && _discardOwner.HasOpenPotionSlots)
        {
            Action? next = _afterDiscard;
            ClosePotionDiscard();
            next?.Invoke();
        }
    }

    protected float LayoutPotionDiscard(float y)
    {
        for (int i = 0; i < _discardRows.Count; i++)
        {
            StyleRow(_discardRows[i], i == _discardCursor, dimmed: _discardSent);
        }

        return LayoutRows(_discardRows, y);
    }

    protected void OnPotionDiscardCommand(CouchHudCommand command)
    {
        int count = _discardRows.Count;
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                _discardCursor = (_discardCursor - 1 + count) % count;
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _discardCursor = (_discardCursor + 1) % count;
                break;
            case CouchHudCommand.Accept:
                if (_discardCursor >= _discardPotions.Count)
                {
                    ClosePotionDiscard();
                }
                else if (!_discardSent)
                {
                    if (CouchRemotePlay.TryDiscardPotion(_discardOwner!, _discardPotions[_discardCursor], out string reason))
                    {
                        _discardSent = true;
                    }
                    else
                    {
                        Flash(reason);
                    }
                }

                break;
            case CouchHudCommand.Back:
                ClosePotionDiscard();
                break;
        }
    }

    private static Texture2D? SafePotionImage(PotionModel potion)
    {
        try
        {
            return potion.Image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected readonly record struct RowView(CouchButton Root, Label Label, TextureRect? Icon, float TextLeft);
}

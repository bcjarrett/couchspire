using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace LocalMultiControl.Scripts.Runtime.Couch;

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

    /// <summary>Writes the hint (with any flash message) below <paramref name="y"/> and sizes the background.</summary>
    protected void FinishLayout(float y, params string[] lines)
    {
        bool flashing = Time.GetTicksMsec() / 1000.0 < _flashUntil;
        IEnumerable<string> all = new[] { flashing ? _flash : "" }.Concat(lines);
        Hint!.Text = string.Join("\n", all.Where((string s) => !string.IsNullOrEmpty(s)));
        Hint.Position = new Vector2(CouchFrame.PadLeft, y + 4f);
        Background!.Size = new Vector2(PanelWidth, Hint.Position.Y + Hint.GetMinimumSize().Y + CouchFrame.PadBottom);
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

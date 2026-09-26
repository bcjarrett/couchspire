using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Shared look and plumbing for the teammate's side panels (rest site, treasure, shop, card picker): a dark framed
/// panel with a title, a list of rows with a cursor, and a hint line that shows keyboard or controller keys depending on
/// what the teammate last used.
/// </summary>
internal abstract partial class CouchPanel : Control
{
    protected const float RowGap = 6f;

    private const double FlashSeconds = 3.0;

    private static readonly Color RowColor = new(0.1f, 0.14f, 0.18f, 0.92f);

    private static readonly Color RowCursorColor = new(0.28f, 0.34f, 0.4f, 0.97f);

    private static readonly Color PickedColor = new(0.36f, 0.3f, 0.12f, 0.97f);

    private static readonly Color CursorBorder = new(1f, 0.78f, 0.25f);

    private string _flash = "";

    private double _flashUntil;

    protected Panel? Background { get; private set; }

    protected Label? Title { get; private set; }

    protected Label? Hint { get; private set; }

    protected bool LastInputFromController { get; set; }

    protected abstract float PanelWidth { get; }

    /// <summary>Row icon size; compact panels use smaller rows.</summary>
    protected virtual float RowIconSize => 38f;

    protected virtual int RowFontSize => 18;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        Background = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        Background.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.05f, 0.07f, 0.9f),
            BorderColor = new Color(0.55f, 0.48f, 0.32f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        });
        AddChild(Background);
        Title = CreateLabel(23, new Color("f3efe6"), wrap: true);
        Hint = CreateLabel(16, new Color("d8d2c4"), wrap: true);
        SetProcess(true);
    }

    /// <summary>
    /// Places the panel against the left or right edge of the screen. On the right it starts below the teammate's
    /// relic bar.
    /// </summary>
    protected void PlaceOnSide(bool left, float top, float margin = 36f)
    {
        Vector2 viewport = GetViewportRect().Size;
        Position = new Vector2(left ? margin : viewport.X - PanelWidth - margin, left ? top : CouchTeammateRelicBar.TopBelowBar(top));
    }

    protected void SetTitle(string text)
    {
        Title!.Text = text;
        Title.Position = new Vector2(16f, 12f);
    }

    /// <summary>Title bottom, where content starts.</summary>
    protected float ContentTop => Title!.Position.Y + Title.GetMinimumSize().Y + 10f;

    /// <summary>Lays rows out top to bottom starting at <paramref name="y"/>; returns the y after the last row.</summary>
    protected float LayoutRows(IReadOnlyList<RowView> rows, float y)
    {
        foreach (RowView row in rows)
        {
            float textWidth = PanelWidth - 24f - row.TextLeft - 10f;
            row.Label.CustomMinimumSize = new Vector2(textWidth, 0f);
            float height = Mathf.Max(RowIconSize + 10f, row.Label.GetMinimumSize().Y + 12f);
            row.Root.Position = new Vector2(12f, y);
            row.Root.Size = new Vector2(PanelWidth - 24f, height);
            row.Label.Position = new Vector2(row.TextLeft, 0f);
            row.Label.Size = new Vector2(textWidth, height);
            if (row.Icon != null)
            {
                row.Icon.Position = new Vector2(8f, (height - RowIconSize) * 0.5f);
            }

            y += height + RowGap;
        }

        return y;
    }

    /// <summary>Writes the hint (with any flash message) below <paramref name="y"/> and sizes the background.</summary>
    protected void FinishLayout(float y, params string[] lines)
    {
        IEnumerable<string> all = new[] { Time.GetTicksMsec() / 1000.0 < _flashUntil ? _flash : "" }.Concat(lines);
        Hint!.Text = string.Join("\n", all.Where((string s) => !string.IsNullOrEmpty(s)));
        Hint.Position = new Vector2(16f, y + 4f);
        Background!.Size = new Vector2(PanelWidth, Hint.Position.Y + Hint.GetMinimumSize().Y + 14f);
    }

    protected RowView CreateRow(Texture2D? icon)
    {
        StyleBoxFlat style = new()
        {
            BgColor = RowColor,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        Panel root = new() { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 1 };
        root.AddThemeStyleboxOverride("panel", style);
        TextureRect? iconRect = null;
        float textLeft = 14f;
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
            textLeft = RowIconSize + 16f;
        }

        Label label = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", RowFontSize);
        label.AddThemeColorOverride("font_color", new Color("f3efe6"));
        root.AddChild(label);
        AddChild(root);
        return new RowView(root, style, label, iconRect, textLeft);
    }

    protected static void StyleRow(RowView row, bool isCursor, bool dimmed, bool picked = false)
    {
        row.Style.BgColor = picked ? PickedColor : isCursor ? RowCursorColor : RowColor;
        int border = isCursor ? 2 : 0;
        row.Style.BorderColor = CursorBorder;
        row.Style.BorderWidthLeft = border;
        row.Style.BorderWidthRight = border;
        row.Style.BorderWidthTop = border;
        row.Style.BorderWidthBottom = border;
        row.Root.Modulate = new Color(1f, 1f, 1f, dimmed ? 0.45f : 1f);
    }

    protected static void FreeRows(List<RowView> rows)
    {
        foreach (RowView row in rows)
        {
            row.Root.QueueFree();
        }

        rows.Clear();
    }

    protected Label CreateLabel(int fontSize, Color color, bool wrap)
    {
        Label label = new() { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 1 };
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(PanelWidth - 32f, 0f);
        }

        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color("111111"));
        label.AddThemeConstantOverride("outline_size", 4);
        AddChild(label);
        return label;
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

    protected static void DisableInteraction(Control control)
    {
        control.MouseFilter = MouseFilterEnum.Ignore;
        control.FocusMode = FocusModeEnum.None;
        foreach (Node child in control.GetChildren())
        {
            if (child is Control childControl)
            {
                DisableInteraction(childControl);
            }
        }
    }

    protected readonly record struct RowView(Panel Root, StyleBoxFlat Style, Label Label, TextureRect? Icon, float TextLeft);
}

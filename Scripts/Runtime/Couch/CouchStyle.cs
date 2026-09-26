using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The game's own look for the teammate's UI: its fonts, text colors, tooltip frame, event/reward button art, the
/// controller selection reticle, and the timings of their focus animations. Values are taken from the game's scenes
/// (hover_tip, event_option_button, reward_button, potion_holder, selection_reticle) so the teammate's panels read
/// as part of the game. Every asset is optional: if one fails to load, callers fall back to plain shapes.
/// </summary>
internal static class CouchStyle
{
    public const string FontRegularPath = "res://themes/kreon_regular_shared.tres";

    public const string FontBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";

    public const string FramePath = "res://images/ui/hover_tip.png";

    public const string ButtonPath = "res://images/packed/common_ui/event_button.png";

    public const string ButtonOutlinePath = "res://images/packed/common_ui/event_button_outline.png";

    public const string RewardButtonPath = "res://images/ui/reward_screen/reward_item_button.png";

    public const string PotionPlaceholderPath = "res://images/packed/potions/potion_placeholder.png";

    public const string HsvShaderPath = "res://shaders/hsv.gdshader";

    public const string AdditiveMaterialPath = "res://themes/canvas_item_material_additive_shared.tres";

    public const string ReticleScenePath = "res://scenes/ui/selection_reticle.tscn";

    /// <summary>Body text (hover tips, event options, rewards).</summary>
    public static readonly Color Cream = StsColors.cream;

    /// <summary>Titles (hover tip titles).</summary>
    public static readonly Color Gold = StsColors.gold;

    public static readonly Color Muted = new(1f, 0.965f, 0.886f, 0.72f);

    public static readonly Color TextShadow = new(0f, 0f, 0f, 0.25f);

    public static readonly Color TextOutline = new(0.08f, 0.07f, 0.06f, 1f);

    private static readonly Dictionary<string, Resource?> Cache = new();

    public static Font? Regular => Load<Font>(FontRegularPath);

    public static Font? Bold => Load<Font>(FontBoldPath);

    public static T? Load<T>(string path)
        where T : Resource
    {
        if (!Cache.TryGetValue(path, out Resource? resource))
        {
            try
            {
                resource = ResourceLoader.Load<T>(path);
            }
            catch (Exception ex)
            {
                CouchLog.Warn($"Couldn't load {path}: {ex.Message}");
                resource = null;
            }

            if (resource == null)
            {
                CouchLog.Warn($"Missing game asset {path}; using a plain fallback.");
            }

            Cache[path] = resource;
        }

        return resource as T;
    }

    /// <summary>
    /// Styles a label like the game's text: Kreon, cream (or <paramref name="color"/>), with the soft drop shadow the
    /// hover tips use. <paramref name="outline"/> adds a dark outline for text drawn straight over the battlefield.
    /// </summary>
    public static void Text(Label label, int size, bool bold = false, Color? color = null, int outline = 0)
    {
        Font? font = bold ? Bold : Regular;
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }

        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Cream);
        label.AddThemeColorOverride("font_shadow_color", TextShadow);
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeColorOverride("font_outline_color", TextOutline);
        label.AddThemeConstantOverride("outline_size", outline);
    }

    public static Label CreateLabel(Node parent, int size, bool bold = false, Color? color = null, int outline = 0, float wrapWidth = 0f)
    {
        Label label = new() { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 1 };
        if (wrapWidth > 0f)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(wrapWidth, 0f);
        }

        Text(label, size, bold, color, outline);
        parent.AddChild(label);
        return label;
    }

    public const string GoldIconPath = "res://images/packed/sprite_fonts/gold_icon.png";

    public const string StarIconPath = "res://images/packed/sprite_fonts/star_icon.png";

    public const string DrawPileIconPath = "res://images/packed/combat_ui/draw_pile.png";

    public const string DiscardPileIconPath = "res://images/packed/combat_ui/discard_pile.png";

    /// <summary>A one-line rich text label (for inline icons) in the game's font, with a dark outline.</summary>
    public static RichTextLabel CreateRichLabel(Node parent, int size, int outline)
    {
        RichTextLabel label = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            BbcodeEnabled = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            FitContent = true,
            ClipContents = false,
            ZIndex = 1
        };
        if (Regular != null)
        {
            label.AddThemeFontOverride("normal_font", Regular);
        }

        if (Bold != null)
        {
            label.AddThemeFontOverride("bold_font", Bold);
        }

        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeFontSizeOverride("bold_font_size", size);
        label.AddThemeColorOverride("default_color", Cream);
        label.AddThemeColorOverride("font_shadow_color", TextShadow);
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeColorOverride("font_outline_color", TextOutline);
        label.AddThemeConstantOverride("outline_size", outline);
        parent.AddChild(label);
        return label;
    }

    /// <summary>An inline icon for rich text.</summary>
    public static string Icon(string path, int size)
    {
        return $"[img={size}x{size}]{path}[/img]";
    }

    /// <summary>The game's controller focus corners, sized to <paramref name="size"/>; hidden until selected.</summary>
    public static NSelectionReticle? CreateReticle(Control parent)
    {
        PackedScene? scene = Load<PackedScene>(ReticleScenePath);
        if (scene == null)
        {
            return null;
        }

        NSelectionReticle reticle = scene.Instantiate<NSelectionReticle>(PackedScene.GenEditState.Disabled);
        reticle.MouseFilter = Control.MouseFilterEnum.Ignore;
        parent.AddChild(reticle);
        return reticle;
    }

    /// <summary>Fits a reticle over a <paramref name="size"/>-sized area at <paramref name="position"/>.</summary>
    public static void PlaceReticle(NSelectionReticle? reticle, Vector2 position, Vector2 size)
    {
        if (reticle == null)
        {
            return;
        }

        reticle.Position = position;
        reticle.Size = size;
        reticle.PivotOffset = size * 0.5f;
    }

    public static float Smooth(double delta, float speed)
    {
        return Mathf.Clamp((float)delta * speed, 0f, 1f);
    }
}

/// <summary>
/// A panel background drawn with the game's hover tip frame (and its drop shadow), sized by setting <see cref="Size"/>.
/// Content should keep the frame's padding: <see cref="PadLeft"/>, <see cref="PadTop"/>, <see cref="PadRight"/>,
/// <see cref="PadBottom"/>.
/// </summary>
internal sealed partial class CouchFrame : Control
{
    public const float PadLeft = 22f;

    public const float PadTop = 16f;

    public const float PadRight = 45f;

    public const float PadBottom = 28f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Texture2D? texture = CouchStyle.Load<Texture2D>(CouchStyle.FramePath);
        if (texture == null)
        {
            Panel fallback = new() { MouseFilter = MouseFilterEnum.Ignore };
            fallback.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.08f, 0.1f, 0.92f),
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8
            });
            fallback.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(fallback);
            return;
        }

        NinePatchRect shadow = Patch(texture);
        shadow.Modulate = new Color(0f, 0f, 0f, 0.25f);
        shadow.OffsetLeft = 8f;
        shadow.OffsetTop = 8f;
        shadow.OffsetRight = 8f;
        shadow.OffsetBottom = 8f;
        AddChild(shadow);
        AddChild(Patch(texture));
    }

    private static NinePatchRect Patch(Texture2D texture)
    {
        NinePatchRect patch = new()
        {
            Texture = texture,
            MouseFilter = MouseFilterEnum.Ignore,
            PatchMarginLeft = 55,
            PatchMarginTop = 43,
            PatchMarginRight = 91,
            PatchMarginBottom = 32,
            AxisStretchHorizontal = NinePatchRect.AxisStretchMode.Tile,
            AxisStretchVertical = NinePatchRect.AxisStretchMode.Tile
        };
        patch.SetAnchorsPreset(LayoutPreset.FullRect);
        return patch;
    }
}

internal enum CouchButtonKind
{
    /// <summary>The event option button art (events, rest site, treasure, shop, card picker extras).</summary>
    Event,

    /// <summary>The reward screen's item button art.</summary>
    Reward
}

/// <summary>
/// One option row in the teammate's panels, drawn like the game's event option buttons (their art, outline glow and
/// brightness shader) with the controller selection reticle, and animated like them: on focus it grows slightly, the
/// outline glows and the art brightens; on losing focus it eases back.
/// </summary>
internal sealed partial class CouchButton : Control
{
    private const float ArtHeight = 100f;

    private static readonly StringName ShaderV = new("v");

    private static readonly Color ButtonColor = new(1f, 1f, 1f, 0.9f);

    private static readonly Color PickedColor = new(1f, 0.82f, 0.45f, 0.95f);

    private Control? _art;

    private TextureRect? _rewardImage;

    private NinePatchRect? _image;

    private NinePatchRect? _outline;

    private ShaderMaterial? _hsv;

    private Panel? _fallback;

    private StyleBoxFlat? _fallbackStyle;

    private Tween? _tween;

    private bool _focused;

    private bool _picked;

    private bool _dimmed;

    private bool _styled;

    public NSelectionReticle? Reticle { get; private set; }

    /// <summary>Which of the game's buttons to look like; set before adding the row.</summary>
    public CouchButtonKind Kind { get; set; } = CouchButtonKind.Event;

    /// <summary>The row's text; reward rows turn it gold on focus, as the reward screen does.</summary>
    public Label? Label { get; set; }

    /// <summary>Resting brightness of the art (both buttons).</summary>
    private const float DefaultV = 0.9f;

    private float FocusV => Kind == CouchButtonKind.Reward ? 1.1f : 1.2f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Texture2D? texture = CouchStyle.Load<Texture2D>(Kind == CouchButtonKind.Reward ? CouchStyle.RewardButtonPath : CouchStyle.ButtonPath);
        Shader? shader = CouchStyle.Load<Shader>(CouchStyle.HsvShaderPath);
        if (shader != null)
        {
            _hsv = new ShaderMaterial { Shader = shader };
            _hsv.SetShaderParameter("h", 1f);
            _hsv.SetShaderParameter("s", 1f);
            _hsv.SetShaderParameter(ShaderV, DefaultV);
        }

        if (texture != null && Kind == CouchButtonKind.Reward)
        {
            // NRewardButton: the art stretched over the whole button.
            _rewardImage = new TextureRect
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
                Material = _hsv
            };
            _rewardImage.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_rewardImage);
        }
        else if (texture == null)
        {
            _fallbackStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.1f, 0.14f, 0.18f, 0.92f),
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6
            };
            _fallback = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            _fallback.AddThemeStyleboxOverride("panel", _fallbackStyle);
            _fallback.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_fallback);
        }
        else
        {
            // The art is made for 100px-tall buttons; draw it at that height and scale it down to the row.
            _art = new Control { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(_art);
            _image = ButtonPatch(texture);
            _image.Modulate = ButtonColor;
            _image.Material = _hsv;
            _art.AddChild(_image);
            Texture2D? outlineTexture = CouchStyle.Load<Texture2D>(CouchStyle.ButtonOutlinePath);
            if (outlineTexture != null)
            {
                _outline = ButtonPatch(outlineTexture);
                _outline.Modulate = new Color(0f, 0f, 0f, 0f);
                _outline.Material = CouchStyle.Load<Material>(CouchStyle.AdditiveMaterialPath);
                _art.AddChild(_outline);
            }
        }

        Reticle = CouchStyle.CreateReticle(this);
        Resized += OnResized;
        OnResized();
    }

    /// <summary>Applies focus/picked/dimmed, animating only what changed.</summary>
    public void SetState(bool focused, bool dimmed, bool picked = false)
    {
        if (_styled && focused == _focused && dimmed == _dimmed && picked == _picked)
        {
            return;
        }

        bool firstUnfocused = !_styled && !focused;
        bool focusChanged = !_styled || focused != _focused;
        _styled = true;
        _focused = focused;
        _dimmed = dimmed;
        _picked = picked;
        Modulate = new Color(1f, 1f, 1f, dimmed ? 0.45f : 1f);
        if (_image != null)
        {
            _image.Modulate = picked ? PickedColor : ButtonColor;
        }

        if (_rewardImage != null)
        {
            _rewardImage.Modulate = picked ? PickedColor : Colors.White;
        }

        if (_fallbackStyle != null)
        {
            _fallbackStyle.BgColor = picked ? new Color(0.36f, 0.3f, 0.12f, 0.97f) : focused ? new Color(0.28f, 0.34f, 0.4f, 0.97f) : new Color(0.1f, 0.14f, 0.18f, 0.92f);
        }

        if (!focusChanged || firstUnfocused)
        {
            // Nothing to animate (or a row that has just appeared unfocused): set the resting look directly.
            ApplyOutline();
            return;
        }

        // NEventOptionButton.OnFocus / OnUnfocus.
        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        if (focused)
        {
            _tween.TweenProperty(this, "scale", Vector2.One * 1.01f, 0.05);
            if (_outline != null)
            {
                _tween.TweenProperty(_outline, "modulate", picked ? CouchStyle.Gold : StsColors.blueGlow, 0.05);
            }

            _hsv?.SetShaderParameter(ShaderV, FocusV);
            ColorLabel(focused: true);
            Reticle?.OnSelect();
        }
        else
        {
            _tween.TweenProperty(this, "scale", Vector2.One, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            if (_hsv != null)
            {
                _tween.TweenMethod(Callable.From<float>((float v) => _hsv.SetShaderParameter(ShaderV, v)), FocusV, DefaultV, 0.5)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            }

            ColorLabel(focused: false);

            if (_outline != null)
            {
                _tween.TweenProperty(_outline, "modulate", picked ? PickedOutline : new Color(0f, 0f, 0f, 0f), 0.3);
            }

            Reticle?.OnDeselect();
        }
    }

    /// <summary>NRewardButton: cream text with a teal outline, gold with a dark gold outline while focused.</summary>
    private void ColorLabel(bool focused)
    {
        if (Kind != CouchButtonKind.Reward || Label == null)
        {
            return;
        }

        Label.AddThemeColorOverride("font_color", focused ? StsColors.gold : StsColors.cream);
        Label.AddThemeColorOverride("font_outline_color", focused ? StsColors.rewardLabelGoldOutline : StsColors.rewardLabelOutline);
    }

    private static Color PickedOutline => new(CouchStyle.Gold.R, CouchStyle.Gold.G, CouchStyle.Gold.B, 0.8f);

    private void ApplyOutline()
    {
        if (_outline == null)
        {
            return;
        }

        _outline.Modulate = _focused ? (_picked ? CouchStyle.Gold : StsColors.blueGlow) : _picked ? PickedOutline : new Color(0f, 0f, 0f, 0f);
    }

    private void OnResized()
    {
        PivotOffset = Size * 0.5f;
        CouchStyle.PlaceReticle(Reticle, Vector2.Zero, Size);
        if (_art == null || Size.X < 1f || Size.Y < 1f)
        {
            return;
        }

        float scale = Mathf.Min(0.5f, Size.Y / ArtHeight);
        _art.Scale = new Vector2(scale, scale);
        Vector2 artSize = Size / scale;
        _image!.Size = artSize;
        if (_outline != null)
        {
            // The game draws the outline a touch larger than the button.
            _outline.Size = artSize;
            _outline.PivotOffset = artSize * 0.5f;
            _outline.Scale = new Vector2(1.005f, 1.05f);
        }
    }

    private static NinePatchRect ButtonPatch(Texture2D texture)
    {
        return new NinePatchRect
        {
            Texture = texture,
            MouseFilter = MouseFilterEnum.Ignore,
            PatchMarginLeft = 192,
            PatchMarginTop = 50,
            PatchMarginRight = 192,
            PatchMarginBottom = 50
        };
    }
}

using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Card nodes for couch UI, created outside the game's <c>NodePool</c>. Pooled cards come back to the game with only
/// position, scale, tint and visibility reset; draw order and mouse settings stick. Cards we raise (ZIndex) or make
/// click-through would then show up in the driver's shop, upgrade and removal screens drawn over the overlay and
/// ignoring the mouse. Private instances avoid that entirely.
/// </summary>
internal static class CouchCards
{
    private const string CardScenePath = "res://scenes/cards/card.tscn";

    private static readonly StringName PlacedMeta = "couch_placed";

    private static readonly StringName GlowMeta = "couch_glow";

    /// <summary>A display-only card node for <paramref name="card"/>, added under <paramref name="parent"/>.</summary>
    public static NCard Create(CardModel card, Node parent)
    {
        NCard node = PreloadManager.Cache.GetScene(CardScenePath).Instantiate<NCard>(PackedScene.GenEditState.Disabled);
        node.OnInstantiated();
        node.Model = card;
        DisableInteraction(node);
        parent.AddChild(node);
        DisableInteraction(node);
        node.UpdateVisuals(card.Pile?.Type ?? PileType.None, CardPreviewMode.Normal);
        return node;
    }

    /// <summary>
    /// Moves a card toward its spot the way the game's hand holders do (position at 7/s, scale at 8/s); the first call
    /// places it directly.
    /// </summary>
    public static void Glide(NCard node, Vector2 position, float scale)
    {
        if (!node.HasMeta(PlacedMeta))
        {
            node.SetMeta(PlacedMeta, true);
            node.Position = position;
            node.Scale = new Vector2(scale, scale);
            return;
        }

        double delta = node.GetProcessDeltaTime();
        node.Position = node.Position.Lerp(position, CouchStyle.Smooth(delta, 7f));
        float current = Mathf.Lerp(node.Scale.X, scale, CouchStyle.Smooth(delta, 8f));
        node.Scale = new Vector2(current, current);
    }

    /// <summary>
    /// The card's outline glow, as the driver's hand shows it (<c>NHandCardHolder.UpdateCard</c>): null hides it.
    /// Only animates when the color changes.
    /// </summary>
    public static void SetGlow(NCard node, Color? color)
    {
        string key = color?.ToHtml() ?? "";
        if (node.GetMeta(GlowMeta, "").AsString() == key || node.CardHighlight == null)
        {
            return;
        }

        node.SetMeta(GlowMeta, key);
        if (color.HasValue)
        {
            node.CardHighlight.Modulate = color.Value;
            node.CardHighlight.AnimShow();
        }
        else
        {
            node.CardHighlight.AnimHide();
        }
    }

    /// <summary>The glow a hand card gets: red or gold for cards that call for it, blue when playable.</summary>
    public static Color? HandGlow(CardModel card, bool canAct)
    {
        bool playable = canAct && card.CanPlay();
        if (card.ShouldGlowRed)
        {
            return NCardHighlight.red;
        }

        if (!playable)
        {
            return null;
        }

        return card.ShouldGlowGold ? NCardHighlight.gold : NCardHighlight.playableColor;
    }

    public static void Free(NCard? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        node.Model = null;
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }

    private static void DisableInteraction(Control control)
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        control.FocusMode = Control.FocusModeEnum.None;
        foreach (Node child in control.GetChildren())
        {
            if (child is Control childControl)
            {
                DisableInteraction(childControl);
            }
        }
    }
}

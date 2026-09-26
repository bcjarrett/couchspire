using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Card nodes for couch UI, created outside the game's <c>NodePool</c>. Pooled cards come back to the game with only
/// position, scale, tint and visibility reset; draw order and mouse settings stick. Cards we raise (ZIndex) or make
/// click-through would then show up in the driver's shop, upgrade and removal screens drawn over the overlay and
/// ignoring the mouse. Private instances avoid that entirely.
/// </summary>
internal static class CouchCards
{
    private const string CardScenePath = "res://scenes/cards/card.tscn";

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

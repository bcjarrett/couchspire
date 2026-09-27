using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Shows the teammate's deck changes outside combat the way the game shows the driver's: a transformed card morphs
/// into its replacement (<see cref="NCardTransformVfx"/>), an upgraded card flashes its upgrade
/// (<see cref="NCardUpgradeVfx"/>), and a new card pops up and flies into the deck (what <c>CardCmd.PreviewCardPileAdd</c>
/// does). The game only plays these for the local player's cards. They play on the teammate's side of the screen (where
/// their panels are), not in the middle, so the driver's own choices stay visible. Relics are shown by
/// <see cref="CouchTeammateRelicBar"/>.
/// </summary>
internal sealed partial class CouchTeammateDeckChanges : Node
{
    /// <summary>How long a new card is shown before it flies to the deck (the game holds the driver's for 1.2s).</summary>
    private const float PreviewSeconds = 0.7f;

    /// <summary>Where the effects play; a child of the teammate's panel layer.</summary>
    private Control? _host;

    private Player? _watched;

    private List<CardModel> _deck = new();

    private Dictionary<CardModel, int> _upgradeLevels = new();

    public override void _Ready()
    {
        _host = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_host);
    }

    public override void _Process(double delta)
    {
        Player? teammate = CouchTeammate.FindTeammate();
        if (teammate != _watched)
        {
            _watched = teammate;
            Snapshot(teammate);
        }

        if (teammate != null && !CombatManager.Instance.IsInProgress)
        {
            DetectChanges(teammate);
        }
    }

    private void DetectChanges(Player teammate)
    {
        List<CardModel> deck = PileType.Deck.GetPile(teammate).Cards.ToList();
        List<CardModel> added = deck.Where((CardModel c) => !_deck.Contains(c)).ToList();
        List<CardModel> removed = _deck.Where((CardModel c) => !deck.Contains(c)).ToList();
        List<CardModel> upgraded = deck.Where((CardModel c) => _upgradeLevels.TryGetValue(c, out int level) && c.CurrentUpgradeLevel > level).ToList();
        if (added.Count == 0 && removed.Count == 0 && upgraded.Count == 0)
        {
            return;
        }

        Snapshot(teammate);
        bool transformed = added.Count == 1 && removed.Count == 1;
        CouchLog.Info($"Teammate {teammate.NetId} deck changed: "
            + (transformed ? $"{removed[0].Title} -> {added[0].Title} (transformed)" : string.Join(", ", added.Select((CardModel c) => $"+{c.Title}").Concat(removed.Select((CardModel c) => $"-{c.Title}"))))
            + (upgraded.Count > 0 ? $"; upgraded {string.Join(", ", upgraded.Select((CardModel c) => c.Title))}" : ""));

        // If the teammate is on the main screen, the game shows these itself.
        if (LocalContext.IsMe(teammate) || _host == null || NRun.Instance == null)
        {
            return;
        }

        int slot = 0;
        if (transformed)
        {
            Place(NCardTransformVfx.Create(removed[0], added[0], null), slot++);
        }
        else
        {
            foreach (CardModel card in added)
            {
                PreviewAdd(card, SpotFor(slot++));
            }
        }

        foreach (CardModel card in upgraded)
        {
            Place(NCardUpgradeVfx.Create(card), slot++);
        }
    }

    /// <summary>On the teammate's side of the screen (their panels' side), stepping left for each extra card.</summary>
    private Vector2 SpotFor(int slot)
    {
        Vector2 viewport = _host!.GetViewportRect().Size;
        return new Vector2(viewport.X - 340f - slot * 90f, viewport.Y * 0.5f);
    }

    private void Place(Node2D? vfx, int slot)
    {
        if (vfx == null)
        {
            return;
        }

        vfx.Position = SpotFor(slot);
        _host!.AddChildSafely(vfx);
    }

    /// <summary><c>CardCmd.PreviewCardPileAdd</c> without its local-player check: pop the card up, then fly it to the deck.</summary>
    private void PreviewAdd(CardModel card, Vector2 spot)
    {
        if (card.Pile == null)
        {
            return;
        }

        NCard? node = NCard.Create(card);
        if (node == null)
        {
            return;
        }

        _host!.AddChildSafely(node);
        node.Position = spot;
        node.UpdateVisuals(card.Pile.Type, CardPreviewMode.Normal);
        Tween tween = node.CreateTween();
        tween.TweenProperty(node, "scale", Vector2.One, 0.25).From(Vector2.Zero).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenCallback(Callable.From(() =>
        {
            NCardFlyVfx? fly = card.Pile != null ? NCardFlyVfx.Create(node, card.Pile.Type, isAddingToPile: true, card.Owner.Character.TrailPath) : null;
            Node? trail = NRun.Instance?.GlobalUi?.TopBar.TrailContainer;
            if (fly != null && trail != null)
            {
                trail.AddChildSafely(fly);
            }
            else
            {
                node.QueueFreeSafely();
            }
        })).SetDelay(PreviewSeconds);
    }

    private void Snapshot(Player? teammate)
    {
        _deck = teammate == null ? new List<CardModel>() : PileType.Deck.GetPile(teammate).Cards.ToList();
        _upgradeLevels = _deck.ToDictionary((CardModel c) => c, (CardModel c) => c.CurrentUpgradeLevel);
    }
}

using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// A short-lived notice when the teammate's deck or relics change outside combat: a card added, removed, transformed
/// ("Strike → Anger") or upgraded, or a relic gained, with a preview of the new card. The game's own reveal animations
/// only play for the driver, so without this the teammate never sees what a transform or reward gave them.
/// </summary>
internal sealed partial class CouchTeammateFeed : CouchPanel
{
    private const double ShowSeconds = 6.0;

    private const float CardScale = 0.4f;

    private readonly List<string> _lines = new();

    private Player? _watched;

    private List<CardModel> _deck = new();

    private Dictionary<CardModel, int> _upgradeLevels = new();

    private List<RelicModel> _relics = new();

    private NCard? _preview;

    private double _hideAt;

    protected override float PanelWidth => 360f;

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

        double now = Time.GetTicksMsec() / 1000.0;
        if (now > _hideAt || _lines.Count == 0)
        {
            if (Visible)
            {
                Visible = false;
                _lines.Clear();
                CouchCards.Free(_preview);
                _preview = null;
            }

            return;
        }

        Visible = true;
        Vector2 viewport = GetViewportRect().Size;
        Vector2 cardSize = NCard.defaultSize * CardScale;
        float height = 70f + _lines.Count * 26f + (_preview != null ? cardSize.Y + 16f : 0f);
        Position = new Vector2(CouchConfig.EventPanelOnLeft ? viewport.X - PanelWidth - 36f : 36f, viewport.Y - height - 190f);
        SetTitle($"{SeatLabel(teammate!)}'s deck");
        float y = ContentTop;
        if (_preview != null)
        {
            _preview.Position = new Vector2(PanelWidth * 0.5f, y + cardSize.Y * 0.5f + 4f);
            y += cardSize.Y + 16f;
        }

        FinishLayout(y, string.Join("\n", _lines));
    }

    private void DetectChanges(Player teammate)
    {
        List<CardModel> deck = PileType.Deck.GetPile(teammate).Cards.ToList();
        List<RelicModel> relics = teammate.Relics.ToList();
        List<CardModel> added = deck.Where((CardModel c) => !_deck.Contains(c)).ToList();
        List<CardModel> removed = _deck.Where((CardModel c) => !deck.Contains(c)).ToList();
        List<CardModel> upgraded = deck.Where((CardModel c) => _upgradeLevels.TryGetValue(c, out int level) && c.CurrentUpgradeLevel > level).ToList();
        List<RelicModel> newRelics = relics.Where((RelicModel r) => !_relics.Contains(r)).ToList();
        if (added.Count == 0 && removed.Count == 0 && upgraded.Count == 0 && newRelics.Count == 0)
        {
            return;
        }

        List<string> lines = new();
        CardModel? highlight = null;
        if (added.Count == 1 && removed.Count == 1)
        {
            lines.Add($"{removed[0].Title} → {added[0].Title} (transformed)");
            highlight = added[0];
        }
        else
        {
            lines.AddRange(added.Select((CardModel c) => $"+ {c.Title}"));
            lines.AddRange(removed.Select((CardModel c) => $"− {c.Title} (removed)"));
            highlight = added.LastOrDefault();
        }

        foreach (CardModel card in upgraded)
        {
            lines.Add($"{card.Title} upgraded");
            highlight ??= card;
        }

        lines.AddRange(newRelics.Select((RelicModel r) => $"Relic: {CouchText.Plain(r.Title.GetFormattedText())}"));
        CouchLog.Info($"Teammate {teammate.NetId} deck/relics changed: {string.Join("; ", lines)}");
        PlaySound(teammate, added, removed, upgraded);

        _lines.AddRange(lines);
        while (_lines.Count > 6)
        {
            _lines.RemoveAt(0);
        }

        if (highlight != null)
        {
            CouchCards.Free(_preview);
            _preview = CouchCards.Create(highlight, this);
            _preview.Scale = new Vector2(CardScale, CardScale);
            _preview.ZIndex = 3;
        }

        _hideAt = Time.GetTicksMsec() / 1000.0 + ShowSeconds;
        Snapshot(teammate);
    }

    /// <summary>
    /// The game's own sounds for these (transform, smith, card into the deck) come with animations it only plays for
    /// the local player. Relic sounds are played by <see cref="CouchTeammateRelicBar"/>, in and out of combat.
    /// </summary>
    private static void PlaySound(Player teammate, List<CardModel> added, List<CardModel> removed, List<CardModel> upgraded)
    {
        if (LocalContext.IsMe(teammate))
        {
            return;
        }

        if (added.Count == 1 && removed.Count == 1)
        {
            CouchSfx.Transform();
        }
        else if (added.Count > 0)
        {
            CouchSfx.CardAdded();
        }

        if (upgraded.Count > 0)
        {
            CouchSfx.Upgrade();
        }
    }

    private void Snapshot(Player? teammate)
    {
        _deck = teammate == null ? new List<CardModel>() : PileType.Deck.GetPile(teammate).Cards.ToList();
        _upgradeLevels = _deck.ToDictionary((CardModel c) => c, (CardModel c) => c.CurrentUpgradeLevel);
        _relics = teammate == null ? new List<RelicModel>() : teammate.Relics.ToList();
    }
}

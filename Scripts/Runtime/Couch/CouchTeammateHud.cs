using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace LocalMultiControl.Scripts.Runtime.Couch;

internal enum CouchHudCommand
{
    Left,
    Right,
    Up,
    Down,

    /// <summary>Switch between the hand and the potion row (keyboard U).</summary>
    ToggleRow,

    /// <summary>Play the card / use the potion under the cursor, confirm a target, or pick/toggle a choice option.</summary>
    Accept,

    /// <summary>Leave targeting or the potion row, or clear the choice selection.</summary>
    Back,

    /// <summary>Confirm a multi-card choice; in the potion row, discard the potion (press twice).</summary>
    Submit,

    EndTurn,

    /// <summary>Controller Y ("Proceed / End Turn"): confirms a choice while one is open, otherwise ends the turn.</summary>
    SubmitOrEndTurn,

    /// <summary>Open or close the teammate's deck/relics view (keyboard V, controller View/Back).</summary>
    Info
}

/// <summary>
/// The teammate's own controls during simultaneous combat: their hand with a cursor, their potions, target selection
/// with a marker over the creature, and the options of any choice their cards ask for. Driven by the teammate's
/// controller (routed by <see cref="CouchInputRouter"/>) or the keyboard block handled by <see cref="CouchInputGate"/>.
/// Actions go through <see cref="CouchRemotePlay"/> and <see cref="CouchTeammateChoices"/>, so the driver's screen is
/// never taken. Placement and scale come from <see cref="CouchConfig"/> (hud_x, hud_y, hud_scale).
/// </summary>
internal sealed partial class CouchTeammateHud : Control
{
    public const string HudNodeName = "CouchTeammateHud";

    private const float CardSpacingFactor = 0.8f;

    private const float CursorDrop = 0.1f;

    private const float CardsTopOffset = 64f;

    private const float PotionSlotSize = 44f;

    private const float RightMargin = 40f;

    private const float BusyAlpha = 0.2f;

    private const double FlashSeconds = 2.5;

    private const double DiscardArmSeconds = 2.5;

    /// <summary>Scale of the big card shown next to the creature being targeted.</summary>
    private const float TargetPreviewScale = 0.62f;

    private static readonly Color CursorTint = Colors.White;

    private static readonly Color IdleTint = new(0.82f, 0.82f, 0.82f);

    private static readonly Color UnplayableTint = new(0.45f, 0.45f, 0.45f, 0.9f);

    private static readonly Color PickedTint = new(1f, 0.86f, 0.45f);

    private static CouchTeammateHud? _instance;

    private readonly List<NCard> _cardNodes = new();

    private readonly List<CouchPotionSlot> _potionSlots = new();

    private readonly HashSet<int> _picked = new();

    private List<CardModel> _shownCards = new();

    private Label? _header;

    private Label? _hint;

    private CouchTargetMarker? _marker;

    /// <summary>The card being aimed, shown big beside the target with the damage it would deal to that target.</summary>
    private NCard? _targetPreview;

    /// <summary>The hand card whose numbers currently include a target (vulnerable, etc.).</summary>
    private NCard? _handPreviewNode;

    /// <summary>The teammate's cards in play last frame, to hear them land in the discard pile.</summary>
    private List<CardModel> _cardsInPlay = new();

    private HudMode _mode = HudMode.Hand;

    private HudMode _modeBeforeTargeting = HudMode.Hand;

    private int _cursor;

    private int _potionCursor;

    private int _targetIndex;

    private CardModel? _targetingCard;

    private PotionModel? _targetingPotion;

    private PotionModel? _discardArmed;

    private double _discardArmedUntil;

    private CouchTeammateChoice? _choice;

    private Player? _teammate;

    private bool _lastInputFromController;

    private string _flash = "";

    private float _headerRight;

    private double _flashUntil;

    private enum HudMode
    {
        Hand,
        Potions,
        Targeting,
        Choice
    }

    /// <summary>True while the HUD is up for some teammate.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible && _instance._teammate != null;

    public static void Attach(NCombatRoom room)
    {
        NPlayerHand? hand = room.Ui?.Hand;
        Node? parent = hand?.GetParent();
        if (hand == null || parent == null || parent.GetNodeOrNull(HudNodeName) != null)
        {
            return;
        }

        CouchTeammateHud hud = new() { Name = HudNodeName };
        parent.AddChild(hud);
        parent.MoveChild(hud, hand.GetIndex() + 1);
    }

    /// <summary>Where the HUD's top line (header text, then potion slots) ends, while the HUD is up.</summary>
    public static float? HeaderRight => IsActive ? _instance!._headerRight : null;

    /// <summary>True if the HUD is currently showing this player (so other overlays can skip them).</summary>
    public static bool IsShowing(ulong playerId)
    {
        return IsActive && _instance!._teammate?.NetId == playerId;
    }

    /// <summary>
    /// Runs a command if the HUD is showing <paramref name="playerId"/> (a controller seat), or any teammate when null
    /// (the keyboard block). Returns false when the HUD isn't active, so the input can go elsewhere.
    /// </summary>
    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        CouchTeammateHud? hud = _instance;
        if (!IsActive || (playerId.HasValue && hud!._teammate!.NetId != playerId.Value))
        {
            return false;
        }

        hud!._lastInputFromController = playerId.HasValue;
        hud.OnCommand(command);
        return true;
    }

    public override void _Ready()
    {
        _instance = this;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _header = CreateLabel(22, new Color("f3efe6"));
        _hint = CreateLabel(17, new Color("d8d2c4"));
        _marker = new CouchTargetMarker { Visible = false, ZIndex = 20 };
        AddChild(_marker);
        Visible = false;
        SetProcess(true);
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
    }

    public override void _ExitTree()
    {
        CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
        ClearCards();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        _teammate = CouchTeammate.DriverKeepsScreen ? CouchRemotePlay.FindTeammate() : null;
        if (_teammate?.PlayerCombatState == null)
        {
            if (Visible)
            {
                Visible = false;
                ClearCards();
                _mode = HudMode.Hand;
                _choice = null;
                _picked.Clear();
            }

            return;
        }

        Visible = true;
        UpdateMode();
        List<CardModel> cards = _mode == HudMode.Choice ? _choice!.Options.ToList() : _teammate.PlayerCombatState.Hand.Cards.ToList();
        if (!cards.SequenceEqual(_shownCards))
        {
            RebuildCards(cards);
        }

        _cursor = _shownCards.Count == 0 ? 0 : Mathf.Clamp(_cursor, 0, _shownCards.Count - 1);
        SyncPotionSlots();
        UpdateTexts();
        Layout();
        UpdateMarker();
        UpdateTargetPreview();
        ListenForPlayedCards();
        Modulate = new Color(1f, 1f, 1f, DriverIsBusy() ? BusyAlpha : 1f);
    }

    /// <summary>
    /// The driver has something in the middle of the screen (a hand selection, an overlay screen, the map or deck view);
    /// fade out of its way.
    /// </summary>
    private static bool DriverIsBusy()
    {
        return (NPlayerHand.Instance?.IsInCardSelection ?? false)
            || (NOverlayStack.Instance?.ScreenCount ?? 0) > 0
            || (NCapstoneContainer.Instance?.InUse ?? false);
    }

    private void UpdateMode()
    {
        CouchTeammateChoice? pending = CouchTeammateChoices.Pending.FirstOrDefault((CouchTeammateChoice c) => c.Player == _teammate);
        if (pending != null)
        {
            if (_mode != HudMode.Choice || _choice != pending)
            {
                _mode = HudMode.Choice;
                _choice = pending;
                _picked.Clear();
                _cursor = 0;
                _targetingCard = null;
                _targetingPotion = null;
            }

            return;
        }

        if (_mode == HudMode.Choice)
        {
            _mode = HudMode.Hand;
            _choice = null;
            _picked.Clear();
            _cursor = 0;
        }

        if (_mode == HudMode.Targeting && !TargetingStillValid())
        {
            EndTargeting();
        }

        if (_mode == HudMode.Potions && !_teammate!.Potions.Any())
        {
            _mode = HudMode.Hand;
        }
    }

    private bool TargetingStillValid()
    {
        if (_targetingCard != null)
        {
            return _teammate!.PlayerCombatState!.Hand.Cards.Contains(_targetingCard) && Targets().Count > 0;
        }

        return _targetingPotion != null && _teammate!.PotionSlots.Contains(_targetingPotion) && Targets().Count > 0;
    }

    private void OnCommand(CouchHudCommand command)
    {
        switch (_mode)
        {
            case HudMode.Hand:
                OnHandCommand(command);
                break;
            case HudMode.Potions:
                OnPotionCommand(command);
                break;
            case HudMode.Targeting:
                OnTargetingCommand(command);
                break;
            case HudMode.Choice:
                OnChoiceCommand(command);
                break;
        }
    }

    private void OnHandCommand(CouchHudCommand command)
    {
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Right:
                MoveCursor(command == CouchHudCommand.Left ? -1 : 1);
                CouchSfx.Move();
                break;
            case CouchHudCommand.Up:
            case CouchHudCommand.ToggleRow:
                EnterPotionRow();
                break;
            case CouchHudCommand.Accept:
                PlayOrStartTargeting();
                break;
            case CouchHudCommand.EndTurn:
            case CouchHudCommand.SubmitOrEndTurn:
                EndTurn();
                break;
        }
    }

    private void OnPotionCommand(CouchHudCommand command)
    {
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Right:
                MovePotionCursor(command == CouchHudCommand.Left ? -1 : 1);
                CouchSfx.Move();
                break;
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
            case CouchHudCommand.Back:
                _mode = HudMode.Hand;
                CouchSfx.Back();
                break;
            case CouchHudCommand.Accept:
                UseOrStartTargetingPotion();
                break;
            case CouchHudCommand.Submit:
                DiscardPotion();
                break;
            case CouchHudCommand.EndTurn:
            case CouchHudCommand.SubmitOrEndTurn:
                EndTurn();
                break;
        }
    }

    private void OnTargetingCommand(CouchHudCommand command)
    {
        IReadOnlyList<Creature> targets = Targets();
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Right:
                if (targets.Count > 0)
                {
                    _targetIndex = (_targetIndex + (command == CouchHudCommand.Left ? -1 : 1) + targets.Count) % targets.Count;
                    CouchSfx.Move();
                }

                break;
            case CouchHudCommand.Accept:
            case CouchHudCommand.Submit:
            case CouchHudCommand.SubmitOrEndTurn:
                if (targets.Count > 0)
                {
                    Creature target = targets[Mathf.Clamp(_targetIndex, 0, targets.Count - 1)];
                    string reason = "";
                    bool sent = _targetingCard != null
                        ? CouchRemotePlay.TryPlay(_teammate!, _targetingCard, target, out reason)
                        : _targetingPotion != null && CouchRemotePlay.TryUsePotion(_teammate!, _targetingPotion, target, out reason);
                    if (sent)
                    {
                        CouchSfx.Accept();
                    }
                    else
                    {
                        Flash(reason);
                    }
                }

                EndTargeting();
                break;
            case CouchHudCommand.Back:
                EndTargeting();
                CouchSfx.Back();
                break;
        }
    }

    private void OnChoiceCommand(CouchHudCommand command)
    {
        CouchTeammateChoice choice = _choice!;
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Right:
                MoveCursor(command == CouchHudCommand.Left ? -1 : 1);
                CouchSfx.Move();
                break;
            case CouchHudCommand.Accept:
                if (_shownCards.Count == 0)
                {
                    break;
                }

                if (choice.MaxSelect == 1)
                {
                    CouchSfx.Accept();
                    CouchTeammateChoices.Answer(choice, new[] { _cursor });
                }
                else if (_picked.Remove(_cursor))
                {
                    CouchSfx.Toggle(false);
                }
                else if (_picked.Count >= choice.MaxSelect)
                {
                    Flash($"At most {choice.MaxSelect}");
                }
                else
                {
                    _picked.Add(_cursor);
                    CouchSfx.Toggle(true);
                }

                break;
            case CouchHudCommand.Submit:
            case CouchHudCommand.SubmitOrEndTurn:
                if (_picked.Count < choice.MinSelect || _picked.Count > choice.MaxSelect)
                {
                    Flash(choice.MinSelect == choice.MaxSelect ? $"Pick exactly {choice.MinSelect}" : $"Pick {choice.MinSelect} to {choice.MaxSelect}");
                    break;
                }

                CouchSfx.Accept();
                CouchTeammateChoices.Answer(choice, _picked.OrderBy((int i) => i).ToList());
                break;
            case CouchHudCommand.Back:
                _picked.Clear();
                CouchSfx.Back();
                break;
        }
    }

    private void PlayOrStartTargeting()
    {
        if (_shownCards.Count == 0)
        {
            return;
        }

        CardModel card = _shownCards[_cursor];
        if (!CouchRemotePlay.CanActNow(out string reason))
        {
            Flash(reason);
            return;
        }

        if (!card.CanPlay())
        {
            Flash($"Can't play {card.Title} now");
            return;
        }

        if (CouchRemotePlay.NeedsTarget(card))
        {
            StartTargeting(card, null);
            return;
        }

        if (CouchRemotePlay.TryPlay(_teammate!, card, null, out reason))
        {
            CouchSfx.CardSelect();
        }
        else
        {
            Flash(reason);
        }
    }

    private void UseOrStartTargetingPotion()
    {
        PotionModel? potion = PotionAtCursor();
        if (potion == null)
        {
            Flash("Empty slot");
            return;
        }

        if (!CouchRemotePlay.CanUsePotion(potion, out string reason))
        {
            Flash(reason);
            return;
        }

        if (CouchRemotePlay.PotionNeedsTargetChoice(potion))
        {
            StartTargeting(null, potion);
            return;
        }

        if (CouchRemotePlay.TryUsePotion(_teammate!, potion, null, out reason))
        {
            CouchSfx.Accept();
        }
        else
        {
            Flash(reason);
        }
    }

    private void DiscardPotion()
    {
        PotionModel? potion = PotionAtCursor();
        if (potion == null)
        {
            Flash("Empty slot");
            return;
        }

        double now = Time.GetTicksMsec() / 1000.0;
        if (_discardArmed != potion || now > _discardArmedUntil)
        {
            _discardArmed = potion;
            _discardArmedUntil = now + DiscardArmSeconds;
            Flash($"Press {Keys("O", "X")} again to discard {PotionTitle(potion)}", denied: false);
            CouchSfx.Move();
            return;
        }

        _discardArmed = null;
        if (CouchRemotePlay.TryDiscardPotion(_teammate!, potion, out string reason))
        {
            CouchSfx.Back();
        }
        else
        {
            Flash(reason);
        }
    }

    private void EndTurn()
    {
        if (CouchRemotePlay.ToggleEndTurn(_teammate!, out string reason))
        {
            CouchSfx.Accept();
        }
        else
        {
            Flash($"Can't end turn: {reason}");
        }
    }

    private void StartTargeting(CardModel? card, PotionModel? potion)
    {
        _targetingCard = card;
        _targetingPotion = potion;
        if (Targets().Count == 0)
        {
            _targetingCard = null;
            _targetingPotion = null;
            Flash("No valid target");
            return;
        }

        _modeBeforeTargeting = _mode;
        _mode = HudMode.Targeting;
        _targetIndex = 0;
        if (card != null)
        {
            CouchSfx.CardSelect();
        }
        else
        {
            CouchSfx.Accept();
        }
    }

    private void EndTargeting()
    {
        _mode = _modeBeforeTargeting == HudMode.Potions ? HudMode.Potions : HudMode.Hand;
        _targetingCard = null;
        _targetingPotion = null;
    }

    private void EnterPotionRow()
    {
        if (!_teammate!.Potions.Any())
        {
            Flash("No potions");
            return;
        }

        _mode = HudMode.Potions;
        CouchSfx.Move();
        if (PotionAtCursor() == null)
        {
            MovePotionCursor(1);
        }
    }

    private IReadOnlyList<Creature> Targets()
    {
        if (_teammate == null)
        {
            return new List<Creature>();
        }

        if (_targetingCard != null)
        {
            return CouchRemotePlay.ValidTargets(_targetingCard, _teammate);
        }

        return _targetingPotion != null ? CouchRemotePlay.ValidPotionTargets(_targetingPotion, _teammate) : new List<Creature>();
    }

    private PotionModel? PotionAtCursor()
    {
        IReadOnlyList<PotionModel?> slots = _teammate!.PotionSlots;
        return _potionCursor >= 0 && _potionCursor < slots.Count ? slots[_potionCursor] : null;
    }

    /// <summary>Moves to the next filled potion slot.</summary>
    private void MovePotionCursor(int step)
    {
        IReadOnlyList<PotionModel?> slots = _teammate!.PotionSlots;
        for (int i = 1; i <= slots.Count; i++)
        {
            int index = ((_potionCursor + step * i) % slots.Count + slots.Count) % slots.Count;
            if (slots[index] != null)
            {
                _potionCursor = index;
                return;
            }
        }
    }

    private void MoveCursor(int step)
    {
        if (_shownCards.Count > 0)
        {
            _cursor = (_cursor + step + _shownCards.Count) % _shownCards.Count;
        }
    }

    /// <summary>Shows a short message in the hint line; most are refusals, which also get the "no" sound.</summary>
    private void Flash(string message, bool denied = true)
    {
        _flash = message;
        _flashUntil = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        if (denied)
        {
            CouchSfx.Deny();
        }
    }

    private void RebuildCards(List<CardModel> cards)
    {
        ClearCards();
        _shownCards = cards;
        foreach (CardModel card in cards)
        {
            _cardNodes.Add(CouchCards.Create(card, this));
        }
    }

    private void ClearCards()
    {
        ClearTargetPreview();
        _handPreviewNode = null;
        _cardsInPlay = new List<CardModel>();
        foreach (NCard node in _cardNodes)
        {
            CouchCards.Free(node);
        }

        _cardNodes.Clear();
        _shownCards = new List<CardModel>();
    }

    private void SyncPotionSlots()
    {
        IReadOnlyList<PotionModel?> slots = _teammate!.PotionSlots;
        while (_potionSlots.Count < slots.Count)
        {
            CouchPotionSlot slot = new() { Size = new Vector2(PotionSlotSize, PotionSlotSize), ZIndex = 10 };
            AddChild(slot);
            _potionSlots.Add(slot);
        }

        while (_potionSlots.Count > slots.Count)
        {
            _potionSlots[^1].QueueFree();
            _potionSlots.RemoveAt(_potionSlots.Count - 1);
        }

        _potionCursor = slots.Count == 0 ? 0 : Mathf.Clamp(_potionCursor, 0, slots.Count - 1);
        bool showCursor = _mode == HudMode.Potions || (_mode == HudMode.Targeting && _targetingPotion != null);
        for (int i = 0; i < slots.Count; i++)
        {
            _potionSlots[i].Potion = slots[i];
            _potionSlots[i].Highlighted = showCursor && i == _potionCursor;
        }
    }

    private void Layout()
    {
        Vector2 viewport = GetViewportRect().Size;
        float scale = CouchConfig.HudScale;
        Vector2 cardSize = NCard.defaultSize * scale;
        float left = CouchConfig.HudX;
        float top = CouchConfig.HudY;

        _header!.Position = new Vector2(left, top);
        _hint!.Position = new Vector2(left, top + 30f);

        // Potion slots sit right after the header text.
        float potionX = left + _header.GetMinimumSize().X + 20f;
        for (int i = 0; i < _potionSlots.Count; i++)
        {
            _potionSlots[i].Position = new Vector2(potionX + i * (PotionSlotSize + 6f), top - 8f);
        }

        _headerRight = potionX + _potionSlots.Count * (PotionSlotSize + 6f);

        // Cards: left-aligned under the text; overlap more when the hand is too wide for the space.
        float availableWidth = Mathf.Max(cardSize.X, viewport.X - RightMargin - left - cardSize.X);
        float spacing = cardSize.X * CardSpacingFactor;
        if (_cardNodes.Count > 1)
        {
            spacing = Mathf.Min(spacing, availableWidth / (_cardNodes.Count - 1));
        }

        float firstCenterX = left + cardSize.X * 0.5f;
        float centerY = top + CardsTopOffset + cardSize.Y * 0.5f;
        for (int i = 0; i < _cardNodes.Count; i++)
        {
            NCard node = _cardNodes[i];
            CardModel card = _shownCards[i];
            bool isCursor = _mode switch
            {
                HudMode.Targeting => card == _targetingCard,
                HudMode.Potions => false,
                _ => i == _cursor
            };
            bool picked = _mode == HudMode.Choice && _picked.Contains(i);
            float drop = (isCursor ? CursorDrop : 0f) + (picked ? CursorDrop * 0.6f : 0f);
            node.Position = new Vector2(firstCenterX + i * spacing, centerY + drop * cardSize.Y);
            float nodeScale = scale * (isCursor ? 1.12f : 1f);
            node.Scale = new Vector2(nodeScale, nodeScale);
            node.ZIndex = isCursor ? 2 : picked ? 1 : 0;
            bool showPlayability = _mode is HudMode.Hand or HudMode.Potions;
            node.Modulate = picked ? PickedTint
                : showPlayability && !card.CanPlay() ? UnplayableTint
                : isCursor ? CursorTint
                : IdleTint;
        }
    }

    private void UpdateTexts()
    {
        Player teammate = _teammate!;
        PlayerCombatState state = teammate.PlayerCombatState!;
        string seat = CouchSeats.FindByPlayer(teammate.NetId)?.Label ?? "P2";
        string character = teammate.Character.Title.GetFormattedText();
        int draw = PileType.Draw.GetPile(teammate).Cards.Count;
        int discard = PileType.Discard.GetPile(teammate).Cards.Count;
        bool usesStars = teammate.Character.ShouldAlwaysShowStarCounter || state.Stars > 0;
        bool ended = CombatManager.Instance.IsPlayerReadyToEndTurn(teammate);
        bool canAct = CouchRemotePlay.CanActNow(out _);
        _header!.Text = $"{seat} · {character}    Energy {state.Energy}/{state.MaxEnergy}    Gold {teammate.Gold}"
            + (usesStars ? $"    Stars {state.Stars}" : "")
            + $"    Draw {draw} · Discard {discard}"
            + (ended ? "    TURN ENDED" : "")
            + (canAct ? "" : "    (enemy turn)");

        string hint = _mode switch
        {
            HudMode.Targeting => $"Target for {TargetingName()}:  {Keys("J/L", "D-pad")} choose · {Keys("I", "A")} confirm · {Keys("K", "B")} back",
            HudMode.Potions => PotionHint(),
            HudMode.Choice when _choice!.MaxSelect == 1 => $"{_choice.Prompt}  {Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick",
            HudMode.Choice => $"{_choice!.Prompt}  ({_picked.Count} picked, {_choice.MinSelect}-{_choice.MaxSelect})  {Keys("J/L", "D-pad")} move · {Keys("I", "A")} toggle · {Keys("O", "Y")} confirm · {Keys("K", "B")} clear",
            _ => $"{Keys("J/L", "D-pad")} choose · {Keys("I", "A")} play · {Keys("U", "Up")} potions · {Keys("P", "Y")} end turn"
        };
        if (Time.GetTicksMsec() / 1000.0 < _flashUntil)
        {
            hint = $"{_flash}    —    {hint}";
        }

        _hint!.Text = hint;
    }

    private string PotionHint()
    {
        PotionModel? potion = PotionAtCursor();
        string about = potion == null ? "Empty slot" : $"{PotionTitle(potion)}: {CouchText.Plain(potion.DynamicDescription.GetFormattedText())}";
        return $"{about}    {Keys("J/L", "D-pad")} choose · {Keys("I", "A")} use · {Keys("O", "X")} discard · {Keys("U", "Down")} back to hand";
    }

    private string TargetingName()
    {
        return _targetingCard?.Title ?? (_targetingPotion != null ? PotionTitle(_targetingPotion) : "");
    }

    private string Keys(string keyboard, string controller)
    {
        return _lastInputFromController ? controller : keyboard;
    }

    private static string PotionTitle(PotionModel potion)
    {
        return CouchText.Plain(potion.Title.GetFormattedText());
    }

    private void UpdateMarker()
    {
        IReadOnlyList<Creature> targets = _mode == HudMode.Targeting ? Targets() : new List<Creature>();
        NCreature? node = targets.Count > 0
            ? NCombatRoom.Instance?.GetCreatureNode(targets[Mathf.Clamp(_targetIndex, 0, targets.Count - 1)])
            : null;
        if (node?.Hitbox == null)
        {
            _marker!.Visible = false;
            return;
        }

        _marker!.Visible = true;
        _marker.GlobalPosition = node.Hitbox.GlobalPosition + new Vector2(node.Hitbox.Size.X * 0.5f, -12f);
    }

    /// <summary>The creature under the target marker, if the teammate is aiming something.</summary>
    private Creature? CurrentTarget()
    {
        IReadOnlyList<Creature> targets = _mode == HudMode.Targeting ? Targets() : new List<Creature>();
        return targets.Count > 0 ? targets[Mathf.Clamp(_targetIndex, 0, targets.Count - 1)] : null;
    }

    /// <summary>
    /// While a card is aimed: the card's numbers against that target (vulnerable, weak, etc.), on the hand card and on
    /// a big copy beside the target, as the game does when the driver drags a card over an enemy.
    /// </summary>
    private void UpdateTargetPreview()
    {
        Creature? target = _targetingCard != null ? CurrentTarget() : null;
        NCreature? creatureNode = target != null ? NCombatRoom.Instance?.GetCreatureNode(target) : null;
        int handIndex = target != null ? _shownCards.IndexOf(_targetingCard!) : -1;
        NCard? handNode = handIndex >= 0 ? _cardNodes[handIndex] : null;
        if (_handPreviewNode != handNode)
        {
            SetPreviewTargetSafe(_handPreviewNode, null);
            _handPreviewNode = handNode;
        }

        SetPreviewTargetSafe(handNode, target);
        if (target == null || creatureNode?.Hitbox == null)
        {
            ClearTargetPreview();
            return;
        }

        if (_targetPreview == null || _targetPreview.Model != _targetingCard)
        {
            ClearTargetPreview();
            _targetPreview = CouchCards.Create(_targetingCard!, this);
            _targetPreview.Scale = new Vector2(TargetPreviewScale, TargetPreviewScale);
            _targetPreview.ZIndex = 15;
        }

        _targetPreview.SetPreviewTarget(target);

        // Beside the target (left of it, or right when there's no room), clamped to the screen.
        Vector2 viewport = GetViewportRect().Size;
        Vector2 size = NCard.defaultSize * TargetPreviewScale;
        Rect2 hitbox = new(creatureNode.Hitbox.GlobalPosition, creatureNode.Hitbox.Size);
        float x = hitbox.Position.X - size.X * 0.5f - 36f;
        if (x - size.X * 0.5f < 16f)
        {
            x = hitbox.End.X + size.X * 0.5f + 36f;
        }

        float y = Mathf.Clamp(hitbox.GetCenter().Y, size.Y * 0.5f + 16f, viewport.Y - size.Y * 0.5f - 16f);
        _targetPreview.GlobalPosition = new Vector2(Mathf.Clamp(x, size.X * 0.5f + 16f, viewport.X - size.X * 0.5f - 16f), y);
    }

    private void ClearTargetPreview()
    {
        CouchCards.Free(_targetPreview);
        _targetPreview = null;
    }

    private static void SetPreviewTargetSafe(NCard? node, Creature? target)
    {
        if (node != null && IsInstanceValid(node) && node.Model != null)
        {
            node.SetPreviewTarget(target);
        }
    }

    /// <summary>Keeps the teammate's card numbers current (strength, weak, ...), as the driver's hand does.</summary>
    private void OnCombatStateChanged(CombatState _)
    {
        foreach (NCard? node in _cardNodes.Append(_targetPreview))
        {
            if (node != null && IsInstanceValid(node) && node.Model != null)
            {
                node.UpdateVisuals(node.DisplayingPile, CardPreviewMode.Normal);
            }
        }
    }

    /// <summary>
    /// The driver hears their cards land in the discard pile; the teammate's are only shown fading out over their
    /// character, silently. Play the same sound when one of the teammate's played cards reaches their discard pile.
    /// </summary>
    private void ListenForPlayedCards()
    {
        Player teammate = _teammate!;
        CardPile discard = PileType.Discard.GetPile(teammate);
        if (_cardsInPlay.Any((CardModel card) => card.Pile == discard))
        {
            CouchSfx.CardToDiscard();
        }

        _cardsInPlay = PileType.Play.GetPile(teammate).Cards.ToList();
    }

    private Label CreateLabel(int fontSize, Color color)
    {
        Label label = new() { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 10 };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color("111111"));
        label.AddThemeConstantOverride("outline_size", 5);
        AddChild(label);
        return label;
    }

    private static void DisableInteraction(Control control)
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
}

/// <summary>
/// One potion slot in the teammate HUD: the potion's art on a dark tile, outlined when the cursor is on it.
/// </summary>
internal sealed partial class CouchPotionSlot : Control
{
    private static readonly Color Tile = new(0f, 0f, 0f, 0.55f);

    private static readonly Color EmptyOutline = new(1f, 1f, 1f, 0.25f);

    private static readonly Color CursorOutline = new(1f, 0.78f, 0.25f);

    private PotionModel? _potion;

    private Texture2D? _texture;

    private bool _highlighted;

    public PotionModel? Potion
    {
        get => _potion;
        set
        {
            if (_potion == value)
            {
                return;
            }

            _potion = value;
            _texture = value?.Image;
            QueueRedraw();
        }
    }

    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
            {
                return;
            }

            _highlighted = value;
            QueueRedraw();
        }
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        Rect2 rect = new(Vector2.Zero, Size);
        DrawRect(rect, Tile);
        if (_texture != null)
        {
            DrawTextureRect(_texture, rect.Grow(-3f), false);
        }

        DrawRect(rect, _highlighted ? CursorOutline : EmptyOutline, false, _highlighted ? 3f : 1f);
    }
}

/// <summary>
/// A bobbing downward arrow drawn over the teammate's chosen target. Its position is the arrow's tip.
/// </summary>
internal sealed partial class CouchTargetMarker : Control
{
    private static readonly Color Fill = new(1f, 0.78f, 0.25f);

    private static readonly Color Outline = new(0.1f, 0.08f, 0.05f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        float bob = Mathf.Sin((float)Time.GetTicksMsec() / 180f) * 6f;
        Vector2[] arrow =
        {
            new(0f, bob),
            new(-22f, -34f + bob),
            new(22f, -34f + bob)
        };
        DrawColoredPolygon(arrow, Fill);
        DrawPolyline(new[] { arrow[0], arrow[1], arrow[2], arrow[0] }, Outline, 3f);
    }
}

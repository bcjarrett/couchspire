using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace CouchSpire.Scripts.Runtime.Couch;

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
    Info,

    /// <summary>LB, as the driver's "view deck": opens the teammate's deck, or switches to its deck tab.</summary>
    TabLeft,

    /// <summary>RB: opens the teammate's relics, or switches to that tab.</summary>
    TabRight
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

    /// <summary>The focused card grows like the driver's focused hand card (from hand scale to full size).</summary>
    private const float FocusScale = 1.25f;

    /// <summary>Neighbors of the focused card make room for it (<c>NPlayerHand.RefreshLayout</c>: up to 100px).</summary>
    private const float FocusSpread = 100f;

    private const float CardsTopOffset = 80f;

    /// <summary>Header line, below the potion slots' top edge.</summary>
    private const float HeaderOffset = 12f;

    /// <summary>Band top when the players list isn't there to line up with.</summary>
    private const float DefaultBandTop = 170f;


    private const float RightMargin = 40f;

    private const float BusyAlpha = 0.2f;

    private const double FlashSeconds = 2.5;

    private const double DiscardArmSeconds = 2.5;

    /// <summary>The card being aimed is held up large, as the driver's is while targeting.</summary>
    private const float TargetingScale = 0.7f;

    private static readonly AccessTools.FieldRef<NCreature, NSelectionReticle> CreatureReticleRef =
        AccessTools.FieldRefAccess<NCreature, NSelectionReticle>("_selectionReticle");

    private static CouchTeammateHud? _instance;

    private readonly List<NCard> _cardNodes = new();


    private readonly HashSet<int> _picked = new();

    private List<CardModel> _shownCards = new();

    private RichTextLabel? _header;

    private Label? _hint;

    /// <summary>The game's targeting arrow (smaller), drawn from the aimed card or potion to the target.</summary>
    private CouchTargetingArrow? _arrow;

    /// <summary>What the arrow comes out of: the aimed card or potion slot.</summary>
    private Control? _arrowFrom;

    /// <summary>
    /// Where the arrow starts: the bottom edge of the aimed card (or potion), drawn behind it, so the arrow never
    /// covers the card's text and the numbers it shows against the target.
    /// </summary>
    private Control? _arrowAnchor;

    /// <summary>The creature whose selection reticle the teammate's aim has lit.</summary>
    private NCreature? _aimedCreature;

    /// <summary>The hand card whose numbers currently include a target (vulnerable, etc.).</summary>
    private NCard? _handPreviewNode;

    /// <summary>
    /// Cards the teammate has played that are still in their hand while the play waits in the action queue. Like the
    /// driver's played cards, they leave the hand right away; they come back only if the play is cancelled.
    /// </summary>
    private readonly List<QueuedPlay> _queuedPlays = new();

    private ActionQueueSet? _actionQueues;

    private bool _cardTextDirty;

    /// <summary>The teammate's cards in play last frame, to hear them land in the discard pile.</summary>
    private List<CardModel> _cardsInPlay = new();

    private HudMode _mode = HudMode.Hand;

    private HudMode _modeBeforeTargeting = HudMode.Hand;

    private int _cursor;

    private int _potionCursor;

    private int _relicCursor;

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

    private float _headerMiddle;

    private float _headerTop;

    private double _flashUntil;

    private enum HudMode
    {
        Hand,
        Potions,

        /// <summary>The cursor is on the teammate's relic row (right of their potions), showing tooltips.</summary>
        Relics,
        Targeting,

        /// <summary>A card or potion with no target picked up, waiting for a second press, like the driver's controller play.</summary>
        Holding,
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

    /// <summary>
    /// Top of the teammate's band (potion slots and status line, then cards): level with the top of the players list
    /// (health bars) on the left, which is below the driver's relic row, so the driver's relics can run the full width.
    /// Also used by the relic bar outside combat. <c>hud_y</c> overrides it.
    /// </summary>
    public static float BandTop()
    {
        if (CouchConfig.HudY >= 0f)
        {
            return CouchConfig.HudY;
        }

        Control? list = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        return list != null && IsInstanceValid(list) && list.IsVisibleInTree() ? list.GlobalPosition.Y : DefaultBandTop;
    }

    /// <summary>The HUD's mode (hand, potions, targeting, choice) while it's up, for screenshots.</summary>
    public static string? ModeName => IsActive ? _instance!._mode.ToString() : null;

    /// <summary>Where the HUD's top line (header text, then potion slots) ends, while the HUD is up.</summary>
    public static float? HeaderRight => IsActive ? _instance!._headerRight : null;

    /// <summary>Vertical middle of the HUD's top line, while the HUD is up.</summary>
    public static float? HeaderMiddle => IsActive ? _instance!._headerMiddle : null;

    /// <summary>Top of the HUD's header line (before <see cref="BandTop"/>'s <c>HeaderOffset</c>), while the HUD is
    /// up. Used by the in-game test runner (docs/design/testing-plan.md §6.5) to check the HUD band against the
    /// players list without measuring the whole HUD's bounding box, which also spans the (separately laid out) hand
    /// of cards and can extend above the header line when a card is focused/enlarged.</summary>
    public static float? HeaderTop => IsActive ? _instance!._headerTop : null;

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
        _header = CouchStyle.CreateRichLabel(this, 24, outline: 8);
        _header.ZIndex = 10;
        _hint = CouchStyle.CreateLabel(this, 19, outline: 7);
        _hint.ZIndex = 10;
        _arrowAnchor = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 0 };
        AddChild(_arrowAnchor);
        _arrow = new CouchTargetingArrow();
        AddChild(_arrow);
        Visible = false;
        SetProcess(true);
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        _actionQueues = RunManager.Instance.ActionQueueSet;
        _actionQueues.ActionEnqueued += OnActionEnqueued;
    }

    public override void _ExitTree()
    {
        CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
        if (_actionQueues != null)
        {
            _actionQueues.ActionEnqueued -= OnActionEnqueued;
        }
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
                CouchTeammateTopBar.SetCursor(null);
                CouchTeammateRelicBar.Focus(null);
            }

            return;
        }

        Visible = true;
        UpdateMode();
        PruneQueuedPlays();
        List<CardModel> cards = _mode == HudMode.Choice
            ? _choice!.Options.ToList()
            : _teammate.PlayerCombatState.Hand.Cards.Where((CardModel c) => !_queuedPlays.Any((QueuedPlay q) => q.Card == c)).ToList();
        if (!cards.SequenceEqual(_shownCards))
        {
            RebuildCards(cards);
        }

        _cursor = _shownCards.Count == 0 ? 0 : Mathf.Clamp(_cursor, 0, _shownCards.Count - 1);
        SyncPotionCursor();
        UpdateTexts();
        Layout();
        RefreshCardText();
        UpdateTargeting();
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

        if (_mode is HudMode.Targeting or HudMode.Holding && !TargetingStillValid())
        {
            EndTargeting();
        }

        if (_mode == HudMode.Potions && !_teammate!.Potions.Any())
        {
            _mode = HudMode.Hand;
        }

        if (_mode == HudMode.Relics && CouchTeammateRelicBar.RelicCount == 0)
        {
            _mode = HudMode.Hand;
        }
    }

    private bool TargetingStillValid()
    {
        bool targetsOk = _mode == HudMode.Holding || Targets().Count > 0;
        if (_targetingCard != null)
        {
            return _teammate!.PlayerCombatState!.Hand.Cards.Contains(_targetingCard) && targetsOk;
        }

        return _targetingPotion != null && _teammate!.PotionSlots.Contains(_targetingPotion) && targetsOk;
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
            case HudMode.Relics:
                OnRelicCommand(command);
                break;
            case HudMode.Targeting:
                OnTargetingCommand(command);
                break;
            case HudMode.Holding:
                OnHoldingCommand(command);
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
                if (StepPotionCursor(-1))
                {
                    CouchSfx.Move();
                }

                break;
            case CouchHudCommand.Right:
                if (StepPotionCursor(1))
                {
                    CouchSfx.Move();
                }
                else
                {
                    EnterRelicRow(0);
                }

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

    /// <summary>The relic row, reached by moving right past the potions: move through the relics to read them.</summary>
    private void OnRelicCommand(CouchHudCommand command)
    {
        int count = CouchTeammateRelicBar.RelicCount;
        switch (command)
        {
            case CouchHudCommand.Left:
                if (_relicCursor > 0)
                {
                    _relicCursor--;
                    CouchSfx.Move();
                }
                else if (_teammate!.Potions.Any())
                {
                    _mode = HudMode.Potions;
                    _potionCursor = _teammate.PotionSlots.Count;
                    StepPotionCursor(-1);
                    CouchSfx.Move();
                }

                break;
            case CouchHudCommand.Right:
                if (_relicCursor < count - 1)
                {
                    _relicCursor++;
                    CouchSfx.Move();
                }

                break;
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
            case CouchHudCommand.Back:
                _mode = HudMode.Hand;
                CouchSfx.Back();
                break;
            case CouchHudCommand.EndTurn:
            case CouchHudCommand.SubmitOrEndTurn:
                EndTurn();
                break;
        }
    }

    private void EnterRelicRow(int index)
    {
        int count = CouchTeammateRelicBar.RelicCount;
        if (count == 0)
        {
            return;
        }

        _mode = HudMode.Relics;
        _relicCursor = Mathf.Clamp(index, 0, count - 1);
        CouchSfx.Move();
    }

    /// <summary>A picked-up card or potion that needs no target: press again to play or use it, back to put it down.</summary>
    private void OnHoldingCommand(CouchHudCommand command)
    {
        switch (command)
        {
            case CouchHudCommand.Accept:
            case CouchHudCommand.SubmitOrEndTurn:
                string reason = "";
                bool sent = _targetingCard != null
                    ? CouchRemotePlay.TryPlay(_teammate!, _targetingCard, null, out reason)
                    : _targetingPotion != null && CouchRemotePlay.TryUsePotion(_teammate!, _targetingPotion, null, out reason);
                if (sent)
                {
                    CouchSfx.Accept();
                    SendCardAway(_targetingCard);
                }
                else
                {
                    Flash(reason);
                }

                EndTargeting();
                break;
            case CouchHudCommand.Submit:
                if (_targetingPotion != null)
                {
                    DiscardPotion();
                }

                break;
            case CouchHudCommand.Back:
                EndTargeting();
                CouchSfx.Back();
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
                        SendCardAway(_targetingCard);
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
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(_teammate!))
        {
            Flash($"Turn ended ({Keys("P", "Y")} to take it back)");
            return;
        }

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

        StartHolding(card, null);
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

        StartHolding(null, potion);
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

    /// <summary>Picks up a card or potion that needs no target; the next press plays or uses it.</summary>
    private void StartHolding(CardModel? card, PotionModel? potion)
    {
        _targetingCard = card;
        _targetingPotion = potion;
        _modeBeforeTargeting = _mode;
        _mode = HudMode.Holding;
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
            if (CouchTeammateRelicBar.RelicCount > 0)
            {
                EnterRelicRow(0);
                return;
            }

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

    /// <summary>Moves to the next filled potion slot in that direction, without wrapping; false at the end.</summary>
    private bool StepPotionCursor(int step)
    {
        IReadOnlyList<PotionModel?> slots = _teammate!.PotionSlots;
        for (int index = _potionCursor + step; index >= 0 && index < slots.Count; index += step)
        {
            if (slots[index] != null)
            {
                _potionCursor = index;
                return true;
            }
        }

        return false;
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
            CouchLog.Info($"Teammate HUD refused ({_mode}): {message}");
        }
    }

    /// <summary>
    /// Brings the card nodes in line with <paramref name="cards"/>, keeping the nodes of cards still shown (they glide to
    /// their new spots, as the driver's hand does) and creating only the new ones. Creating a card node is expensive, and
    /// the hand changes on every play and draw.
    /// </summary>
    private void RebuildCards(List<CardModel> cards)
    {
        Dictionary<CardModel, NCard> existing = new();
        for (int i = 0; i < _shownCards.Count && i < _cardNodes.Count; i++)
        {
            existing.TryAdd(_shownCards[i], _cardNodes[i]);
        }

        List<NCard> nodes = new();
        foreach (CardModel card in cards)
        {
            nodes.Add(existing.Remove(card, out NCard? node) && IsInstanceValid(node) ? node : CouchCards.Create(card, this));
        }

        foreach (NCard leftover in existing.Values)
        {
            if (_handPreviewNode == leftover)
            {
                _handPreviewNode = null;
            }

            CouchCards.Free(leftover);
        }

        _cardNodes.Clear();
        _cardNodes.AddRange(nodes);
        _shownCards = cards;
    }

    private void ClearCards()
    {
        _queuedPlays.Clear();
        StopTargetingVisuals();
        _handPreviewNode = null;
        _cardsInPlay = new List<CardModel>();
        foreach (NCard node in _cardNodes)
        {
            CouchCards.Free(node);
        }

        _cardNodes.Clear();
        _shownCards = new List<CardModel>();
    }

    /// <summary>The teammate's potions live in their part of the top bar; point its cursor and the relic cursor.</summary>
    private void SyncPotionCursor()
    {
        IReadOnlyList<PotionModel?> slots = _teammate!.PotionSlots;
        _potionCursor = slots.Count == 0 ? 0 : Mathf.Clamp(_potionCursor, 0, slots.Count - 1);
        bool showCursor = _mode == HudMode.Potions || (_mode is HudMode.Targeting or HudMode.Holding && _targetingPotion != null);
        CouchTeammateTopBar.SetCursor(showCursor ? _potionCursor : null);
        CouchTeammateRelicBar.Focus(_mode == HudMode.Relics ? _relicCursor : null);
    }

    private void Layout()
    {
        Vector2 viewport = GetViewportRect().Size;
        float scale = CouchConfig.HudScale;
        Vector2 cardSize = NCard.defaultSize * scale;
        float left = CouchLayout.RightOfPlayersList(CouchConfig.HudX);
        float top = BandTop();

        _header!.Position = new Vector2(left, top + HeaderOffset);
        _hint!.Position = new Vector2(left, top + HeaderOffset + 32f);

        _header.Size = new Vector2(Mathf.Max(_header.GetContentWidth(), 10f), 40f);
        _headerRight = left + _header.GetContentWidth();
        _headerMiddle = _header.Position.Y + _header.GetContentHeight() * 0.5f;
        _headerTop = _header.Position.Y;

        // Cards: left-aligned under the text; overlap more when the hand is too wide for the space.
        float availableWidth = Mathf.Max(cardSize.X, viewport.X - RightMargin - left - cardSize.X);
        float spacing = cardSize.X * CardSpacingFactor;
        if (_cardNodes.Count > 1)
        {
            spacing = Mathf.Min(spacing, availableWidth / (_cardNodes.Count - 1));
        }

        // Like the driver's hand: the focused card snaps to full size, clear of the others, and its neighbors move
        // aside; everything else glides into place.
        // After ending the turn the hand slides away, as the driver's does, and comes back if the turn is taken back.
        bool handAway = _mode != HudMode.Choice && CombatManager.Instance.IsPlayerReadyToEndTurn(_teammate!);
        int focus = handAway ? -1 : FocusIndex();
        float spread = FocusSpread * scale / 0.75f;
        float cardsTop = top + CardsTopOffset;
        float firstCenterX = left + cardSize.X * 0.5f;
        bool canAct = CouchRemotePlay.CanActNow(out _);
        for (int i = 0; i < _cardNodes.Count; i++)
        {
            NCard node = _cardNodes[i];
            CardModel card = _shownCards[i];
            bool focused = i == focus;
            bool picked = _mode == HudMode.Choice && _picked.Contains(i);
            float x = firstCenterX + i * spacing;
            if (focus >= 0 && !focused)
            {
                x -= Mathf.Sign(focus - i) * Mathf.Lerp(spread, 0f, Mathf.Min(1f, Mathf.Abs(focus - i) / 4f));
            }

            float nodeScale = focused ? (_mode is HudMode.Targeting or HudMode.Holding ? TargetingScale : scale * FocusScale) : scale;
            float y = cardsTop + NCard.defaultSize.Y * nodeScale * 0.5f + (focused ? CursorDrop * cardSize.Y : 0f) + (picked ? CursorDrop * 0.6f * cardSize.Y : 0f);
            if (focused)
            {
                node.Scale = new Vector2(nodeScale, nodeScale);
                node.Position = new Vector2(node.Position.X, y);
            }

            if (handAway)
            {
                y -= cardSize.Y * 0.6f;
            }

            CouchCards.Glide(node, new Vector2(x, Mathf.Min(y, viewport.Y - NCard.defaultSize.Y * nodeScale * 0.5f)), nodeScale);
            node.ZIndex = focused ? 2 : picked ? 1 : 0;
            float alpha = Mathf.Lerp(node.Modulate.A, handAway ? 0f : 1f, CouchStyle.Smooth(node.GetProcessDeltaTime(), 8f));
            node.Modulate = new Color(1f, 1f, 1f, alpha);
            node.Visible = alpha > 0.02f;
            CouchCards.SetGlow(node, _mode switch
            {
                HudMode.Choice => picked ? NCardHighlight.gold : NCardHighlight.playableColor,
                HudMode.Targeting or HudMode.Holding when focused => NCardHighlight.playableColor,
                _ => CouchCards.HandGlow(card, canAct)
            });
        }
    }

    /// <summary>The card that is up front: the cursor card, or the card being aimed; none in the potion row.</summary>
    private int FocusIndex()
    {
        return _mode switch
        {
            HudMode.Targeting or HudMode.Holding => _targetingCard != null ? _shownCards.IndexOf(_targetingCard) : -1,
            HudMode.Potions or HudMode.Relics => -1,
            _ => _shownCards.Count > 0 ? _cursor : -1
        };
    }

    private void UpdateTexts()
    {
        Player teammate = _teammate!;
        PlayerCombatState state = teammate.PlayerCombatState!;
        int draw = PileType.Draw.GetPile(teammate).Cards.Count;
        int discard = PileType.Discard.GetPile(teammate).Cards.Count;
        bool usesStars = teammate.Character.ShouldAlwaysShowStarCounter || state.Stars > 0;
        bool ended = CombatManager.Instance.IsPlayerReadyToEndTurn(teammate);
        bool canAct = CouchRemotePlay.CanActNow(out _);

        // Like the driver's combat UI: the character's energy icon, stars, and the draw and discard piles. Name, HP, gold
        // and potions are in the teammate's part of the top bar.
        string energyColor = state.Energy > 0 ? CouchStyle.Cream.ToHtml(false) : StsColors.red.ToHtml(false);
        string header = $"{CouchStyle.Icon(EnergyIconHelper.GetPath(teammate.Character.CardPool), 34)} [b][color=#{energyColor}]{state.Energy}/{state.MaxEnergy}[/color][/b]"
            + (usesStars ? $"   {CouchStyle.Icon(CouchStyle.StarIconPath, 30)} [b]{state.Stars}[/b]" : "")
            + $"   {CouchStyle.Icon(CouchStyle.DrawPileIconPath, 34)} [b]{draw}[/b]"
            + $"   {CouchStyle.Icon(CouchStyle.DiscardPileIconPath, 34)} [b]{discard}[/b]"
            + (ended ? $"   [b][color=#{StsColors.gold.ToHtml(false)}]Turn ended[/color][/b]" : "")
            + (canAct ? "" : $"   [color=#{CouchStyle.Muted.ToHtml(false)}]Enemy turn[/color]");
        if (_header!.Text != header)
        {
            _header.Text = header;
        }

        string hint = _mode switch
        {
            HudMode.Targeting => $"Target for {TargetingName()}:  {Keys("J/L", "D-pad")} choose · {Keys("I", "A")} confirm · {Keys("K", "B")} back",
            HudMode.Holding when _targetingPotion != null => $"{TargetingName()}:  {Keys("I", "A")} use · {Keys("O", "X")} discard · {Keys("K", "B")} back",
            HudMode.Holding => $"{TargetingName()}:  {Keys("I", "A")} play · {Keys("K", "B")} back",
            HudMode.Potions => PotionHint(),
            HudMode.Relics => $"{CouchTeammateRelicBar.FocusedName ?? "Relics"}    {Keys("J/L", "D-pad")} move · {Keys("K", "B")} back to hand",
            HudMode.Choice when _choice!.MaxSelect == 1 => $"{_choice.Prompt}  {Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick",
            HudMode.Choice => $"{_choice!.Prompt}  ({_picked.Count} picked, {_choice.MinSelect}-{_choice.MaxSelect})  {Keys("J/L", "D-pad")} move · {Keys("I", "A")} toggle · {Keys("O", "Y")} confirm · {Keys("K", "B")} clear",
            _ when ended => $"Turn ended.  {Keys("P", "Y")} to take it back",
            _ => $"{Keys("J/L", "D-pad")} choose · {Keys("I", "A")} play · {Keys("U", "Up")} potions and relics · {Keys("P", "Y")} end turn"
        };
        if (Time.GetTicksMsec() / 1000.0 < _flashUntil)
        {
            hint = $"{_flash}    —    {hint}";
        }

        _hint!.Text = hint;
    }

    /// <summary>The potion itself is described by its tooltip in the top bar.</summary>
    private string PotionHint()
    {
        PotionModel? potion = PotionAtCursor();
        return $"{(potion == null ? "Empty slot" : PotionTitle(potion))}    {Keys("J/L", "D-pad")} choose (right for relics) · {Keys("I", "A")} use · {Keys("O", "X")} discard · {Keys("U", "Down")} back to hand";
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

    /// <summary>The creature being aimed at, if the teammate is aiming something.</summary>
    private Creature? CurrentTarget()
    {
        IReadOnlyList<Creature> targets = _mode == HudMode.Targeting ? Targets() : new List<Creature>();
        return targets.Count > 0 ? targets[Mathf.Clamp(_targetIndex, 0, targets.Count - 1)] : null;
    }

    /// <summary>
    /// While aiming, what the driver sees when aiming with a controller (<c>NTargetManager</c>): the game's targeting
    /// arrow from the card or potion to the creature, tinted for enemies or allies, the creature's selection reticle,
    /// and the card's numbers against that creature (vulnerable, weak, ...).
    /// </summary>
    private void UpdateTargeting()
    {
        Creature? target = CurrentTarget();
        Control? from = null;
        if (target != null && _targetingCard != null)
        {
            int index = _shownCards.IndexOf(_targetingCard);
            from = index >= 0 ? _cardNodes[index] : null;
        }
        else if (target != null && _targetingPotion != null)
        {
            from = CouchTeammateTopBar.Slot(_potionCursor);
        }

        NCard? cardNode = from as NCard;
        if (_handPreviewNode != cardNode)
        {
            SetPreviewTargetSafe(_handPreviewNode, null);
            _handPreviewNode = cardNode;
        }

        SetPreviewTargetSafe(cardNode, target);
        NCreature? creatureNode = target != null ? NCombatRoom.Instance?.GetCreatureNode(target) : null;
        if (from == null || creatureNode == null || _arrow == null || _arrowAnchor == null)
        {
            StopTargetingVisuals();
            return;
        }

        Rect2 fromRect = from.GetGlobalRect();
        float halfHeight = from is NCard ? NCard.defaultSize.Y * from.Scale.Y * 0.5f : fromRect.Size.Y * 0.5f;
        Vector2 center = from is NCard ? from.GlobalPosition : fromRect.GetCenter();
        _arrowAnchor.GlobalPosition = center + new Vector2(0f, halfHeight - 12f);
        if (_arrowFrom != from)
        {
            _arrow.StartDrawingFrom(_arrowAnchor);
            _arrowFrom = from;
            _aimedCreature = null;
        }

        if (_aimedCreature != creatureNode)
        {
            HideAimReticle();
            _arrow.SetHighlightingOn(target!.IsEnemy);
            creatureNode.ShowSingleSelectReticle();
            _aimedCreature = creatureNode;
        }
        else if (!CreatureReticleRef(creatureNode).IsSelected)
        {
            // The driver's own aiming turned it off.
            creatureNode.ShowSingleSelectReticle();
        }

        _arrow.UpdateDrawingTo(creatureNode.VfxSpawnPosition);
    }

    private void StopTargetingVisuals()
    {
        if (_arrowFrom != null)
        {
            _arrow?.StopDrawing();
            _arrowFrom = null;
        }

        HideAimReticle();
    }

    private void HideAimReticle()
    {
        if (_aimedCreature != null && IsInstanceValid(_aimedCreature))
        {
            _aimedCreature.HideSingleSelectReticle();
        }

        _aimedCreature = null;
    }

    private static void SetPreviewTargetSafe(NCard? node, Creature? target)
    {
        if (node != null && IsInstanceValid(node) && node.Model != null)
        {
            node.SetPreviewTarget(target);
        }
    }

    /// <summary>
    /// A played card leaves the hand at once, as the driver's does: it flies toward the teammate's character (where the
    /// game's own play animation for them starts) while its play waits in the action queue.
    /// </summary>
    private void SendCardAway(CardModel? card)
    {
        if (card == null)
        {
            return;
        }

        _queuedPlays.Add(new QueuedPlay(card, NetCombatCard.FromModel(card), Time.GetTicksMsec() / 1000.0));
        int index = _shownCards.IndexOf(card);
        if (index < 0)
        {
            return;
        }

        NCard node = _cardNodes[index];
        _cardNodes.RemoveAt(index);
        _shownCards.RemoveAt(index);
        if (_handPreviewNode == node)
        {
            _handPreviewNode = null;
        }

        CouchCards.SetGlow(node, null);
        node.ZIndex = 5;
        Vector2 destination = NCombatRoom.Instance?.GetCreatureNode(_teammate!.Creature)?.VfxSpawnPosition ?? node.GlobalPosition + new Vector2(0f, 200f);
        Tween tween = node.CreateTween().SetParallel();
        tween.TweenProperty(node, "global_position", destination, 0.3).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenProperty(node, "scale", node.Scale * 0.3f, 0.3).SetEase(Tween.EaseType.In);
        tween.TweenProperty(node, "modulate:a", 0f, 0.3).SetEase(Tween.EaseType.In);
        tween.Chain().TweenCallback(Callable.From(() => CouchCards.Free(node)));
    }

    /// <summary>Matches the queue's play action to the teammate's played card, to know if it later gets cancelled.</summary>
    private void OnActionEnqueued(GameAction action)
    {
        if (action is not PlayCardAction play)
        {
            return;
        }

        QueuedPlay? queued = _queuedPlays.FirstOrDefault((QueuedPlay q) => q.Action == null && q.Net.Equals(play.NetCombatCard));
        if (queued != null)
        {
            queued.Action = action;
        }
    }

    /// <summary>Played cards return to the hand if their play was cancelled, or never reached the queue.</summary>
    private void PruneQueuedPlays()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _queuedPlays.RemoveAll((QueuedPlay q) =>
            q.Card.Pile?.Type != PileType.Hand
            || q.Action?.State is GameActionState.Canceled or GameActionState.Finished
            || (q.Action == null && now - q.SentAt > 3.0));
    }

    private sealed class QueuedPlay(CardModel card, NetCombatCard net, double sentAt)
    {
        public CardModel Card { get; } = card;

        public NetCombatCard Net { get; } = net;

        public double SentAt { get; } = sentAt;

        public GameAction? Action { get; set; }
    }

    /// <summary>Keeps the teammate's card numbers current (strength, weak, ...), as the driver's hand does.</summary>
    /// <summary>
    /// The combat state changes many times per action (every damage, block and power change); refresh the teammate's
    /// card text once per frame at most, not on each one.
    /// </summary>
    private void OnCombatStateChanged(CombatState _)
    {
        _cardTextDirty = true;
    }

    private void RefreshCardText()
    {
        if (!_cardTextDirty)
        {
            return;
        }

        _cardTextDirty = false;
        foreach (NCard node in _cardNodes)
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
/// One potion slot in the teammate HUD, like the driver's top bar potion holders: the potion, or the empty-slot
/// placeholder, with the controller selection reticle when the cursor is on it.
/// </summary>
internal sealed partial class CouchPotionSlot : Control
{
    private PotionModel? _potion;

    private TextureRect? _empty;

    private TextureRect? _image;

    private NSelectionReticle? _reticle;

    private Tween? _tween;

    private bool _highlighted;

    public PotionModel? Potion
    {
        get => _potion;
        set
        {
            if (_potion == value && _image?.Texture != null == (value != null))
            {
                return;
            }

            _potion = value;
            Refresh();
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
            if (value)
            {
                _reticle?.OnSelect();
            }
            else
            {
                _reticle?.OnDeselect();
            }

            _tween?.Kill();
            _tween = CreateTween();
            _tween.TweenProperty(_image!, "scale", Vector2.One * (value ? 1.15f : 1f), value ? 0.05 : 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _empty = new TextureRect
        {
            Texture = CouchStyle.Load<Texture2D>(CouchStyle.PotionPlaceholderPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_empty);
        _image = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_image);
        _reticle = CouchStyle.CreateReticle(this);
        Resized += OnResized;
        OnResized();
        Refresh();
    }

    private void OnResized()
    {
        if (_empty == null || _image == null)
        {
            return;
        }

        _empty.Position = new Vector2(4f, 4f);
        _empty.Size = Size - new Vector2(8f, 8f);
        _image.Position = Vector2.Zero;
        _image.Size = Size;
        _image.PivotOffset = Size * 0.5f;
        CouchStyle.PlaceReticle(_reticle, Vector2.Zero, Size);
    }

    private void Refresh()
    {
        if (_image == null || _empty == null)
        {
            return;
        }

        _image.Texture = _potion?.Image;
        _image.Visible = _potion != null;
        _empty.Visible = _potion == null;
    }
}

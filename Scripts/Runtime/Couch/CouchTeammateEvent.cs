using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's own event, in a panel beside the driver's event screen. Every player has their own copy of the
/// event (<see cref="EventSynchronizer"/>); the teammate reads theirs here and picks options with their own controls,
/// sent as that teammate's <see cref="OptionIndexChosenMessage"/>. In shared events the teammate votes with a
/// <see cref="VotedForSharedEventOptionMessage"/> instead, and the game resolves once everyone has voted. The driver
/// can't leave the room until the teammate is done. Options are drawn and animated like the game's event option buttons,
/// and the option under the teammate's cursor shows what its hover tips would (the card, relic or keyword it
/// references) inside the panel, below the options, rather than as floating tips over the driver's screen.
/// The panel hides while the teammate plays the Crystal Sphere minigame (<see cref="CouchTeammateCrystalSphere"/>).
/// The panel also hides while an event's fight is on (e.g. Punch Off's "Fight"): the event room stays under the combat, and
/// the event isn't finished, but the teammate has to play the combat instead.
/// </summary>
internal sealed partial class CouchTeammateEvent : CouchPanel
{
    public const string PanelNodeName = "CouchTeammateEvent";

    private const float SideMargin = 36f;

    private const float PanelTop = 130f;

    private static readonly AccessTools.FieldRef<EventSynchronizer, uint> PageIndexRef =
        AccessTools.FieldRefAccess<EventSynchronizer, uint>("_pageIndex");

    private static CouchTeammateEvent? _instance;

    /// <summary>The teammate event the teammate has said they're done with.</summary>
    private static EventModel? _doneEvent;

    private readonly List<RowView> _rows = new();

    private Label? _description;

    private Player? _teammate;

    private EventModel? _event;

    private int _stateVersion;

    private int _waitingForStateVersion = -1;

    /// <summary>The <see cref="_stateVersion"/> at which the tracked event started a combat; -1 if it hasn't.</summary>
    private int _enteredCombatAtStateVersion = -1;

    private string _renderedKey = "";

    private int _cursor;

    /// <summary>Scale of the card previews in the details below the options; the card picker's readable size.</summary>
    private const float TipCardScale = 0.46f;

    private const float TipCardGap = 10f;

    /// <summary>The option under the cursor the details were built for.</summary>
    private EventOption? _tipFor;

    /// <summary>The option whose hover tips the details show; null when none are.</summary>
    private EventOption? _tipOption;

    private readonly List<NCard> _tipCards = new();

    private Label? _tipText;

    /// <summary>The option whose details are showing under the teammate's cursor; null if none (tests).</summary>
    public static int? TipOptionIndex => IsActive && _instance!._tipOption != null ? _instance._cursor : null;

    /// <summary>How many card previews the details show (tests).</summary>
    public static int TipCardCount => IsActive ? _instance!._tipCards.Count : 0;

    /// <summary>True while the teammate's event panel is up.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    /// <summary>
    /// True while the teammate still has their event open, or is playing its Crystal Sphere minigame; the driver can't
    /// leave the room.
    /// </summary>
    public static bool BlocksProceed => IsActive || CouchTeammateCrystalSphere.IsActive;

    protected override float PanelWidth => 480f;

    protected override int RowFontSize => 19;

    public static void Attach(NEventRoom room)
    {
        if (room.GetNodeOrNull(PanelNodeName) == null)
        {
            room.AddChild(new CouchTeammateEvent { Name = PanelNodeName });
        }
    }

    public static void NotifyProceedBlocked()
    {
        if (!BlocksProceed)
        {
            return;
        }

        Player? teammate = IsActive ? _instance!._teammate : CouchTeammate.FindTeammate();
        string seat = (teammate == null ? null : CouchSeats.FindByPlayer(teammate.NetId)?.Label) ?? "P2";
        NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create($"Waiting for {seat} to finish their event"));
        if (IsActive)
        {
            _instance!.Flash("The other player is waiting for you");
        }
    }

    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        if (!IsActive || (playerId.HasValue && _instance!._teammate?.NetId != playerId.Value))
        {
            return false;
        }

        _instance!.LastInputFromController = playerId.HasValue;
        _instance.OnCommand(command);
        return true;
    }

    public override void _Ready()
    {
        base._Ready();
        _instance = this;
        TopLevel = true;
        ZIndex = 50;
        _description = CouchStyle.CreateLabel(this, 20, wrapWidth: PanelWidth - CouchFrame.PadLeft - CouchFrame.PadRight);
        _tipText = CouchStyle.CreateLabel(this, 16, wrapWidth: PanelWidth - CouchFrame.PadLeft - CouchFrame.PadRight);
    }

    public override void _ExitTree()
    {
        Track(null);
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        _teammate = CouchTeammate.FindTeammate();
        EventSynchronizer synchronizer = RunManager.Instance.EventSynchronizer;
        EventModel? teammateEvent = _teammate == null ? null : synchronizer.Events.FirstOrDefault((EventModel e) => e.Owner == _teammate);
        bool shared = teammateEvent != null && synchronizer.IsShared;
        if (teammateEvent != null && shared && teammateEvent.IsFinished)
        {
            // Shared events finish for everyone at once; nothing left for the teammate to do.
            _doneEvent = teammateEvent;
        }

        // An event combat pushes a CombatRoom over the EventRoom; the teammate plays that instead. A Combat-layout event
        // (Punch Off) fights inside the event room, so the room change lags the option a little: hide from the moment
        // the event starts the combat until its page changes (an event resumed after combat sets a new page).
        bool inEventCombat = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is not EventRoom
            || (teammateEvent == _event && _enteredCombatAtStateVersion == _stateVersion);
        // The Crystal Sphere minigame has its own panel; keep tracking the event underneath so its finish page shows after.
        bool inMinigame = CouchTeammateCrystalSphere.IsActive;
        if (teammateEvent == null || teammateEvent == _doneEvent || inEventCombat || inMinigame)
        {
            Visible = false;
            if (!inEventCombat && !inMinigame)
            {
                Track(null);
            }

            return;
        }

        Track(teammateEvent);
        Visible = true;
        PlaceOnSide(CouchConfig.EventPanelOnLeft, PanelTop, SideMargin);

        IReadOnlyList<EventOption> options = teammateEvent.IsFinished ? new List<EventOption>() : teammateEvent.CurrentOptions;
        int rowCount = options.Count + (teammateEvent.IsFinished ? 1 : 0);
        string key = $"{_stateVersion}|{rowCount}|{teammateEvent.IsFinished}|{shared}";
        if (key != _renderedKey)
        {
            _renderedKey = key;
            Render(teammateEvent, options);
        }

        _cursor = rowCount == 0 ? 0 : Mathf.Clamp(_cursor, 0, rowCount - 1);
        Layout(options, shared, synchronizer);
    }

    /// <summary>Follows the event's state changes so text is only rebuilt when a page changes.</summary>
    private void Track(EventModel? eventModel)
    {
        if (_event == eventModel)
        {
            return;
        }

        if (_event != null)
        {
            _event.StateChanged -= OnEventStateChanged;
            _event.EnteringEventCombat -= OnEnteringEventCombat;
        }

        _event = eventModel;
        _renderedKey = "";
        _waitingForStateVersion = -1;
        _enteredCombatAtStateVersion = -1;
        _cursor = 0;
        if (_event != null)
        {
            _event.StateChanged += OnEventStateChanged;
            _event.EnteringEventCombat += OnEnteringEventCombat;
        }
    }

    private void OnEventStateChanged(EventModel _)
    {
        _stateVersion++;
        _waitingForStateVersion = -1;
    }

    private void OnEnteringEventCombat()
    {
        _enteredCombatAtStateVersion = _stateVersion;
        CouchLog.Info($"Teammate event {_event?.Id.Entry} entered combat; hiding the teammate event panel.");
    }

    private void Render(EventModel eventModel, IReadOnlyList<EventOption> options)
    {
        SetTitle($"{SeatLabel(eventModel.Owner!)} · {CouchText.Plain(eventModel.Title.GetFormattedText())}");
        _description!.Text = DescriptionText(eventModel);
        SetTipOption(null);
        FreeRows(_rows);
        foreach (EventOption option in options)
        {
            // NEventOptionButton._Ready: an Ancient's relic option shows the relic's icon.
            Texture2D? icon = eventModel is AncientEventModel && option.Relic != null ? option.Relic.Icon : null;
            _rows.Add(CreateOptionRow(icon, OptionText(eventModel, option.Title), OptionText(eventModel, option.Description)));
        }

        if (eventModel.IsFinished)
        {
            _rows.Add(CreateOptionRow(null, "Done", "Finished with this event"));
        }
    }

    /// <summary>
    /// <c>NEventOptionButton.OnFocus</c> opens the focused option's hover tips over the screen. The teammate's are drawn
    /// in the panel instead, below the options, so they don't cover the driver's screen: card previews in a row, then
    /// the text tips (keywords, relics). Returns the y below them.
    /// </summary>
    private float LayoutTips(IReadOnlyList<EventOption> options, float y)
    {
        EventOption? option = _cursor < options.Count && !options[_cursor].IsLocked ? options[_cursor] : null;
        SetTipOption(option);
        float width = PanelWidth - CouchFrame.PadLeft - CouchFrame.PadRight;
        _tipText!.Visible = _tipText.Text.Length > 0;
        if (_tipCards.Count == 0)
        {
            if (_tipText.Visible)
            {
                SetTipTextWidth(width);
                _tipText.Position = new Vector2(CouchFrame.PadLeft, y + 4f);
                y = _tipText.Position.Y + _tipText.GetMinimumSize().Y;
            }

            return y;
        }

        float scale = Mathf.Min(TipCardScale, (width - TipCardGap * (_tipCards.Count - 1)) / _tipCards.Count / NCard.defaultSize.X);
        Vector2 cardSize = NCard.defaultSize * scale;
        float top = y + 8f;
        if (_tipCards.Count == 1)
        {
            // One card: on the left, with the text tips beside it.
            PlaceTipCard(_tipCards[0], CouchFrame.PadLeft + cardSize.X * 0.5f, top, scale);
            float bottom = top + cardSize.Y;
            if (_tipText.Visible)
            {
                float textLeft = CouchFrame.PadLeft + cardSize.X + 14f;
                SetTipTextWidth(PanelWidth - CouchFrame.PadRight - textLeft);
                _tipText.Position = new Vector2(textLeft, top);
                bottom = Mathf.Max(bottom, top + _tipText.GetMinimumSize().Y);
            }

            return bottom + 8f;
        }

        // Several cards: a centered row, the text tips below.
        float rowWidth = cardSize.X * _tipCards.Count + TipCardGap * (_tipCards.Count - 1);
        float x = PanelWidth * 0.5f - rowWidth * 0.5f + cardSize.X * 0.5f;
        foreach (NCard card in _tipCards)
        {
            PlaceTipCard(card, x, top, scale);
            x += cardSize.X + TipCardGap;
        }

        y = top + cardSize.Y + 8f;
        if (_tipText.Visible)
        {
            SetTipTextWidth(width);
            _tipText.Position = new Vector2(CouchFrame.PadLeft, y + 4f);
            y = _tipText.Position.Y + _tipText.GetMinimumSize().Y;
        }

        return y;
    }

    /// <summary>A label keeps its width when its minimum shrinks, so set both (the text wraps at this width).</summary>
    private void SetTipTextWidth(float width)
    {
        _tipText!.CustomMinimumSize = new Vector2(width, 0f);
        _tipText.Size = new Vector2(width, 0f);
    }

    /// <summary>NCard's position is its center; <paramref name="top"/> is where its top edge goes.</summary>
    private static void PlaceTipCard(NCard card, float centerX, float top, float scale)
    {
        card.Scale = new Vector2(scale, scale);
        card.Position = new Vector2(centerX, top + NCard.defaultSize.Y * scale * 0.5f);
    }

    private void SetTipOption(EventOption? option)
    {
        if (option == _tipFor)
        {
            return;
        }

        _tipFor = option;

        foreach (NCard card in _tipCards)
        {
            CouchCards.Free(card);
        }

        _tipCards.Clear();
        _tipText!.Text = "";
        _tipOption = option == null || !option.HoverTips.Any() ? null : option;
        if (_tipOption == null)
        {
            return;
        }

        List<string> lines = new();
        foreach (IHoverTip tip in IHoverTip.RemoveDupes(_tipOption.HoverTips))
        {
            if (tip is CardHoverTip cardTip)
            {
                NCard card = CouchCards.Create(cardTip.Card, this);
                card.ZIndex = 3;
                _tipCards.Add(card);
            }
            else if (tip is HoverTip textTip)
            {
                string description = CouchText.Plain(textTip.Description);
                lines.Add(textTip.Title != null ? $"{CouchText.Plain(textTip.Title)}: {description}" : description);
            }
        }

        _tipText.Text = string.Join("\n", lines);
    }

    private RowView CreateOptionRow(Texture2D? icon, string title, string description)
    {
        RowView row = CreateRow(icon);
        row.Label.Text = description.Length > 0 ? $"{title}\n{description}" : title;
        return row;
    }

    private void Layout(IReadOnlyList<EventOption> options, bool shared, EventSynchronizer synchronizer)
    {
        _description!.Position = new Vector2(CouchFrame.PadLeft, ContentTop);
        _description.Visible = _description.Text.Length > 0;
        float y = _description.Visible ? _description.Position.Y + _description.GetMinimumSize().Y + 12f : ContentTop;

        uint? teammateVote = shared ? synchronizer.GetPlayerVote(_teammate!) : null;
        bool waiting = _waitingForStateVersion == _stateVersion;
        for (int i = 0; i < _rows.Count; i++)
        {
            bool locked = i < options.Count && options[i].IsLocked;
            bool voted = teammateVote.HasValue && teammateVote.Value == i;
            StyleRow(_rows[i], i == _cursor, dimmed: locked || waiting, picked: voted);
        }

        y = LayoutRows(_rows, y);
        y = LayoutTips(options, y);
        string status = waiting ? "Waiting for the event..." : "";
        if (shared)
        {
            status = $"Shared event: everyone votes. {VoteSummary(synchronizer, options)}";
        }

        FinishLayout(y, status, $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} choose");
    }

    private string VoteSummary(EventSynchronizer synchronizer, IReadOnlyList<EventOption> options)
    {
        IEnumerable<string> votes = LocalControlRuntime.SessionState.OrderedPlayerIds
            .Select((ulong id) => RunManager.Instance.DebugOnlyGetState()?.GetPlayer(id))
            .Where((Player? p) => p != null)
            .Select((Player? p) =>
            {
                uint? vote = synchronizer.GetPlayerVote(p!);
                string label = CouchSeats.FindByPlayer(p!.NetId)?.Label ?? "?";
                string choice = vote.HasValue && vote.Value < options.Count ? OptionText(_event!, options[(int)vote.Value].Title) : "not voted";
                return $"{label}: {choice}";
            });
        return string.Join(" · ", votes);
    }

    private void OnCommand(CouchHudCommand command)
    {
        EventModel? eventModel = _event;
        if (eventModel == null)
        {
            return;
        }

        int rowCount = _rows.Count;
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                if (rowCount > 0)
                {
                    _cursor = (_cursor - 1 + rowCount) % rowCount;
                }

                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                if (rowCount > 0)
                {
                    _cursor = (_cursor + 1) % rowCount;
                }

                break;
            case CouchHudCommand.Accept:
                Choose(eventModel);
                break;
        }
    }

    private void Choose(EventModel eventModel)
    {
        if (eventModel.IsFinished)
        {
            _doneEvent = eventModel;
            CouchLog.Info($"Teammate {eventModel.Owner!.NetId} is done with event {eventModel.Id.Entry}.");
            return;
        }

        if (_waitingForStateVersion == _stateVersion)
        {
            Flash("Waiting for the event...");
            return;
        }

        IReadOnlyList<EventOption> options = eventModel.CurrentOptions;
        if (_cursor >= options.Count)
        {
            return;
        }

        if (options[_cursor].IsLocked)
        {
            Flash("That option is locked");
            return;
        }

        LocalLoopbackHostGameService? netService = LocalSelfCoopContext.NetService;
        if (netService == null)
        {
            return;
        }

        EventSynchronizer synchronizer = RunManager.Instance.EventSynchronizer;
        Player teammate = eventModel.Owner!;
        if (synchronizer.IsShared)
        {
            CouchLog.Info($"Teammate {teammate.NetId} votes for option {_cursor} ({options[_cursor].TextKey}) in shared event {eventModel.Id.Entry}.");
            netService.DispatchLoopback(new VotedForSharedEventOptionMessage
            {
                optionIndex = (uint)_cursor,
                pageIndex = PageIndexRef(synchronizer),
                location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation
            }, teammate.NetId);
            return;
        }

        CouchLog.Info($"Teammate {teammate.NetId} chooses option {_cursor} ({options[_cursor].TextKey}) in event {eventModel.Id.Entry}.");
        _waitingForStateVersion = _stateVersion;
        netService.DispatchLoopback(new OptionIndexChosenMessage
        {
            type = OptionIndexType.Event,
            optionIndex = (uint)_cursor,
            location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation
        }, teammate.NetId);
    }

    /// <summary>Formats the teammate's page text the way <c>NEventRoom.SetDescription</c> does.</summary>
    private static string DescriptionText(EventModel eventModel)
    {
        LocString description = eventModel.Description ?? eventModel.InitialDescription;
        if (!description.Exists())
        {
            return "";
        }

        eventModel.Owner!.Character.AddDetailsTo(description);
        description.Add("IsMultiplayer", eventModel.Owner.RunState.Players.Count > 1);
        eventModel.DynamicVars.AddTo(description);
        return CouchText.Plain(description.GetFormattedText());
    }

    /// <summary>Formats option text the way <c>NEventOptionButton</c> does.</summary>
    private static string OptionText(EventModel eventModel, LocString text)
    {
        if (!text.Exists())
        {
            return "";
        }

        eventModel.DynamicVars.AddTo(text);
        return CouchText.Plain(text.GetFormattedText());
    }

}

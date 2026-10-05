using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes;
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
/// can't leave the room until the teammate is done. Options are drawn and animated like the game's event option buttons.
/// The panel hides while an event's fight is on (e.g. Punch Off's "Fight"): the event room stays under the combat, and
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

    /// <summary>True while the teammate's event panel is up.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    /// <summary>True while the teammate still has their event open; the driver can't leave the room.</summary>
    public static bool BlocksProceed => IsActive;

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
        if (!IsActive)
        {
            return;
        }

        string seat = CouchSeats.FindByPlayer(_instance!._teammate!.NetId)?.Label ?? "P2";
        NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create($"Waiting for {seat} to finish their event"));
        _instance.Flash("The other player is waiting for you");
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
        if (teammateEvent == null || teammateEvent == _doneEvent || inEventCombat)
        {
            Visible = false;
            if (!inEventCombat)
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
        FreeRows(_rows);
        foreach (EventOption option in options)
        {
            _rows.Add(CreateOptionRow(OptionText(eventModel, option.Title), OptionText(eventModel, option.Description)));
        }

        if (eventModel.IsFinished)
        {
            _rows.Add(CreateOptionRow("Done", "Finished with this event"));
        }
    }

    private RowView CreateOptionRow(string title, string description)
    {
        RowView row = CreateRow(null);
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

using System.Collections.Generic;
using System.Linq;
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
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's own event, in a panel beside the driver's event screen. Every player has their own copy of the
/// event (<see cref="EventSynchronizer"/>); the teammate reads theirs here and picks options with their own controls,
/// sent as that teammate's <see cref="OptionIndexChosenMessage"/>. In shared events the teammate votes with a
/// <see cref="VotedForSharedEventOptionMessage"/> instead, and the game resolves once everyone has voted. The driver
/// can't leave the room until the teammate is done.
/// </summary>
internal sealed partial class CouchTeammateEvent : Control
{
    public const string PanelNodeName = "CouchTeammateEvent";

    private const float PanelWidth = 470f;

    private const float SideMargin = 36f;

    private const float PanelTop = 130f;

    private const float RowGap = 6f;

    private const double FlashSeconds = 3.0;

    private static readonly AccessTools.FieldRef<EventSynchronizer, uint> PageIndexRef =
        AccessTools.FieldRefAccess<EventSynchronizer, uint>("_pageIndex");

    private static readonly Color RowColor = new(0.1f, 0.14f, 0.18f, 0.92f);

    private static readonly Color RowCursorColor = new(0.28f, 0.34f, 0.4f, 0.97f);

    private static readonly Color CursorBorder = new(1f, 0.78f, 0.25f);

    private static readonly Color VotedColor = new(0.36f, 0.3f, 0.12f, 0.97f);

    private static CouchTeammateEvent? _instance;

    /// <summary>The teammate event the teammate has said they're done with.</summary>
    private static EventModel? _doneEvent;

    private readonly List<OptionRow> _rows = new();

    private Panel? _background;

    private Label? _title;

    private Label? _description;

    private Label? _hint;

    private Player? _teammate;

    private EventModel? _event;

    private int _stateVersion;

    private int _waitingForStateVersion = -1;

    private string _renderedKey = "";

    private int _cursor;

    private bool _lastInputFromController;

    private string _flash = "";

    private double _flashUntil;

    /// <summary>True while the teammate's event panel is up.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    /// <summary>True while the teammate still has their event open; the driver can't leave the room.</summary>
    public static bool BlocksProceed => IsActive;

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

        _instance!._lastInputFromController = playerId.HasValue;
        _instance.OnCommand(command);
        return true;
    }

    public override void _Ready()
    {
        _instance = this;
        TopLevel = true;
        ZIndex = 50;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _background = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _background.AddThemeStyleboxOverride("panel", new StyleBoxFlat
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
        AddChild(_background);
        _title = CreateLabel(24, new Color("f3efe6"), wrap: false);
        _description = CreateLabel(17, new Color("e6e0d2"), wrap: true);
        _hint = CreateLabel(16, new Color("d8d2c4"), wrap: true);
        SetProcess(true);
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

        if (teammateEvent == null || teammateEvent == _doneEvent)
        {
            Visible = false;
            Track(null);
            return;
        }

        Track(teammateEvent);
        Visible = true;
        Vector2 viewport = GetViewportRect().Size;
        float left = CouchConfig.EventPanelOnLeft ? SideMargin : viewport.X - PanelWidth - SideMargin;
        Position = new Vector2(left, CouchConfig.EventPanelOnLeft ? PanelTop : CouchTeammateRelicBar.TopBelowBar(PanelTop));

        IReadOnlyList<EventOption> options = teammateEvent.IsFinished ? new List<EventOption>() : teammateEvent.CurrentOptions;
        int rowCount = options.Count + (teammateEvent.IsFinished ? 1 : 0);
        string key = $"{_stateVersion}|{rowCount}|{teammateEvent.IsFinished}|{shared}";
        if (key != _renderedKey)
        {
            _renderedKey = key;
            Render(teammateEvent, options);
        }

        _cursor = rowCount == 0 ? 0 : Mathf.Clamp(_cursor, 0, rowCount - 1);
        float height = Layout(teammateEvent, options, shared, synchronizer);
        _background!.Size = new Vector2(PanelWidth, height);
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
        }

        _event = eventModel;
        _renderedKey = "";
        _waitingForStateVersion = -1;
        _cursor = 0;
        if (_event != null)
        {
            _event.StateChanged += OnEventStateChanged;
        }
    }

    private void OnEventStateChanged(EventModel _)
    {
        _stateVersion++;
        _waitingForStateVersion = -1;
    }

    private void Render(EventModel eventModel, IReadOnlyList<EventOption> options)
    {
        string seat = CouchSeats.FindByPlayer(eventModel.Owner!.NetId)?.Label ?? "P2";
        _title!.Text = $"{seat} · {CouchText.Plain(eventModel.Title.GetFormattedText())}";
        _description!.Text = DescriptionText(eventModel);

        foreach (OptionRow row in _rows)
        {
            row.Root.QueueFree();
        }

        _rows.Clear();
        foreach (EventOption option in options)
        {
            _rows.Add(CreateRow(OptionText(eventModel, option.Title), OptionText(eventModel, option.Description)));
        }

        if (eventModel.IsFinished)
        {
            _rows.Add(CreateRow("Done", "Finished with this event"));
        }
    }

    private float Layout(EventModel eventModel, IReadOnlyList<EventOption> options, bool shared, EventSynchronizer synchronizer)
    {
        _title!.Position = new Vector2(16f, 12f);
        _description!.Position = new Vector2(16f, 48f);
        float y = _description.Position.Y + _description.GetMinimumSize().Y + 12f;

        uint? teammateVote = shared ? synchronizer.GetPlayerVote(_teammate!) : null;
        bool waiting = _waitingForStateVersion == _stateVersion;
        for (int i = 0; i < _rows.Count; i++)
        {
            OptionRow row = _rows[i];
            bool locked = i < options.Count && options[i].IsLocked;
            bool voted = teammateVote.HasValue && teammateVote.Value == i;
            row.Style.BgColor = voted ? VotedColor : i == _cursor ? RowCursorColor : RowColor;
            int border = i == _cursor ? 2 : 0;
            row.Style.BorderColor = CursorBorder;
            row.Style.BorderWidthLeft = border;
            row.Style.BorderWidthRight = border;
            row.Style.BorderWidthTop = border;
            row.Style.BorderWidthBottom = border;
            row.Root.Modulate = new Color(1f, 1f, 1f, locked || waiting ? 0.45f : 1f);
            row.Root.Position = new Vector2(12f, y);
            float height = Mathf.Max(48f, row.Label.GetMinimumSize().Y + 14f);
            row.Root.Size = new Vector2(PanelWidth - 24f, height);
            row.Label.Size = new Vector2(PanelWidth - 48f, height);
            y += height + RowGap;
        }

        string keys = _lastInputFromController ? "D-pad move · A choose" : "J/L move · I choose";
        string status = waiting ? "Waiting for the event..." : "";
        if (shared)
        {
            status = $"Shared event: everyone votes. {VoteSummary(synchronizer, options)}";
        }

        string hint = string.Join("\n", new[] { Time.GetTicksMsec() / 1000.0 < _flashUntil ? _flash : "", status, keys }.Where((string s) => s.Length > 0));
        _hint!.Text = hint;
        _hint.Position = new Vector2(16f, y + 4f);
        return _hint.Position.Y + _hint.GetMinimumSize().Y + 14f;
    }

    private string VoteSummary(EventSynchronizer synchronizer, IReadOnlyList<EventOption> options)
    {
        IEnumerable<string> votes = LocalMultiControlRuntime.SessionState.OrderedPlayerIds
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

    private OptionRow CreateRow(string title, string description)
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
        Label label = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(12f, 0f),
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = description.Length > 0 ? $"{title}\n{description}" : title,
            CustomMinimumSize = new Vector2(PanelWidth - 48f, 0f)
        };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", new Color("f3efe6"));
        root.AddChild(label);
        AddChild(root);
        return new OptionRow(root, style, label);
    }

    private Label CreateLabel(int fontSize, Color color, bool wrap)
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

    /// <summary>Shows a refusal or a "still waiting" note, with the "no" sound.</summary>
    private void Flash(string message)
    {
        _flash = message;
        _flashUntil = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        CouchSfx.Deny();
    }

    private readonly record struct OptionRow(Panel Root, StyleBoxFlat Style, Label Label);
}

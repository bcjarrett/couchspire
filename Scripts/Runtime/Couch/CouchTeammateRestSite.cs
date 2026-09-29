using Godot;
using CouchSpire.Scripts.Compat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's own rest site options (rest, smith, and anything relics add). Every player has their own options in
/// <see cref="RestSiteSynchronizer"/>; the teammate picks with an <see cref="OptionIndexChosenMessage"/>. "Skip" is
/// tracked locally by remembering the room the teammate skipped in (<see cref="_skippedRoom"/>), and also sent as the
/// teammate where the game branch has a skip message (<see cref="GameCompat.NotifyRestSiteSkipped"/>). Smithing opens
/// the teammate's card picker. The driver can't leave until the teammate is
/// done (see <see cref="BlocksProceed"/>, which is itself purely local).
/// </summary>
internal sealed partial class CouchTeammateRestSite : CouchPanel
{
    private static CouchTeammateRestSite? _instance;

    /// <summary>The rest site whose driver focus has been put on its first option.</summary>
    private static NRestSiteRoom? _focusFixedRoom;

    private readonly List<RowView> _rows = new();

    private List<RestSiteOption> _shownOptions = new();

    private Player? _teammate;

    private RestSiteSynchronizer? _subscribedTo;

    private bool _busy;

    private int _cursor;

    /// <summary>The rest site room instance the teammate has chosen to skip (see class remarks); resets naturally
    /// because a new <see cref="NRestSiteRoom"/> is created for the next visit.</summary>
    private NRestSiteRoom? _skippedRoom;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    public static bool BlocksProceed => IsActive;

    /// <summary>Lives in the shared P2 column (<see cref="CouchPanel.ColumnWidth"/>).</summary>
    protected override float PanelWidth => ColumnWidth;

    public static void NotifyProceedBlocked()
    {
        if (IsActive)
        {
            AnnounceWaiting(_instance!._teammate!, "at the rest site");
            _instance.Flash("The other player is waiting for you");
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
    }

    public override void _ExitTree()
    {
        Subscribe(null);
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        FixDriverFocus();
        IReadOnlyList<RestSiteOption> options = CurrentOptions();
        if (options.Count == 0)
        {
            Visible = false;
            _busy = false;
            return;
        }

        // Smith opens the card picker on top of the rest site; both live in the same column, so let it take over.
        if (CouchTeammateChoicePanel.IsActiveFor(_teammate!.NetId))
        {
            Visible = false;
            return;
        }

        Visible = true;
        if (!options.SequenceEqual(_shownOptions))
        {
            Rebuild(options);
        }

        PlaceInColumn(130f);
        SetTitle($"{SeatLabel(_teammate!)} · Rest site    Gold {_teammate!.Gold}");
        for (int i = 0; i < _rows.Count; i++)
        {
            bool enabled = i >= _shownOptions.Count || _shownOptions[i].IsEnabled;
            StyleRow(_rows[i], i == _cursor, dimmed: !enabled || _busy);
        }

        string status = _busy ? "Doing it..." : "";
        string keys = $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} choose";
        float footer = MeasureFooterHeight(status, keys);
        float y = LayoutRowsScrolled(_rows, ContentTop, RowsBudgetMaxY(ContentTop) - footer, _cursor);
        FinishLayout(y, status, keys);
    }

    /// <summary>
    /// The rest site tries to focus its first option for the driver's controller before the options are enabled, which
    /// doesn't take, so the driver's focus starts up in the top bar. Once per visit, when the first option is ready and the
    /// driver's focus isn't already in the room, put it there, as the game means to.
    /// </summary>
    private void FixDriverFocus()
    {
        NRestSiteRoom? room = NRestSiteRoom.Instance;
        if (room == null || room == _focusFixedRoom || room.DefaultFocusedControl is not NClickableControl { IsEnabled: true } first || !first.IsVisibleInTree())
        {
            return;
        }

        _focusFixedRoom = room;
        Control? owner = GetViewport().GuiGetFocusOwner();
        if (owner == null || !room.IsAncestorOf(owner))
        {
            first.TryGrabFocus();
            CouchLog.Info($"Rest site: driver focus moved to the first option (was {owner?.Name ?? "nothing"}).");
        }
    }

    /// <summary>The teammate's remaining rest site options, or none when there's nothing for them to do.</summary>
    private IReadOnlyList<RestSiteOption> CurrentOptions()
    {
        _teammate = CouchTeammate.FindTeammate();
        if (_teammate == null || NRestSiteRoom.Instance == null || RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is not RestSiteRoom
            || NRestSiteRoom.Instance == _skippedRoom)
        {
            return new List<RestSiteOption>();
        }

        RestSiteSynchronizer synchronizer = RunManager.Instance.RestSiteSynchronizer;
        Subscribe(synchronizer);
        try
        {
            return synchronizer.GetOptionsForPlayer(_teammate);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return new List<RestSiteOption>();
        }
    }

    private void Rebuild(IReadOnlyList<RestSiteOption> options)
    {
        FreeRows(_rows);
        _shownOptions = options.ToList();
        foreach (RestSiteOption option in _shownOptions)
        {
            RowView row = CreateRow(SafeIcon(option));
            string description = CouchText.Plain(option.Description.GetFormattedText());
            row.Label.Text = $"{CouchText.Plain(option.Title.GetFormattedText())}{(description.Length > 0 ? "\n" + description : "")}";
            _rows.Add(row);
        }

        RowView skip = CreateRow(null);
        skip.Label.Text = "Skip the rest";
        _rows.Add(skip);
        _cursor = Godot.Mathf.Clamp(_cursor, 0, _rows.Count - 1);
    }

    private void OnCommand(CouchHudCommand command)
    {
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                _cursor = (_cursor - 1 + _rows.Count) % _rows.Count;
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _cursor = (_cursor + 1) % _rows.Count;
                break;
            case CouchHudCommand.Accept:
                Choose();
                break;
        }
    }

    private void Choose()
    {
        if (_busy || _teammate == null)
        {
            return;
        }

        if (_cursor >= _shownOptions.Count)
        {
            CouchLog.Info($"Teammate {_teammate.NetId} skips the rest of the rest site.");
            _skippedRoom = NRestSiteRoom.Instance;
            GameCompat.NotifyRestSiteSkipped(_teammate);
            return;
        }

        RestSiteOption option = _shownOptions[_cursor];
        if (!option.IsEnabled)
        {
            Flash("Not available");
            return;
        }

        _busy = true;
        CouchLog.Info($"Teammate {_teammate.NetId} chooses rest site option {option.OptionId}.");
        CouchRemotePlay.DispatchAs(_teammate, new OptionIndexChosenMessage
        {
            type = OptionIndexType.RestSite,
            optionIndex = (uint)_cursor,
            location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation
        });
    }

    private void Subscribe(RestSiteSynchronizer? synchronizer)
    {
        if (_subscribedTo == synchronizer)
        {
            return;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.AfterPlayerOptionChosen -= OnOptionChosen;
        }

        _subscribedTo = synchronizer;
        if (_subscribedTo != null)
        {
            _subscribedTo.AfterPlayerOptionChosen += OnOptionChosen;
        }
    }

    private void OnOptionChosen(RestSiteOption option, bool success, ulong playerId)
    {
        if (_teammate?.NetId != playerId)
        {
            return;
        }

        _busy = false;
        if (!success)
        {
            Flash($"{CouchText.Plain(option.Title.GetFormattedText())} didn't happen");
        }
    }

    private static Godot.Texture2D? SafeIcon(RestSiteOption option)
    {
        try
        {
            return option.Icon;
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's own rest site options (rest, smith, and anything relics add). Every player has their own options in
/// <see cref="RestSiteSynchronizer"/>; the teammate picks with an <see cref="OptionIndexChosenMessage"/> or skips with a
/// <see cref="RestSiteSkippedMessage"/>, sent as the teammate. Smithing opens the teammate's card picker. The driver
/// can't leave until the teammate is done.
/// </summary>
internal sealed partial class CouchTeammateRestSite : CouchPanel
{
    private static CouchTeammateRestSite? _instance;

    private readonly List<RowView> _rows = new();

    private List<RestSiteOption> _shownOptions = new();

    private Player? _teammate;

    private RestSiteSynchronizer? _subscribedTo;

    private bool _busy;

    private int _cursor;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    public static bool BlocksProceed => IsActive;

    protected override float PanelWidth => 460f;

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
        IReadOnlyList<RestSiteOption> options = CurrentOptions();
        if (options.Count == 0)
        {
            Visible = false;
            _busy = false;
            return;
        }

        Visible = true;
        if (!options.SequenceEqual(_shownOptions))
        {
            Rebuild(options);
        }

        PlaceOnSide(CouchConfig.EventPanelOnLeft, 130f);
        SetTitle($"{SeatLabel(_teammate!)} · Rest site    Gold {_teammate!.Gold}");
        for (int i = 0; i < _rows.Count; i++)
        {
            bool enabled = i >= _shownOptions.Count || _shownOptions[i].IsEnabled;
            StyleRow(_rows[i], i == _cursor, dimmed: !enabled || _busy);
        }

        float y = LayoutRows(_rows, ContentTop);
        FinishLayout(y, _busy ? "Doing it..." : "", $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} choose");
    }

    /// <summary>The teammate's remaining rest site options, or none when there's nothing for them to do.</summary>
    private IReadOnlyList<RestSiteOption> CurrentOptions()
    {
        _teammate = CouchTeammate.FindTeammate();
        if (_teammate == null || NRestSiteRoom.Instance == null || RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is not RestSiteRoom)
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

        MegaCrit.Sts2.Core.Runs.RunLocation location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation;
        if (_cursor >= _shownOptions.Count)
        {
            CouchLog.Info($"Teammate {_teammate.NetId} skips the rest of the rest site.");
            CouchRemotePlay.DispatchAs(_teammate, new RestSiteSkippedMessage { Location = location });
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
            location = location
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

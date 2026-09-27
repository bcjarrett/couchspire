using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's pick from a treasure chest. Relic picking is a vote (<see cref="TreasureRoomRelicSynchronizer"/>): each
/// player picks a relic (or skips) with a <see cref="PickRelicAction"/>, and relics are handed out once everyone has
/// picked. The teammate's pick is sent as their own action. The panel goes away once the teammate has picked.
/// </summary>
internal sealed partial class CouchTeammateTreasure : CouchPanel
{
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, List<TreasureRoomRelicSynchronizer.PlayerVote>> VotesRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, List<TreasureRoomRelicSynchronizer.PlayerVote>>("_votes");

    private static CouchTeammateTreasure? _instance;

    private readonly List<RowView> _rows = new();

    private List<RelicModel> _shownRelics = new();

    private Player? _teammate;

    private bool _sent;

    private int _cursor;

    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    protected override float PanelWidth => 460f;

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
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        IReadOnlyList<RelicModel>? relics = TeammateStillPicking();
        if (relics == null)
        {
            Visible = false;
            _sent = false;
            return;
        }

        Visible = true;
        if (!relics.SequenceEqual(_shownRelics))
        {
            Rebuild(relics);
        }

        PlaceOnSide(CouchConfig.EventPanelOnLeft, 130f);
        SetTitle($"{SeatLabel(_teammate!)} · Treasure: pick a relic");
        for (int i = 0; i < _rows.Count; i++)
        {
            StyleRow(_rows[i], i == _cursor, dimmed: _sent);
        }

        float y = LayoutRows(_rows, ContentTop);
        FinishLayout(y, _sent ? "Picked, waiting for the chest..." : "", $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick");
    }

    /// <summary>The relics on offer while the teammate hasn't picked yet; null otherwise.</summary>
    private IReadOnlyList<RelicModel>? TeammateStillPicking()
    {
        _teammate = CouchTeammate.FindTeammate();
        TreasureRoomRelicSynchronizer synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        IReadOnlyList<RelicModel>? relics = synchronizer?.CurrentRelics;
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (_teammate == null || relics == null || relics.Count == 0 || runState == null)
        {
            return null;
        }

        List<TreasureRoomRelicSynchronizer.PlayerVote> votes = VotesRef(synchronizer!);
        int slot = runState.GetPlayerSlotIndex(_teammate);
        if (slot < 0 || slot >= votes.Count || votes[slot].voteReceived)
        {
            return null;
        }

        return relics;
    }

    private void Rebuild(IReadOnlyList<RelicModel> relics)
    {
        FreeRows(_rows);
        _shownRelics = relics.ToList();
        foreach (RelicModel relic in _shownRelics)
        {
            RowView row = CreateRow(SafeIcon(relic));
            string description = CouchText.Plain(relic.DynamicDescription.GetFormattedText());
            row.Label.Text = $"{CouchText.Plain(relic.Title.GetFormattedText())}{(description.Length > 0 ? "\n" + description : "")}";
            _rows.Add(row);
        }

        RowView skip = CreateRow(null);
        skip.Label.Text = "Skip";
        _rows.Add(skip);
        _cursor = 0;
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
                if (_sent || _teammate == null)
                {
                    return;
                }

                int? index = _cursor < _shownRelics.Count ? _cursor : null;
                _sent = true;
                CouchLog.Info($"Teammate {_teammate.NetId} picks treasure relic {(index.HasValue ? _shownRelics[index.Value].Id.Entry : "none (skip)")}.");
                CouchRemotePlay.SendAs(_teammate, new PickRelicAction(_teammate, index));
                break;
        }
    }

    private static Godot.Texture2D? SafeIcon(RelicModel relic)
    {
        try
        {
            return relic.Icon;
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}

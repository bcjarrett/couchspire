using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime;

internal sealed class LocalMultiSessionState
{
    private readonly List<ulong> _orderedPlayerIds = new();

    private int _activeIndex;

    public bool IsInitialized { get; private set; }

    public IReadOnlyList<ulong> OrderedPlayerIds => _orderedPlayerIds;

    public ulong? CurrentControlledPlayerId
    {
        get
        {
            if (!IsInitialized || _orderedPlayerIds.Count == 0)
            {
                return null;
            }

            return _orderedPlayerIds[_activeIndex];
        }
    }

    public void InitializeFromRunState(RunState runState)
    {
        Reset("Preparing to initialize new session");

        if (runState.Players.Count < 2)
        {
            LocalMultiControlLogger.Info($"Player count for this run={runState.Players.Count}; local multi-control session is not enabled when there are fewer than 2 players.");
            return;
        }

        if (LocalSelfCoopContext.IsEnabled)
        {
            foreach (ulong localPlayerId in LocalSelfCoopContext.LocalPlayerIds)
            {
                Player? player = runState.GetPlayer(localPlayerId);
                if (player != null && !_orderedPlayerIds.Contains(player.NetId))
                {
                    _orderedPlayerIds.Add(player.NetId);
                }
            }
        }

        if (_orderedPlayerIds.Count == 0)
        {
            foreach (Player player in runState.Players)
            {
                if (!_orderedPlayerIds.Contains(player.NetId))
                {
                    _orderedPlayerIds.Add(player.NetId);
                }
            }
        }

        _activeIndex = 0;
        IsInitialized = true;
        LocalMultiControlLogger.Info(
            $"Local multi-control session initialized, player list: {string.Join(",", _orderedPlayerIds)}, currently controlled player: {_orderedPlayerIds[_activeIndex]}");
    }

    public void Reset(string reason)
    {
        if (IsInitialized || _orderedPlayerIds.Count > 0)
        {
            LocalMultiControlLogger.Info($"Resetting local multi-control session, reason: {reason}");
        }

        IsInitialized = false;
        _orderedPlayerIds.Clear();
        _activeIndex = 0;
    }

    public bool SwitchNextPlayer()
    {
        if (!CanSwitch("switch to next player"))
        {
            return false;
        }

        int previousIndex = _activeIndex;
        _activeIndex = (_activeIndex + 1) % _orderedPlayerIds.Count;
        LocalMultiControlLogger.Info($"Switching controlled player (next): {_orderedPlayerIds[previousIndex]} -> {_orderedPlayerIds[_activeIndex]}");
        return true;
    }

    public bool TrySetCurrentPlayer(ulong playerId)
    {
        if (!IsInitialized)
        {
            return false;
        }

        int index = _orderedPlayerIds.IndexOf(playerId);
        if (index < 0)
        {
            LocalMultiControlLogger.Warn($"Failed to set the current controlled player: player {playerId} is not in the session.");
            return false;
        }

        if (_activeIndex == index)
        {
            return true;
        }

        ulong previousPlayerId = _orderedPlayerIds[_activeIndex];
        _activeIndex = index;
        LocalMultiControlLogger.Info($"Switching controlled player (explicit): {previousPlayerId} -> {_orderedPlayerIds[_activeIndex]}");
        return true;
    }

    private bool CanSwitch(string actionName)
    {
        if (!IsInitialized)
        {
            LocalMultiControlLogger.Warn($"Ignoring {actionName}, reason: session not initialized.");
            return false;
        }

        if (_orderedPlayerIds.Count < 2)
        {
            LocalMultiControlLogger.Warn($"Ignoring {actionName}, reason: fewer than 2 players.");
            return false;
        }

        return true;
    }
}

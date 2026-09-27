namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// One couch seat: a character (player NetId) and the controller that drives it.
/// </summary>
internal sealed class CouchSeat
{
    public CouchSeat(int index, ulong playerId)
    {
        Index = index;
        PlayerId = playerId;
    }

    public int Index { get; }

    public ulong PlayerId { get; set; }

    public CouchDeviceKey? Device { get; set; }

    public bool DeviceConnected { get; set; }

    public string Label => $"P{Index + 1}";
}

/// <summary>
/// Maps controllers to seats and seats to characters. Seats follow the session's player order
/// (<see cref="LocalMultiSessionState.OrderedPlayerIds"/>); controllers bind on their first press.
/// Bindings are kept by seat index across runs so the same couch keeps the same pads.
/// </summary>
internal static class CouchSeats
{
    private static readonly List<CouchSeat> _seats = new();

    private static CouchDeviceKey? _lastMenuDevice;

    public static IReadOnlyList<CouchSeat> All => _seats;

    /// <summary>
    /// Remembers the controller used in menus, so it becomes P1 (the host character) when a run starts.
    /// </summary>
    public static void NoteMenuDevice(CouchDeviceKey device)
    {
        _lastMenuDevice = device;
    }

    public static void SyncWithSession(IReadOnlyList<ulong> orderedPlayerIds)
    {
        bool unchanged = _seats.Count == orderedPlayerIds.Count
            && _seats.Select((CouchSeat seat) => seat.PlayerId).SequenceEqual(orderedPlayerIds);
        if (unchanged)
        {
            return;
        }

        for (int i = 0; i < orderedPlayerIds.Count; i++)
        {
            if (i < _seats.Count)
            {
                _seats[i].PlayerId = orderedPlayerIds[i];
            }
            else
            {
                _seats.Add(new CouchSeat(i, orderedPlayerIds[i]));
            }
        }

        if (_seats.Count > orderedPlayerIds.Count)
        {
            _seats.RemoveRange(orderedPlayerIds.Count, _seats.Count - orderedPlayerIds.Count);
        }

        // With Steam Input the game polls only Steam's first controller in menus, so that one is the menu controller.
        CouchDeviceKey? menuDevice = CouchSteamPoller.PrimaryController() ?? _lastMenuDevice;
        if (menuDevice is CouchDeviceKey device && FindByDevice(device) == null && _seats.Count > 0 && _seats[0].Device == null)
        {
            Bind(_seats[0], device, "menu controller");
        }

        CouchLog.Info($"Seats synced with session: {Describe()}");
    }

    public static CouchSeat? FindByDevice(CouchDeviceKey device)
    {
        return _seats.FirstOrDefault((CouchSeat seat) => seat.Device == device);
    }

    public static CouchSeat? FindByPlayer(ulong playerId)
    {
        return _seats.FirstOrDefault((CouchSeat seat) => seat.PlayerId == playerId);
    }

    /// <summary>
    /// Binds an unknown controller to the first free seat, or to a seat whose controller disconnected.
    /// Returns null when every seat already has a connected controller.
    /// </summary>
    public static CouchSeat? TryBindNewDevice(CouchDeviceKey device)
    {
        CouchSeat? seat = _seats.FirstOrDefault((CouchSeat s) => s.Device == null)
            ?? _seats.FirstOrDefault((CouchSeat s) => !s.DeviceConnected);
        if (seat == null)
        {
            return null;
        }

        Bind(seat, device, seat.Device == null ? "first press" : $"replaces disconnected {seat.Device}");
        return seat;
    }

    public static void MarkConnection(CouchDeviceKey device, bool connected)
    {
        CouchSeat? seat = FindByDevice(device);
        if (seat == null || seat.DeviceConnected == connected)
        {
            return;
        }

        seat.DeviceConnected = connected;
        CouchLog.Info($"{seat.Label} controller {device} {(connected ? "reconnected" : "disconnected")}.");
    }

    /// <summary>
    /// Forgets every controller binding; each controller binds again on its next press (P1 first).
    /// </summary>
    public static void UnbindAll(string reason)
    {
        foreach (CouchSeat seat in _seats)
        {
            seat.Device = null;
            seat.DeviceConnected = false;
        }

        _lastMenuDevice = null;
        CouchLog.Info($"All seat bindings cleared ({reason}); the next controller to press a button becomes P1.");
    }

    public static string Describe()
    {
        if (_seats.Count == 0)
        {
            return "(no seats)";
        }

        return string.Join(", ", _seats.Select((CouchSeat seat) =>
            $"{seat.Label}=player {seat.PlayerId} on {seat.Device?.ToString() ?? "unbound"}{(seat.Device != null && !seat.DeviceConnected ? " (disconnected)" : "")}"));
    }

    private static void Bind(CouchSeat seat, CouchDeviceKey device, string reason)
    {
        seat.Device = device;
        seat.DeviceConnected = true;
        CouchLog.Info($"{seat.Label} (player {seat.PlayerId}) bound to controller {device} ({reason}).");
    }
}

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

internal enum CouchRouteDecision
{
    /// <summary>Let the input through: it comes from the seat that is driving.</summary>
    Pass,

    /// <summary>Swallow the input: it comes from a seat that isn't driving and couldn't take over.</summary>
    Block,

    /// <summary>The press handed control to its seat. The press itself is swallowed so it can't also act.</summary>
    Claimed
}

/// <summary>
/// Decides what happens to each controller input during a couch run. The seat whose character is the
/// current local player (<see cref="LocalContext.NetId"/>) is the driver and its input passes. A button press
/// from another seat claims control when the fork's switch guard allows it (nobody mid-card-play, targeting,
/// or card selection, and combat is in the play phase); otherwise it is swallowed.
/// </summary>
internal static class CouchInputRouter
{
    /// <summary>Inputs that were let through, per device, so their releases are let through too.</summary>
    private static readonly Dictionary<CouchDeviceKey, HashSet<string>> _passedHeld = new();

    /// <summary>Presses that were swallowed, per device, so their releases are swallowed too.</summary>
    private static readonly Dictionary<CouchDeviceKey, HashSet<string>> _swallowedHeld = new();

    private static bool _wasActive;

    /// <summary>
    /// True during a local multi-character run with at least two characters and routing enabled.
    /// Outside of that, every input passes and the game behaves as the fork does.
    /// </summary>
    public static bool IsActive
    {
        get
        {
            bool active = CouchConfig.RoutingEnabled
                && LocalSelfCoopContext.IsEnabled
                && LocalMultiControlRuntime.SessionState.IsInitialized
                && LocalMultiControlRuntime.SessionState.OrderedPlayerIds.Count >= 2
                && RunManager.Instance.IsInProgress;
            if (active != _wasActive)
            {
                _wasActive = active;
                OnActiveChanged(active);
            }

            return active;
        }
    }

    public static CouchRouteDecision Decide(CouchDeviceKey device, string input, CouchInputKind kind)
    {
        if (!IsActive)
        {
            if (kind == CouchInputKind.Press)
            {
                CouchSeats.NoteMenuDevice(device);
            }

            return CouchRouteDecision.Pass;
        }

        CouchSeats.SyncWithSession(LocalMultiControlRuntime.SessionState.OrderedPlayerIds);
        CouchSeat? seat = CouchSeats.FindByDevice(device);
        if (seat == null && (kind == CouchInputKind.Press || kind == CouchInputKind.NavPress))
        {
            seat = CouchSeats.TryBindNewDevice(device);
        }

        if (seat == null)
        {
            if (kind != CouchInputKind.Motion)
            {
                CouchLog.Throttled($"noseat:{device}", $"Controller {device} has no seat (all seats taken); ignoring its input.", 5000);
            }

            return CouchRouteDecision.Block;
        }

        seat.DeviceConnected = true;
        CouchRouteDecision decision = DecideForSeat(seat, device, input, kind);
        if (kind != CouchInputKind.Motion && (decision != CouchRouteDecision.Pass || CouchConfig.ProbeEnabled))
        {
            CouchLog.Remember($"{seat.Label} {device} {input} {kind} -> {decision}");
            CouchLog.Probe($"route {seat.Label} {device} {input} {kind} -> {decision} (driver={LocalContext.NetId})");
        }

        return decision;
    }

    private static CouchRouteDecision DecideForSeat(CouchSeat seat, CouchDeviceKey device, string input, CouchInputKind kind)
    {
        if (kind == CouchInputKind.Release)
        {
            if (Held(_swallowedHeld, device).Remove(input))
            {
                return CouchRouteDecision.Block;
            }

            // Pass the release of anything we let through, even if this seat has since lost control,
            // so no action is left stuck in the pressed state.
            return Held(_passedHeld, device).Remove(input) || LocalContext.NetId == seat.PlayerId
                ? CouchRouteDecision.Pass
                : CouchRouteDecision.Block;
        }

        if (LocalContext.NetId == seat.PlayerId)
        {
            if (kind != CouchInputKind.Motion)
            {
                Held(_passedHeld, device).Add(input);
            }

            return CouchRouteDecision.Pass;
        }

        if (kind == CouchInputKind.Motion)
        {
            return CouchRouteDecision.Block;
        }

        Held(_swallowedHeld, device).Add(input);

        // Simultaneous mode: a teammate's controller drives their own HUD and panels; it only takes the main screen
        // with the break-glass stick click.
        if (CouchConfig.SimultaneousEnabled)
        {
            if (CouchHudInput.IsBreakGlass(input))
            {
                return TryClaim(seat) ? CouchRouteDecision.Claimed : CouchRouteDecision.Block;
            }

            if (CouchHudInput.TryMapController(input, out CouchHudCommand command))
            {
                if (command == CouchHudCommand.Info && !CouchTeammateInfo.IsActive)
                {
                    CouchTeammateInfo.Toggle(seat.PlayerId, fromController: true);
                }
                else
                {
                    CouchTeammateUi.Handle(seat.PlayerId, command);
                }
            }

            return CouchRouteDecision.Block;
        }

        if (kind == CouchInputKind.NavPress)
        {
            return CouchRouteDecision.Block;
        }

        return TryClaim(seat) ? CouchRouteDecision.Claimed : CouchRouteDecision.Block;
    }

    private static bool TryClaim(CouchSeat seat)
    {
        ulong? previousDriver = LocalContext.NetId;
        if (!LocalControlSwitchGuard.TrySwitchTo(seat.PlayerId, $"couch:{seat.Label}"))
        {
            CouchLog.Throttled($"claim-blocked:{seat.Index}", $"{seat.Label} can't take control yet (driver {previousDriver} is mid-action or it's not the play phase).");
            return false;
        }

        if (LocalContext.NetId != seat.PlayerId)
        {
            CouchLog.Warn($"{seat.Label} claim did not take effect (switch was rolled back); driver is still {LocalContext.NetId}.");
            return false;
        }

        CouchLog.Info($"{seat.Label} took control: {previousDriver} -> {seat.PlayerId}.");
        return true;
    }

    private static void OnActiveChanged(bool active)
    {
        _passedHeld.Clear();
        _swallowedHeld.Clear();
        CouchLog.Info(active
            ? $"Couch routing active for this run. {CouchSeats.Describe()}"
            : "Couch routing inactive (no local multi-character run in progress).");
        if (active)
        {
            CouchInputProbe.DumpEnvironment("routing became active");
        }
    }

    private static HashSet<string> Held(Dictionary<CouchDeviceKey, HashSet<string>> held, CouchDeviceKey device)
    {
        if (!held.TryGetValue(device, out HashSet<string>? inputs))
        {
            inputs = new HashSet<string>();
            held[device] = inputs;
        }

        return inputs;
    }
}

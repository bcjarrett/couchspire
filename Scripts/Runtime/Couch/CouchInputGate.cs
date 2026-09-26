using System.Collections.Generic;
using Godot;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// First stop for every input event. Godot calls <c>_Input</c> in reverse tree order, so this node keeps
/// itself as the root's last child and sees events before any game node. Joypad events are routed through
/// <see cref="CouchInputRouter"/>; anything that doesn't pass is marked handled so no game node sees it.
/// Events synthesized by the couch pollers are already routed (<see cref="CouchEventDevice.IsRouted"/>) and pass.
/// </summary>
internal sealed partial class CouchInputGate : Node
{
    public const string GateNodeName = "CouchInputGate";

    private readonly Dictionary<(int Device, JoyAxis Axis), bool> _probeAxisPastHalf = new();

    private bool _loggedRawPadsDropped;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(true);
        SetProcessInput(true);
    }

    public override void _Process(double delta)
    {
        Node? parent = GetParent();
        if (parent != null && GetIndex() != parent.GetChildCount() - 1)
        {
            parent.MoveChild(this, -1);
        }

        CouchScreenshots.Tick();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.F10)
            {
                CouchDebugOverlay.Toggle(this);
            }
            else if (key.Keycode == Key.F9)
            {
                CouchSeats.UnbindAll("F9");
            }
            else if (key.Keycode == Key.F11)
            {
                CouchScreenshots.Take("f11");
            }
            else if (key.Keycode == Key.F7)
            {
                CouchRemotePlay.PlayCardAsTeammate(preferStarterCards: !key.ShiftPressed);
            }
            else if (key.Keycode == Key.F6)
            {
                CouchRemotePlay.ToggleEndTurnAsTeammate();
            }
            else if (key.Keycode == Key.F5)
            {
                CouchTeammateChoices.AnswerOldestWithFirstOptions();
            }
        }

        // V opens the teammate's deck/relics view whenever there is a teammate.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.V } viewKey
            && !viewKey.CtrlPressed && !viewKey.AltPressed && !viewKey.MetaPressed && !viewKey.ShiftPressed
            && CouchTeammate.FindTeammate() != null)
        {
            CouchTeammateInfo.Toggle(null, fromController: false);
            GetViewport().SetInputAsHandled();
            return;
        }

        // Keyboard block for the teammate HUD (J/L/I/K/O/P/U), only while the HUD is up so the keys stay free otherwise.
        if (inputEvent is InputEventKey { Pressed: true } hudKey
            && !hudKey.CtrlPressed && !hudKey.AltPressed && !hudKey.MetaPressed && !hudKey.ShiftPressed
            && CouchHudInput.TryMapKey(hudKey.Keycode, out CouchHudCommand hudCommand)
            && CouchTeammateUi.IsActive)
        {
            if (!hudKey.Echo || hudCommand is CouchHudCommand.Left or CouchHudCommand.Right)
            {
                CouchTeammateUi.Handle(null, hudCommand);
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (!TryClassify(inputEvent, out CouchDeviceKey device, out string input, out CouchInputKind kind))
        {
            return;
        }

        ProbeLog(inputEvent, kind);
        if (CouchSteamPoller.OwnsInput && CouchInputRouter.IsActive)
        {
            // Steam Input already reported these controllers; raw joypad events would be duplicates.
            if (!_loggedRawPadsDropped)
            {
                _loggedRawPadsDropped = true;
                CouchLog.Info($"Dropping raw joypad events ({device} {input}) while Steam Input controllers are polled directly.");
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (CouchInputRouter.Decide(device, input, kind) != CouchRouteDecision.Pass)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private static bool TryClassify(InputEvent inputEvent, out CouchDeviceKey device, out string input, out CouchInputKind kind)
    {
        device = default;
        input = "";
        kind = CouchInputKind.Motion;
        if (CouchEventDevice.IsRouted(inputEvent.Device))
        {
            return false;
        }

        switch (inputEvent)
        {
            case InputEventJoypadButton button:
                device = CouchDeviceKey.Pad(button.Device);
                input = $"btn{(int)button.ButtonIndex}";
                kind = !button.Pressed
                    ? CouchInputKind.Release
                    : IsDpad(button.ButtonIndex) ? CouchInputKind.NavPress : CouchInputKind.Press;
                return true;
            case InputEventJoypadMotion motion:
                device = CouchDeviceKey.Pad(motion.Device);
                input = $"axis{(int)motion.Axis}";
                kind = CouchInputKind.Motion;
                return true;
            default:
                return false;
        }
    }

    private static bool IsDpad(JoyButton button)
    {
        return button is JoyButton.DpadUp or JoyButton.DpadDown or JoyButton.DpadLeft or JoyButton.DpadRight;
    }

    private void ProbeLog(InputEvent inputEvent, CouchInputKind kind)
    {
        if (!CouchConfig.ProbeEnabled)
        {
            return;
        }

        if (inputEvent is InputEventJoypadMotion motion)
        {
            bool pastHalf = Mathf.Abs(motion.AxisValue) >= 0.5f;
            (int, JoyAxis) key = (motion.Device, motion.Axis);
            if (_probeAxisPastHalf.TryGetValue(key, out bool was) && was == pastHalf)
            {
                return;
            }

            _probeAxisPastHalf[key] = pastHalf;
        }

        CouchLog.Probe($"raw {inputEvent.GetType().Name} device={inputEvent.Device} {kind} \"{inputEvent.AsText()}\"");
    }
}

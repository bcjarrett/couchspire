using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Replaces <see cref="GodotControllerInputStrategy.ProcessInput"/> during couch runs. The game turns stick and
/// trigger axes into button actions with <c>Input.IsActionJustPressed</c>, which merges every joypad into one.
/// This reads each joypad's axes separately, routes each transition through <see cref="CouchInputRouter"/>, and
/// emits only what passes, tagged with <see cref="CouchEventDevice.GodotAnalogBase"/> + device id.
/// Joypad buttons don't need this: their events already carry the device and are routed by the input gate.
/// </summary>
internal static class CouchGodotPadPoller
{
    private static readonly AccessTools.FieldRef<GodotControllerInputStrategy, Dictionary<StringName, StringName[]>> AnalogToDigitalRef =
        AccessTools.FieldRefAccess<GodotControllerInputStrategy, Dictionary<StringName, StringName[]>>("_analogToDigitalInput");

    private static readonly MethodInfo? UpdateControllerConfigMethod =
        AccessTools.Method(typeof(GodotControllerInputStrategy), "UpdateControllerConfig");

    /// <summary>Raw analog action -> joypad axis bindings, read from the game's InputMap.</summary>
    private static readonly Dictionary<StringName, List<(JoyAxis Axis, float Sign)>> _axisBindings = new();

    /// <summary>Raw analog actions currently past their deadzone, per Godot device.</summary>
    private static readonly Dictionary<int, HashSet<StringName>> _down = new();

    private static bool _wasPolling;

    /// <summary>
    /// Harmony prefix body for <see cref="GodotControllerInputStrategy.ProcessInput"/>.
    /// Returns true when the original method should run.
    /// </summary>
    public static bool ProcessInput(GodotControllerInputStrategy strategy)
    {
        if (!CouchInputRouter.IsActive)
        {
            StopPolling();
            return true;
        }

        if (!_wasPolling)
        {
            _wasPolling = true;
            CouchLog.Info($"Polling {Input.GetConnectedJoypads().Count} Godot joypad(s) individually.");
        }

        foreach (StringName action in Controller.AllControllerInputs)
        {
            if (Input.IsActionJustPressed(action))
            {
                UpdateControllerConfigMethod?.Invoke(strategy, null);
                break;
            }
        }

        Dictionary<StringName, StringName[]> analogToDigital = AnalogToDigitalRef(strategy);
        foreach (int device in Input.GetConnectedJoypads())
        {
            HashSet<StringName> down = Down(device);
            foreach (KeyValuePair<StringName, StringName[]> mapping in analogToDigital)
            {
                bool isDown = IsPastDeadzone(device, mapping.Key);
                if (isDown == down.Contains(mapping.Key))
                {
                    continue;
                }

                if (isDown)
                {
                    down.Add(mapping.Key);
                }
                else
                {
                    down.Remove(mapping.Key);
                }

                Emit(device, mapping.Key, mapping.Value, isDown);
            }
        }

        return false;
    }

    private static void Emit(int device, StringName rawAction, StringName[] targets, bool pressed)
    {
        bool isTrigger = rawAction.ToString().Contains("trigger", StringComparison.Ordinal);
        CouchInputKind kind = !pressed
            ? CouchInputKind.Release
            : isTrigger ? CouchInputKind.Press : CouchInputKind.NavPress;
        if (CouchInputRouter.Decide(CouchDeviceKey.Pad(device), rawAction, kind) != CouchRouteDecision.Pass)
        {
            return;
        }

        foreach (StringName target in targets)
        {
            Input.ParseInputEvent(new InputEventAction { Action = target, Pressed = pressed, Device = CouchEventDevice.GodotAnalogBase + device });
        }
    }

    private static bool IsPastDeadzone(int device, StringName rawAction)
    {
        if (!_axisBindings.TryGetValue(rawAction, out List<(JoyAxis Axis, float Sign)>? bindings))
        {
            bindings = new List<(JoyAxis Axis, float Sign)>();
            if (InputMap.HasAction(rawAction))
            {
                foreach (InputEvent inputEvent in InputMap.ActionGetEvents(rawAction))
                {
                    if (inputEvent is InputEventJoypadMotion motion)
                    {
                        bindings.Add((motion.Axis, Mathf.Sign(motion.AxisValue)));
                    }
                }
            }

            _axisBindings[rawAction] = bindings;
            CouchLog.Probe($"Analog action {rawAction} reads axes [{string.Join(", ", bindings)}]");
        }

        float deadzone = InputMap.HasAction(rawAction) ? InputMap.ActionGetDeadzone(rawAction) : 0.5f;
        foreach ((JoyAxis axis, float sign) in bindings)
        {
            if (Input.GetJoyAxis(device, axis) * sign > deadzone)
            {
                return true;
            }
        }

        return false;
    }

    private static void StopPolling()
    {
        if (!_wasPolling)
        {
            return;
        }

        _wasPolling = false;
        _down.Clear();
        CouchLog.Info("Stopped per-joypad polling; the game's own polling resumes.");
    }

    private static HashSet<StringName> Down(int device)
    {
        if (!_down.TryGetValue(device, out HashSet<StringName>? set))
        {
            set = new HashSet<StringName>();
            _down[device] = set;
        }

        return set;
    }
}

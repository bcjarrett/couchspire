using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Replaces <see cref="SteamControllerInputStrategy.ProcessInput"/> during couch runs. The game only polls the
/// first Steam Input controller (<c>GetConnectedControllers(...)[0]</c>); this polls every connected controller,
/// routes each transition through <see cref="CouchInputRouter"/> at the source, and only emits the inputs that pass.
/// Emitted events carry <see cref="CouchEventDevice.SteamBase"/> + slot as their device so the input gate skips them.
/// </summary>
internal static class CouchSteamPoller
{
    private const int MaxControllers = 16;

    private const float StickThreshold = 0.5f;

    private static readonly string[] StickDirections = { "Joy_Up", "Joy_Down", "Joy_Left", "Joy_Right" };

    private static readonly HashSet<string> NavigationActions = new() { "Up", "Down", "Left", "Right" };

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, ControllerConfig?> ControllerConfigRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, ControllerConfig?>("_controllerConfig");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, Dictionary<StringName, InputDigitalActionHandle_t>> DigitalHandleCacheRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, Dictionary<StringName, InputDigitalActionHandle_t>>("_digitalActionHandleCache");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, bool> AttemptedHandleCacheRebuildRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, bool>("_attemptedHandleCacheRebuild");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, InputAnalogActionHandle_t> JoystickActionHandleRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, InputAnalogActionHandle_t>("_joystickActionHandle");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, Vector2> LStickPositionRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, Vector2>("_lStickPosition");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, List<string>> PressedInputsRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, List<string>>("_pressedInputs");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, double> NextControllerCheckTimeRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, double>("_nextControllerCheckTime");

    private static readonly AccessTools.FieldRef<SteamControllerInputStrategy, InputActionSetHandle_t?> CurrentActionSetHandleRef =
        AccessTools.FieldRefAccess<SteamControllerInputStrategy, InputActionSetHandle_t?>("_currentActionSetHandle");

    private static readonly MethodInfo? UpdateControllerConnectionsMethod =
        AccessTools.Method(typeof(SteamControllerInputStrategy), "UpdateControllerConnections");

    private static readonly MethodInfo? UpdateInputMapMethod =
        AccessTools.Method(typeof(SteamControllerInputStrategy), "UpdateInputMap");

    private static readonly InputHandle_t[] _handles = new InputHandle_t[MaxControllers];

    private static readonly Dictionary<ulong, int> _slots = new();

    private static readonly Dictionary<ulong, HashSet<string>> _downInputs = new();

    private static readonly Dictionary<ulong, HashSet<string>> _emittedDown = new();

    private static readonly Dictionary<ulong, Vector2> _stickPositions = new();

    private static readonly Dictionary<ulong, HashSet<string>> _observedDown = new();

    private static double _nextObserveRefresh;

    private static ControllerConfig? _mappedConfig;

    private static List<KeyValuePair<string, StringName>> _actionMap = new();

    private static int _handleCount;

    private static bool _wasPolling;

    /// <summary>
    /// True while couch routing polls Steam Input controllers. Raw Godot joypad events are then duplicates of
    /// the same physical controllers and are dropped by the input gate.
    /// </summary>
    public static bool OwnsInput { get; private set; }

    public static int HandleCount => _handleCount;

    /// <summary>
    /// Harmony prefix body for <see cref="SteamControllerInputStrategy.ProcessInput"/>.
    /// Returns true when the original method should run.
    /// </summary>
    public static bool ProcessInput(SteamControllerInputStrategy strategy)
    {
        if (!SteamInitializer.Initialized)
        {
            StopPolling(strategy);
            return true;
        }

        if (!CouchInputRouter.IsActive)
        {
            StopPolling(strategy);
            if (CouchConfig.ProbeEnabled)
            {
                ObserveOnly(strategy);
            }

            return true;
        }

        try
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now >= NextControllerCheckTimeRef(strategy))
            {
                NextControllerCheckTimeRef(strategy) = now + 1.0;
                // The original keeps controller type, glyphs and the action handle cache up to date from the first
                // controller; keep that, then activate the action set on every controller.
                UpdateControllerConnectionsMethod?.Invoke(strategy, null);
                RefreshHandles(strategy);
            }

            if (_handleCount == 0)
            {
                // No Steam Input controllers: the original falls back to the Godot joypad strategy.
                StopPolling(strategy);
                return true;
            }

            if (!_wasPolling)
            {
                _wasPolling = true;
                PressedInputsRef(strategy).Clear();
                CouchLog.Info($"Polling {_handleCount} Steam Input controller(s) individually.");
            }

            OwnsInput = true;
            SteamInput.RunFrame();
            if (!PrepareActionMap(strategy, out Dictionary<StringName, InputDigitalActionHandle_t>? handleCache))
            {
                return false;
            }

            for (int i = 0; i < _handleCount; i++)
            {
                PollDigital(_handles[i], handleCache!);
                PollStick(strategy, _handles[i]);
            }
        }
        catch (InvalidOperationException ex)
        {
            CouchLog.Throttled("steam-poll-error", $"Steam Input polling failed, falling back to the game's own polling: {ex.Message}");
            StopPolling(strategy);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The controller Steam Input lists first, which is the one the game itself polls in menus.
    /// </summary>
    public static CouchDeviceKey? PrimaryController()
    {
        if (!SteamInitializer.Initialized)
        {
            return null;
        }

        try
        {
            InputHandle_t[] handles = new InputHandle_t[MaxControllers];
            return SteamInput.GetConnectedControllers(handles) > 0 ? CouchDeviceKey.Steam(handles[0].m_InputHandle) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static string DescribeHandles()
    {
        if (!SteamInitializer.Initialized)
        {
            return "Steam not initialized";
        }

        List<string> parts = new();
        for (int i = 0; i < _handleCount; i++)
        {
            parts.Add(DescribeHandle(_handles[i]));
        }

        return parts.Count == 0 ? "no Steam Input controllers" : string.Join(", ", parts);
    }

    public static string DescribeHandle(InputHandle_t handle)
    {
        ESteamInputType type = SteamInput.GetInputTypeForHandle(handle);
        int gamepadIndex = SteamInput.GetGamepadIndexForController(handle);
        string slot = _slots.TryGetValue(handle.m_InputHandle, out int s) ? $"slot {s}" : "no slot";
        return $"{CouchDeviceKey.Steam(handle.m_InputHandle)} ({type}, {slot}, xinput index {gamepadIndex})";
    }

    private static void RefreshHandles(SteamControllerInputStrategy strategy)
    {
        int count = SteamInput.GetConnectedControllers(_handles);
        _handleCount = Math.Clamp(count, 0, MaxControllers);

        InputActionSetHandle_t actionSet = CurrentActionSetHandleRef(strategy) ?? SteamInput.GetActionSetHandle("Controls");
        HashSet<ulong> connected = new();
        for (int i = 0; i < _handleCount; i++)
        {
            InputHandle_t handle = _handles[i];
            ulong id = handle.m_InputHandle;
            connected.Add(id);
            SteamInput.ActivateActionSet(handle, actionSet);
            if (!_slots.ContainsKey(id))
            {
                _slots[id] = _slots.Count;
                CouchLog.Info($"Steam Input controller connected: {DescribeHandle(handle)}");
            }

            CouchSeats.MarkConnection(CouchDeviceKey.Steam(id), connected: true);
        }

        foreach (ulong id in _downInputs.Keys.Where((ulong id) => !connected.Contains(id)).ToList())
        {
            CouchLog.Info($"Steam Input controller disconnected: {CouchDeviceKey.Steam(id)}");
            ReleaseAll(id);
            _downInputs.Remove(id);
            _stickPositions.Remove(id);
            CouchSeats.MarkConnection(CouchDeviceKey.Steam(id), connected: false);
        }
    }

    private static bool PrepareActionMap(SteamControllerInputStrategy strategy, out Dictionary<StringName, InputDigitalActionHandle_t>? handleCache)
    {
        handleCache = DigitalHandleCacheRef(strategy);
        ControllerConfig? config = ControllerConfigRef(strategy);
        if (config == null)
        {
            return false;
        }

        if (handleCache.Count == 0 && !AttemptedHandleCacheRebuildRef(strategy))
        {
            AttemptedHandleCacheRebuildRef(strategy) = true;
            UpdateInputMapMethod?.Invoke(strategy, null);
        }

        if (!ReferenceEquals(config, _mappedConfig))
        {
            // SteamInputControllerMap builds a new dictionary on every read; cache it per config.
            _mappedConfig = config;
            _actionMap = config.SteamInputControllerMap.ToList();
        }

        return true;
    }

    private static void PollDigital(InputHandle_t handle, Dictionary<StringName, InputDigitalActionHandle_t> handleCache)
    {
        ulong id = handle.m_InputHandle;
        HashSet<string> down = Set(_downInputs, id);
        foreach (KeyValuePair<string, StringName> mapping in _actionMap)
        {
            if (!handleCache.TryGetValue(mapping.Key, out InputDigitalActionHandle_t actionHandle))
            {
                continue;
            }

            bool isDown = SteamInput.GetDigitalActionData(handle, actionHandle).bState == 1;
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

            CouchInputKind kind = !isDown
                ? CouchInputKind.Release
                : NavigationActions.Contains(mapping.Key) ? CouchInputKind.NavPress : CouchInputKind.Press;
            Emit(id, mapping.Key, mapping.Value, isDown, kind);
        }
    }

    private static void PollStick(SteamControllerInputStrategy strategy, InputHandle_t handle)
    {
        ulong id = handle.m_InputHandle;
        InputAnalogActionData_t data = SteamInput.GetAnalogActionData(handle, JoystickActionHandleRef(strategy));
        Vector2 position = new(data.x, data.y);
        CouchDeviceKey device = CouchDeviceKey.Steam(id);
        CouchSeat? seat = CouchSeats.FindByDevice(device);
        bool isDriver = seat != null && LocalContext.NetId == seat.PlayerId;

        if (isDriver)
        {
            // Read by GetLeftAnalogStickDirection (map scrolling, etc.).
            LStickPositionRef(strategy) = position;
        }

        _stickPositions.TryGetValue(id, out Vector2 previous);
        if (isDriver && position.DistanceTo(previous) > 0.05f)
        {
            int eventDevice = SteamEventDevice(id);
            Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = position.X, Device = eventDevice });
            Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = -position.Y, Device = eventDevice });
        }

        _stickPositions[id] = position;

        HashSet<string> down = Set(_downInputs, id);
        foreach (string direction in StickDirections)
        {
            bool isDown = direction switch
            {
                "Joy_Up" => position.Y >= StickThreshold,
                "Joy_Down" => position.Y <= -StickThreshold,
                "Joy_Left" => position.X <= -StickThreshold,
                _ => position.X >= StickThreshold
            };
            if (isDown == down.Contains(direction))
            {
                continue;
            }

            if (isDown)
            {
                down.Add(direction);
            }
            else
            {
                down.Remove(direction);
            }

            StringName? action = StickAction(direction);
            if (action != null)
            {
                Emit(id, direction, action, isDown, isDown ? CouchInputKind.NavPress : CouchInputKind.Release);
            }
        }
    }

    private static StringName? StickAction(string direction)
    {
        string digitalKey = direction.Substring("Joy_".Length);
        foreach (KeyValuePair<string, StringName> mapping in _actionMap)
        {
            if (mapping.Key == digitalKey)
            {
                return mapping.Value;
            }
        }

        return null;
    }

    private static void Emit(ulong id, string input, StringName action, bool pressed, CouchInputKind kind)
    {
        CouchRouteDecision decision = CouchInputRouter.Decide(CouchDeviceKey.Steam(id), input, kind);
        if (decision != CouchRouteDecision.Pass)
        {
            return;
        }

        HashSet<string> emitted = Set(_emittedDown, id);
        if (pressed)
        {
            emitted.Add(input);
        }
        else
        {
            emitted.Remove(input);
        }

        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = pressed, Device = SteamEventDevice(id) });
    }

    /// <summary>
    /// Releases everything this poller pressed on the game's behalf, so nothing stays held after
    /// routing stops or a controller disconnects.
    /// </summary>
    private static void ReleaseAll(ulong id)
    {
        if (!_emittedDown.TryGetValue(id, out HashSet<string>? emitted) || emitted.Count == 0)
        {
            return;
        }

        foreach (string input in emitted)
        {
            StringName? action = input.StartsWith("Joy_", StringComparison.Ordinal)
                ? StickAction(input)
                : _actionMap.FirstOrDefault((KeyValuePair<string, StringName> m) => m.Key == input).Value;
            if (action != null)
            {
                Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false, Device = SteamEventDevice(id) });
            }
        }

        emitted.Clear();
    }

    private static void StopPolling(SteamControllerInputStrategy strategy)
    {
        OwnsInput = false;
        if (!_wasPolling)
        {
            return;
        }

        _wasPolling = false;
        foreach (ulong id in _emittedDown.Keys.ToList())
        {
            ReleaseAll(id);
        }

        _downInputs.Clear();
        _stickPositions.Clear();
        PressedInputsRef(strategy).Clear();
        CouchLog.Info("Stopped per-controller Steam Input polling; the game's own polling resumes.");
    }

    /// <summary>
    /// Probe mode outside couch runs: log every Steam Input controller's button transitions without emitting
    /// anything, to show whether the API sees each controller separately.
    /// </summary>
    private static void ObserveOnly(SteamControllerInputStrategy strategy)
    {
        try
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now >= _nextObserveRefresh)
            {
                _nextObserveRefresh = now + 1.0;
                int previousCount = _handleCount;
                _handleCount = Math.Clamp(SteamInput.GetConnectedControllers(_handles), 0, MaxControllers);
                for (int i = 0; i < _handleCount; i++)
                {
                    if (!_slots.ContainsKey(_handles[i].m_InputHandle))
                    {
                        _slots[_handles[i].m_InputHandle] = _slots.Count;
                        CouchLog.Probe($"Steam Input sees controller {DescribeHandle(_handles[i])}");
                    }
                }

                if (previousCount != _handleCount)
                {
                    CouchLog.Probe($"Steam Input controller count: {previousCount} -> {_handleCount}");
                }
            }

            if (!PrepareActionMap(strategy, out Dictionary<StringName, InputDigitalActionHandle_t>? handleCache))
            {
                return;
            }

            for (int i = 0; i < _handleCount; i++)
            {
                ulong id = _handles[i].m_InputHandle;
                HashSet<string> down = Set(_observedDown, id);
                foreach (KeyValuePair<string, StringName> mapping in _actionMap)
                {
                    if (!handleCache!.TryGetValue(mapping.Key, out InputDigitalActionHandle_t actionHandle))
                    {
                        continue;
                    }

                    bool isDown = SteamInput.GetDigitalActionData(_handles[i], actionHandle).bState == 1;
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

                    CouchLog.Probe($"steam {CouchDeviceKey.Steam(id)} {mapping.Key} ({mapping.Value}) {(isDown ? "down" : "up")} [observe only]");
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            CouchLog.Throttled("steam-observe-error", $"Steam Input observe failed: {ex.Message}", 10000);
        }
    }

    private static int SteamEventDevice(ulong id)
    {
        if (!_slots.TryGetValue(id, out int slot))
        {
            slot = _slots.Count;
            _slots[id] = slot;
        }

        return CouchEventDevice.SteamBase + slot;
    }

    private static HashSet<string> Set(Dictionary<ulong, HashSet<string>> sets, ulong id)
    {
        if (!sets.TryGetValue(id, out HashSet<string>? set))
        {
            set = new HashSet<string>();
            sets[id] = set;
        }

        return set;
    }
}

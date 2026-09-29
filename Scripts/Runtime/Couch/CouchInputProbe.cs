using Godot;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Input diagnostics for couch co-op (enable with <c>COUCHSPIRE_PROBE=1</c>). Dumps what each input path can
/// see: Steam Input controllers, Godot joypads, and the InputMap bindings for controller actions. Together with
/// the per-event probe lines from the gate and pollers, this shows whether each event can be traced to a
/// physical controller.
/// </summary>
internal static class CouchInputProbe
{
    private static readonly string[] ActionPrefixes = { "controller_", "raw_", "ui_", "mega_" };

    public static void DumpEnvironment(string reason)
    {
        if (!CouchConfig.ProbeEnabled)
        {
            return;
        }

        CouchLog.Probe($"===== Input environment ({reason}) =====");
        CouchLog.Probe($"Routing enabled={CouchConfig.RoutingEnabled}, active={CouchInputRouter.IsActive}, Steam initialized={SteamInitializer.Initialized}");
        DumpSteamControllers();
        DumpGodotJoypads();
        DumpInputMap();
        CouchLog.Probe($"Seats: {CouchSeats.Describe()}");
        CouchLog.Probe("===== end =====");
    }

    private static void DumpSteamControllers()
    {
        if (!SteamInitializer.Initialized)
        {
            return;
        }

        try
        {
            InputHandle_t[] handles = new InputHandle_t[16];
            int count = SteamInput.GetConnectedControllers(handles);
            CouchLog.Probe($"Steam Input controllers: {count}");
            for (int i = 0; i < count; i++)
            {
                CouchLog.Probe($"  [{i}] {CouchSteamPoller.DescribeHandle(handles[i])}");
            }
        }
        catch (InvalidOperationException ex)
        {
            CouchLog.Probe($"Steam Input query failed: {ex.Message}");
        }
    }

    private static void DumpGodotJoypads()
    {
        Godot.Collections.Array<int> joypads = Input.GetConnectedJoypads();
        CouchLog.Probe($"Godot joypads: {joypads.Count}");
        foreach (int device in joypads)
        {
            CouchLog.Probe($"  pad{device}: \"{Input.GetJoyName(device)}\" guid={Input.GetJoyGuid(device)}");
        }
    }

    private static void DumpInputMap()
    {
        foreach (StringName action in InputMap.GetActions().OrderBy((StringName a) => a.ToString()))
        {
            string name = action.ToString();
            if (!ActionPrefixes.Any((string prefix) => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            string events = string.Join(" | ", InputMap.ActionGetEvents(action).Select((InputEvent e) => $"{e.GetType().Name}(dev {e.Device}): {e.AsText()}"));
            CouchLog.Probe($"  map {name} (deadzone {InputMap.ActionGetDeadzone(action):F2}): {(events.Length == 0 ? "-" : events)}");
        }
    }
}

using Godot;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Boots couch co-op: attaches the input gate to the scene root once the tree is running, and tracks
/// joypad connections so seats know when their controller drops out.
/// </summary>
internal static class CouchRuntime
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        CouchLog.Info($"Couch co-op loaded (routing={(CouchConfig.RoutingEnabled ? "on" : "off")}, probe={(CouchConfig.ProbeEnabled ? "on" : "off")}, overlay={(CouchConfig.OverlayAtStart ? "on" : "off")}; settings from {CouchConfig.Source}).");
        Input.Singleton.JoyConnectionChanged += OnJoyConnectionChanged;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += AttachGate;
        }
        else
        {
            CouchLog.Warn("No SceneTree at mod init; couch input gate not attached.");
        }
    }

    private static void AttachGate()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        tree.ProcessFrame -= AttachGate;
        if (tree.Root.GetNodeOrNull(CouchInputGate.GateNodeName) != null)
        {
            return;
        }

        CouchInputGate gate = new() { Name = CouchInputGate.GateNodeName };

        // The teammate's relic bar and room panels live above the game's UI layers (the bar first, under the panels).
        CanvasLayer panels = new() { Name = "CouchTeammatePanels", Layer = 110 };
        panels.AddChild(new CouchTeammateRelicBar { Name = "CouchTeammateRelicBar" });
        panels.AddChild(new CouchTeammateChoicePanel { Name = "CouchTeammateChoicePanel" });
        panels.AddChild(new CouchTeammateRestSite { Name = "CouchTeammateRestSite" });
        panels.AddChild(new CouchTeammateTreasure { Name = "CouchTeammateTreasure" });
        panels.AddChild(new CouchTeammateShop { Name = "CouchTeammateShop" });
        panels.AddChild(new CouchTeammateDeckChanges { Name = "CouchTeammateDeckChanges" });
        panels.AddChild(new CouchTeammateInfo { Name = "CouchTeammateInfo" });
        gate.AddChild(panels);
        tree.Root.CallDeferred(Node.MethodName.AddChild, gate);
        if (CouchConfig.OverlayAtStart)
        {
            Callable.From(() => CouchDebugOverlay.Toggle(gate)).CallDeferred();
        }

        tree.CreateTimer(5.0, processAlways: true).Timeout += () => CouchInputProbe.DumpEnvironment("startup");
        CouchLog.Info("Couch input gate attached.");
    }

    private static void OnJoyConnectionChanged(long device, bool connected)
    {
        CouchLog.Info($"Joypad pad{device} {(connected ? "connected" : "disconnected")}: \"{Input.GetJoyName((int)device)}\"");
        CouchSeats.MarkConnection(CouchDeviceKey.Pad((int)device), connected);
        CouchInputProbe.DumpEnvironment($"pad{device} {(connected ? "connected" : "disconnected")}");
    }
}

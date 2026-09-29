using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Boots couch co-op: attaches the input gate to the scene root once the tree is running, and tracks
/// joypad connections so seats know when their controller drops out.
/// </summary>
internal static class CouchRuntime
{
    private static bool _initialized;

    /// <summary>The teammate's relic bar, top bar section and room panels.</summary>
    private static Control? _panels;

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

        // The teammate's relic bar, top bar section and room panels (the bars first, under the panels). Attached to the
        // game once it exists (see EnsurePanelsAttached).
        Control panels = new() { Name = "CouchTeammatePanels", MouseFilter = Control.MouseFilterEnum.Ignore };
        panels.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        panels.AddChild(new CouchTeammateRelicBar { Name = "CouchTeammateRelicBar" });
        panels.AddChild(new CouchTeammateTopBar { Name = "CouchTeammateTopBar" });
        panels.AddChild(new CouchTeammateChoicePanel { Name = "CouchTeammateChoicePanel" });
        panels.AddChild(new CouchTeammateRestSite { Name = "CouchTeammateRestSite" });
        panels.AddChild(new CouchTeammateTreasure { Name = "CouchTeammateTreasure" });
        panels.AddChild(new CouchTeammateShop { Name = "CouchTeammateShop" });
        panels.AddChild(new CouchTeammateDeckChanges { Name = "CouchTeammateDeckChanges" });
        panels.AddChild(new CouchTeammateInfo { Name = "CouchTeammateInfo" });
        _panels = panels;
        tree.Root.CallDeferred(Node.MethodName.AddChild, gate);
        if (CouchConfig.OverlayAtStart)
        {
            Callable.From(() => CouchDebugOverlay.Toggle(gate)).CallDeferred();
        }

        tree.CreateTimer(5.0, processAlways: true).Timeout += () => CouchInputProbe.DumpEnvironment("startup");
        CouchLog.Info("Couch input gate attached.");
    }

    /// <summary>
    /// Keeps the teammate's UI in the game's own draw order: over the run (its rooms, top bar and overlay screens), under
    /// the game's hover tips, popups and screen transitions, so tooltips are never covered by the teammate's UI. Called
    /// every frame by the input gate.
    /// </summary>
    public static void EnsurePanelsAttached()
    {
        NGame? game = NGame.Instance;
        Node? tips = game?.HoverTipsContainer;
        if (_panels == null || game == null || tips == null || !GodotObject.IsInstanceValid(game))
        {
            return;
        }

        if (_panels.GetParent() != game)
        {
            _panels.GetParent()?.RemoveChild(_panels);
            game.AddChild(_panels);
            CouchLog.Info("Teammate UI attached under the game's hover tips.");
        }

        if (_panels.GetIndex() > tips.GetIndex())
        {
            game.MoveChild(_panels, tips.GetIndex());
        }
    }

    private static void OnJoyConnectionChanged(long device, bool connected)
    {
        CouchLog.Info($"Joypad pad{device} {(connected ? "connected" : "disconnected")}: \"{Input.GetJoyName((int)device)}\"");
        CouchSeats.MarkConnection(CouchDeviceKey.Pad((int)device), connected);
        CouchInputProbe.DumpEnvironment($"pad{device} {(connected ? "connected" : "disconnected")}");
    }
}

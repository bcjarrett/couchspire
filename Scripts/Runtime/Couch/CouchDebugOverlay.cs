using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Top-left text overlay for couch debugging: routing state, input path, seats and their controllers,
/// the current driver, and recent routing decisions. Toggle with F10, or show at startup with
/// <c>COUCHSPIRE_OVERLAY=1</c> (implied by <c>COUCHSPIRE_PROBE=1</c>).
/// </summary>
internal sealed partial class CouchDebugOverlay : CanvasLayer
{
    private const string OverlayNodeName = "CouchDebugOverlay";

    private const double RefreshIntervalSeconds = 0.25;

    private Label? _label;

    private double _untilRefresh;

    public static void Toggle(Node host)
    {
        CouchDebugOverlay? overlay = host.GetNodeOrNull<CouchDebugOverlay>(OverlayNodeName);
        if (overlay == null)
        {
            host.AddChild(new CouchDebugOverlay { Name = OverlayNodeName });
            return;
        }

        overlay.Visible = !overlay.Visible;
    }

    public override void _Ready()
    {
        Layer = 120;
        ProcessMode = ProcessModeEnum.Always;
        PanelContainer panel = new() { Position = new Vector2(12, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.7f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6 });
        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 16);
        _label.AddThemeColorOverride("font_color", Colors.White);
        panel.AddChild(_label);
        AddChild(panel);
    }

    public override void _Process(double delta)
    {
        _untilRefresh -= delta;
        if (!Visible || _label == null || _untilRefresh > 0)
        {
            return;
        }

        _untilRefresh = RefreshIntervalSeconds;
        _label.Text = BuildText();
    }

    private static string BuildText()
    {
        StringBuilder text = new();
        bool active = CouchInputRouter.IsActive;
        text.AppendLine($"Couch routing: {(active ? "ACTIVE" : "inactive")}{(CouchConfig.RoutingEnabled ? "" : " (disabled by COUCHSPIRE_ROUTING=0)")}");
        text.AppendLine(CouchSteamPoller.OwnsInput
            ? $"Input: Steam Input API, {CouchSteamPoller.HandleCount} controller(s)"
            : $"Input: Godot joypads ({Input.GetConnectedJoypads().Count}); Steam: {CouchSteamPoller.DescribeHandles()}");
        text.AppendLine($"Driver (local player): {LocalContext.NetId?.ToString() ?? "none"}");

        RunState? runState = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        foreach (CouchSeat seat in CouchSeats.All)
        {
            string marker = LocalContext.NetId == seat.PlayerId ? ">" : " ";
            Player? player = runState?.GetPlayer(seat.PlayerId);
            string character = player?.Character.Title.GetFormattedText() ?? "?";
            string controller = seat.Device == null ? "unbound" : $"{seat.Device}{(seat.DeviceConnected ? "" : " (disconnected)")}";
            text.AppendLine($"{marker} {seat.Label}  {character,-12} player {seat.PlayerId}  {controller}");
        }

        foreach (CouchTeammateChoice choice in CouchTeammateChoices.Pending)
        {
            text.AppendLine($"! Teammate {choice.Player.NetId} must choose: {choice.Describe()}  [F5 = first option(s)]");
        }

        string[] history = CouchLog.History.ToArray();
        if (history.Length > 0)
        {
            text.AppendLine("Recent:");
            foreach (string entry in history)
            {
                text.AppendLine($"  {entry}");
            }
        }

        return text.ToString().TrimEnd();
    }
}

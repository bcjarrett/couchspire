using System;
using System.IO;
using Godot;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Screenshots of what the game draws, for UI work: F11 saves one; with <c>shots = 1</c> one is also saved shortly after
/// each teammate screen or HUD mode opens. They go to <c>couch_shots/</c> in the game's user folder (next to the logs).
/// Taken from the game's own renderer, so no OS screen-recording permission is needed.
/// </summary>
internal static class CouchScreenshots
{
    /// <summary>How long a screen must stay up before the automatic shot (lets panels finish sliding in).</summary>
    private const double SettleSeconds = 0.8;

    private static bool _pending;

    private static string _key = "";

    private static double _keySince;

    private static string _shotKey = "";

    public static string Folder => ProjectSettings.GlobalizePath("user://couch_shots");

    /// <summary>Called every frame; takes the automatic shot once a new teammate screen has settled.</summary>
    public static void Tick()
    {
        if (!CouchConfig.ShotsEnabled)
        {
            return;
        }

        double now = Time.GetTicksMsec() / 1000.0;
        string key = CurrentScreen();
        if (key != _key)
        {
            _key = key;
            _keySince = now;
            return;
        }

        if (key.Length > 0 && key != _shotKey && now - _keySince >= SettleSeconds)
        {
            _shotKey = key;
            Take(key);
        }
        else if (key.Length == 0)
        {
            _shotKey = "";
        }
    }

    /// <summary>Saves what's on screen at the end of this frame.</summary>
    public static void Take(string tag)
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        void Capture()
        {
            RenderingServer.FramePostDraw -= Capture;
            _pending = false;
            Save(tag);
        }

        RenderingServer.FramePostDraw += Capture;
    }

    private static void Save(string tag)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                return;
            }

            Image image = tree.Root.GetTexture().GetImage();
            if (image.GetWidth() > 1920)
            {
                image.Resize(1920, image.GetHeight() * 1920 / image.GetWidth(), Image.Interpolation.Bilinear);
            }

            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{tag}.jpg");
            Error error = image.SaveJpg(path, 0.85f);
            CouchLog.Info(error == Error.Ok ? $"Screenshot saved: {path}" : $"Screenshot failed ({error}): {path}");
        }
        catch (Exception ex)
        {
            CouchLog.Warn($"Screenshot failed: {ex.Message}");
        }
    }

    /// <summary>Which teammate screen is up, as a short tag ("" when none).</summary>
    private static string CurrentScreen()
    {
        return CouchTeammateInfo.IsActive ? "info"
            : CouchTeammateChoicePanel.IsActive ? "picker"
            : CouchTeammateRewards.IsActive ? (CouchTeammateRewards.IsChoosingCard ? "rewards-cards" : "rewards")
            : CouchTeammateEvent.IsActive ? "event"
            : CouchTeammateRestSite.IsActive ? "rest"
            : CouchTeammateTreasure.IsActive ? "treasure"
            : CouchTeammateShop.IsActive ? "shop"
            : CouchTeammateHud.ModeName is string mode ? $"combat-{mode.ToLowerInvariant()}"
            : "";
    }
}

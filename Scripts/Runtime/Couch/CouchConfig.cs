using System;
using System.Collections.Generic;
using System.IO;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Couch co-op switches, read once at startup from <c>CouchSpire.cfg</c> next to the mod DLL (<c>key = value</c> lines,
/// <c>#</c> comments). Environment variables override the file (<c>COUCHSPIRE_PROBE=1 %command%</c> in Steam launch
/// options works on Linux; Steam for Mac doesn't run launch options through a shell, so use the file there).
/// </summary>
internal static class CouchConfig
{
    public const string FileName = "CouchSpire.cfg";

    private static readonly (Dictionary<string, string> Settings, string Source) LoadedFile = LoadFile();

    /// <summary>Where the settings came from, for the startup log.</summary>
    public static readonly string Source = LoadedFile.Source;

    /// <summary>
    /// Per-controller routing: each controller drives its own character. On by default;
    /// <c>routing = 0</c> restores the fork's single-controller behavior.
    /// </summary>
    public static readonly bool RoutingEnabled = Flag("routing", "COUCHSPIRE_ROUTING", defaultValue: true);

    /// <summary>
    /// Verbose input diagnostics: device inventory, input map, and every controller event with its routing decision.
    /// </summary>
    public static readonly bool ProbeEnabled = Flag("probe", "COUCHSPIRE_PROBE", defaultValue: false);

    /// <summary>
    /// Simultaneous combat: the driver keeps the main screen for the whole combat and the other characters act as
    /// remote teammates (their plays and choices go through the loopback network instead of taking over the screen).
    /// <c>simultaneous = 0</c> restores the fork's hotseat behavior (auto-switch after end turn, choices on the main screen).
    /// </summary>
    public static readonly bool SimultaneousEnabled = Flag("simultaneous", "COUCHSPIRE_SIMULTANEOUS", defaultValue: true);

    /// <summary>
    /// Show the seat/driver debug overlay at startup. F10 toggles it at any time.
    /// </summary>
    public static readonly bool OverlayAtStart = ProbeEnabled || Flag("overlay", "COUCHSPIRE_OVERLAY", defaultValue: false);

    /// <summary>
    /// Which side the teammate's event panel sits on. Left by default, since the event screen puts the driver's text and
    /// options on the right.
    /// </summary>
    public static readonly bool EventPanelOnLeft = Flag("event_panel_left", "COUCHSPIRE_EVENT_PANEL_LEFT", defaultValue: true);

    /// <summary>Teammate HUD position (top-left of its header, in the game's UI coordinates) and card scale.</summary>
    public static readonly float HudX = Number("hud_x", 300f);

    /// <summary>Saves a screenshot each time a teammate screen or HUD mode opens (<see cref="CouchScreenshots"/>).</summary>
    public static readonly bool ShotsEnabled = Flag("shots", "COUCHSPIRE_SHOTS", defaultValue: false);

    /// <summary>Negative (the default) lines the HUD up with the players list on the left; see <see cref="CouchTeammateHud.BandTop"/>.</summary>
    public static readonly float HudY = Number("hud_y", -1f);

    public static readonly float HudScale = Number("hud_scale", 0.45f);

    private static float Number(string key, float defaultValue)
    {
        return LoadedFile.Settings.TryGetValue(key, out string? value)
            && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : defaultValue;
    }

    private static bool Flag(string key, string environmentVariable, bool defaultValue)
    {
        if (TryParse(Environment.GetEnvironmentVariable(environmentVariable), out bool fromEnvironment))
        {
            return fromEnvironment;
        }

        if (LoadedFile.Settings.TryGetValue(key, out string? fromFile) && TryParse(fromFile, out bool parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool TryParse(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static (Dictionary<string, string> Settings, string Source) LoadFile()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        string? modDirectory = Path.GetDirectoryName(typeof(CouchConfig).Assembly.Location);
        if (string.IsNullOrEmpty(modDirectory))
        {
            return (settings, "defaults (mod folder unknown)");
        }

        string path = Path.Combine(modDirectory, FileName);
        if (!File.Exists(path))
        {
            return (settings, $"defaults (no {path})");
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Split('#')[0].Trim();
                int separator = line.IndexOf('=');
                if (separator > 0)
                {
                    settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            return (settings, $"{path} ({settings.Count} setting(s))");
        }
        catch (IOException ex)
        {
            return (settings, $"defaults (couldn't read {path}: {ex.Message})");
        }
    }
}

using Godot;

namespace LocalMultiControl.Scripts.Runtime.Couch;

internal enum CouchDeviceSource
{
    /// <summary>A joypad as Godot sees it (<see cref="InputEvent.Device"/>).</summary>
    GodotPad,

    /// <summary>A controller as the Steam Input API sees it (<c>InputHandle_t</c>).</summary>
    SteamHandle
}

/// <summary>
/// Identity of one physical controller, independent of which input path reported it.
/// </summary>
internal readonly record struct CouchDeviceKey(CouchDeviceSource Source, ulong Id)
{
    public static CouchDeviceKey Pad(int device)
    {
        return new CouchDeviceKey(CouchDeviceSource.GodotPad, (ulong)device);
    }

    public static CouchDeviceKey Steam(ulong handle)
    {
        return new CouchDeviceKey(CouchDeviceSource.SteamHandle, handle);
    }

    public override string ToString()
    {
        return Source == CouchDeviceSource.GodotPad ? $"pad{Id}" : $"steam:{Id:X}";
    }
}

internal enum CouchInputKind
{
    /// <summary>A button press. The only kind that can claim control for a waiting seat.</summary>
    Press,

    /// <summary>A navigation press (d-pad or stick direction). Never claims, so a stray nudge can't steal control.</summary>
    NavPress,

    Release,

    /// <summary>Continuous analog input. Never claims.</summary>
    Motion
}

/// <summary>
/// Device ids stamped on input events that couch code synthesizes after routing them at the source.
/// Any event whose <see cref="InputEvent.Device"/> is at or above <see cref="RoutedBase"/> has already been
/// routed, so the input gate lets it through untouched. Real joypads use small ids (0-15).
/// </summary>
internal static class CouchEventDevice
{
    public const int RoutedBase = 1000;

    /// <summary>Events synthesized from a Steam Input handle: base + per-session slot.</summary>
    public const int SteamBase = 1000;

    /// <summary>Events synthesized from a Godot joypad's analog axes: base + Godot device id.</summary>
    public const int GodotAnalogBase = 2000;

    public static bool IsRouted(int device)
    {
        return device >= RoutedBase;
    }
}

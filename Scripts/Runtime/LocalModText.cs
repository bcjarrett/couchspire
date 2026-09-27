namespace LocalMultiControl.Scripts.Runtime;

/// <summary>Player-facing strings. English only.</summary>
internal static class LocalModText
{
    private const string UnknownSlotLabel = "?";

    public static string RoleSlot(string slotLabel)
    {
        return slotLabel == UnknownSlotLabel ? "Unknown Player" : $"Player {slotLabel}";
    }

    public static string RestartRoomButton => "Restart Room";
    public static string RestartRoomFailed => "Restart failed, please continue manually";

    public static string LocalSelfCoopCardTitle => "Couch Co-op";
    public static string LocalSelfCoopCardDescription => "Two players on one screen.\nEach controller drives its own character.";

    public static string EnteredLocalSelfCoopHint => "Couch co-op: press a button on each controller to pick a character";

    public static string LobbyEditingSlot(string slotLabel)
    {
        return $"P{slotLabel} is choosing";
    }

    public static string ControlledSlot(string slotLabel)
    {
        return $"Controlled Character: {RoleSlot(slotLabel)}";
    }

    public static string RandomCharacterNotSupported => "Random character is not supported in couch co-op";

    public static string RestSiteAllChosen => "Rest site complete: all selectable players finished";

    public static string RestSiteFocusHint => "Rest site tip: if options are missing, press Tab to switch once";
}

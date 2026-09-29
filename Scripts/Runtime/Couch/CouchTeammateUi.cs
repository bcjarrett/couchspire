namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Sends teammate commands to whichever teammate UI is up: the card picker, the rewards panel, the event, rest site,
/// treasure and shop panels, or the combat HUD.
/// </summary>
internal static class CouchTeammateUi
{
    public static bool IsActive =>
        CouchTeammateInfo.IsActive
        || CouchTeammateChoicePanel.IsActive
        || CouchTeammateRewards.IsActive
        || CouchTeammateEvent.IsActive
        || CouchTeammateRestSite.IsActive
        || CouchTeammateTreasure.IsActive
        || CouchTeammateShop.IsActive
        || CouchTeammateHud.IsActive;

    /// <summary>
    /// The deck/relics view comes first while open (it's modal); then the card picker, which the teammate's other
    /// screens are waiting on. The panels get the usual menu sounds here; the combat HUD plays its own.
    /// </summary>
    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        bool handledByPanel = CouchTeammateInfo.Handle(playerId, command)
            || CouchTeammateChoicePanel.Handle(playerId, command)
            || CouchTeammateRewards.Handle(playerId, command)
            || CouchTeammateEvent.Handle(playerId, command)
            || CouchTeammateRestSite.Handle(playerId, command)
            || CouchTeammateTreasure.Handle(playerId, command)
            || CouchTeammateShop.Handle(playerId, command);
        if (handledByPanel)
        {
            PanelSound(command);
            return true;
        }

        return CouchTeammateHud.Handle(playerId, command);
    }

    private static void PanelSound(CouchHudCommand command)
    {
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Right:
            case CouchHudCommand.Up:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                CouchSfx.Move();
                break;
            case CouchHudCommand.Back:
            case CouchHudCommand.Info:
                CouchSfx.Back();
                break;
            default:
                CouchSfx.Accept();
                break;
        }
    }
}

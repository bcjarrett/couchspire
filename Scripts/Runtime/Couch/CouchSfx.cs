using System;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Sounds for the teammate's own UI and for what happens to the teammate. The game plays most feedback sounds only for
/// the local player (gold, relics, the card landing in the discard pile) or from the mouse/focus handling of its own
/// buttons, so the teammate's panels would otherwise be silent.
/// </summary>
internal static class CouchSfx
{
    private static bool _warned;

    /// <summary>Cursor moved.</summary>
    public static void Move() => Play("event:/sfx/ui/clicks/ui_hover");

    /// <summary>Something picked or confirmed.</summary>
    public static void Accept() => Play("event:/sfx/ui/clicks/ui_click");

    /// <summary>Backed out or closed.</summary>
    public static void Back() => Play("event:/sfx/ui/clicks/ui_back");

    /// <summary>A multi-select option toggled.</summary>
    public static void Toggle(bool on) => Play(on ? "event:/sfx/ui/clicks/ui_checkbox_on" : "event:/sfx/ui/clicks/ui_checkbox_off");

    /// <summary>Not allowed (can't play, not enough gold, ...).</summary>
    public static void Deny() => PlayTemp("deny.mp3", 0.5f);

    /// <summary>A card picked up to play (the game's "select card" sound).</summary>
    public static void CardSelect() => PlayTemp("card_select.mp3", 0.5f);

    /// <summary>A played card landing in the discard pile.</summary>
    public static void CardToDiscard() => Play("event:/sfx/ui/cards/card_movement_B_play_into_discard");

    public static void Gold(int amount) => Play(amount >= 100 ? "event:/sfx/ui/gold/gold_3" : amount > 30 ? "event:/sfx/ui/gold/gold_2" : "event:/sfx/ui/gold/gold_1");

    public static void RelicGained() => PlayTemp("relic_get.mp3");

    public static void MerchantThanks() => Play("event:/sfx/npcs/merchant/merchant_thank_yous");

    public static void MerchantNo() => Play("event:/sfx/npcs/merchant/merchant_dissapointment");

    private static void Play(string sfx)
    {
        try
        {
            SfxCmd.Play(sfx);
        }
        catch (Exception ex)
        {
            WarnOnce(sfx, ex);
        }
    }

    /// <summary>Sounds the game still plays from plain audio files (its "temporary" sounds).</summary>
    private static void PlayTemp(string file, float volume = 1f)
    {
        try
        {
            NDebugAudioManager.Instance?.Play(file, volume, PitchVariance.Small);
        }
        catch (Exception ex)
        {
            WarnOnce(file, ex);
        }
    }

    private static void WarnOnce(string sound, Exception ex)
    {
        if (!_warned)
        {
            _warned = true;
            CouchLog.Warn($"Couldn't play {sound}: {ex.Message}");
        }
    }
}

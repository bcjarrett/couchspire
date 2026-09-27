using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace LocalMultiControl.Scripts.Runtime;

/// <summary>
/// Session-level state for a two-player local co-op run: the two local player ids, the loopback net service, and the
/// character select lobby while it is being set up.
/// </summary>
internal static class LocalSelfCoopContext
{
    /// <summary>Couch co-op is always exactly two local players.</summary>
    public const int PlayerCount = 2;

    private const int MaxLocalAscensionLevel = 10;

    private static readonly List<ulong> _localPlayerIds = new() { 1, 2 };

    private static bool _isSyncingCharacterHighlight;
    private static ulong? _pendingEventAutoSwitchPlayerId;
    private static bool _eventAutoSwitchPending;

    public static bool UseSingleAdventureMode => true;

    public static bool UseSingleEventFlow => false;

    public static IReadOnlyList<ulong> LocalPlayerIds => _localPlayerIds;

    public static ulong PrimaryPlayerId => _localPlayerIds[0];

    public static bool IsEnabled { get; private set; }

    public static LocalLoopbackHostGameService? NetService { get; private set; }

    public static ulong CurrentLobbyEditingPlayerId { get; private set; } = 1;

    public static NCharacterSelectScreen? ActiveCharacterSelectScreen { get; set; }

    /// <summary>Uses the platform (Steam) id for P1 and the next id for P2.</summary>
    public static ulong ResolvePrimaryPlayerId()
    {
        ulong primary = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        if (primary == 0)
        {
            primary = 1;
        }

        ulong secondary = primary == ulong.MaxValue ? 1UL : primary + 1UL;
        ApplyLocalPlayerIds(new List<ulong> { primary, secondary });
        LocalMultiControlLogger.Info($"Local player ids: {string.Join(",", _localPlayerIds)}");
        return PrimaryPlayerId;
    }

    /// <summary>Restores the ids of a saved run. False (ids unchanged) unless there are exactly two.</summary>
    public static bool UseSavedPlayerIds(IReadOnlyList<ulong> playerIds)
    {
        List<ulong> ids = playerIds.Where((id) => id != 0).Distinct().ToList();
        if (ids.Count != PlayerCount)
        {
            LocalMultiControlLogger.Warn($"Ignoring saved player ids [{string.Join(",", playerIds)}]: need exactly {PlayerCount}.");
            return false;
        }

        ApplyLocalPlayerIds(ids);
        CurrentLobbyEditingPlayerId = PrimaryPlayerId;
        LocalMultiControlLogger.Info($"Local player ids restored from save: {string.Join(",", _localPlayerIds)}");
        return true;
    }

    public static bool IsSaveOwnedByLocalSelfCoop(SerializableRun run)
    {
        return run.Players.Count == PlayerCount
            && _localPlayerIds.All((playerId) => run.Players.Any((player) => player.NetId == playerId));
    }

    public static void Enable(LocalLoopbackHostGameService netService)
    {
        IsEnabled = true;
        NetService = netService;
        CurrentLobbyEditingPlayerId = PrimaryPlayerId;
        ActiveCharacterSelectScreen = null;
        netService.SetCurrentSenderId(CurrentLobbyEditingPlayerId);
        LocalContext.NetId = CurrentLobbyEditingPlayerId;
        LocalMultiControlLogger.Info("Local co-op enabled.");
    }

    public static void Disable(string reason)
    {
        if (!IsEnabled && NetService == null)
        {
            return;
        }

        IsEnabled = false;
        NetService = null;
        CurrentLobbyEditingPlayerId = PrimaryPlayerId;
        ActiveCharacterSelectScreen = null;
        _pendingEventAutoSwitchPlayerId = null;
        _eventAutoSwitchPending = false;
        LocalMultiControlLogger.Info($"Local co-op disabled: {reason}");
    }

    /// <summary>On character select, hands the lobby to the other player (Tab).</summary>
    public static bool SwitchLobbyEditingPlayer()
    {
        ulong other = CurrentLobbyEditingPlayerId == _localPlayerIds[0] ? _localPlayerIds[1] : _localPlayerIds[0];
        return SetLobbyEditingPlayer(other, "switch-lobby-editing-player");
    }

    public static bool SetLobbyEditingPlayer(ulong playerId, string source)
    {
        if (!IsEnabled || NetService == null || !GetActiveLobbyLocalPlayerIds().Contains(playerId))
        {
            return false;
        }

        ulong previousPlayerId = CurrentLobbyEditingPlayerId;
        CurrentLobbyEditingPlayerId = playerId;
        EnsureLobbySenderContext(source);
        SyncCharacterSelectHighlight();

        if (previousPlayerId != CurrentLobbyEditingPlayerId)
        {
            string slotLabel = GetSlotLabel(CurrentLobbyEditingPlayerId);
            LocalMultiControlLogger.Info($"Lobby editing player: {previousPlayerId} -> {CurrentLobbyEditingPlayerId} (P{slotLabel}, source={source})");
            NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create(LocalModText.LobbyEditingSlot(slotLabel)));
        }

        return true;
    }

    public static bool TryGetSlotIndex(ulong playerId, out int slotIndex)
    {
        slotIndex = _localPlayerIds.IndexOf(playerId);
        return slotIndex >= 0;
    }

    public static string GetSlotLabel(ulong playerId)
    {
        return TryGetSlotIndex(playerId, out int slotIndex) ? (slotIndex + 1).ToString() : "?";
    }

    public static bool EnsureLobbySenderContext(string source)
    {
        if (!IsEnabled || NetService == null)
        {
            return false;
        }

        EnsureLobbyEditingPlayerIsValid();
        NetService.SetCurrentSenderId(CurrentLobbyEditingPlayerId);
        LocalContext.NetId = CurrentLobbyEditingPlayerId;
        LocalMultiControlLogger.Info($"Lobby sender: player={CurrentLobbyEditingPlayerId}, source={source}");
        return true;
    }

    public static void NotifyCharacterSelectPlayerChanged(ulong playerId)
    {
        if (IsEnabled && playerId == CurrentLobbyEditingPlayerId)
        {
            SyncCharacterSelectHighlight();
        }
    }

    public static void RequestEventAutoSwitchAfterChoice(ulong playerId)
    {
        if (!IsEnabled)
        {
            return;
        }

        _pendingEventAutoSwitchPlayerId = playerId;
        LocalMultiControlLogger.Info($"Event auto-switch requested: player={playerId}");
    }

    public static bool ShouldQueueEventAutoSwitchAfterEventState(EventModel eventModel)
    {
        if (!IsEnabled || !_pendingEventAutoSwitchPlayerId.HasValue || eventModel.Owner == null)
        {
            return false;
        }

        if (!eventModel.IsFinished || eventModel.Owner.NetId != _pendingEventAutoSwitchPlayerId.Value)
        {
            return false;
        }

        _pendingEventAutoSwitchPlayerId = null;
        _eventAutoSwitchPending = true;
        return true;
    }

    public static bool TryConsumePendingEventAutoSwitch()
    {
        if (!_eventAutoSwitchPending)
        {
            return false;
        }

        _eventAutoSwitchPending = false;
        return true;
    }

    /// <summary>Adds P2 to the host's character select lobby and readies them.</summary>
    public static bool BootstrapLocalPlayers(NCharacterSelectScreen characterSelectScreen)
    {
        ActiveCharacterSelectScreen = characterSelectScreen;
        if (!IsEnabled || NetService == null)
        {
            return false;
        }

        StartRunLobby? lobby = GetLobby(characterSelectScreen);
        if (lobby == null)
        {
            LocalMultiControlLogger.Warn("Lobby setup skipped: the lobby isn't initialized yet.");
            return false;
        }

        SerializableUnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress().ToSerializable();
        foreach (ulong playerId in _localPlayerIds)
        {
            if (lobby.Players.Any((player) => player.id == playerId))
            {
                continue;
            }

            NetService.SetCurrentSenderId(playerId);
            _ = lobby.AddLocalHostPlayerInternal(unlockState, MaxLocalAscensionLevel);
        }

        int index = lobby.Players.FindIndex((player) => player.id == _localPlayerIds[1]);
        if (index >= 0 && !lobby.Players[index].isReady)
        {
            StartRunLobbyPlayer lobbyPlayer = lobby.Players[index];
            lobbyPlayer.isReady = true;
            lobby.Players[index] = lobbyPlayer;
            characterSelectScreen.PlayerChanged(lobbyPlayer, false);
        }

        EnsureLobbySenderContext("bootstrap-local-players");
        SyncCharacterSelectHighlight();
        EnsureLobbyAscensionCapacity(lobby, "bootstrap-local-players");
        LocalMultiControlLogger.Info($"Lobby set up with {lobby.Players.Count} players.");
        return true;
    }

    public static void EnsureLocalAscensionOptionsUnlocked(NCharacterSelectScreen screen, string source)
    {
        if (IsEnabled && GetLobby(screen) is { } lobby)
        {
            EnsureLobbyAscensionCapacity(lobby, source);
        }
    }

    private static StartRunLobby? GetLobby(NCharacterSelectScreen screen)
    {
        return AccessTools.Field(typeof(NCharacterSelectScreen), "_lobby")?.GetValue(screen) as StartRunLobby;
    }

    /// <summary>Opens every ascension level: the lobby otherwise caps it at the multiplayer unlock progress.</summary>
    private static void EnsureLobbyAscensionCapacity(StartRunLobby lobby, string source)
    {
        bool changed = false;
        for (int i = 0; i < lobby.Players.Count; i++)
        {
            StartRunLobbyPlayer player = lobby.Players[i];
            if (player.maxMultiplayerAscensionUnlocked >= MaxLocalAscensionLevel)
            {
                continue;
            }

            player.maxMultiplayerAscensionUnlocked = MaxLocalAscensionLevel;
            lobby.Players[i] = player;
            lobby.LobbyListener.PlayerChanged(player, false);
            changed = true;
        }

        if (lobby.MaxAscension < MaxLocalAscensionLevel)
        {
            AccessTools.Field(typeof(StartRunLobby), "<MaxAscension>k__BackingField")?.SetValue(lobby, MaxLocalAscensionLevel);
            lobby.LobbyListener.MaxAscensionChanged();
            changed = true;
        }

        int clampedAscension = Math.Clamp(lobby.Ascension, 0, MaxLocalAscensionLevel);
        if (lobby.Ascension != clampedAscension)
        {
            lobby.SyncAscensionChange(clampedAscension);
            changed = true;
        }

        if (changed)
        {
            LocalMultiControlLogger.Info($"Ascension 0-{MaxLocalAscensionLevel} unlocked for the lobby (source={source}).");
        }
    }

    private static void EnsureLobbyEditingPlayerIsValid()
    {
        List<ulong> activePlayerIds = GetActiveLobbyLocalPlayerIds();
        if (!activePlayerIds.Contains(CurrentLobbyEditingPlayerId))
        {
            CurrentLobbyEditingPlayerId = activePlayerIds.Count > 0 ? activePlayerIds[0] : PrimaryPlayerId;
        }
    }

    private static List<ulong> GetActiveLobbyLocalPlayerIds()
    {
        if (ActiveCharacterSelectScreen == null
            || !GodotObject.IsInstanceValid(ActiveCharacterSelectScreen)
            || GetLobby(ActiveCharacterSelectScreen) is not { } lobby)
        {
            return _localPlayerIds.ToList();
        }

        return _localPlayerIds.Where((id) => lobby.Players.Any((player) => player.id == id)).ToList();
    }

    /// <summary>
    /// Points the character select buttons at the editing player: their pick shows as selected, the other player's
    /// pick shows as a remote selection.
    /// </summary>
    private static void SyncCharacterSelectHighlight()
    {
        if (ActiveCharacterSelectScreen == null || _isSyncingCharacterHighlight)
        {
            return;
        }

        try
        {
            _isSyncingCharacterHighlight = true;

            EnsureLobbyEditingPlayerIsValid();
            StartRunLobby? lobby = GetLobby(ActiveCharacterSelectScreen);
            int localPlayerIndex = lobby?.Players.FindIndex((player) => player.id == CurrentLobbyEditingPlayerId) ?? -1;
            if (lobby == null || localPlayerIndex < 0)
            {
                return;
            }

            StartRunLobbyPlayer localPlayer = lobby.Players[localPlayerIndex];
            if (AccessTools.Field(typeof(NCharacterSelectScreen), "_charButtonContainer")?.GetValue(ActiveCharacterSelectScreen)
                is not Control charButtonContainer)
            {
                return;
            }

            List<NCharacterSelectButton> buttons = charButtonContainer.GetChildren().OfType<NCharacterSelectButton>().ToList();
            NCharacterSelectButton? selectedButton = null;
            foreach (NCharacterSelectButton button in buttons)
            {
                foreach (StartRunLobbyPlayer player in lobby.Players)
                {
                    button.OnRemotePlayerDeselected(player.id);
                }

                bool isSelected = button.Character == localPlayer.character;
                AccessTools.Field(typeof(NCharacterSelectButton), "_isSelected")?.SetValue(button, isSelected);
                if (isSelected)
                {
                    selectedButton = button;
                }
            }

            foreach (StartRunLobbyPlayer player in lobby.Players.Where((player) => player.id != localPlayer.id))
            {
                buttons.FirstOrDefault((button) => button.Character == player.character)?.OnRemotePlayerSelected(player.id);
            }

            foreach (NCharacterSelectButton button in buttons)
            {
                AccessTools.Method(typeof(NCharacterSelectButton), "RefreshState")?.Invoke(button, Array.Empty<object>());
            }

            AccessTools.Field(typeof(NCharacterSelectScreen), "_selectedButton")?.SetValue(ActiveCharacterSelectScreen, selectedButton);
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Character select highlight sync failed: {exception.Message}");
        }
        finally
        {
            _isSyncingCharacterHighlight = false;
        }
    }

    private static void ApplyLocalPlayerIds(IReadOnlyList<ulong> playerIds)
    {
        _localPlayerIds.Clear();
        _localPlayerIds.AddRange(playerIds.Take(PlayerCount));
    }
}

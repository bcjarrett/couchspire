using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CouchSpire.Scripts.Runtime;

internal static class LocalQuickRestartLoader
{
    public static async Task<bool> TryLoadDirectlyAsync()
    {
        try
        {
            if (!LocalSelfCoopSaveTag.TryReadCurrentProfile(out List<ulong> playerIds) || playerIds.Count < 2)
            {
                ModLog.Warn("Quick restart failed: no valid local co-op player marker found.");
                return false;
            }

            ulong primaryPlayerId = playerIds[0];
            LocalSelfCoopContext.UseSavedPlayerIds(playerIds);

            ReadSaveResult<SerializableRun> readSaveResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(primaryPlayerId);
            if (!readSaveResult.Success || readSaveResult.SaveData == null)
            {
                ModLog.Warn("Quick restart failed: failed to read multiplayer save.");
                return false;
            }

            SerializableRun saveData = readSaveResult.SaveData;
            if (!LocalSelfCoopContext.IsSaveOwnedByLocalSelfCoop(saveData))
            {
                LocalSelfCoopSaveTag.ClearCurrentProfile();
                ModLog.Warn("Quick restart failed: save players do not match the local co-op marker.");
                return false;
            }

            LocalLoopbackHostGameService netService = new LocalLoopbackHostGameService(primaryPlayerId);
            LocalSelfCoopContext.Enable(netService);

            LoadRunLobby lobby = new LoadRunLobby(netService, NoopLoadRunLobbyListener.Instance, saveData);
            lobby.AddLocalHostPlayer();

            // The game's load screen stops the main menu music when the run begins (NMultiplayerLoadGameScreen.BeginRun);
            // this direct load skips that screen, so stop it here or it keeps playing under the run's music.
            NAudioManager.Instance?.StopMusic();

            NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is null.");
            game.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, lobby.PlayerIds);
            game.ReactionContainer.InitializeNetworking(netService);

            SerializablePlayer localPlayer = saveData.Players.First((player) => player.NetId == primaryPlayerId);
            if (localPlayer.CharacterId != null)
            {
                CharacterModel transitionCharacter = ModelDb.GetById<CharacterModel>(localPlayer.CharacterId);
                SfxCmd.Play(transitionCharacter.CharacterTransitionSfx);
                await game.Transition.FadeOut(0.8f, transitionCharacter.CharacterSelectTransitionPath);
            }
            else
            {
                await game.Transition.FadeOut();
            }

            RunState runState = RunState.FromSerializable(saveData);
            await RunManager.Instance.SetUpSavedMultiplayer(runState, lobby);
            await game.LoadRun(runState, saveData.PreFinishedRoom);
            lobby.CleanUp(disconnectSession: false);
            await game.Transition.FadeIn();
            ModLog.Info("ESC quick restart directly completed loading the save and entered the game.");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Error($"ESC quick restart direct load failed: {exception}");
            return false;
        }
    }

    private sealed class NoopLoadRunLobbyListener : ILoadRunLobbyListener
    {
        internal static readonly NoopLoadRunLobbyListener Instance = new NoopLoadRunLobbyListener();

        public void PlayerConnected(LoadRunLobbyPlayer player)
        {
        }

        public void RemotePlayerDisconnected(ulong playerId)
        {
        }

        public Task<bool> ShouldAllowRunToBegin()
        {
            return Task.FromResult(true);
        }

        public void BeginRun()
        {
        }

        public void PlayerReadyChanged(ulong playerId)
        {
        }

        public void LocalPlayerDisconnected(NetErrorInfo info)
        {
        }
    }
}

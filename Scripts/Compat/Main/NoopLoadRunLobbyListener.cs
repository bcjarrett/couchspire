using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace CouchSpire.Scripts.Compat;

/// <summary>Lobby listener for the ESC quick-restart direct load, which never shows a lobby screen.</summary>
internal sealed class NoopLoadRunLobbyListener : ILoadRunLobbyListener
{
    internal static readonly NoopLoadRunLobbyListener Instance = new NoopLoadRunLobbyListener();

    public void PlayerConnected(ulong playerId)
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

using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace CouchSpire.Scripts.Runtime;

/// <summary>Members the beta branch added to <see cref="INetHostGameService"/>.</summary>
internal sealed partial class LocalLoopbackHostGameService
{
    // Every loopback peer is this same game instance, so the local version info is the correct answer for all of
    // them. The game's lobbies dereference GetVersionInfoForPeer(...).Value unguarded, so never return null.
    public PeerVersionInfo LocalVersion => PeerVersionInfo.LocalDefault();

    // Handshake failures cannot happen on the loopback; never raised.
#pragma warning disable CS0067
    public event Action<ulong, NetErrorInfo>? ClientConnectionFailed;
#pragma warning restore CS0067

    public PeerVersionInfo? GetVersionInfoForPeer(ulong peerId)
    {
        return LocalVersion;
    }
}

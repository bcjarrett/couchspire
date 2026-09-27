#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace LocalMultiControl.Scripts.Testing;

internal sealed partial class CouchTestContext
{
    /// <summary>
    /// Enters a room with the seeded <c>room</c> console command and settles. Use this instead of calling
    /// <c>Console(P1, "room …")</c> directly.
    /// </summary>
    public async Task EnterRoom(RoomType roomType)
    {
        ThrowIfCancelled();
        await Console(P1, $"room {roomType}");
        await Settle();

        // RunManager.EnterRoomDebug records the room in map history with model?.Id, which is null for "room <type>"
        // because the encounter is picked afterwards. Normal play records the encounter id, and abandoning a run with
        // a null id throws in ProgressSaveManager.IncrementEncounterLoss, which breaks the runner's abandon. Copy in
        // the id the room actually resolved to, so the history matches what a real run records.
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        MapPointRoomHistoryEntry? entry = runState?.CurrentMapPointHistoryEntry?.Rooms.LastOrDefault();
        if (entry != null && entry.ModelId == null && runState?.CurrentRoom?.ModelId is ModelId roomModelId)
        {
            entry.ModelId = roomModelId;
            CouchTestLog.Info($"EnterRoom({roomType}): filled the map-history model id the debug command left empty ({roomModelId}).");
        }
    }
}
#endif

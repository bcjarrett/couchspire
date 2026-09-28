#if COUCHSPIRE_TESTS
using System.Threading;
using Godot;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// Raised by <see cref="CouchTestContext.Expect"/> (and a few internal checks) to abort the current scenario.
/// <see cref="CouchTestRunner"/> catches this per scenario; its message becomes the scenario result's message.
/// </summary>
internal sealed class CouchTestExpectationFailedException : System.Exception
{
    public CouchTestExpectationFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The small API a scenario drives the game through (docs/design/testing-plan.md §6.4). Deliberately minimal: only
/// what every scenario needs. Area-specific helpers (e.g. combat's P2Play/P2EndTurn wrapping
/// <see cref="CouchRemotePlay"/>) go in their own partial file, <c>CouchTestContext.&lt;Area&gt;.cs</c>, and must
/// call <see cref="ThrowIfCancelled"/> before touching the game.
/// </summary>
internal sealed partial class CouchTestContext
{
    private static readonly string[] BannedConsoleCommands = { "fight", "godmode" };
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromSeconds(20);

    private readonly SceneTree _tree;
    private readonly CancellationToken _cancellationToken;
    private readonly CouchTestCheckpointOptions _layoutOptions;
    private readonly List<CouchTestLayoutSnapshot> _layoutSnapshots = new();

    /// <param name="cancellationToken">
    /// Cancelled by <see cref="CouchTestRunner"/> the moment a scenario's timeout fires, before it abandons the run.
    /// Every method below checks it first (and <see cref="Settle"/>/<see cref="WaitUntil"/> keep checking it while
    /// they poll), so a scenario that outlives its timeout stops touching the game instead of running on as a
    /// "zombie" that could press buttons or run console commands inside the *next* scenario's run.
    /// </param>
    /// <param name="layoutOptions">Scenario/aspect/output-dir context <see cref="Checkpoint"/> needs for the layout
    /// checks and snapshot compare/bless (docs/design/testing-plan.md §6.5, WP4).</param>
    public CouchTestContext(SceneTree tree, CancellationToken cancellationToken, CouchTestCheckpointOptions layoutOptions)
    {
        _tree = tree;
        _cancellationToken = cancellationToken;
        _layoutOptions = layoutOptions;
    }

    /// <summary>Every checkpoint's layout snapshot so far this run, in order. Read by <see cref="CouchTestRunner"/>
    /// after the scenario finishes to feed <c>--repeat</c>'s cross-run compare.</summary>
    public IReadOnlyList<CouchTestLayoutSnapshot> LayoutSnapshots => _layoutSnapshots;

    /// <summary>Cancelled when the scenario times out; pass it to anything that waits.</summary>
    public CancellationToken CancellationToken => _cancellationToken;

    /// <summary>Call first in every helper that touches the game (see the constructor's cancellation note).</summary>
    public void ThrowIfCancelled() => _cancellationToken.ThrowIfCancellationRequested();

    /// <summary>The driver's id (platform id; 1 under <c>--force-steam off</c>, see docs/design/testing-plan.md §4).</summary>
    public ulong P1Id => LocalSelfCoopContext.LocalPlayerIds[0];

    /// <summary>The teammate's id (P1Id + 1 unless P1Id wrapped, see <see cref="LocalSelfCoopContext.ResolvePrimaryPlayerId"/>).</summary>
    public ulong P2Id => LocalSelfCoopContext.LocalPlayerIds[1];

    /// <summary>P1's live <see cref="Player"/> handle in the current run. Re-resolved on every access.</summary>
    public Player P1 => ResolvePlayer(P1Id);

    /// <summary>P2's live <see cref="Player"/> handle in the current run. Re-resolved on every access.</summary>
    public Player P2 => ResolvePlayer(P2Id);

    /// <summary>
    /// Runs a dev console command as <paramref name="player"/> and awaits it (<c>NDevConsole.ProcessNetCommand</c>).
    /// <c>fight</c> and <c>godmode</c> are banned (docs/design/testing-plan.md §4, §7): the first reseeds from the
    /// wall clock, the second keeps state in fields shared across players, and both would break determinism.
    /// </summary>
    public async Task Console(Player player, string command)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        string verb = command.Split(' ', 2)[0];
        foreach (string banned in BannedConsoleCommands)
        {
            if (string.Equals(verb, banned, StringComparison.OrdinalIgnoreCase))
            {
                throw new CouchTestExpectationFailedException(
                    $"The '{banned}' console command is banned in scenarios (docs/design/testing-plan.md §4, §7): it breaks determinism.");
            }
        }

        CouchTestLog.Info($"Console: player={player.NetId}, command=\"{command}\"");
        await NDevConsole.Instance.ProcessNetCommand(player, command);
    }

    /// <summary>Clicks a control exactly as P1 (or the main-screen UI) would, via AutoSlay's <see cref="UiHelper.Click"/>.</summary>
    public async Task ClickAsync(NClickableControl control)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        await UiHelper.Click(control);
    }

    /// <summary>
    /// Sends a teammate HUD command as P2, exactly what P2's pad or keyboard feeds (<see cref="CouchTeammateUi.Handle"/>).
    /// A command that isn't handled means P2's input did nothing — exactly the soft-lock symptom this suite hunts
    /// for — so that fails the scenario rather than silently continuing.
    /// </summary>
    public Task P2Press(CouchHudCommand command)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        bool handled = CouchTeammateUi.Handle(P2Id, command);
        CouchTestLog.Info($"P2 press: {command} (handled={handled})");
        if (!handled)
        {
            throw new CouchTestExpectationFailedException(
                $"P2 input {command} was not handled (teammateUiActive={CouchTeammateUi.IsActive}, " +
                $"hudMode={CouchTeammateHud.ModeName ?? "none"}, overlayTop={NOverlayStack.Instance?.Peek()?.GetType().Name ?? "none"}).");
        }

        return Task.CompletedTask;
    }

    /// <summary>Records the first failure and aborts the scenario. Never weaken this to force a pass (AGENTS.md workflow rule).</summary>
    public void Expect(bool condition, string message)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!condition)
        {
            throw new CouchTestExpectationFailedException(message);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, or throws with <paramref name="what"/> in the message.</summary>
    public async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await WaitHelper.Until(condition, _cancellationToken, timeout ?? DefaultWaitTimeout, $"Timed out waiting for: {what}");
        }
        catch (AutoSlayTimeoutException ex)
        {
            throw new CouchTestExpectationFailedException(ex.Message);
        }
    }

    /// <summary>
    /// Waits for the queue-idle/no-pending-choice/stable-overlay state defined in docs/design/testing-plan.md §6.6
    /// rule 4:
    /// <list type="bullet">
    /// <item>the action queue is empty and the executor is idle (<see cref="RunManager.ActionQueueSet"/>/<see cref="RunManager.ActionExecutor"/>);</item>
    /// <item>no teammate choice is pending (<see cref="CouchTeammateChoices.Pending"/>);</item>
    /// <item>the overlay stack top and count (<see cref="NOverlayStack"/>) are unchanged across 2 consecutive engine frames.</item>
    /// </list>
    /// </summary>
    public Task Settle(TimeSpan? timeout = null) => SettleCore(requireNoPendingChoice: true, "Settle()", timeout);

    /// <summary>
    /// Like <see cref="Settle"/> (queue-idle, stable overlay for 2 frames), but does not require
    /// <see cref="CouchTeammateChoices.Pending"/> to be empty. Use this between navigation presses while
    /// intentionally leaving a teammate choice open (e.g. moving the panel cursor before answering it) — that
    /// choice is expected to still be pending, so waiting on <see cref="Settle"/>'s own "no pending choice" rule
    /// there would time out by design instead of settling.
    /// </summary>
    public Task SettleDuringPendingChoice(TimeSpan? timeout = null) => SettleCore(requireNoPendingChoice: false, "SettleDuringPendingChoice()", timeout);

    private async Task SettleCore(bool requireNoPendingChoice, string what, TimeSpan? timeout)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultSettleTimeout);
        IOverlayScreen? lastTop = null;
        int lastCount = -1;
        int stableFrames = 0;

        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            await NextFrameAsync();
            _cancellationToken.ThrowIfCancellationRequested();

            bool queueIdle = !RunManager.Instance.IsInProgress
                || (RunManager.Instance.ActionQueueSet.IsEmpty && !RunManager.Instance.ActionExecutor.IsRunning);
            bool noPendingChoice = !requireNoPendingChoice || CouchTeammateChoices.Pending.Count == 0;

            IOverlayScreen? top = NOverlayStack.Instance?.Peek();
            int count = NOverlayStack.Instance?.ScreenCount ?? 0;
            bool overlayUnchanged = ReferenceEquals(top, lastTop) && count == lastCount;
            lastTop = top;
            lastCount = count;
            stableFrames = overlayUnchanged ? stableFrames + 1 : 0;

            if (queueIdle && noPendingChoice && stableFrames >= 2)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new CouchTestExpectationFailedException(
                    $"{what} timed out: queueIdle={queueIdle}, noPendingChoice={noPendingChoice}, " +
                    $"overlayStableFrames={stableFrames}, overlayTop={top?.GetType().Name ?? "none"}, overlayCount={count}.");
            }
        }
    }

    /// <summary>
    /// Records a state summary line, then runs the layout rules and the snapshot compare/bless for this checkpoint
    /// (docs/design/testing-plan.md §6.5; see <see cref="CouchTestLayout.RunCheckpointAsync"/>). A rule or snapshot
    /// failure aborts the scenario, naming the node and the numbers, exactly like <see cref="Expect"/>.
    /// </summary>
    public async Task Checkpoint(string label)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CouchTestLog.Info(
            $"Checkpoint '{label}': room={runState?.CurrentRoom?.RoomType.ToString() ?? "none"}, floor={runState?.TotalFloor.ToString() ?? "none"}, " +
            $"driver={LocalContext.NetId?.ToString() ?? "none"}, overlayTop={NOverlayStack.Instance?.Peek()?.GetType().Name ?? "none"}.");

        CouchTestLayoutSnapshot snapshot = await CouchTestLayout.RunCheckpointAsync(_tree, _layoutOptions, label, _cancellationToken);
        _layoutSnapshots.Add(snapshot);
    }

    private static Player ResolvePlayer(ulong id)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        Player? player = runState?.GetPlayer(id);
        if (player == null)
        {
            throw new CouchTestExpectationFailedException($"Player {id} not found in the current run state.");
        }

        return player;
    }

    private Task NextFrameAsync()
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        void OnFrame()
        {
            _tree.ProcessFrame -= OnFrame;
            registration.Dispose();
            tcs.TrySetResult(true);
        }

        _tree.ProcessFrame += OnFrame;

        // Cancelling unblocks this immediately instead of waiting for an actual next frame, so a scenario that's
        // mid-Settle() when its timeout fires stops on the spot rather than one more frame late.
        registration = _cancellationToken.Register(() =>
        {
            _tree.ProcessFrame -= OnFrame;
            tcs.TrySetCanceled(_cancellationToken);
        });

        return tcs.Task;
    }
}
#endif

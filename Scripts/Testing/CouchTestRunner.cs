#if COUCHSPIRE_TESTS
using System.Threading;
using Godot;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// Entry point and lifecycle for the in-game scenario runner (docs/design/testing-plan.md §6.1-§6.3). Started from
/// <see cref="Scripts.Entry.Init"/> when <c>--couch-test</c> is on the command line; always ends by calling
/// <c>GetTree().Quit(exitCode)</c>, including on an internal runner exception, because SIGTERM makes the game's
/// .NET host abort with a crash report instead of exiting cleanly.
/// </summary>
internal static class CouchTestRunner
{
    private const string MainMenuPath = "/root/Game/RootSceneContainer/MainMenu";
    private const string PauseButtonPath = "/root/Game/RootSceneContainer/Run/GlobalUi/TopBar/RightAlignedStuff/PauseButton";

    /// <summary>The one aspect pass WP3 runs at (docs/design/testing-plan.md WP0 facts). WP4's seam: turn this into
    /// a list of (AspectRatioSetting, Vector2I, label) passes and loop scenarios over it.</summary>
    private const string DefaultAspectLabel = "16:9";

    private static readonly TimeSpan MenuStepTimeout = TimeSpan.FromSeconds(20);

    public static void StartIfRequested()
    {
        if (!CommandLineHelper.HasArg("couch-test"))
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            CouchTestLog.Error("No SceneTree at mod init; can't run --couch-test.");
            return;
        }

        // Mods initialize before the tree/main menu exist (docs/design/testing-plan.md §4), so attach the same way
        // CouchRuntime.Initialize does: wait one frame, then start.
        tree.ProcessFrame += OnFirstProcessFrame;
    }

    private static void OnFirstProcessFrame()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        tree.ProcessFrame -= OnFirstProcessFrame;
        _ = RunAndQuitAsync(tree);
    }

    private static async Task RunAndQuitAsync(SceneTree tree)
    {
        int exitCode;
        try
        {
            exitCode = await RunAllScenariosAsync(tree);
        }
        catch (Exception ex)
        {
            CouchTestLog.Error($"Runner crashed outside a scenario: {ex}");
            exitCode = 2;
        }

        CouchTestLog.Info($"Exiting with code {exitCode}.");
        tree.Quit(exitCode);
    }

    private static async Task<int> RunAllScenariosAsync(SceneTree tree)
    {
        if (!CommandLineHelper.TryGetValue("couch-test", out string? selection) || string.IsNullOrWhiteSpace(selection))
        {
            CouchTestLog.Error("--couch-test requires a value ('all' or a comma-separated scenario list).");
            return 2;
        }

        if (!CommandLineHelper.TryGetValue("couch-test-out", out string? outDir) || string.IsNullOrWhiteSpace(outDir))
        {
            CouchTestLog.Error("--couch-test-out <abs dir> is required when --couch-test is given.");
            return 2;
        }

        if (!Path.IsPathRooted(outDir))
        {
            CouchTestLog.Error($"--couch-test-out must be an absolute path, got '{outDir}'.");
            return 2;
        }

        List<ICouchTestScenario> scenarios;
        try
        {
            scenarios = CouchTestScenarioRegistry.Resolve(selection);
        }
        catch (ArgumentException ex)
        {
            CouchTestLog.Error(ex.Message);
            return 2;
        }

        List<string> notes = new();
        RecordNoOpFlags(notes);
        CouchTestLog.Info($"Selected scenario(s): {string.Join(", ", scenarios.Select((scenario) => scenario.Name))}. Output: {outDir}");

        await WaitHelper.Until(() => NGame.Instance != null, CancellationToken.None, TimeSpan.FromSeconds(60), "NGame instance not initialized");
        await NGame.Instance!.GameStartupComplete;
        Control mainMenu = await WaitHelper.ForNode<Control>(tree.Root, MainMenuPath, CancellationToken.None, TimeSpan.FromSeconds(60));

        PrepareEnvironment();

        // Belt-and-braces for the §4 static-ctor caveat: touching AutoSlayer's helpers (WaitHelper/UiHelper/
        // GameOverScreenHandler) runs AutoSlayer's static ctor, which is harmless only as long as nothing ever
        // actually starts AutoSlay in this process. If it's active, something outside this runner's control did —
        // that's a runner-environment problem, not a scenario failure, so it's a hard exit 2.
        if (AutoSlayer.IsActive)
        {
            CouchTestLog.Error("AutoSlayer.IsActive is true after environment prep; AutoSlay must never run alongside --couch-test (docs/design/testing-plan.md §4, §7). Aborting.");
            return 2;
        }

        List<CouchTestScenarioResult> results = new();
        foreach (ICouchTestScenario scenario in scenarios)
        {
            (CouchTestScenarioResult result, bool canContinue) = await RunScenarioAsync(tree, mainMenu, scenario, outDir);
            results.Add(result);
            if (!canContinue)
            {
                CouchTestLog.Error($"Could not return to the main menu after '{scenario.Name}'; stopping (remaining scenarios not run).");
                notes.Add($"Stopped early after '{scenario.Name}': could not reliably return to the main menu.");
                break;
            }
        }

        CouchTestResultsWriter.Write(outDir, results, notes);
        bool passed = results.Count == scenarios.Count && results.All((result) => result.Outcome == CouchTestOutcome.Pass);
        return passed ? 0 : 1;
    }

    private static void RecordNoOpFlags(List<string> notes)
    {
        int repeat = 1;
        if (CommandLineHelper.TryGetValue("repeat", out string? repeatValue) && int.TryParse(repeatValue, out int parsedRepeat) && parsedRepeat > 0)
        {
            repeat = parsedRepeat;
        }

        bool bless = CommandLineHelper.HasArg("bless");
        bool review = CommandLineHelper.HasArg("review");
        if (repeat != 1 || bless || review)
        {
            string note = $"--repeat={repeat}, --bless={bless}, --review={review} were parsed but are no-ops in this build " +
                "(WP4 implements --repeat/--bless/--review: layout baselines, blessing, and the review contact sheet).";
            CouchTestLog.Info(note);
            notes.Add(note);
        }
    }

    private static void PrepareEnvironment()
    {
        // FastMode is clamped to Fast during startup; Instant only sticks if set afterwards (docs/design/testing-plan.md §6.3).
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
        SaveManager.Instance.SetFtuesEnabled(false);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Silent1Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Regent1Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Defect1Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Necrobinder1Epoch>(), EpochState.Revealed);

        // Pin window/aspect the game's own way (WP0 spike facts). Aspect passes are WP4's job; DefaultAspectLabel is
        // the seam it extends.
        SettingsSave settings = SaveManager.Instance.SettingsSave;
        settings.AspectRatioSetting = AspectRatioSetting.SixteenByNine;
        settings.WindowSize = new Vector2I(1600, 900);
        NGame.Instance!.ApplyDisplaySettings();

        CouchTestLog.Info("Test environment prepared: FastMode=Instant, FTUEs off, all epochs unlocked, display pinned to 16:9 1600x900.");
    }

    private static async Task<(CouchTestScenarioResult Result, bool CanContinue)> RunScenarioAsync(SceneTree tree, Control mainMenu, ICouchTestScenario scenario, string outDir)
    {
        CouchTestLog.Info($"--- Scenario '{scenario.Name}' starting (seed={scenario.Seed}, p1={scenario.P1Character.Id.Entry}, p2={scenario.P2Character.Id.Entry}) ---");
        long startMs = (long)Time.GetTicksMsec();
        using CouchTestLogWatch logWatch = new();
        using CancellationTokenSource scenarioCts = new();
        CouchTestOutcome outcome = CouchTestOutcome.Pass;
        string message = "";

        try
        {
            await StartCouchRunAsync(mainMenu, scenario);
            CouchTestContext context = new(tree, scenarioCts.Token);
            Task scenarioTask = scenario.RunAsync(context);
            Task timeoutTask = Task.Delay(scenario.Timeout);
            Task completed = await Task.WhenAny(scenarioTask, timeoutTask);
            if (completed == timeoutTask)
            {
                outcome = CouchTestOutcome.Timeout;
                message = CouchTestDiagnostics.DumpOnTimeout(scenario.Name);

                // Stop the scenario from touching the game any further (every CouchTestContext method checks this
                // token) before we go on to abandon the run — otherwise the zombie scenario task could still press
                // buttons or run console commands inside the *next* scenario's run.
                scenarioCts.Cancel();
                ObserveLateCompletion(scenarioTask, scenario.Name);
            }
            else
            {
                await scenarioTask;
            }
        }
        catch (CouchTestExpectationFailedException ex)
        {
            outcome = CouchTestOutcome.Fail;
            message = ex.Message;
        }
        catch (Exception ex)
        {
            outcome = CouchTestOutcome.Fail;
            message = $"Unhandled exception: {ex}";
        }

        if (outcome == CouchTestOutcome.Pass && logWatch.FirstFailure is string logFailure)
        {
            outcome = CouchTestOutcome.Fail;
            message = logFailure;
        }

        long durationMs = (long)Time.GetTicksMsec() - startMs;
        // Uppercase outcome word ("FAIL"/"TIMEOUT"/"PASS"): deploy.sh test's failing-log grep looks for
        // "\[CouchTest\].*(FAIL|TIMEOUT)" in godot.log, case-sensitively.
        CouchTestLog.Info($"--- Scenario '{scenario.Name}' finished: {CouchTestResultsWriter.OutcomeText(outcome).ToUpperInvariant()} ({durationMs}ms){(message.Length > 0 ? $" — {message}" : "")} ---");

        if (outcome != CouchTestOutcome.Pass)
        {
            try
            {
                await CouchScreenshots.TakeToAsync(Path.Combine(outDir, $"failure-{scenario.Name}.png"));
            }
            catch (Exception ex)
            {
                CouchTestLog.Warn($"Could not save a failure screenshot for '{scenario.Name}': {ex.Message}");
            }
        }

        bool canContinue = await TryAbandonToMainMenuAsync(tree);
        CouchTestScenarioResult result = new(scenario.Name, DefaultAspectLabel, outcome, message, scenario.Seed, durationMs);
        return (result, canContinue);
    }

    private static void ObserveLateCompletion(Task task, string scenarioName)
    {
        _ = task.ContinueWith(
            (finished) =>
            {
                if (finished.Exception != null)
                {
                    CouchTestLog.Warn($"Scenario '{scenarioName}' threw after its timeout was already reported: {finished.Exception.Flatten().InnerException}");
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Drives the real menu buttons (docs/design/testing-plan.md §6.3): Multiplayer → Host → the injected Couch
    /// Co-op card (<c>NMultiplayerHostSubmenuPatch</c>), picks characters per player, sets the seed, then Embarks.
    /// </summary>
    private static async Task StartCouchRunAsync(Control mainMenu, ICouchTestScenario scenario)
    {
        NButton multiplayerButton = await WaitHelper.ForNode<NButton>(mainMenu, "MainMenuTextButtons/MultiplayerButton", CancellationToken.None, MenuStepTimeout);
        await UiHelper.Click(multiplayerButton);

        NButton hostButton = await WaitHelper.ForNode<NButton>(mainMenu, "Submenus/MultiplayerSubmenu/ButtonContainer/HostButton", CancellationToken.None, MenuStepTimeout);
        await UiHelper.Click(hostButton);

        NButton couchCoopCard = await WaitHelper.ForNode<NButton>(mainMenu, "Submenus/MultiplayerHostSubmenu/LocalSelfCoopButton", CancellationToken.None, MenuStepTimeout);
        await UiHelper.Click(couchCoopCard);

        NCharacterSelectScreen characterSelect = await WaitHelper.ForNode<NCharacterSelectScreen>(mainMenu, "Submenus/CharacterSelectScreen", CancellationToken.None, MenuStepTimeout);
        Control charButtonContainer = characterSelect.GetNode<Control>("CharSelectButtons/ButtonContainer");
        foreach (NCharacterSelectButton button in charButtonContainer.GetChildren().OfType<NCharacterSelectButton>())
        {
            button.UnlockIfPossible();
        }

        IReadOnlyList<ulong> playerIds = LocalSelfCoopContext.LocalPlayerIds;
        if (playerIds.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 local player ids after entering couch co-op, found {playerIds.Count}.");
        }

        SelectCharacterFor(characterSelect, charButtonContainer, playerIds[0], scenario.P1Character);
        SelectCharacterFor(characterSelect, charButtonContainer, playerIds[1], scenario.P2Character);

        // Set right before Embark, after character select has initialized: AfterInitialized() clears any earlier
        // value (docs/design/testing-plan.md §4 Seeds).
        NGame.Instance!.DebugSeedOverride = scenario.Seed;

        NButton embarkButton = await WaitHelper.ForNode<NButton>(mainMenu, "Submenus/CharacterSelectScreen/ConfirmButton", CancellationToken.None, MenuStepTimeout);
        await UiHelper.Click(embarkButton);

        await WaitHelper.Until(() => RunManager.Instance.DebugOnlyGetState() != null, CancellationToken.None, TimeSpan.FromSeconds(30), "Run state not initialized after Embark");
        RunState runState = RunManager.Instance.DebugOnlyGetState()!;
        await WaitHelper.Until(() => runState.CurrentRoom != null && runState.CurrentRoom.RoomType != RoomType.Unassigned, CancellationToken.None, TimeSpan.FromSeconds(30), "Room type not assigned after Embark");

        CouchTestLog.Info($"Couch run started: scenario={scenario.Name}, seed={scenario.Seed}, p1={playerIds[0]}:{scenario.P1Character.Id.Entry}, p2={playerIds[1]}:{scenario.P2Character.Id.Entry}");
    }

    private static void SelectCharacterFor(NCharacterSelectScreen screen, Control buttonContainer, ulong playerId, CharacterModel character)
    {
        if (!LocalSelfCoopContext.SetLobbyEditingPlayer(playerId, "couch-test-character-select"))
        {
            throw new InvalidOperationException($"Could not set the lobby editing player to {playerId} for character select.");
        }

        NCharacterSelectButton button = buttonContainer.GetChildren()
            .OfType<NCharacterSelectButton>()
            .FirstOrDefault((candidate) => candidate.Character == character)
            ?? throw new InvalidOperationException($"No character select button found for {character.Id.Entry}.");

        screen.SelectCharacter(button, character);
    }

    /// <summary>
    /// Returns to the main menu after a scenario, following <c>AutoSlayer.AbandonRunAsync</c> when a run is active
    /// (pause menu → Give Up → confirm → game-over screen → main menu), or popping the submenu stack when the
    /// scenario failed before a run started. Never throws: any failure here is reported and turned into
    /// <c>CanContinue = false</c>, per docs/design/testing-plan.md §6.5's "if abandoning itself fails, stop running
    /// further scenarios" rule.
    /// </summary>
    private static async Task<bool> TryAbandonToMainMenuAsync(SceneTree tree)
    {
        Node root = tree.Root;
        try
        {
            if (IsMainMenuVisible(root) && !RunManager.Instance.IsInProgress)
            {
                return true;
            }

            if (RunManager.Instance.IsInProgress)
            {
                await AbandonActiveRunAsync(root);
            }
            else
            {
                await BackOutOfMenusAsync(root);
            }

            bool reachedMainMenu = IsMainMenuVisible(root);
            if (!reachedMainMenu)
            {
                CouchTestLog.Error("Abandon-to-menu finished without the main menu becoming visible.");
            }

            return reachedMainMenu;
        }
        catch (Exception ex)
        {
            CouchTestLog.Error($"Abandon-to-menu failed: {ex}");
            return false;
        }
    }

    private static bool IsMainMenuVisible(Node root)
    {
        return root.GetNodeOrNull<Control>(MainMenuPath)?.IsVisibleInTree() ?? false;
    }

    private static async Task AbandonActiveRunAsync(Node root)
    {
        NTopBarPauseButton pauseButton = await WaitHelper.ForNode<NTopBarPauseButton>(root, PauseButtonPath, CancellationToken.None, MenuStepTimeout);
        await UiHelper.Click(pauseButton);

        NPauseMenu? pauseMenu = null;
        await WaitHelper.Until(() => (pauseMenu = UiHelper.FindFirst<NPauseMenu>(root)) != null && pauseMenu.IsVisibleInTree(), CancellationToken.None, MenuStepTimeout, "Pause menu did not open");
        NPauseMenuButton giveUpButton = pauseMenu!.GetNode<Control>("%ButtonContainer").GetNode<NPauseMenuButton>("GiveUp");
        await UiHelper.Click(giveUpButton);

        NAbandonRunConfirmPopup? confirmPopup = null;
        await WaitHelper.Until(() => (confirmPopup = UiHelper.FindFirst<NAbandonRunConfirmPopup>(root)) != null, CancellationToken.None, MenuStepTimeout, "Abandon confirm popup did not appear");
        NVerticalPopup verticalPopup = confirmPopup!.GetNode<NVerticalPopup>("VerticalPopup");
        await UiHelper.Click(verticalPopup.YesButton);

        await WaitHelper.Until(() => NOverlayStack.Instance?.Peek() is NGameOverScreen, CancellationToken.None, MenuStepTimeout, "Game over screen did not appear after confirming abandon");
        await new GameOverScreenHandler().HandleAsync(new Rng(0), CancellationToken.None);

        await WaitHelper.Until(() => IsMainMenuVisible(root), CancellationToken.None, TimeSpan.FromSeconds(30), "Main menu did not reappear after abandoning the run");
    }

    /// <summary>Fallback when a scenario failed before a run started: pop the main menu's submenu stack directly
    /// (the same <c>NSubmenuStack.Pop()</c> the Back button calls) instead of hunting for a Back button per submenu.</summary>
    private static async Task BackOutOfMenusAsync(Node root)
    {
        Control? mainMenu = root.GetNodeOrNull<Control>(MainMenuPath);
        NSubmenuStack? stack = mainMenu?.GetNodeOrNull<NSubmenuStack>("Submenus");
        int guard = 0;
        while (stack != null && stack.SubmenusOpen && guard++ < 10)
        {
            stack.Pop();
            await Task.Delay(100);
        }

        if (LocalSelfCoopContext.IsEnabled)
        {
            LocalSelfCoopContext.Disable("couch-test-abandon-fallback");
        }
    }
}
#endif

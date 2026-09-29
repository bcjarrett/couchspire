# Automated testing plan

Status: **done.** How to run and write tests: [docs/testing.md](../testing.md). Progress and corrections to this plan: §8.2. The game facts below were checked against decompiled game source v0.111.0 (`src/`).

## 1. Why

Every change is verified today by a human playtest: launch the game, play, send back `[Couch]` log lines and F11 screenshots. That is slow, and an agent can't do it alone.

What the history says the tests must catch:
- Across the git history, about 28% of fixes are soft-locks and about 28% are the wrong player getting or losing something. Only in-game scenarios catch these.
- Game-update API drift broke the mod at every game patch (CHANGELOG v1.30–v1.32). The mod has about 141 string-named `AccessTools` lookups and 32 string-named `[HarmonyPatch]` targets. Most of them fail silently (`?.`), so this class needs an offline check.
- UI showing the wrong player's state, or panels misplaced, is about 15% of fixes. Layout checks cover it.

## 2. Requirements (decided by the maintainer)

- **Deterministic.** The same code gives the same pass/fail on every run, and flakiness counts as a bug in the suite.
- **No judgement in the verdict.** Pass/fail never depends on an LLM or on interpreting images. Screenshots are optional diagnostics only.
- **Local.** It runs on the maintainer's Mac before pushing. CI/CD is out of scope for now, but nothing should rule it out later (see §9).
- **First milestone:** scripted scenarios that drive both players into specific situations and assert on game state and UI layout.
- **Exercise the real paths.** Test hooks must not change the code paths players run (see §7).

## 3. Two layers

| Layer | What | Runs with | Catches |
|---|---|---|---|
| A | Offline check that every Harmony/reflection target exists in the current `sts2.dll` | `dotnet test`, no game | Game-patch drift |
| B | In-game scenario runner: launch the real game with the mod, drive P1 and P2, assert on state and layout, quit with an exit code | `./deploy.sh test [scenario\|all]` | Soft-locks, wrong-player bugs, misplaced UI |

## 4. Verified facts the design relies on

All paths are relative to the repo root. `src/` is the decompiled game source: read-only and gitignored.

**Launching outside Steam, with isolated saves**
- `--force-steam off` skips Steam init (`src/Core/Nodes/NGame.cs:1385-1392`).
- Saves then go to `user://default/<id>/…` instead of `user://steam/<steamid>/…` (`src/Core/Saves/UserDataPathProvider.cs`). On macOS, `user://` is `~/Library/Application Support/SlayTheSpire2/`.
- The null platform's player id is 1 unless `--clientId` is given (`src/Core/Platform/Null/NullPlatformUtilStrategy.cs`). The test tree is therefore `…/SlayTheSpire2/default/1/`.
- **Mod consent gotcha.** `settings.save` is account-scoped (`SaveManager.ConstructDefault`, `src/Core/Saves/SaveManager.cs:188`). It holds `ModSettings.PlayerAgreedToModLoading` and the per-mod enable list.
  - A fresh `default/1/` has no consent, so `ModManager` skips every mod (`src/Core/Modding/ModManager.cs:675`).
  - Fix: seed `default/1/settings.save` by copying the real `steam/<steamid>/settings.save`. Glob for the folder; never hardcode the id.
- Sentry is suppressed for modded sessions (`SentryService.DisableSentryIfModded`).
- The game's own env switches (`src/Core/Debug/DebugSettings.cs`): `STS2_DEV_WINDOWED` forces windowed mode, and `STS2_DEV_SKIP` skips the intro logo.

**Startup order** (`NGame.GameStartup`, `NGame.cs:651-715`)
1. Mods' `[ModInitializer]` runs inside `OneTimeInitialization.ExecuteVeryEarly`, before `ModelDb`, `PrefsSave`, the dev console and the main menu exist.
2. `NGame.Instance.GameStartupComplete` is a public `Task` that completes after startup.
3. `CommandLineHelper.HasArg/GetValue` (`src/Core/Helpers/CommandLineHelper.cs`) parse `--key value`, `--key=value` and bare flags. Keep test flags before any `--`.

**The game's bot, AutoSlay** (`src/Core/AutoSlay/`)
- `AutoSlayer` itself is unusable here. `--autoslay` is gated by `NGame.IsReleaseGame()`, which is hardcoded `true`. `Start` always plays singleplayer and quits the game when done.
- Its helpers are public and reusable:
  - `UiHelper.Click(NClickableControl)` calls `ForceClick()`: `OnRelease()` plus the `Released` signal, with no simulated mouse and no layout dependence.
  - `UiHelper.FindAll/FindFirst`.
  - `WaitHelper.Until/ForNode/ForTask/WithTimeout`.
  - `Watchdog.DumpState()`.
  - The screen and room handlers, which have public parameterless constructors. `ShopRoomHandler` needs a drain callback.
- The handlers assume one local player (`LocalContext.GetMe`). They can drive the **driver** (P1) only.
- Touching `AutoSlayer` runs its static constructor, which overwrites `NonInteractiveMode.AutoSlayerCheck`. It's harmless as long as `AutoSlayer.IsActive` stays false.
- AutoSlay's combat handler uses `CardCmd.AutoPlay`, which bypasses energy and the action queue. **Don't reuse it for combat assertions.** Real card play is `CardModel.TryManualPlay` → `PlayCardAction` through the queue.

**Dev console as the scenario setup tool** (`src/Core/DevConsole/`)
- Debug commands are enabled whenever a mod is loaded (`src/Core/Nodes/Debug/NDevConsole.cs:359`).
- `NDevConsole.Instance.ProcessNetCommand(Player?, string)` is public. It runs the command immediately as that player and returns a `Task` (`NDevConsole.cs:919-946`).
  - The plain `ProcessCommand` enqueues as whoever `LocalContext` says is "me". In couch mode that is the driver.
- Commands:
  - Scope depends on the command:
    - Per player (the issuing player): `gold`, `card <id> [pile]`, `relic [add|remove] <id>`, `potion <id>`, `heal`, `die`.
    - Global: `room <RoomType>`, `event <ID>`, `act <n>`, `win`, `kill`.
  - `room`, `event` and `act` use the seeded run (`RunManager.EnterRoomDebug`).
  - **`fight <id>` reseeds from the wall clock** (`EncounterModel.DebugRandomizeRng`). It is banned.
  - **`godmode` keeps its on/off state in instance fields shared across players.** It is banned.
- The mod can add its own `AbstractConsoleCmd` subclasses; they are auto-registered (`DevConsole.cs:33`).

**Seeds**
- `NGame.Instance.DebugSeedOverride` (public) wins over the lobby seed (`src/Core/Multiplayer/Game/Lobby/StartRunLobby.cs:726-737`).
- `NCharacterSelectScreen.AfterInitialized` clears it, so set it **after** character select initializes, right before Embark (as `AutoSlayer.cs:501` does).

**Idle and state signals for waiting and asserting**
- Idle signals: `RunManager.Instance.ActionQueueSet.IsEmpty` / `BecameEmpty()` and `ActionExecutor.IsRunning`.
- Run and player state:
  - `RunManager.Instance.DebugOnlyGetState()` returns the `RunState`, which gives you `Players`, `CurrentRoom`, `TotalFloor`, `CurrentMapCoord`.
  - `Player`: `Gold`, `Deck`, `Relics`, `Potions`, `Creature` (`CurrentHp`, `Block`, `Powers`), `PlayerCombatState` (`Hand`, piles, `Energy`).
  - `CombatManager.Instance`: `IsInProgress`, `DebugOnlyGetState()`, and events (`TurnStarted`, `CombatEnded`, …).
- UI state: `NOverlayStack.Instance.Peek()` and `ScreenCount`, `NMapScreen.Instance.IsOpen`.
- Logs: `Log.LogCallback` (`src/Core/Logging/Log.cs:11`) is a global event carrying every log line with its level.

**Mod entry points to reuse** (under `Scripts/`)
- **Couch run start:** `NMultiplayerHostSubmenuPatch.OnLocalSelfCoopPressed` (`Scripts/Patch/NMultiplayerHostSubmenuPatch.cs:132-162`) is behind the injected **Couch Co-op** card. Driving the real menu buttons with `UiHelper.Click` exercises the host-route and injection patches too.
- **Character picks:**
  1. Call `LocalSelfCoopContext.SetLobbyEditingPlayer(id, source)` to choose whose pick you're editing.
  2. Then pick with `NCharacterSelectScreen.SelectCharacter(button, model)`.
  3. Embark calls `lobby.SetReady(true)`. The patched chain then readies P2 and starts the run.
  - Player ids: P1 is the platform id and P2 is P1 + 1 (`LocalSelfCoopContext.ResolvePrimaryPlayerId`). With Steam off, P1 = 1 and P2 = 2. WP0 verifies this.
- **P2 input, exactly what P2's pad or keyboard feeds:** `CouchTeammateUi.Handle(ulong? playerId, CouchHudCommand)` (`Scripts/Runtime/Couch/CouchTeammateUi.cs:23`).
  - Combat also has `CouchRemotePlay`. Its `TryPlay`, `ToggleEndTurn` and `TryUsePotion` dispatch real `RequestEnqueueActionMessage`s as P2 through the loopback.
  - Choices go through `CouchTeammateChoices.Answer/AnswerIndex`.
- **Driver swap, if a scenario needs one:** `LocalMultiControlRuntime.SwitchControlledPlayerTo(id, source)`, or the guarded `LocalControlSwitchGuard.TrySwitchTo`.
- **Screenshots:** `CouchScreenshots` (`Scripts/Runtime/Couch/CouchScreenshots.cs`).
- **Stable UI root names**: `CouchTeammateHud`, `CouchTeammateRelicBar`, `CouchTeammateTopBar`, `CouchTeammateInfo`, `CouchTeammatePanels`, `CouchTeammateRewards`, `CouchTeammateRestSite`, `CouchTeammateShop`, `CouchTeammateTreasure`, `CouchTeammateEvent`, `CouchTeammateChoicePanel`, `CouchTeammateDeckChanges`, `CouchDebugOverlay`, `CouchInputGate`.
- **Automatic driver switches a scenario must expect:**
  - Every combat setup makes P1 the driver (`Scripts/Patch/NCombatRoomPatch.cs:20`).
  - Merged rewards switch to the first living player (`CombatRoomOfferRewardsPatch.cs:106`).
  - Shared-event options visit each player in turn (`EventSynchronizerPatch.cs:256-265`).

## 5. Layer A: offline patch-target check

- **New project** `Tests/CouchSpire.Tests/`: xunit, plain `Microsoft.NET.Sdk`, net9.0, with **Mono.Cecil**. It reads the built `CouchSpire.dll` and the game's `sts2.dll` as files, so Godot is never started.
- **Test 1: Harmony targets.** For every `[HarmonyPatch]`, merge class- and method-level attributes and resolve the target type and member in `sts2.dll`, walking base types.
  - Cover `MethodType` Getter, Setter and Constructor, and the argument-type overloads.
- **Test 2: reflection lookups.** For every `call HarmonyLib.AccessTools::*` in the mod's IL, walk back to the nearest `ldstr` (member name) and `ldtoken` (type), then resolve the pair.
  - Cover every variant the mod uses (`Field`, `Method`, `Property`, `PropertyGetter/Setter`, `Declared*`, `FieldRefAccess`, `TypeByName`, …). Grep `Scripts/` for the list.
  - Call sites without a static type (e.g. `x.GetType()`) are printed on an "unchecked" list; they don't fail the test.
  - Intentional legacy-name fallbacks are allow-listed with a comment; `BeginRunIfAllPlayersReady` is one (see `Scripts/Patch/LoadRunLobbyPatch.cs`).
- The failure message names the mod file/type, the target string, and the game type searched.
- This replaces the manual "static sweep of string-based targets" (CHANGELOG v1.31, v1.32) and becomes step 4 of the AGENTS.md §5 playbook.

## 6. Layer B: in-game scenario runner

### 6.1 Build gating and entry
- Test code lives in `Scripts/Testing/`. It compiles only in **Debug**: a `COUCHSPIRE_TESTS` define, plus `<Compile Remove="Scripts/Testing/**" />` for other configurations.
- The shipped Release DLL must contain no test types.
- `Scripts/Entry.cs` gets one `#if COUCHSPIRE_TESTS` hook: if `CommandLineHelper.HasArg("couch-test")`, start `CouchTestRunner` on the main thread. Attach through `SceneTree.ProcessFrame`, as `CouchRuntime.Initialize` does.

### 6.2 Command-line flags

| Flag | Meaning |
|---|---|
| `--couch-test <all\|name[,name]>` | Scenarios to run |
| `--couch-test-out <abs dir>` | Results folder |
| `--repeat <N>` | Run the selection N times; fail if any run's results or layout snapshots differ from the first |
| `--bless` | Rewrite layout baselines instead of comparing |
| `--review` | Also save checkpoint screenshots and a contact sheet (never affects the verdict) |

### 6.3 Runner lifecycle (`CouchTestRunner`)
1. Await `NGame.Instance.GameStartupComplete`, then the main menu node `/root/Game/RootSceneContainer/MainMenu`.
2. Prepare the environment:
   - `SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant`, set after startup, because startup clamps Instant to Fast.
   - FTUEs off, and characters unlocked via `ObtainEpochOverride` and `NCharacterSelectButton.UnlockIfPossible`, as in `AutoSlayer.cs:176-182` and `:490-495`.
3. For each scenario, for each aspect pass it declares:
   - Start a fresh couch run from the main menu: Multiplayer → Host → **Couch Co-op**. Pick characters per player, set `DebugSeedOverride`, then Embark.
   - Run the body under the scenario timeout (default 90 s). The timeout is the soft-lock detector.
   - Abandon back to the main menu, following `AutoSlayer.AbandonRunAsync`.
4. Write results, then call `GetTree().Quit(failed ? 1 : 0)`.

### 6.4 Scenario API (`CouchTestContext`; keep it small)
- `P1` and `P2` are `Player` handles. `Console(player, "cmd")` wraps `ProcessNetCommand` and awaits it.
- `ClickAsync(node)`: P1 and the main-screen UI, via `UiHelper.Click`.
- `P2Press(CouchHudCommand)`: goes to `CouchTeammateUi.Handle(P2.NetId, …)`. In combat, `P2Play(cardId, target)`, `P2EndTurn()` and similar wrap `CouchRemotePlay`.
- `Settle()` (see §6.6 rule 4). `WaitUntil(cond, what, timeout)`. `Expect(cond, message)` records the first failure and aborts the scenario.
- `Checkpoint(label)`: runs layout rules and the snapshot compare (§6.5) and records a state summary. With `--review` it also takes a screenshot.

### 6.5 Failure detection
- **State expectations** on `RunState`, `Player`, `CombatState`, `LocalContext.NetId` and `NOverlayStack`.
- **Timeouts** dump the overlay stack, current room, driver, action queue state, and both players' pending choices and panels, then save one diagnostic screenshot.
- **Log watch** (`CouchTestLogWatch`) subscribes to `Log.LogCallback` for each scenario.
  - Any `Error` fails the scenario, and so does any mod line matching a failure pattern. The pattern list lives in one file, with an allowlist for known-benign lines. Start from:
    - `over 2000ms` (`UsePotionActionWatchdogPatch.cs:31`).
    - `Flow-block watchdog` (`LocalMultiControlRuntime.cs:1042`).
    - Rollback lines (`LocalMultiControlRuntime.cs:294`, `CouchInputRouter.cs:210`).
    - Swallowed exceptions (`ActionQueueFailSafePatch.cs:32,105`).
    - The enemy-turn fallback (`CombatManagerReadyEnemyTurnPatch.cs:101`, logged at Info).
    - Unknown teammate choice kind (`CouchTeammateChoices.cs:141`).
    - Patch drift (`CombatManagerReadyEnemyTurnPatch.cs:131`, `LocalMultiControlRuntime.cs:375`).
    - Checksum or desync errors.
- **Layout checks** (`CouchTestLayout`), run at every checkpoint:
  1. **Rules**, on the `GetGlobalRect()` of each visible mod UI root:
     - the node is inside the viewport and has non-zero size;
     - it doesn't overlap named P1 elements (P1's relic row, top bar, end-turn button) or other visible mod panels;
     - a few anchoring rules hold. For example, P2's combat relic row is vertically centered on the status line within ±4 px (regression for commit 4c19bc4), and the HUD band starts below the players list.
  2. **Snapshots.** Write the rects of all mod UI roots and a few game anchor nodes to `Tests/layout-baselines/<scenario>/<checkpoint>@<aspect>.json`, which is committed.
     - Any move or resize over 4 px fails and names the node and the delta.
     - `--bless` rewrites the baselines, so intentional layout changes show up as diffs in the commit.
  3. **Aspect passes.** Layout-sensitive scenarios (`combat`, `rewards`, `rest_site`, `shop`) run at 16:9 and at 16:10 (the Steam Deck shape), set through `SettingsSave.AspectRatioSetting` plus the window size.
- **No pixel diffs.** Animations, particles and enemy idles make them flaky. `--review` screenshots and the contact sheet are for humans only.

### 6.6 Determinism rules
1. **Identical starting state.** Before every launch, the host script deletes everything under `…/SlayTheSpire2/default/1/` except `settings.save`.
   - Progress changes code paths; the host-route patch, for example, behaves differently when `NumberOfRuns == 0`.
   - The script must refuse any path outside `default/1/`, with a hard check, and never touch `steam/`.
2. **Fixed seeds.** Each scenario declares a seed and characters. Defaults: P1 Ironclad, P2 Silent. Rooms come only from `room`, `event` and `act`, never `fight`.
3. **The driver makes no random choices.** Every pick is explicit: a card ID, an option ID, or a stable ordering. Cards a scenario depends on are placed with `card <ID>` for the right player, not drawn.
4. **No sleeps as gates.** `Settle()` waits until:
   - the action queue is empty and the executor is idle;
   - no teammate choice is pending;
   - the overlay stack top and count are unchanged for 2 consecutive frames.
   - P1 and P2 steps are issued one at a time, each settled before the next.
   - Timeouts only detect hangs and are generous.
5. **Clock-free assertions.** Where values come from seeded RNG, use relative checks, e.g. "P2's gold rose by the gold reward's amount". Never assert on `Rng.Chaotic` outputs (cosmetics, YummyCookie, Trial numbers).
6. **Pinned display.** The runner sets the window size and aspect ratio explicitly per pass. Baselines are recorded on the maintainer's Mac.
7. **Pinned mod settings.** The host script sets the `COUCHSPIRE_*` env vars to defaults, so a local `CouchSpire.cfg` can't change behavior.
   - The values are `ROUTING=1`, `SIMULTANEOUS=1`, `PROBE=0`, `OVERLAY=0`, `SHOTS=0`, `EVENT_PANEL_LEFT=1` and `GAMESCOPE_FOCUS=1`.
   - Number settings aren't env-overridable (`CouchConfig.Number` reads the file only). The runner therefore forces the cfg defaults in-process, or the host script installs no `CouchSpire.cfg` for test runs.
8. **Proof.** `--repeat 3` must pass, and two separate launches must give identical `results.json` apart from durations.

### 6.7 Output: `test-results/<timestamp>/` (gitignored)
- `results.json`: per scenario and aspect pass, one of pass/fail/timeout, the failing expectation or log line, the seed, and the duration.
- `summary.txt`.
- `layout/`: this run's snapshots, plus diffs on failure.
- `godot.log`: a copy of the game log.
- `failure-<scenario>.png` for diagnosis. With `--review`, also checkpoint PNGs and `contact-sheet.png`.

### 6.8 Host script: `./deploy.sh test [scenario|all] [--repeat N] [--bless] [--review]`
1. Take a lock with `mkdir "$TMPDIR/couchspire-test.lock"`, and refuse if the game is running. Only one test run may use the game install and the `default/1/` tree at a time; this matters when several agents work in parallel.
2. Build Debug and install to the Mac mods folder, reusing `build` and `install_atomic`. Install no `CouchSpire.cfg` for tests.
3. Seed `default/1/settings.save` if missing (§4), then reset the rest of `default/1/` (§6.6 rule 1).
4. Launch `…/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2 --force-steam off --couch-test <sel> --couch-test-out <dir> …` with `STS2_DEV_WINDOWED=1`, `STS2_DEV_SKIP=1` and the pinned `COUCHSPIRE_*` values.
5. Apply an outer timeout that kills the game. Print `summary.txt` and the failing log lines, release the lock, and exit with the game's exit code.

### 6.9 Milestone-1 scenarios (`Scripts/Testing/Scenarios/`, one file per area)

| Scenario | Steps and assertions | Mod code that guarantees it |
|---|---|---|
| `start` | The couch run starts from the menu. Session order is [P1, P2], the driver is P1, and both characters are as picked. `CombatStateSynchronizer` is disabled. The save tag reads `v3:players=P1,P2`. | `NMultiplayerHostSubmenuPatch.cs:132-162`, `LocalMultiControlRuntime.cs:48-66`, `LocalSelfCoopSaveTag.cs` |
| `combat` | `room Monster`. P2 plays a card through the HUD: it leaves P2's hand and spends P2's energy, while P1's hand and energy are unchanged. P2 ends their turn, then P1 ends theirs. The enemy turn runs and control returns to the player turn. `win` opens the rewards screen. | `CouchRemotePlay.cs`, `CombatManagerReadyEnemyTurnPatch.cs`, `CombatManagerPatch.cs` |
| `choice` | `card SURVIVOR` for P2 (check the ID in `src/`), and P2 plays it. A teammate choice is pending and P1's screen is unchanged. P2 answers through the HUD, and the discarded card comes from **P2's** hand. | `CouchTeammateChoicePatch.cs`, `CouchTeammateChoices.cs:155-231` |
| `rewards` | After `win`, P1's screen lists only P1's rewards. P2 takes gold (P2's gold goes up, P1's doesn't) and a card (P2's deck grows by 1). P1's Proceed is blocked until P2 is done, then the map opens. | `CombatRoomOfferRewardsPatch.cs`, `CouchTeammateRewards.cs`, `CouchRewardsProceedPatch.cs` |
| `rest_site` | `room RestSite`. P1 rests while P2 smiths through the panel. Exactly one card in P2's deck is upgraded and none in P1's. Proceed is blocked until P2 is done. | `RestSitePatch.cs`, `CouchTeammateRoomsPatch.cs:19-30` |
| `shop` | `gold 500` for both, then `room Shop`. P2 buys a card, a potion and a relic through the panel. P2's gold drops by the listed prices, and P2's deck, potions and relics change; P1's don't. P1 can't leave until P2 is done. | `LocalMerchantInventoryRuntime.cs`, `NMerchantInventoryPatch.cs`, `CouchTeammateRoomsPatch.cs:32-43` |
| `treasure` | `room Treasure`. Both players pick. Each gets exactly one relic plus chest gold, with no duplicates. | `TreasureRoomRelicSynchronizerPatch.cs`, `CouchTeammateRoomsPatch.cs:49-69` |
| `map` | From the map, P1 votes a node. Both players move: `TotalFloor` goes up by 1 and the room is entered. | `MapSelectionSynchronizerPatch.cs:40-78` |

## 7. Deliberately not used (each would bypass what we test)
- `TestMode.IsOn`: it flips the mod's reward code to auto-select paths (`CombatRoomOfferRewardsPatch.cs:109-117`, `RewardsSetPatch.cs`, `RewardsCmdPatch.cs:101-108`, `CouchTeammateRewards.ShouldTakeRewards`).
- `CardSelectCmd.UseSelector`: a global selector answers every player's choices and skips the mod's choice patches and the choice synchronizer.
- `NonInteractiveMode.AutoSlayerCheck = () => true`: it makes action-state errors throw and skips combat pauses. It could come later as an opt-in `--turbo`, never as the default.
- Patching `NGame.IsReleaseGame`: it may be JIT-inlined and it changes unrelated behavior.
- AutoSlay's `CombatRoomHandler`: it plays cards through `CardCmd.AutoPlay`, which skips energy and the queue.
- The `fight` and `godmode` commands (§4).

## 8. Work packages

Dependencies: WP0 and WP2 first. WP1 and WP7 can run in parallel with everything else. WP3 needs WP0 and WP2. WP4 and WP5x need WP3. WP6 goes last.

Rules for every package:
- Follow `AGENTS.md`: English only, `[LocalMultiControl]` log prefix, commit per logical change, run `dotnet format --verify-no-changes`.
- Never touch `~/Library/Application Support/SlayTheSpire2/steam/`.
- Only `deploy.sh test` launches the game, and it holds the lock.
- **Never weaken an assertion to get a pass.** If a scenario exposes a real mod bug, stop and report it. Fix it in a separate commit with a CHANGELOG entry, or record it in `TODO.md` if it's out of scope.
- Record anything this doc gets wrong back into this doc.

**WP0: Feasibility spike (blocks WP3)**
- Steps:
  1. Seed `default/1/settings.save`.
  2. Launch the installed macOS binary directly with `--force-steam off`, `STS2_DEV_WINDOWED=1` and `STS2_DEV_SKIP=1`.
  3. Confirm that `Mod initialized.` is logged, the main menu shows, and saves land in `default/1/`.
  4. Start a couch run by hand. Confirm P1 = 1 and P2 = 2, and that the run starts and a combat plays.
  5. Confirm the window size and aspect ratio can be set from code, and that `GetGlobalRect()` of `CouchTeammateHud` is identical across two launches.
- Fallback if direct launch fails: launch through `steam://run/<appid>//<args>`, and have the test mode pin save profile 3 with a prefix on `SaveManager.InitProfileId`. Update §4 and §6.8 to match.
- Done when: a short "Spike results" section is added to this doc. Done: see §8.1.

**WP1: Layer A (§5)**
- Done when:
  - `dotnet test Tests/CouchSpire.Tests` passes on the current `sts2.dll`.
  - Misspelling one patch target and one `AccessTools` name makes it fail and name both. Revert afterwards.
  - AGENTS.md §5 step 4 points to it.

**WP2: Build plumbing** (the only package that edits `LocalMultiControl.csproj`)
- Move the `Sts2Dir`/`Sts2DataDir` detection into `Sts2Paths.props`, imported by both projects.
- Add `<Compile Remove="Tests/**" />`; the Godot SDK globs every `.cs` under the repo.
- Add the `COUCHSPIRE_TESTS` define and the `Scripts/Testing/**` exclude outside Debug (§6.1).
- Add `test-results/` to `.gitignore`.
- Done when: the Debug and Release builds both succeed, and the Release DLL has no `CouchTest*` types.

**WP3: Runner core**
- Covers §6.1–§6.4, the log watch, timeouts and dumps, `results.json`, `deploy.sh test` (§6.8), the `start` scenario, and `Settle()`.
- Done when: `./deploy.sh test start` exits 0 unattended, and a forced failure exits 1 with a useful `summary.txt`.

**WP4: Layout checks (§6.5)**
- Covers rules, snapshots, `--bless`, the aspect passes, `--repeat`, and `--review`, with the contact sheet built from a new explicit-path overload in `CouchScreenshots`.
- Done when:
  - Shifting `CouchTeammateRelicBar` by 20 px makes both the anchoring rule and the snapshot fail, naming the node.
  - `--bless` accepts the shift. Revert afterwards.

**WP5a–e: Scenarios (§6.9), one agent each**
- The packages: WP5a `combat` + `choice`, WP5b `rewards`, WP5c `rest_site`, WP5d `shop`, WP5e `treasure` + `map`.
- Done when:
  - The scenario passes with `--repeat 3`.
  - Its key protection is proven. Temporarily disabling the guaranteeing patch makes it fail; for WP5b, disable `CouchRewardsProceedPatch`. Revert afterwards.

**WP6: Docs**
- Write `docs/testing.md`: how to run it, flags, how to write a scenario, determinism rules, and blessing baselines.
- AGENTS.md: §1 scope adds `Tests/`; §3 says to run `./deploy.sh test all` before asking for a playtest or pushing; §8 docs map.
- Add a link from `docs/architecture.md`.
- Mark this doc's status as done.

**WP7: Gold-mirror suppression leak (independent)**
- The bug, found by reading the code and not yet reproduced in play:
  - `GoldMirrorSuppressionContext` (`Scripts/Patch/PlayerGainGoldMirrorPatch.cs:11-41`) increments an `AsyncLocal<int>` in `RelicCmdPatch`'s synchronous prefix (`RelicCmdPatch.cs:24-27`).
  - It decrements the counter inside an async method (`ExitSuppressionWhenCompleteAsync`), and AsyncLocal writes made inside an async method never flow back to the caller.
  - So after `await RelicCmd.Obtain(...)`, the rest of the caller's flow still has gold mirroring suppressed.
- Steps:
  1. Write a failing unit test in `Tests/CouchSpire.Tests` (needs `InternalsVisibleTo`), or an in-game scenario: obtain a relic, then gain gold in the same flow, in a context where gold mirrors (post-combat or Crystal Sphere).
  2. Fix it so enter and exit happen in the same flow.
  3. Add a CHANGELOG entry.

### 8.1 Spike results (WP0, 2026-09-27, game v0.111.0, macOS)

- **Direct launch works.** `STS2_DEV_WINDOWED=1 STS2_DEV_SKIP=1 "<app>/Contents/MacOS/Slay the Spire 2" --force-steam off` reaches the main menu in about 7 s, with no Steam relaunch. The log shows `Applying Harmony patches.` → build marker → `Mod initialized.` and no Harmony errors. The `steam://` fallback isn't needed.
- **Mod consent.** `settings.save` is plain JSON. Consent is `"mods_enabled": true` (`ModSettings.PlayerAgreedToModLoading`) plus a `mod_list` entry for `CouchSpire`. Copying the real `steam/<id>/settings.save` into `default/1/` is enough.
- **Save location.** Modded sessions write to `default/1/modded/profile1/saves/` (`prefs.save`, `progress.save`, `history`). The §6.6 rule 1 reset (everything under `default/1/` except `settings.save`) covers it. The file mtimes under `steam/` were identical before and after the run.
- **Pinning the display.** Set `SettingsSave.AspectRatioSetting` and `SettingsSave.WindowSize`, then call `NGame.Instance.ApplyDisplaySettings()` (`NGame.cs:783`). This also sets the UI scale target (`ContentScaleSize`: 16:9 → 1920×1080, 16:10 → 1920×1200). A raw `DisplayServer.WindowSetSize` leaves the UI scaled for the old aspect. The game doesn't re-apply its saved settings during play; it writes them back only on quit. `STS2_DEV_WINDOWED` only forces windowed mode and doesn't change size or aspect. Observed: requested 1600×900 gave window (1600, 900) and viewport (1920, 1080).
- **Quit, don't kill.** SIGTERM makes .NET abort during shutdown (SIGABRT in `SafeExitProcess`), which leaves a macOS crash report. The runner must end with `GetTree().Quit(code)`. The host script's SIGTERM/SIGKILL is only for hangs.
- **Mod UI roots.** `CouchTeammateHud` and `CouchTeammateRelicBar` are plain `Control`s, not `CanvasLayer`s, so `GetGlobalRect()` works on them directly. At the main menu the relic bar exists at zero size, and the HUD doesn't exist yet.
- **Not verified by hand:** the manual couch run (P1 = 1 and P2 = 2 with Steam off) and identical HUD rects across launches. These are left to automation: the `start` scenario asserts the player ids, and WP4's `--repeat` / two-launch check asserts rect stability.

### 8.2 Status and corrections (2026-09-28)

**Merged on `testing-harness`:** WP0-WP4, WP5a-e (`combat`, `choice`, `rewards`, `rest_site`, `shop`, `treasure`, `map`), WP6 (`docs/testing.md`, AGENTS.md), and WP7 (the fix plus the `gold_mirror` scenario).

**First in-game pass (2026-09-28):** 10 of 11 scenarios pass (`treasure` is red on a real mod error; see `TODO.md`
A3). The `shop`, `map` and `gold_mirror` protection proofs fail as they should; `treasure`'s is inconclusive.
`rest_site` verifies the freeze fix. The suite found and fixed the rewards-merge flag leak (3a21a7f).

**Second in-game pass (2026-09-28):** all 13 scenarios pass, including `--repeat 2`. Fixed the treasure-room
duplicate-hand error and the `RestSiteSynchronizer.Dispose()` exception (both TODO A3; see CHANGELOG). Layout
baselines were blessed. Remaining known items are non-blocking: the `treasure` protection proof, cosmetic
layout jitter/darkening on some checkpoints, and one pre-existing 1px `--repeat` mismatch on
`combat/p2-played-card`.

**Known weak spot:** `treasure` checks only that P2's chest gold *rose*. P2's amount comes from P2's own seeded roll, and nothing exposes it the way P1's chest display does.

**Corrections to the plan above:**
- §4: the debug `room <type>` command records a **null** encounter id in the map history. Abandoning such a run
  throws in `ProgressSaveManager.IncrementEncounterLoss`. Scenarios use `context.EnterRoom`, which fills the id in.
- §4: the `damage` console command only works in combat.
- §4: Godot's file logger buffers ordinary lines, so after a force-quit `godot.log` can stop early.
  `deploy.sh test` also captures `game-stdout.txt`, which doesn't have that problem.
- §6.4: `Checkpoint` is `async` and must be awaited. CS4014 is a build error, so the compiler enforces it.
- §6.5: no "checksum or desync" log lines exist in the mod, so that pattern was dropped.
- §6.5: the HUD band doesn't start *below* the players list. By design it lines up with the list's top
  (`CouchTeammateHud.BandTop`) and sits to its right. The rule checks the header isn't above the list's top.
- §6.5: baseline file names use `16x9`/`16x10`, because `:` is invalid on Windows.
- §6.9 `choice`: the teammate choice can become pending in the same call that plays the card, before the HUD
  switches mode. Wait for `CouchTeammateHud.ModeName == "Choice"` before answering.
- §6.9 `combat`: the guaranteeing patch is `CombatManagerReadyEnemyTurnPatch`. `CombatManagerPatch` is a no-op
  in simultaneous mode.
- Scenarios are discovered by reflection. There's no registry list to edit.
- §6.5 **changed by the maintainer (2026-09-28):** layout snapshots are a non-blocking drift report. The relational
  rules are the gate. `--strict-layout` restores failing on drift. Why: a golden snapshot checks "unchanged", not
  "correct"; it breaks all at once on a game patch or intended tweak; it can't see the wrong player's data; and it
  is tied to one screen size. **Planned, not done:**
  - more rules (P2's panels don't cover P1's Proceed; text fits its panel; the HUD is right of the players list);
  - content checks for the wrong-player bug class (P2's panel shows P2's seat, gold and HP);
  - a rules-only 1280x800 Steam Deck pass.
- §6.9 `map`: P2 never votes. `MapSelectionSynchronizerPatch` fills P2's vote with P1's destination and triggers the move.
- §8 WP7: no shipped game flow reaches the bug (every event does relic *or* gold). The regression test uses a test-only
  console command, run in the Crystal Sphere event, because the post-combat rewards screen keeps
  `CombatRewardMergeContext` active, and that switches mirroring off.

**Mod bugs the tests found:**
- The rest-site entry check froze the game (unbounded same-frame retries; see `TODO.md` A/A2).
- A dead `NRestSiteRoom.UpdateNavigation` reflection call (Layer A).
- A spurious error when entering Couch Co-op on a profile with no saves.

## 9. Later (not in this milestone)
- Two-player soak bot: AutoSlay's screen handlers for P1, panel input for P2, and real `PlayCardAction`s, playing whole seeded runs.
- Save & quit → continue scenario.
- `./deploy.sh test bazzite`.
- Headless mode. Nothing statically blocks `--headless`, but layout under headless is unverified.
- CI on a self-hosted runner that owns the game. Game files can't go on hosted runners.
- `--turbo` via `NonInteractiveMode`.
- Unit tests for pure logic behind a logging and clock seam: `CouchConfig` parsing, `LocalSelfCoopSaveTag` parsing, `CouchSeats` binding, and choice validation.

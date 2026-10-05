# Testing

CouchSpire has two automated test layers. Layer A runs anywhere; Layer B (in-game scenarios) is macOS-only for now
(see [Layer B: running](#layer-b-running)). This page is the how-to; [Reference](#reference) at the bottom has the
ground-truth facts the harness relies on, for anyone extending it.

| Layer | Command | Needs the game? | Catches |
|---|---|---|---|
| A: patch targets | `dotnet test Tests/CouchSpire.Tests` | No (reads the DLLs as files) | Harmony/`AccessTools` targets renamed or removed by a game patch |
| B: in-game scenarios | `./deploy.sh test [scenario\|all]` | Yes: launches it with Steam off | Soft-locks, the wrong player getting something, misplaced UI |

Run Layer A after every build. Run `./deploy.sh test all` before asking for a playtest or pushing.

## Layer A

`dotnet test Tests/CouchSpire.Tests -p:GameTarget=main|beta` builds the mod for that game branch, then checks every
`[HarmonyPatch]` target and every `AccessTools.*` string lookup against that branch's `sts2.dll` (the
`~/sts2-ref/<branch>/` snapshot, else the installed game), walking base types the way Harmony does. `make test` runs
it for both branches (AGENTS.md §5).

- A failure names the mod type and method, the target string, and the game type searched.
- Lookups with no static type or no literal name (e.g. `AccessTools.Field(x.GetType(), …)`) are printed as
  "unchecked" and never fail the test. Prefer `typeof(T)` and literal names, so they get checked.
- Intentional old-name fallbacks (new name tried first) are allow-listed, each with a reason, in
  `Tests/CouchSpire.Tests/AccessToolsAllowList.cs`. Anything else that doesn't resolve is a real bug.

## Layer B: running

```bash
./deploy.sh test                     # all scenarios
./deploy.sh test combat,choice       # a selection
./deploy.sh test all --repeat 3      # fail if any run's results or layout differ from the first
./deploy.sh test rewards --bless     # rewrite layout baselines instead of comparing
./deploy.sh test shop --review       # also save checkpoint screenshots + contact-sheet.png
./deploy.sh test all --strict-layout # also fail on drift from the layout baselines
```

What it does:
1. Takes a lock, and refuses (exit 3) if the game is already running.
2. Builds **Debug** for the game branch Steam has installed (main or beta; AGENTS.md §5) and installs it with **no**
   `CouchSpire.cfg`. To test the other branch, switch branches in Steam and run it again.
3. Resets the test save profile `~/Library/Application Support/SlayTheSpire2/default/1/` to just `settings.save`,
   copied from your real profile the first time for mod consent. Your `steam/` saves are never touched.
4. Launches the game with `--force-steam off`, pinned `COUCHSPIRE_*` settings, and a fixed window.
5. Prints `summary.txt` and exits with the runner's code: 0 pass, 1 a scenario failed or timed out, 2 runner error,
   3 lock or game busy.

**After a test session, run `./deploy.sh local`** to put your normal build and settings back.

Environment variables:
- `COUCHSPIRE_TEST_TIMEOUT`: outer kill timeout in seconds (default 1200). It's only for hangs; each scenario has
  its own 90 s timeout inside the game.
- `COUCHSPIRE_TEST_LOCK_WAIT`: seconds to queue for the lock instead of failing (for parallel agents).

Output goes to `test-results/<UTC timestamp>/` (gitignored):
- `summary.txt` and `results.json`: one entry per scenario × aspect ratio.
- `godot.log`: the game log, copied after exit.
- `game-stdout.txt`: raw stdout. It survives a force-quit, which the log file may not (see below).
- `failure-<scenario>.png`.
- `layout/`: this run's snapshots, plus diffs.
- With `--review`, checkpoint screenshots and `contact-sheet.png`.

### Reading a failure
- Start with `summary.txt`. It gives the failing expectation, or the timeout dump: overlay stack, room, driver,
  action queue, pending choices, visible panels.
- In `godot.log`, grep for `[CouchTest]`, `^\[ERROR\]` and `Exception`.
- Bare `ERROR:` lines (resources still loading when the game quits, leaks at exit) are Godot engine noise from the
  quick quit. Ignore them.
- If the game hangs outright, the runner's own timeout can't fire (it runs on the game's main thread). Godot's log
  file buffers ordinary lines, so after a force-quit it can end early. Check `game-stdout.txt` instead. A flood of
  one repeated line there points at a runaway loop.

## Scenarios

| Name | Checks |
|---|---|
| `start` | A couch run starts from the menu: player order and ids, driver, characters, save tag |
| `combat` | P2 plays a card through the HUD (only P2's hand and energy change); both end turn; enemy turn; `win` |
| `choice` | P2's Survivor asks P2, not P1; P2 answers through the HUD; the discard comes from P2's hand |
| `rewards` | Per-player rewards; P2 takes gold and a card; P1's Proceed is blocked until P2 is done |
| `rest_site` | P1 rests, P2 smiths exactly one card; Proceed is blocked until P2 is done |
| `shop` | P2 buys a card, a potion and a relic; P2 pays exact prices; P1 is unchanged; leaving is blocked |
| `layout` | Map and combat checkpoints at 16:9 and 16:10 (layout vehicle) |
| `intent_clearance` | Boss fight with P2's hand full: P2's cards stop short of the enemy intents (16:9 and 16:10) |
| `treasure` | P1 opens the chest and picks by click, P2 through their panel; each gets exactly their own relic and chest gold |
| `map` | P1 clicks the leftmost reachable node; the mod fills in P2's vote; the floor rises by 1 and a room is entered |
| `gold_mirror` | In the Crystal Sphere event, a test-only `goldmirrorflow` command obtains a relic then gains gold in one flow; P2 must get the mirrored gold (guards 06b185a) |
| `simultaneous_smith` | P1 and P2 smith at the same time — P1 through the real full-screen upgrade grid, P2 through their column panel — each picks only their own card, and P2's panel never overlaps P1's grid or its confirm/cancel buttons |

## Writing a scenario

1. Add a class to `Scripts/Testing/Scenarios/<Area>Scenarios.cs` that derives from `CouchTestScenarioBase`, and
   wrap the file in `#if COUCHSPIRE_TESTS`. It's discovered automatically; there's no list to edit.
2. Override `Name`. Override `Seed`, `P1Character`/`P2Character` (default Ironclad/Silent), `Timeout` and
   `Aspects` as needed. Layout-sensitive scenarios run at `"16:9"` and `"16:10"`.
3. In `RunAsync(CouchTestContext context)`, touch the game **only through the context**:
   - Setup: `context.EnterRoom(RoomType.X)` for rooms. Never `Console(P1, "room …")`: the debug command leaves a
     null encounter id in the map history, and abandoning then crashes. For everything else, use
     `context.Console(player, "…")`: `gold`, `card <ID> [pile]`, `relic add`, `potion`, `damage` (in combat
     only), `win`. `fight` and `godmode` are banned; they break determinism.
   - P1 (the driver) acts through the real UI: `context.ClickAsync(node)`.
   - P2 acts exactly as their pad would: `context.P2Press(CouchHudCommand.X)`. This fails if the HUD doesn't handle
     the input.
   - Wait with `context.Settle()` (queue idle, no pending choice, overlay stable) or `context.WaitUntil(...)`.
     Never sleep.
   - Assert with `context.Expect(condition, "Expected …, found …")`.
   - Call `await context.Checkpoint("label")` at meaningful UI states. It runs the layout rules and the baseline
     compare. **Always await it**: CS4014 is a build error, so the compiler enforces this.
4. Put area helpers in their own partial, `Scripts/Testing/CouchTestContext.<Area>.cs`:
   - Call `ThrowIfCancelled()` first, and pass `CancellationToken` to waits.
   - Reflection is only for *reading* mod or game state. Look fields up when they're used, not in static
     initializers (a throwing initializer breaks every scenario), and with literal names, so Layer A checks them.
   - After moving a panel cursor, assert it landed where you meant before pressing Accept.
5. Determinism (plan §6.6):
   - Place cards you depend on with `card`; don't rely on draws.
   - Pick by ID or a stable order.
   - Assert seeded values relatively ("gold rose by the reward's amount").
6. Prove it:
   - Two launches must give identical `results.json` apart from `durationMs` (or use `--repeat 3`).
   - Temporarily disable the mod patch that guarantees the behavior, show the scenario fails with a clear message,
     then revert.
7. Never weaken an assertion to get a pass. A scenario that exposes a mod bug has done its job.

### Game quirks worth knowing
- Instant fast mode means P2's panels can open and close in one frame. Add a checkpoint while a panel is open if
  you want it in a `--review` screenshot.
- Taking a teammate's last reward closes their panel by itself.
- The `damage` console command only works in combat.

## Layout baselines

The **layout rules** below decide pass/fail. They encode intent, so they survive intentional tweaks, game patches
and seed changes.

**Baselines** are a drift report, not a gate. They live in `Tests/layout-baselines/<main|beta>/<scenario>/<checkpoint>@<16x9|16x10>.json`
(committed) and hold the whole-pixel rects of the mod UI roots and a few game anchors. A move over 4 px, or a node
appearing or disappearing, is listed under "Layout drift" in `summary.txt` but doesn't fail the run. Pass
`--strict-layout` to make drift fail (e.g. before a release). `--repeat` always compares snapshots between passes,
because that's a determinism check.

A baseline records "what it looked like", not "what's correct", so bless only after a human has looked:
1. Run `./deploy.sh test <scenario> --bless --review`. This writes the proposed baselines (uncommitted) and a
   `contact-sheet.png` of every checkpoint.
2. Look at the contact sheet. The screens themselves must be right, not just rule-clean.
3. Run it twice without `--bless`. Both runs must report no drift.
4. Commit the baselines. Review baseline diffs in git like code.

The layout rules (every checkpoint, both aspects):
- Mod UI is inside the viewport and has non-zero size.
- It doesn't overlap P1's relic row, top bar or end-turn button, enemy intents, or other visible mod panels. The by-design
  exceptions are listed with reasons in `CouchTestLayout.AllowedOverlaps`.
- P2's combat relic row is vertically centered on P2's status line (±4 px).
- The HUD header isn't above the players list's top.

## Reference

Ground-truth facts the harness relies on (checked against decompiled game source, `src/`), for anyone extending
the runner or writing a new scenario.

**Launching outside Steam, with isolated saves**
- `--force-steam off` skips Steam init. Saves then go to `user://default/<id>/…` instead of `user://steam/<steamid>/…`
  (`user://` is `~/Library/Application Support/SlayTheSpire2/` on macOS, `~/.local/share/SlayTheSpire2/` on Linux).
- The null platform's player id is 1 unless `--clientId` is given, so the test tree is `…/SlayTheSpire2/default/1/`.
- **Mod consent gotcha:** `settings.save` is account-scoped and holds `ModSettings.PlayerAgreedToModLoading` plus the
  per-mod enable list. A fresh `default/1/` has no consent, so `ModManager` skips every mod. `deploy.sh test` fixes
  this by seeding `default/1/settings.save` from the real `steam/<steamid>/settings.save` the first time.
- The game's own env switches: `STS2_DEV_WINDOWED` forces windowed mode, `STS2_DEV_SKIP` skips the intro logo.

**Dev console as the scenario setup tool**
- `NDevConsole.Instance.ProcessNetCommand(Player?, string)` runs a command immediately as that player.
- Per-player commands: `gold`, `card <id> [pile]`, `relic [add|remove] <id>`, `potion <id>`, `heal`, `die`.
- Global commands: `room <RoomType>`, `event <ID>`, `act <n>`, `win`, `kill`.
- **`fight <id>` reseeds from the wall clock and `godmode` shares state across players — both are banned** in
  scenarios; they break determinism.

**Seeds** — `NGame.Instance.DebugSeedOverride` wins over the lobby seed. `NCharacterSelectScreen.AfterInitialized`
clears it, so set it right before Embark.

**Idle and state signals** — `RunManager.Instance.ActionQueueSet.IsEmpty`/`BecameEmpty()` and
`ActionExecutor.IsRunning` for idleness; `RunManager.Instance.DebugOnlyGetState()` for run/player state;
`CombatManager.Instance` for combat state and events; `NOverlayStack.Instance` for UI state; `Log.LogCallback`
for a global log-line event feed.

**Mod entry points a scenario drives through** — `NMultiplayerHostSubmenuPatch.OnLocalSelfCoopPressed` (starting a
couch run), `LocalSelfCoopContext.SetLobbyEditingPlayer`/`NCharacterSelectScreen.SelectCharacter` (character picks),
`CouchTeammateUi.Handle` (P2's HUD input), `CouchRemotePlay` (P2's combat actions), `CouchTeammateChoices` (P2's
choices), `LocalControlRuntime.SwitchControlledPlayerTo` (a driver swap), `CouchScreenshots` (screenshots).

**Deliberately not used** (each would bypass what the tests are meant to catch):
- `TestMode.IsOn` — flips reward code to auto-select paths, skipping the mod's real choice patches.
- `CardSelectCmd.UseSelector` — a global selector that answers every player's choices, skipping the choice synchronizer.
- `NonInteractiveMode.AutoSlayerCheck = () => true` — makes action-state errors throw and skips combat pauses.
- Patching `NGame.IsReleaseGame` — may be JIT-inlined, and changes unrelated behavior.
- AutoSlay's `CombatRoomHandler` — plays cards through `CardCmd.AutoPlay`, which skips energy and the action queue.
- The `fight` and `godmode` console commands (see above).

# AGENTS.md — CouchSpire Collaboration Rules

Rules for automated coding agents (and humans) working in this repository. Goal: changes that are stable, verifiable, and easy to roll back.

## 1. Scope & hard constraints

- Modify only mod code and mod metadata: `Scripts/`, `Tests/`, `*.csproj`, `*.json`, `*.cfg.example`, `deploy.sh`, docs.
- `src/` is decompiled game source — **read-only reference, never committed** (gitignored). Regenerate it after each game patch (see §5).
- No destructive git operations (`reset --hard`, force-push, `checkout --` over user changes).
- Language: **English** for all new code comments, commits, logs, and documentation.
- Commit after each logical change with a clear message.

## 2. Build, format, deploy

Run from the repo root:

```bash
dotnet restore LocalMultiControl.csproj
dotnet build LocalMultiControl.csproj -c Debug     # or -c Release for shipping
dotnet format LocalMultiControl.csproj --verify-no-changes
dotnet test Tests/CouchSpire.Tests                 # Layer A: offline Harmony/AccessTools target check (docs/design/testing-plan.md §5)
```

- The build copies the DLL to the repo root: `CouchSpire.dll`. **Always deploy/ship the root artifact**, not `.godot/mono/temp/...`.
- Deploy with `./deploy.sh mac` or `./deploy.sh bazzite [user@host]`: it builds, then installs `CouchSpire.dll` + `CouchSpire.json` (+ `CouchSpire.cfg` if present) into `<game>/mods/CouchSpire/`. No pck export — this is a dll-only mod.
- If the copy fails with *permission denied*, the game is running and holds the DLL lock; retry after it closes.

## 3. Runtime verification

- Log file: `%APPDATA%\SlayTheSpire2\logs\godot.log` (Windows), `~/.local/share/SlayTheSpire2/logs/godot.log` (Linux); `./deploy.sh logs` follows the remote log.
- Log via `Log.Info` with the unified prefix `[LocalMultiControl]` (`Log.Debug` is invisible by default). Add logs for anything you fix.
- On startup the mod logs `Applying Harmony patches.` → build marker → `Mod initialized.`; any Harmony exception between those lines means a patch target broke.
- Automated tests: `dotnet test Tests/CouchSpire.Tests` (offline patch-target check) after every build, and `./deploy.sh test all` (in-game scenarios, macOS) before asking for a playtest or pushing. See `docs/testing.md`. Run `./deploy.sh mac` afterwards to restore the normal build.
- Only `./deploy.sh test` may launch the game for tests: it holds a lock, resets only the `default/1/` test profile, and never touches `steam/` saves. Don't kill a game you didn't start.
- The maintainer still playtests what scenarios don't cover. Provide focused, step-by-step test scripts and read the log after each round.

## 4. Harmony & domain conventions

- Prefer `Postfix` for added behavior, `Prefix` for guards; keep hot-path patches lightweight (no heavy reflection or allocation per frame).
- Control-switch and choice-submission paths must be **idempotent** — repeated triggers must not corrupt ordering or state.
- Never let local mirror/UI state pollute the authoritative game state (run state, piles, synchronizers).
- Patch naming: `PrefixXxx` / `PostfixXxx`; file per game type/scene under `Scripts/Patch/`.

## 5. Game-patch adaptation playbook

When the game updates and the mod breaks:

1. Note the new version from `<game>\release_info.json`.
2. Regenerate decompiled reference source:
   ```bash
   dotnet tool install -g ilspycmd --version 9.1.0.7988   # newer majors may fail to install
   ilspycmd -p --nested-directories -o ~/sts2-src "<game>/data_sts2_windows_x86_64/sts2.dll"
   cp -r ~/sts2-src/MegaCrit/Sts2/. src/
   ```
3. Build; fix compile errors using the decompiled source as ground truth (compile errors = renamed/removed members).
4. Run `dotnet test Tests/CouchSpire.Tests` — it validates every **string-based** Harmony/`AccessTools` target against the current `sts2.dll` without starting Godot, replacing the old manual sweep against the decompiled tree. It names the mod type/method, the target string, and the game type searched for anything that fails to resolve.
5. Fix pattern: call the **new** member name first, with a reflection fallback to the old name (see `InvokeBeginRunIfAllPlayersReady` in `Scripts/Patch/LoadRunLobbyPatch.cs`).
6. Record every fixed breakage in `CHANGELOG.md`.

## 6. Code style

- `using` order: system → third-party → project namespaces; remove unused.
- File-scoped namespaces: `namespace LocalMultiControl.Scripts.Patch;`
- Types/methods/properties `PascalCase`; locals/params `camelCase`; private fields `_camelCase`.
- Explicit types over `var`; `<Nullable>enable</Nullable>` — handle null branches.
- Custom Godot node subclasses must be `partial` (source generators).

## 7. Release flow

Versioning is automated by `semantic-release` (`.releaserc.json`) on a self-hosted GitHub
Actions runner — it has no game install, so it only handles versioning, not building:

1. Write commits using [Conventional Commits](https://www.conventionalcommits.org/) (`fix:`,
   `feat:`, `feat!:`/`BREAKING CHANGE:`, `docs:`, `chore:`, ...) — the commit-analyzer plugin
   decides the version bump from these. Update `COUCH.md` / `PLAYER_GUIDE.md` if player-facing
   behavior changed. Do **not** hand-edit `CouchSpire.json`'s `version` or the `Entry.cs` build
   marker's `CouchSpire X.Y.Z` prefix — CI does that automatically on merge.
2. `CONFIG=Release ./deploy.sh ...` and playtest the installed build locally.
3. `make pr` (build + format-check, then opens a PR to `master`).
4. Merge the PR. This triggers the `Release` workflow: semantic-release computes the version,
   updates `CHANGELOG.md` and the version fields above, commits that back to `master`, tags it,
   and creates the GitHub Release (notes only — no DLL asset yet).
5. `make attach-release` (locally, on `master`): builds the real DLL and attaches it to the
   Release CI just created.
6. `tools/upload-steam-workshop.sh vX.Y.Z` (locally) to push it to the Steam Workshop — see
   `steam-workshop/README.md`. Not part of CI: SteamCMD needs a Steam Guard session the runner
   doesn't have.

To update the Workshop listing's compatibility note without a new release (e.g. confirming the
existing build still works on a new game patch), edit `steam-workshop/verified-versions.txt`,
commit with a `docs:`/`chore:` message (no version bump), and re-run step 6 for the current tag.

## 8. Documentation map

- `README.md` — project front door; `COUCH.md` — couch co-op setup, settings, tests; `PLAYER_GUIDE.md` — how a run plays; `CHANGELOG.md` — history; `TODO.md` — open issues.
- `docs/architecture.md`, `docs/console-commands.md`, `docs/testing.md` (running and writing tests), `docs/design/*` — developer docs.

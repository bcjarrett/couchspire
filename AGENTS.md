# AGENTS.md — CouchSpire Collaboration Rules

Rules for automated coding agents (and humans) working in this repository. Goal: changes that are stable, verifiable, and easy to roll back.

## 1. Scope & hard constraints

- Modify only mod code and mod metadata: `Scripts/`, `Tests/`, `*.csproj`, `*.json`, `*.cfg.example`, `deploy.sh`, `Makefile`, `tools/`, `.github/`, docs.
- `src/` (main game branch) and `src-beta/` (beta game branch) are decompiled game source — **read-only reference, never committed** (gitignored). Regenerate them after each game patch (see §5).
- No destructive git operations (`reset --hard`, force-push, `checkout --` over user changes).
- Language: **English** for all new code comments, commits, logs, and documentation.
- Commit after each logical change with a clear message.
- **Work on a branch in your own git worktree, never directly on `master`** or in the shared main checkout. Several
  agent sessions often run against this repo at once and switch its branch under each other, so a commit made there
  can land on someone else's branch. Start each task with
  `git fetch origin && git worktree add .claude/worktrees/<topic> -b <type>/<topic> origin/master` (`.claude/` is
  gitignored), commit there, and open a PR from that branch (§7). Gitignored files aren't in a new worktree: read
  `src/`/`src-beta/` from the main checkout, and copy `CouchSpire.cfg` over if you need it. Remove the worktree
  (`git worktree remove`) once the PR is merged.

## 2. Build, format, deploy

Run from the repo root:

```bash
dotnet restore CouchSpire.csproj
dotnet build CouchSpire.csproj -c Debug -p:GameTarget=main   # or beta; or -c Release for shipping
GameTarget=main dotnet format CouchSpire.csproj --verify-no-changes
dotnet test Tests/CouchSpire.Tests -p:GameTarget=main        # Layer A: offline Harmony/AccessTools target check (docs/testing.md)
make check                                                   # all of the above, for both game branches
```

- The mod supports two Steam game branches at once, main and beta (see §5). `GameTarget` (default `main`) picks
  which one a build is for; `make check` and `tools/package.sh` always do both.

- The build copies the DLL to the repo root: `CouchSpire.dll`. **Always deploy/ship the root artifact**, not `.godot/mono/temp/...`.
- Deploy with `./deploy.sh local` (macOS or Linux) or `./deploy.sh remote [user@host]` (a Linux machine, e.g. a Steam
  Deck or Bazzite box, over SSH): it builds, then installs `CouchSpire.dll` + `CouchSpire.json` (+ `CouchSpire.cfg`
  if present) into `<game>/mods/CouchSpire/`. No pck export — this is a dll-only mod. Override the local game
  install dir with `STS2_DIR` (matches `Sts2Paths.props`' `Sts2Dir`). It builds for whichever game branch that
  install is on, detected from its `release_info.json` (override with `GAME_TARGET=main|beta`).
- If the copy fails with *permission denied*, the game is running and holds the DLL lock; retry after it closes.

## 3. Runtime verification

- Log file: `%APPDATA%\SlayTheSpire2\logs\godot.log` (Windows), `~/Library/Application Support/SlayTheSpire2/logs/godot.log` (macOS), `~/.local/share/SlayTheSpire2/logs/godot.log` (Linux); `./deploy.sh logs` follows the remote log.
- Log via `Log.Info` with the unified prefix `[CouchSpire]` (`Log.Debug` is invisible by default). Add logs for anything you fix.
- On startup the mod logs `Applying Harmony patches.` → build marker → `Mod initialized.`; any Harmony exception between those lines means a patch target broke.
- Automated tests: `dotnet test Tests/CouchSpire.Tests` (offline patch-target check) after every build, and `./deploy.sh test all` (in-game scenarios, macOS only for now) before asking for a playtest or pushing. See `docs/testing.md`. Run `./deploy.sh local` afterwards to restore the normal build.
- Only `./deploy.sh test` may launch the game for tests: it holds a lock, resets only the `default/1/` test profile, and never touches `steam/` saves. Don't kill a game you didn't start.
- The maintainer still playtests what scenarios don't cover. Provide focused, step-by-step test scripts and read the log after each round.

## 4. Harmony & domain conventions

- Prefer `Postfix` for added behavior, `Prefix` for guards; keep hot-path patches lightweight (no heavy reflection or allocation per frame).
- Control-switch and choice-submission paths must be **idempotent** — repeated triggers must not corrupt ordering or state.
- Never let local mirror/UI state pollute the authoritative game state (run state, piles, synchronizers).
- Patch naming: `PrefixXxx` / `PostfixXxx`; file per game type/scene under `Scripts/Patch/`.

## 5. Game branches and game-patch adaptation

The mod ships for two Steam game branches at once: **main** (the default branch, what most players have) and
**beta** (opt-in, months ahead of main). One codebase builds both:

- Code that differs between the branches lives in `Scripts/Compat/Main/` and `Scripts/Compat/Beta/`; the csproj
  compiles only the folder for `GameTarget`. Shared code calls `GameCompat` (same members in both folders) and never
  uses `#if`. A patch whose target changed shape lives whole in each folder (e.g. `CombatManagerReadyEnemyTurnPatch`);
  a type rename is a `global using` alias in `Compat/Beta/GlobalUsings.cs`; a patch type can be `partial` with its
  per-branch piece in the folders (`GamescopeFocusPatch.Callers`).
- Each build compiles against a snapshot of that branch's game assemblies in `~/sts2-ref/<branch>/`, so both build
  whichever branch Steam has installed. Take a snapshot with Steam on that branch: `make snapshot TARGET=main|beta`.
- In-game tests (`./deploy.sh test`) run against whatever Steam has installed; switch branches in Steam to test the
  other. Layout baselines are per branch: `Tests/layout-baselines/<branch>/`.
- When the beta goes live on main: move `Compat/Beta/` over `Compat/Main/`, delete the beta Workshop item's files in
  `steam-workshop/beta/` (after telling its subscribers), and drop `beta` from `GAME_TARGETS` in the Makefile.

When a game patch lands on either branch and the mod breaks:

1. Switch Steam to that branch and note the new version from `release_info.json` (`./deploy.sh` refuses to build
   when the installed game matches neither snapshot — that's usually the first sign).
2. Refresh the snapshot and the decompiled reference (`src/` for main, `src-beta/` for beta):
   ```bash
   make snapshot TARGET=beta
   dotnet tool install -g ilspycmd --version 9.1.0.7988   # newer majors may fail to install; needs the .NET 8 runtime
   ilspycmd -p --nested-directories -o ~/sts2-src-beta ~/sts2-ref/beta/sts2.dll
   rm -rf src-beta && mkdir src-beta && cp -r ~/sts2-src-beta/MegaCrit/Sts2/. src-beta/
   ```
3. Build that branch (`-p:GameTarget=beta`); fix compile errors using its decompiled source as ground truth (compile
   errors = renamed/removed members). If the fix differs from the other branch, it goes in that branch's compat folder.
4. Run `dotnet test Tests/CouchSpire.Tests -p:GameTarget=<branch>` — it validates every **string-based**
   Harmony/`AccessTools` target against that branch's `sts2.dll` without starting Godot. It names the mod type/method,
   the target string, and the game type searched for anything that fails to resolve.
5. Run `./deploy.sh test all` with Steam on that branch, then `make check` to confirm the other branch still builds.
6. Commit as `fix:` naming the game version; semantic-release puts it in `CHANGELOG.md`. Update the branch's
   `verified-versions.txt` (`steam-workshop/` or `steam-workshop/beta/`).

## 6. Code style

- `using` order: system → third-party → project namespaces; remove unused.
- File-scoped namespaces: `namespace CouchSpire.Scripts.Patch;`
- Types/methods/properties `PascalCase`; locals/params `camelCase`; private fields `_camelCase`.
- Explicit types over `var`; `<Nullable>enable</Nullable>` — handle null branches.
- Custom Godot node subclasses must be `partial` (source generators).

## 7. Release flow

Versioning is automated by `semantic-release` (`.releaserc.json`) on a hosted GitHub Actions
runner (`ubuntu-latest`) — it has no game install, so it only handles versioning, not building:

1. Write commits using [Conventional Commits](https://www.conventionalcommits.org/) (`fix:`,
   `feat:`, `feat!:`/`BREAKING CHANGE:`, `docs:`, `chore:`, ...) — the commit-analyzer plugin
   decides the version bump from these. Update `docs/couch-coop.md` / `docs/player-guide.md` if
   player-facing behavior changed. Do **not** hand-edit `CouchSpire.json`'s `version` or the
   `Entry.cs` build marker's `CouchSpire X.Y.Z` prefix — CI does that automatically on merge.
2. `CONFIG=Release ./deploy.sh ...` and playtest the installed build locally. Before a release, run
   `./deploy.sh test all` on **both** game branches (switch branches in Steam between runs; §5).
3. Push your branch, then `make pr` from its worktree (build + format-check + Layer A tests for both game branches,
   then opens a PR to `master`; §1).
4. Merge the PR. This triggers the `Release` workflow: semantic-release computes the version,
   updates `CHANGELOG.md` and the version fields above, commits that back to `master`, tags it,
   and creates the GitHub Release (notes only — no DLL asset yet).
5. `make attach-release` (locally, on `master`): builds both game branches' DLLs and attaches
   `CouchSpire-vX.Y.Z.zip` (main) and `CouchSpire-beta-vX.Y.Z.zip` (beta) to the Release CI just
   created. Needs both snapshots in `~/sts2-ref/` (§5).
6. `make steam-upload TAG=vX.Y.Z TARGET=main` and again with `TARGET=beta` (locally) to push each
   zip to its own Workshop item — see `steam-workshop/README.md`. Not part of CI: SteamCMD needs a
   Steam Guard session a CI runner doesn't have.

To update the Workshop listing's compatibility note without a new release (e.g. confirming the
existing build still works on a new game patch), edit `steam-workshop/verified-versions.txt` (or
`steam-workshop/beta/verified-versions.txt`), commit with a `docs:`/`chore:` message (no version
bump), and re-run step 6 for the current tag and that target.

## 8. Documentation map

- `README.md` — project front door; `docs/couch-coop.md` — couch co-op setup, settings, tests; `docs/player-guide.md` — how a run plays; `CHANGELOG.md` — history; [GitHub Issues](https://github.com/bcjarrett/couchspire/issues) — open issues.
- `docs/architecture.md`, `docs/console-commands.md`, `docs/testing.md` (running and writing tests, plus a reference appendix for the harness) — developer docs.

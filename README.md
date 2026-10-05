# CouchSpire

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/bcjarrett/couchspire?include_prereleases)](https://github.com/bcjarrett/couchspire/releases)

A **Slay the Spire 2** mod for two-player couch co-op on one screen. Each controller drives its
own character.

## Features

- Per-controller seats: each controller picks and drives its own character. Press a button on
  your controller to take over when your teammate isn't mid-action.
- Simultaneous play in combat, with a teammate HUD (P2 top bar, relics, hand) that uses the
  game's own art.
- Per-character decks, gold, potions, relics and choices; rewards, shops, rest sites, treasure
  and events are all handled per player instead of mirrored or shared.

## Requirements

- A Slay the Spire 2 install (Steam).
- Two controllers (or one controller plus the keyboard — see [docs/couch-coop.md](docs/couch-coop.md)).

## Install

**Players:** download the latest release zip from [GitHub Releases](https://github.com/bcjarrett/couchspire/releases)
(Steam Workshop listing: coming soon), then extract it, or manually copy `CouchSpire.dll` and
`CouchSpire.json` into your game's mods folder:

| OS | Mods folder |
|---|---|
| Windows | `<Steam>\steamapps\common\Slay the Spire 2\mods\CouchSpire\` |
| macOS | `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/CouchSpire/` |
| Linux | `~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/CouchSpire/` |

Launch the game, enable **CouchSpire** under `Settings → Mods`, and restart if prompted.

## Usage

- [docs/couch-coop.md](docs/couch-coop.md): couch co-op setup, settings, controls, and test scripts.
- [docs/player-guide.md](docs/player-guide.md): how a run plays, keyboard reference.

## Compatibility

Each release has two builds, one per Steam game branch: `CouchSpire-vX.Y.Z.zip` for the default
(main) branch, currently game v0.107.1, and `CouchSpire-beta-vX.Y.Z.zip` for the beta branch,
currently game v0.111.0. On the Steam Workshop they're two separate items; subscribe to the one
matching your game branch. Don't enable CouchSpire together with the Workshop DualRoleAdventure
mod: both patch the same code.

## Building from source

Requirements: .NET SDK 9 (`brew install dotnet@9` on macOS, or your distro's `dotnet-sdk-9.0`
package on Linux).

```bash
./deploy.sh local                 # build and install into the local game (macOS or Linux)
./deploy.sh remote user@host      # build and copy to a remote machine (e.g. a Steam Deck or Bazzite box) over SSH
./deploy.sh logs user@host        # follow the couch lines of the remote game log
```

The game path is detected per OS in `Sts2Paths.props`. Override it with `-p:Sts2Dir=...` (or the
`STS2_DIR` environment variable for `deploy.sh`). A manual install copies `CouchSpire.dll` and
`CouchSpire.json` to `<game>/mods/CouchSpire/`.

A build targets one Steam game branch: `-p:GameTarget=main` (default) or `beta`. `deploy.sh` picks the
branch your installed game is on. To build both regardless of which one Steam has installed, save each
branch's game assemblies once with Steam on that branch: `make snapshot TARGET=main` / `TARGET=beta`
(see AGENTS.md §5).

Style gate and offline tests, both required before a PR, for both game branches:

```bash
make check   # Release build + dotnet format + Layer A (offline Harmony/AccessTools target check), main and beta
```

See [docs/testing.md](docs/testing.md) for the full test setup, including the in-game scenario
runner (macOS only for now).

For game-API reference, decompile `sts2.dll` into `src/` (main branch) or `src-beta/` (beta). Both are
gitignored and read-only:

```bash
dotnet tool install -g ilspycmd --version 9.1.0.7988
ilspycmd -p --nested-directories -o /tmp/sts2-src "<game data dir>/sts2.dll"
cp -r /tmp/sts2-src/MegaCrit/Sts2/. src/
```

## Contributing

Issues and pull requests are welcome. Use [Conventional Commits](https://www.conventionalcommits.org/)
for commit messages (they drive the automated release versioning) — see [AGENTS.md](AGENTS.md)
for the full contributor/agent conventions, including the game-patch adaptation playbook for when
a Slay the Spire 2 update breaks the mod.

## Documentation

- [docs/couch-coop.md](docs/couch-coop.md): couch co-op setup, settings, controls, tests
- [docs/player-guide.md](docs/player-guide.md): how a run plays, keyboard reference
- [CHANGELOG](CHANGELOG.md): release history
- [GitHub Issues](https://github.com/bcjarrett/couchspire/issues): known issues and feature requests
- [docs/architecture.md](docs/architecture.md): how the mod works internally
- [docs/console-commands.md](docs/console-commands.md): dev-console commands for testing
- [docs/testing.md](docs/testing.md): running and writing automated tests (patch-target check, in-game scenarios)

## Releasing

`make pr` builds, format-checks, tests, and opens a release PR; merging it triggers
`semantic-release` on CI to version, tag, and cut the GitHub Release. See
[AGENTS.md §7](AGENTS.md#7-release-flow) for the full flow, including attaching the build and
publishing to the Steam Workshop.

## License

MIT — see [LICENSE](LICENSE).

## Credits

Based on Local Multi-Control (DualRoleAdventure) by liwenhao0427 and GuyGinat. Thanks to them for the loopback-networking foundation this mod builds on, and for kindly agreeing to let it be published as a separate, MIT-licensed project.


# CouchSpire

A **Slay the Spire 2** mod for couch co-op on one screen. Each controller drives its own character in a local multi-character run. The game's real multiplayer flow runs underneath, with no networking.

CouchSpire is built on Local Multi-Control, which lets one player run **2–12 characters** on a single machine. On top of that, CouchSpire routes each controller to its own seat and lets teammates act at the same time.

## Features

- Per-controller seats: each controller picks and drives its own character. Press a button on your controller to take over when your teammate isn't mid-action
- Simultaneous play in combat, with a teammate HUD (P2 top bar, relics, hand) that uses the game's own art
- Everything from Local Multi-Control: 2–12 characters, `Tab` / `Shift+Tab` switching, per-character decks, gold, potions, relics and choices, Vakuu AI auto-play, and the `F8` ghost-hands overlay
- Pure code mod: `has_dll=true`, `has_pck=false`

See **[COUCH.md](COUCH.md)** for setup, settings, controls and test scripts, and the **[Player Guide](PLAYER_GUIDE.md)** for the underlying multi-character gameplay.

## Compatibility

Built against game version **v0.111.0** (August 2026). Don't enable CouchSpire together with the Workshop DualRoleAdventure mod: both patch the same code.

## Building and installing

Requirements: .NET SDK 9 (`brew install dotnet@9` on macOS) and a Slay the Spire 2 install.

```bash
./deploy.sh mac                   # build and install into this Mac's game
./deploy.sh bazzite user@bazzite  # build and copy to a Linux/Bazzite box over SSH
./deploy.sh logs user@bazzite     # follow the couch lines of the remote game log
```

The game path is detected per OS in `LocalMultiControl.csproj`. Override it with `-p:Sts2Dir=...`. A manual install copies `CouchSpire.dll` and `CouchSpire.json` to `<game>/mods/CouchSpire/`.

Style gate:

```bash
dotnet format LocalMultiControl.csproj --verify-no-changes
```

For game-API reference, decompile `sts2.dll` into `src/`. It is gitignored and read-only:

```bash
dotnet tool install -g ilspycmd --version 9.1.0.7988
ilspycmd -p --nested-directories -o /tmp/sts2-src "<game data dir>/sts2.dll"
cp -r /tmp/sts2-src/MegaCrit/Sts2/. src/
```

## Documentation

- [COUCH.md](COUCH.md): couch co-op setup, settings, controls, tests
- [Player Guide](PLAYER_GUIDE.md): multi-character gameplay and hotkeys
- [CHANGELOG](CHANGELOG.md): release history
- [TODO](TODO.md): known issues
- [docs/architecture.md](docs/architecture.md): how the mod works internally
- [docs/console-commands.md](docs/console-commands.md): dev-console commands for testing
- [docs/design/](docs/design/): design documents

## Credits

Based on Local Multi-Control (DualRoleAdventure) by liwenhao0427 and GuyGinat.

# CouchSpire

A **Slay the Spire 2** mod for two-player couch co-op on one screen. Each controller drives its own character. The game's real multiplayer flow runs underneath, with no networking.

## Features

- Per-controller seats: each controller picks and drives its own character. Press a button on your controller to take over when your teammate isn't mid-action
- Simultaneous play in combat, with a teammate HUD (P2 top bar, relics, hand) that uses the game's own art
- Per-character decks, gold, potions, relics and choices; rewards, shops, rest sites, treasure and events handled per player
- Pure code mod: `has_dll=true`, `has_pck=false`

See **[COUCH.md](COUCH.md)** for setup, settings, controls and test scripts, and the **[Player Guide](PLAYER_GUIDE.md)** for how a run plays.

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
- [Player Guide](PLAYER_GUIDE.md): how a run plays, keyboard reference
- [CHANGELOG](CHANGELOG.md): release history
- [TODO](TODO.md): known issues
- [docs/architecture.md](docs/architecture.md): how the mod works internally
- [docs/console-commands.md](docs/console-commands.md): dev-console commands for testing
- [docs/design/](docs/design/): design documents

## License

MIT — see [LICENSE](LICENSE).

## Credits

Based on Local Multi-Control (DualRoleAdventure) by liwenhao0427 and GuyGinat. Thanks to them for the loopback-networking foundation this mod builds on, and for kindly agreeing to let it be published as a separate, MIT-licensed project.

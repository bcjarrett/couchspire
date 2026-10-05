# Player Guide

Two players, one screen, one controller each. CouchSpire runs a real two-player Slay the Spire 2 multiplayer run inside one game, with no network. Setup, settings and the full controller layout are in [Couch co-op setup](couch-coop.md).

## Install & enable

1. Install with `deploy.sh`, or place `CouchSpire.dll` + `CouchSpire.json` in `<game>/mods/CouchSpire/` (see [README.md](../README.md)).
2. Launch the game → `Settings → Mods` → enable **CouchSpire**. Restart if prompted.

## Starting a run

1. Main menu → **Multiplayer → Host**.
2. Pick the **Couch Co-op** card (next to Standard / Daily / Custom).
3. The controller you used in the menus is **P1**. Pressing a button on the second controller makes it **P2**. Each player picks a character on their own controller; duplicates are allowed.
4. Start the run as usual.

## During a run

- Each character has their own deck, hand, energy, gold, potions, relics and choices.
- **Combat:** P1 plays on the main screen; P2 plays at the same time from the teammate HUD. Each player ends their own turn.
- **Enemy attack numbers:** when the players would take different damage (e.g. one is Intangible), the intent shows both in seat order: `1 / 12` means P1 takes 1, P2 takes 12.
- **Rewards, shops, rest sites, treasure, events:** P1 uses the main screen, P2 uses their own panel. Purchases and card removal are billed to whoever makes them.
- **Map:** picking the next node completes the "everyone votes" step.
- **Taking the main screen:** P2 clicks a stick in (L3/R3); on the keyboard, press `Tab`.
- **Save & continue:** quit normally; `Multiplayer → Load` resumes the run.

## Keyboard reference

| Key | Context | Action |
|---|---|---|
| `Tab` | character select | switch which player's pick you're editing |
| `Tab` | in a run | hand the main screen to the other character |
| `F9` | anywhere | forget controller bindings |
| `F10` | anywhere | debug overlay |
| `F11` | anywhere | screenshot to `couch_shots/` |

P2's keyboard block (for testing without a second controller) is listed in [Couch co-op setup](couch-coop.md).

## Troubleshooting

- Log file: `%APPDATA%\SlayTheSpire2\logs\godot.log` on Windows, `~/.local/share/SlayTheSpire2/logs/godot.log` on Linux. Mod lines are prefixed `[CouchSpire]` and `[Couch]`.
- If something breaks, note the **act, room/screen, and exact steps**, and include the log when you report it.
- Known issues are tracked on the project's [GitHub Issues](https://github.com/bcjarrett/couchspire/issues) page.

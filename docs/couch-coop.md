# CouchSpire: couch co-op on one screen

CouchSpire runs a two-character multiplayer run in one game, with **per-controller routing**: each controller drives its own character. To take over, press a button on your controller; if your teammate is in the middle of something (playing or targeting a card, picking cards) or it's the enemy turn, the press is ignored and you try again.

## Install

The build machine needs the .NET 9 SDK: `brew install dotnet@9` (macOS) or your distro's `dotnet-sdk-9.0` package (Linux).

```bash
./deploy.sh local                  # build and install into the local game (macOS or Linux)
./deploy.sh remote user@host       # copy to a Linux machine (e.g. a Steam Deck or Bazzite box) over SSH
./deploy.sh logs user@host         # follow the couch lines of the remote game log
```

- **Remote SSH:** enable it once on the remote machine with `sudo systemctl enable --now sshd`. If the game isn't in `~/.local/share/Steam`, set `STS2_REMOTE_DIR` to its install folder, relative to your home directory.
- **Remove the original mod:** unsubscribe from or disable the Workshop DualRoleAdventure mod. Both patch the same code.
- **First modded launch:** the game asks whether to load mods; say yes, then restart.
- **Check it loaded:** the log should contain `CouchSpire 0.1.0 ... loaded` and `[Couch] Couch input gate attached.`, with no Harmony exception between them.

## Settings

Put settings in `CouchSpire.cfg` in the repo root: copy `CouchSpire.cfg.example` and edit it. `deploy.sh` installs it next to the mod, and the startup log line `[Couch] Couch co-op loaded (...; settings from ...)` shows what was read.

| Setting | Effect |
|---|---|
| `probe = 1` | Verbose input diagnostics, and the overlay at startup |
| `routing = 0` | Turn couch routing off: one screen, switch characters with Tab or by right-clicking a character in the players list |
| `overlay = 1` | Only the overlay |
| `simultaneous = 0` | In combat, go back to hotseat behavior: auto-switch after end turn, and the teammate's choices on the main screen |

- **Linux (Steam Deck/Bazzite Game Mode):** the same switches also work as Steam launch options, e.g. `COUCHSPIRE_PROBE=1 %command%`, and they override the file.
- **Mac:** leave Steam launch options empty. Steam for Mac doesn't run them through a shell, so that form fails with "OS Error 260".

Keyboard keys, for testing:
- **F10:** toggle the debug overlay (routing state, seats, controllers, driver, recent decisions).
- **F9:** forget all controller bindings.
- **Tab:** hand the main screen to the other character (on character select: the other player's pick).
- **F7 / Shift+F7 / F6 / F5:** the simultaneous-play test (test D below). The teammate who isn't driving plays a card, ends their turn, or answers a pending choice, without taking the screen.

## Playing

1. Main menu → Multiplayer → Host → **Couch Co-op** → each controller picks its character (on the keyboard, Tab switches which pick you're editing) → each player presses Embark; the run starts once both have.
2. The controller used in the menus becomes **P1**. The first press from the other controller binds it as **P2** and takes control if it's allowed right now.
3. After that, press any button (not a stick or the d-pad) to take control of your own character. Ending your turn hands control to the teammate automatically.

## Tests (on a Linux machine in gamescope/Game Mode, 2 controllers)

Run each test, then send back the `[Couch]` log lines (`./deploy.sh logs`, or `~/.local/share/SlayTheSpire2/logs/godot.log`).

**A. Base co-op works on Linux** (`routing = 0`): start a Couch Co-op run, press Tab to switch, play a combat.

**B. Can the game tell the controllers apart?** (`probe = 1`, `routing = 0`)
1. Wait on the main menu for the `===== Input environment (startup)` dump.
2. Press A, B, X, Y and the d-pad on controller 1, then on controller 2.
3. Repeat with Steam Input turned off for this game (controller settings in the game's Steam properties).

What the log answers:
- Does `Steam Input controllers:` list 2?
- Do `steam ... [observe only]` lines show two different `steam:` ids?
- Do `raw InputEventJoypadButton device=` lines appear, and with which device numbers?

**C. Routing playtest** (`probe = 1`, routing on):
1. Start a 2-character run. The overlay should show `Couch routing: ACTIVE`, with P1 bound to the menu controller.
2. In combat:
   - P2 presses A → the view swaps to P2 (`P2 took control`).
   - P1 presses A while P2 is dragging or targeting a card → ignored (`P1 can't take control yet`).
   - P1 plays a card, P2 swaps in and plays a card, P1 swaps back → no desync lines, no stuck buttons.
   - End turn with each player.
3. Outside combat: map vote from each controller; rewards, shop, rest site, and events from each controller.
4. Unplug P2's controller and plug it back in → the overlay shows it disconnect, then reconnect or rebind.
5. Save & quit → continue.

**D. Simultaneous play test** (keyboard + mouse, works on the Mac too)

Question: can P2 act while P1 keeps the screen? F7 sends P2's card play through the in-process fake network as P2's own request. That is how an online teammate's play reaches the host.
1. Start a Couch Co-op run and enter a combat. P2's hand shows in the HUD.
2. **F7:** P2 plays a Strike or Defend.
   - Expected: P1's hand and UI don't change, the card flies out of P2's character, and the enemy takes damage or P2 gains block.
   - Log lines: `[RemotePlay] ... left the hand, energy 3 -> 2`, then the game's own `Player <P2 id> playing card STRIKE_...`.
3. **Real simultaneity:** start targeting one of P1's cards and press F7 while it's held. P2's card should resolve without cancelling P1's targeting; then finish P1's play.
4. **Press F7 quickly several times:** P2's cards queue and resolve in order until they're out of energy (`no playable card`).
5. **F6:** P2 ends their turn; P1 keeps playing. P1 then ends their turn → the enemy turn starts, and **P1 keeps the screen** (no auto-swap). Pressing F6 again before P1 ends un-ends P2's turn.
6. **Shift+F7** on a card that asks for a choice (e.g. Survivor, "discard 1 card"):
   - P1's screen should not change. The overlay shows `! Teammate ... must choose: ...`, and P2's other queued plays wait.
   - Press **F5** to answer with P2's first option. The log shows `Teammate ... answers choice` followed by the game's own `Player <P2 id> chose cards [...]`. The chosen card must come from **P2's** hand.
7. Finish the combat and check the log for desync or checksum errors.

**E. Teammate HUD** (P2 plays with their own cursor while P1 keeps the screen)

P2's portrait, HP, gold and potions sit in the empty middle of the top bar all run, drawn like P1's. P2's relics are under the top bar on the right. In a combat, P2's controls sit in a band across the top of the screen, above the characters:
- a status line with the game's icons (P2's energy, stars for the Regent, draw and discard piles, turn ended);
- a key hint, which follows whether P2 last used keys or a pad;
- P2's hand, or the options of a choice. After P2 ends their turn the hand slides away, and comes back if they take the turn back.

The HUD fades while P1 has a hand selection, overlay, map or deck view open, or a tooltip for something in the top bar (P1's potions, relics). Move or resize it with `hud_x`, `hud_y`, `hud_scale` in `CouchSpire.cfg`; `p1_hand_scale` (default 0.85) sets P1's resting hand size and `p1_focus_scale` (default 0.65, the game uses 1) the size of P1's hovered or picked-up card; `p1_focus_drop` (pixels, default 0) lowers the hovered card.

| P2 action | Keyboard | Controller |
|---|---|---|
| Move the cursor (cards, potions, relics, targets, choice options) | J / L | D-pad or stick left/right |
| Pick up the card / potion, then play or use it (a second press, as P1's controller); confirm the target; pick a choice option | I | A (Cross) |
| Switch between the hand and the potion row (right past the last potion: P2's relics, with tooltips) | U | Up / Down |
| Put the card or potion back; leave the potion or relic row; clear the choice selection | K | B (Circle) |
| Confirm a multi-card choice; discard a potion (press twice) | O | X (Square) |
| End or un-end the turn | P | Y (Triangle); in a choice, Y confirms |
| P2's deck / relics view | V | LB (deck) / RB (relics); View toggles |

1. Play a Strike with P2: I, then J/L to move P1's style of targeting arrow between enemies, then I. P1 can be mid-play at the same time.
   - While aiming, the card is held up large and shows the damage against **that** enemy (Vulnerable, etc.). P2's hand numbers also stay current as Strength or Weak change.
2. Play a Defend: I picks it up, I again plays it (K puts it back). Try a card P2 can't afford; the hint line should show a reason.
3. Survivor: after I, the HUD shows P2's hand as the choice options. Pick one with I; for a multi-card choice, toggle with I and confirm with O.
4. Potions: U, J/L to a potion (the hint shows what it does), then I. Enemy-targeted potions show the arrow. O twice discards.
5. P ends P2's turn; the status line shows `TURN ENDED`.
6. With a controller bound to P2 (on a gamescope/Game Mode setup), the same actions work on the pad. During combat, P2's pad no longer takes over the screen; outside combat it still does.

**F. Teammate rewards panel** (after a combat, both players take rewards at once)

P1's "Loot!" screen now shows only P1's rewards. P2's rewards are in a panel on the right, with P2's own controls:

| P2 action | Keyboard | Controller |
|---|---|---|
| Move between rewards / cards / Skip | J / L | D-pad |
| Take the reward; pick the card | I | A |
| Close the card choice without taking a card | K | B |
| Jump to "Done" | O | Y |

1. Take P2's gold and potion. Each taken reward leaves the list, as on P1's screen, and P2's gold and potions go up.
2. Take P2's card reward. The panel shows P2's three cards plus Skip (and Reroll if a relic allows it). Pick one with I; it goes into P2's deck. Skip or K leaves the reward untaken.
3. While P2 is still choosing, P1 presses Proceed. It shows "Waiting for P2 to finish their rewards" and stays put. After P2 takes everything or picks Done, P1's Proceed works.
4. A potion reward with a full belt lists P2's potions to discard one (or "Keep my potions"); once one is discarded, the reward is taken. The same happens when P2 buys a potion in the shop with a full belt.
5. Known gaps: a relic that asks to pick cards from the deck still opens on P1's screen. Treasure rooms, shops, events and rest sites still take turns.

**H. Rest sites, treasure, shops, and P2's card picker**

All of these use the same keys: J/L to move and I to choose (D-pad and A on a pad). They all share one column on the right edge, below P2's relic bar and clear of P1's Proceed button; a list taller than the column scrolls to keep the row under the cursor in view. `event_panel_left` doesn't affect them — it's only for the event panel (see below).

- **Rest site:** P2's options, like Rest and Smith, with a "Skip the rest" row. P1 picks on screen and P2 picks in the panel, at the same time. P1's Proceed waits until P2 is done.
- **P2's card picker:** opens whenever P2 has to pick cards from their deck: Smith, shop removal, event "remove / transform / upgrade" outcomes, relics. It also handles picking from a grid of cards, or a pack. Card names list vertically with one focused preview above them; an upgrade pick (Smith) shows the card before and after upgrading stacked, with a down arrow between. It takes over the column from the rest site or shop panel underneath while it's open.
  - Single picks: I picks immediately.
  - Multi picks: I toggles, O confirms.
  - K cancels when the game allows it.
- **Treasure:** once the chest is open, P2's panel lists the relics plus Skip. Both players pick; relics are handed out once both have picked, as in online co-op. P2 now also gets their own chest gold.
- **Shop:** P2's own stock (cards, colorless, relics, potions, card removal) with prices and P2's gold. The highlighted card is previewed. I buys. "Remove a card" opens P2's card picker. "Done shopping" lets P1 leave; until then P1's leave button waits.

**P2's info and deck changes**
- **V** (keyboard) or **View/Back** (P2's pad) opens P2's deck and relics, with HP, gold and potions. J/L scrolls, U switches between deck and relics, and V or K closes it.
- When P2's deck changes outside combat, the game's own animations play as they do for P1: a transformed card morphs into its replacement, an upgraded card flashes, and a new card pops up and flies into the deck. New relics pop into P2's relic row.
- **Break glass:** clicking the left stick in on P2's pad (L3) hands P2 the main screen; Tab does the same on the keyboard. There are no on-screen swap buttons.
- **Layout:** P2's combat band and the relic row line up with the top of the players list (names and health bars) on the left, leaving P1's relic row clear; the band starts to the right of that list. Panels on the left start below the list, panels on the right below P2's relic row.
- **Screenshots:** F11 saves one to `couch_shots/` in the game's user folder; `shots = 1` also saves one each time a P2 screen opens.
- **Mend** (rest site): P2 picks who to heal in their card picker.
- **Relic choices** (events, "choose a relic"): open in P2's card picker.
- **Event options:** the option under P2's cursor shows its tooltips, like P1's focused option: the card, relic or keyword it mentions (e.g. an Ancient's Dusty Tome shows the card it gives). An Ancient's relic options show the relic's icon.
- **Crystal Sphere** (the "divination" event): P2's pick opens their own grid in place of the event panel. Move with the D-pad (keyboard: J/L across, U down, K up), A or I divines, and X or O switches between the big (3×3) and small (1 cell) tool. P2's rewards come up in their rewards panel once the divinations are spent. Each player plays their own sphere: P2's cost and finds don't carry over to P1, and P1's don't carry over to P2.
- **P2's relics on the main screen:** a row on the right, across from P1's relics. In combat it sits at the end of the HUD's top line. It uses the game's own relic icons, so counters, greyed-out relics and the flash when a relic triggers all show. Mouse over one for its tooltip, or click to inspect. New relics pop in with the game's animation and sound. Panels on the right start below the row.
- **Sounds:** P2's panels and HUD click, move and back like the game's menus, and play a "no" sound when something is refused. P2's cards landing in the discard pile, P2's gold going up, P2's new relics, transforms, upgrades, cards added to P2's deck and P2's shop purchases each play the game's own sound. The game normally plays these only for the local player.

Checks:
1. Rest site: P2 Smiths a card while P1 Rests. P2's card is upgraded in P2's deck.
2. Shop: P2 buys a card, a potion and a relic, then removes a card. P2's gold goes down and P2's deck, potions and relics change; P1's don't.
3. Treasure: both pick relics. Each gets their relic, and P2's gold goes up from the chest.
4. An event outcome that removes or transforms a card: P2's card picker opens instead of P1's screen.
5. Crystal Sphere: P2 picks "Uncover the future", spends the divinations in their grid, then claims the rewards. P1's gold and deck don't change.

## Later (known gaps)

- **P2's gold on the map:** shown in combat and in the rewards, rest site and shop panels, not on the map screen.
- **Polish:** the P2 panels have no hover tooltips, animations or art yet.
- **Rare edge:** if P2 holds a Spoils Map for the current act, P2 skips the separate chest gold (the base co-op code settles the map instead).

## How it works (for development)

- `Scripts/Runtime/Couch/CouchInputGate.cs`: a node kept as the scene root's last child, so it receives input first. It routes raw joypad events and marks blocked ones handled.
- `Scripts/Runtime/Couch/CouchSteamPoller.cs`: the game only polls the **first** Steam Input controller (`SteamControllerInputStrategy.UpdateControllerConnections` uses `[0]`). This polls every controller and routes at the source.
- `Scripts/Runtime/Couch/CouchGodotPadPoller.cs`: the same for the Godot joypad path (Steam Input off). Sticks and triggers are read per pad.
- `Scripts/Runtime/Couch/CouchInputRouter.cs`: pass, block, or claim. Claims go through `LocalControlSwitchGuard.TrySwitchTo`, and the driver is `LocalContext.NetId`.
- `Scripts/Runtime/Couch/CouchSeats.cs`: seat ↔ controller ↔ character.
- Events the pollers synthesize use device ids ≥ 1000 (`CouchEventDevice`), so the gate knows they've already been routed.
- `Scripts/Runtime/Couch/CouchRemotePlay.cs`: the simultaneous-play test. It dispatches a `RequestEnqueueActionMessage` from the teammate on `LocalLoopbackHostGameService`, which reaches `ActionQueueSynchronizer.HandleRequestEnqueueActionMessage(message, senderId)` exactly like an online client's request.
- `Scripts/Runtime/Couch/CouchTeammateHud.cs`: the teammate's hand, cursor, target marker and choice picker. It is attached to every combat room by `CouchTeammateHudPatch`. Controller input reaches it through `CouchInputRouter` (via `CouchHudInput`), and the keyboard block through `CouchInputGate`.
- `Scripts/Runtime/Couch/CouchTeammateChoices.cs` and `Scripts/Patch/CouchTeammateChoicePatch.cs`: in simultaneous combat, a teammate's card choice takes the game's remote path (`WaitForRemoteChoice`). It is answered with a `PlayerChoiceMessage` sent as the teammate.
- `Scripts/Runtime/Couch/CouchTeammateRewards.cs`: the teammate's post-combat rewards panel. The merged reward offer (`CombatRoomOfferRewardsPatch`) leaves the teammate's set out of P1's screen and hands it to the panel. Rewards are claimed with `Reward.SelectUnsynchronized()`. A card reward then takes the game's remote path (`WaitForRemoteChoice`), and the panel answers it with a `PlayerChoiceMessage` sent as the teammate. `CouchRewardsProceedPatch` holds P1's Proceed until the teammate is done.
- `Scripts/Runtime/Couch/CouchPanel.cs`: shared base for the rest site, treasure, shop and card-picker panels. They live on a CanvasLayer (layer 110) under the input gate.
- `CouchTeammateChoicePanel`: answers the teammate's out-of-combat card selections. Deck selections (`FromDeck*`), reward grids and bundles are captured in `CouchTeammateChoicePatch`. The forced-local selection (`CardSelectCmdPatch`) is skipped only when such a request exists (`CouchTeammateChoices.HasRequest`), so an unsupported selection falls back to the main screen instead of waiting forever.
- `CouchTeammateRestSite`: sends `OptionIndexChosenMessage` (RestSite) or `RestSiteSkippedMessage` as the teammate.
- `CouchTeammateTreasure`: sends `PickRelicAction` as the teammate.
- `CouchTeammateShop`: buys from the teammate's `MerchantInventory` with the entry's own purchase logic. Card removal uses `OneOffSynchronizer.DoMerchantCardRemoval` (the remote-player path).
- `CouchTeammateRoomsPatch`: holds the rest site and shop exits for the teammate, and sends the teammate's `TreasureChestOpenedMessage`.
- `CouchTeammateRelicBar`: the teammate's relics as game `NRelicInventoryHolder`s (not the top bar's `NRelicInventory`, which assumes the local player). It also plays the gold sound for the teammate, since `PlayerCmd.GainGold` plays it only for `LocalContext.GetMe`.
- `CouchSfx`: the sounds used by the teammate UI. UI clicks come from `SfxCmd.Play` (FMOD events). A few come from the game's remaining plain audio files via `NDebugAudioManager`: card select, deny, relic get, smith.
- Damage preview: `NCard.SetPreviewTarget(creature)`, the same call the game's `NCardPlay` makes while the driver hovers a target.
- `Scripts/Patch/NCardPlayQueueRemotePlayPatch.cs`: skips the play-queue preview for teammate cards. That preview needs the teammate intent UI, which local co-op disables. The card still animates out of the teammate when it resolves.

Decompiled game source for reference: `src/` (gitignored; regenerate with `ilspycmd` per `AGENTS.md` §5).

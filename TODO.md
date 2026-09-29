# Known Issues & Requests

Status as of 2026-08-31 (post-v1.32, unreleased work in the repo). The four issues
carried over from the pre-v1.31 notes were re-audited against the current
code; two were already resolved by earlier redesigns.

## Open — needs reporter logs / repro

### A. Black screen + hard lock in the room after a rest site (Workshop, 2026-08-26, v0.111)
Reporter: Xiao Tong. No log yet. The mod's rest-site option flow (RestSitePatch) traces clean;
suspects are the next-room transition (map vote → room build) or an event room rebuild.
Ask for `%APPDATA%/SlayTheSpire2/logs/godot.log` and whether it happens at every campfire.
Possible cause, found 2026-09-27 by the `rest_site` test scenario: `NRestSiteRoomReadyPatch.EnsurePrimaryPlayerOptionsVisible`
retried with no limit while the net service reported loading, each retry in the same frame, so the game hard-froze
(the log fills with `Object was deleted while awaiting a callback.`). Fixed in 7a96451, but only verified from the log, not
yet in game. If the reporter's log shows that line, this was it.

### A3. Found by the in-game test suite (2026-09-28)
- ~~Couch treasure room always logs `[ERROR] Tried to add hand for player 2 twice!`~~ (FIXED, 2026-09-28): the
  `UpdateHandVisibility` patch was asking the input synchronizer for P2's screen type before P2 had ever sent any
  input, which lazily creates their input state and re-fires the "hand added" event for a hand the room already
  added. See CHANGELOG. `treasure` now passes.
- ~~`RestSiteSynchronizer.Dispose()` throws "A task may only be disposed if it is in a completion state"~~ (FIXED,
  2026-09-28): the hover-message task is now detached before `Dispose()` runs instead of being disposed mid-flight.
  See CHANGELOG. `./deploy.sh test all --repeat 2` passes clean now.
- The `treasure` protection proof is inconclusive: picks came out the same with `TreasureRoomRelicSynchronizerBeginPatch`
  disabled. Find a repro where the base game's auto-vote-on-behalf actually changes P2's pick.
- Layout jitter: `CouchTeammateRelicBar` moved 5 px between runs at `combat/new-player-round`, and
  `CouchTeammateRestSite`'s height varies by 16 px (453 vs 469). Some `--review` checkpoint screenshots come out blank
  (`start/run-started`, one `layout/map`).
- Checkpoints still come out uniformly darkened (~30%, P1's UI included) at `rest_site/p2-smith-card-choice-open` and
  the `*/map-open` screens, even after the screen-stability waits. `NOverlayStack._backstopFade`, `NMapScreen._backstop`
  and `NTransition` all read as idle at capture time. Find what dims them before blessing those baselines.

### A2. Follow-ups from the rest-site freeze (2026-09-27)
- The loopback `IsGameLoading` flag can stay true for a long time. Test runs log `loading state updated: True` with no
  matching `False` (`NetLoadingHandle` is ref-counted). Find out whether that happens in normal play and what holds it.
- `RoomFocusGuardPatch` (`TryRecoverRestSiteAfterReadyOutOfRange`) and `RestSiteAutoSwitchUtil.EnsureOptionsAfterSwitch`
  also count "frames" with `CallDeferred`, which doesn't wait a frame when called from a deferred call. They have attempt
  limits, so they can't freeze, but their waits take no time. Switch them to a real next-frame wait once there's a test
  covering them.

### B. Selection-type potions used cross-character do nothing (Workshop, 2026-08-29, v0.111)
Reporter: LH. Slot 2 uses a choose-a-card potion (e.g. Droplet of Precognition / Liquid
Memories, `TargetType.AnyPlayer`) on slot 1 → no effect; on self it works.
Static trace of the whole pipeline finds every known hole already patched:
- `CardSelectCmd.ShouldSelectLocalCard` → forced local for all local characters (CardSelectCmdPatch)
- `PlayerChoiceSynchronizer.SyncLocalChoice` → sender/context switched to the choosing player (PlayerChoiceContextPatch)
- `GameActionPlayerChoiceContext/HookPlayerChoiceContext.SignalPlayerChoiceEnded` → resume forced unconditionally
- Host-side `RequestResumeActionAfterPlayerChoice` → resumes with no owner validation
So the failure is somewhere runtime-only; UsePotionActionWatchdogPatch should log
"Potion action waited for selection over 2000ms" if the action stalls. Need the reporter's log. Also verify
where the selected card lands (it goes to the TARGET's hand, which is backgrounded —
could read as "no effect" if the user doesn't switch).

### C. Heir skipped a draw step in the final-act Queen fight (Workshop, 2026-08-29, v0.111)
Reporter: LH. After Torch Head died, one turn the Heir (many powers active) drew nothing;
hand held a single Spectral colorless. Unclear if mod-related (turn mirroring) or a game
quirk. Needs repro/log; check CombatManagerReadyEnemyTurnPatch interplay with extra-turn
players (`CombatTurnState.PlayersTakingExtraTurn`) if it recurs.

## Feature requests

### ~~D. Keyboard controls one character, gamepad the other~~ (DONE: couch co-op)
Per-controller routing now gives each controller its own character (see COUCH.md).

## Resolved by re-audit (needs playtest confirmation only)

### ~~Issue 1: Extra card group after combat~~ (REMOVED)
The opt-in cross-character card group was removed with the switch to two-player couch co-op.

### ~~Issue 2: Treasure chest deadlock~~ (RESOLVED by earlier redesign)
Current flow: per-character voting with auto-switch (TreasureRoomRelicSynchronizerPatch)
plus `_localPlayerId` re-sync on switch (LocalControlRuntime:486). Vanilla's
IsSingleplayerOrFakeMultiplayer auto-random-vote path is inactive (loopback Type=Host), and
the old "pick while not active" crash is a benign warning in v0.111.

### ~~Issue 3: Potion bar purchase/display~~ (RESOLVED by earlier work)
NPotionContainerPatch is active again: the bar rebinds (holders rebuilt, events reconnected)
to the controlled character on Initialize/switch. Buying with slot 2 needs a playtest pass
to confirm gold deduction; if still broken, capture a log.

### ~~Issue 4: Potion targeting in combat~~ (PARTLY RESOLVED)
Buff (self-target) potions follow the controlled character (PotionManualUseTargetPatch).
Remaining piece is open item B above (cross-character selection potions).

### ~~Issue 5: Silken Tress — Glam enchant not applied~~ (FIXED in repo, unreleased)
Root cause: double reward generation. Fixed by CombatRoomOfferRewardsPatch (see CHANGELOG).
Verify: Neow → Silken Tress → first combat reward offers Glam cards.

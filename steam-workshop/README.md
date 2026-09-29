# Steam Workshop assets

Place the Workshop preview image (the item's thumbnail) at `steam-workshop/preview.jpg`.

`screenshots/` holds extra gallery images (gameplay shots, not the thumbnail) — steamcmd's
`workshop_build.vdf` upload only sets `previewfile`, so these aren't pushed automatically; add
them to the Workshop item's screenshot gallery by hand from the item's Steam page (Edit Item >
Screenshots). Capture fresh ones with `./deploy.sh test <scenario> --review`, which runs with
the mod's debug overlay forced off (see `docs/testing.md`); pick clean checkpoint PNGs from
`test-results/<run>/layout/<scenario>/`.

The release flow is: CI (semantic-release, on a hosted GitHub Actions runner) tags and creates
the GitHub Release from a merge to master, then `make attach-release`, run locally, builds the
real DLL and attaches it as a Release asset (CI has no game install and can't build it). Once
that's done, push it to Steam:

```bash
STEAM_USERNAME="<steam-user>" tools/upload-steam-workshop.sh vX.Y.Z
```

By default this downloads the zip that `make attach-release` just attached to the GitHub
Release, so Steam, GitHub, and later Nexus all ship identical bytes. Pass `--local-build` to
build fresh locally instead (useful before the release flow exists yet, or for a quick test).
See `tools/upload-steam-workshop.sh --help` for all options — it's deliberately not wired into
CI: SteamCMD needs a Steam Guard-authenticated session, which doesn't survive well on
short-lived (or Steam-account-less) CI runners.

The script writes `steam-workshop/couchspire.vdf`. On the very first upload it's created
with `publishedfileid "0"`; SteamCMD fills that in with the real Workshop item ID after a
successful upload. Commit that file afterwards so future uploads update the same Workshop
item instead of creating a duplicate.

The item starts at visibility `2` (private) by default — pass `--visibility 0` once you're
ready to make it public.

## Compatibility

`verified-versions.txt` lists game versions this build has actually been confirmed working
against (not the same as `CouchSpire.json`'s `min_game_version`, which is only a floor). It
gets folded into the Workshop item's description. Updating it doesn't require a new mod
release — see the comment at the top of that file.

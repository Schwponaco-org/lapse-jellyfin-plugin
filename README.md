# LAPSE for Jellyfin

Jellyfin plugin that fixes subtitles which have drifted out of time. It lines them up against the movie's own audio, or against another subtitle file.

**This repo is only the Jellyfin plugin.** The alignment engines it drives are separate projects and none of them are bundled here. The plugin downloads them for you from their own releases, or you can point it at builds you made yourself. See [Engines](#engines) for what's supported.

## Installing

1. In Jellyfin go to **Dashboard > Plugins > Repositories** and add this URL:

   ```
   https://raw.githubusercontent.com/rs-jensen/lapse-jellyfin-plugin/main/manifest.json
   ```

2. Open the **Catalog** tab, find LAPSE, and install it.
3. Restart Jellyfin.
4. Open the LAPSE plugin page and install an engine from the **Engines** section.

Needs Jellyfin 10.11 or newer.

## What you get

**A "Sync Subtitles" button in the three dot menu on any movie.** Press it, press Sync, and it runs. If the movie has more than one subtitle file you get asked which one. There is also an Advanced option if you want to pick the alignment mode or hand tune the timing.

**A dashboard page** with:

- Install any of the supported engines, pick a default, and see at a glance which ones actually work
- Every movie in your library with its sync status, searchable, so you can sync one without hunting through menus
- Bulk sync for a whole library or a single folder, with a progress bar
- Skip flags for movies or folders you never want touched
- Subtitle to subtitle sync, for lining up two subtitle files without involving a movie at all

**Auto sync for new movies** as they get added to your library.

**Manual fine tuning.** If a sync gets close but is still slightly off, the Advanced dialog has a box where you can shift the subtitle by a number of seconds. Minus makes subtitles show up earlier, plus makes them show up later. This one does not involve the engine at all, so it works even if the engine is not set up. Handles `.srt` and `.vtt`.

## Engines

The plugin can drive three different sync engines. Install whichever you want from the dashboard, set one as the default, and the quick Sync button and bulk sync will use it.

| Engine | Standard | Standard OLS | Split | Notes |
|---|---|---|---|---|
| [LAPSE](https://github.com/rs-jensen/lapse) | yes | yes | yes | Linux amd64 and arm64. |
| [alass](https://github.com/kaegi/alass) | yes | no | yes | Good at uneven drift. Penalty runs 0 to 1000. **x86_64 only.** |
| [ffsubsync](https://github.com/smacke/ffsubsync) | yes | no | no | Also corrects framerate mismatches. Linux x86_64 and arm64. |

Modes an engine cannot do are shown greyed out rather than hidden, so it is clear the engine is the reason.

Engines install into your Jellyfin data folder under `lapse/engines/<engine>`. If a project has no build for your server (alass on ARM, say), build it yourself and point the **Binary path override** in Settings at it.

## Alignment modes

**Standard** finds a single best constant offset for the whole file and shifts everything by it. This is the default, what the quick Sync button uses, and what subtitle to subtitle sync always uses. Every engine supports it.

**Standard OLS** fits a slope and intercept across the whole file instead of a flat offset. LAPSE only.

**Split** breaks the subtitle into sections that each get their own timing, for subtitles that drift unevenly. The penalty controls how eager it is to add splits, higher means fewer. The scale differs per engine, so the dashboard shows each engine's own range and default.

### If an engine will not start

The Engines section shows the actual error when a binary is installed but cannot run. The usual cause is a missing shared library. Either use a build with its dependencies statically linked, or install the missing libraries in your container.

The manual fine tuning feature keeps working regardless, since it only rewrites timestamps and never touches an engine.

## Building from source

```
dotnet build --configuration Release
```

Copy `Jellyfin.Plugin.Lapse.dll` from `Jellyfin.Plugin.Lapse/bin/Release/net9.0/` into a `LAPSE` folder inside your Jellyfin `plugins` directory, then restart the server.

## Releasing

Tag a commit and push the tag:

```
git tag v1.0.0
git push origin v1.0.0
```

The release workflow builds the plugin, zips it, attaches it to a GitHub release, and adds the new version to `manifest.json` on `main`. Anyone subscribed to the manifest URL then sees the update in Jellyfin.

## License

GPL v3. See [LICENSE](LICENSE).

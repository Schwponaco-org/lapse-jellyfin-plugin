# LAPSE for Jellyfin

Jellyfin plugin that fixes subtitles which have drifted out of time. It lines them up against the movie's own audio, or against another subtitle file.

**This repo is only the Jellyfin plugin.** The alignment engine it drives lives in a separate project, [rs-jensen/lapse](https://github.com/rs-jensen/lapse), and is not bundled here. The plugin downloads the engine for you from that project's releases, or you can point it at your own build.

## Installing

1. In Jellyfin go to **Dashboard > Plugins > Repositories** and add this URL:

   ```
   https://raw.githubusercontent.com/rs-jensen/lapse-jellyfin-plugin/main/manifest.json
   ```

2. Open the **Catalog** tab, find LAPSE, and install it.
3. Restart Jellyfin.
4. Open the LAPSE plugin page and hit **Download engine** in the Engine section.

Needs Jellyfin 10.11 or newer.

## What you get

**A "Sync Subtitles" button in the three dot menu on any movie.** Press it, press Sync, and it runs. If the movie has more than one subtitle file you get asked which one. There is also an Advanced option if you want to pick the alignment mode or hand tune the timing.

**A dashboard page** with:

- Engine download and a status badge telling you whether the engine actually works
- Every movie in your library with its sync status, searchable, so you can sync one without hunting through menus
- Bulk sync for a whole library or a single folder, with a progress bar
- Skip flags for movies or folders you never want touched
- Subtitle to subtitle sync, for lining up two subtitle files without involving a movie at all

**Auto sync for new movies** as they get added to your library.

**Manual fine tuning.** If a sync gets close but is still slightly off, the Advanced dialog has a box where you can shift the subtitle by a number of seconds. Minus makes subtitles show up earlier, plus makes them show up later. This one does not involve the engine at all, so it works even if the engine is not set up. Handles `.srt` and `.vtt`.

## Alignment modes

**Standard** works out one timing correction for the whole file. This is what you want most of the time, and it is what the quick Sync button uses.

**Split** lets the engine break the subtitle into sections that each get their own timing. Useful when a subtitle drifts unevenly, for example if it was made for a cut with different ad breaks. The penalty value controls how eager it is to add splits. Higher means fewer splits, and 6 is a good starting point.

## The engine

The plugin looks for the engine binary in your Jellyfin data folder, under `lapse/engines/lapse`. The Download engine button fetches the right build for your server from the [engine releases](https://github.com/rs-jensen/lapse/releases/latest).

Only Linux builds (amd64 and arm64) are published. On anything else, build the engine yourself and set the **Binary path override** in the plugin settings.

### If the engine will not start

The Engine section shows a red "Not working" badge with the actual error when the binary is there but cannot run. The usual cause is a missing shared library. The engine links against libavcodec, libavformat, libavutil, libfvad and libfftw3, and the official Jellyfin Docker images do not ship all of those. Either use an engine build that has its dependencies statically linked, or install the missing libraries in your container.

The manual fine tuning feature keeps working regardless, since it only rewrites timestamps.

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

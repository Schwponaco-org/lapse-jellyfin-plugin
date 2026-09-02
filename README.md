<p align="center">
  <img src="Jellyfin.Plugin.Lapse/Configuration/LAPSE.png" alt="LAPSE" width="420">
</p>

# LAPSE for Jellyfin

Subtitles that show up late, drift out over the runtime, or belong to a different cut of the film. LAPSE listens to the audio, works out where the speech actually is, and moves the subtitle to match. Press Sync on an item and it sorts itself out.

This repo is the Jellyfin plugin. The actual syncing is done by a separate program called the engine, which the plugin downloads for you on first run. Three engines are supported and you only need one.

## Installing

Add the repository in Jellyfin under **Dashboard > Plugins > Repositories**:

```
https://raw.githubusercontent.com/rs-jensen/lapse-jellyfin-plugin/main/manifest.json
```

Find LAPSE in the **Catalog** tab, install it, and restart Jellyfin.

Open the plugin from **Dashboard > Plugins > LAPSE** and install an engine from the **Engines** tab (or the Install button a fresh install shows you). A fresh install has no engine on disk and cannot sync anything until you do this.

Requires Jellyfin 10.11.11 or newer.

## Features

Every film, episode and loose video gets these entries in its three dot menu:

- **Sync Subtitles** - the main one. Pick a subtitle if there's more than one, press Sync, done. Advanced options let you pick a different mode, output format, translate at the same time, or nudge the timing by hand.
- **Sync Subtitles to Reference** - for items with several subtitle tracks where one is already correct. Lines every other track up against it, or just the one track you pick. Skips the audio entirely, so it's fast and usually more accurate than syncing each track on its own.
- **Shift Subtitles** - manual millisecond nudging with a live preview, for when a sync is close but not quite right. Works even without an engine installed.
- **Convert Subtitles** - writes a subtitle out as `.srt`, `.vtt`, `.ass` or `.ssa`, leaving the original alone unless you say otherwise. Useful for players that only take one format, or for formats no engine can sync directly (LAPSE converts those on its own when needed, so this is rarely something you have to do by hand).
- **Extract Embedded Subtitles** - writes the subtitle tracks living inside the video out as files beside it. Optionally rebuilds the video without them, which is what gets you direct play: nothing is re-encoded, so the picture and sound come out identical, and a track that couldn't be saved as a file (PGS, VobSub) is never dropped. Replacing the original video is a second, separate opt-in; leave it off and the rebuilt file is written as `.nosubs` for you to check first.
- **Readable Subtitles** - writes a copy with a dyslexia-friendly font, larger text and wider letter spacing set inside the file rather than in a client. Every client that plays it honours it, phone and TV included, with nothing to configure per device. The font itself installs from the dashboard under Subtitle appearance.
- **Translate** (experimental) - a separate job from syncing that never touches the original file. Providers include MyMemory (no setup needed), self-hosted LibreTranslate and Lingarr, and DeepL or Google Cloud with a key. Set a default language under Translation and the dialogs start on it, so nothing has to be typed on a TV remote.

Elsewhere in the dashboard:

- **Sync status, Bulk sync, Subtitle to subtitle** - a searchable list of every syncable item, a page to sync a whole library or folder at once, and a way to line up two subtitle files directly without a library item involved. An item counts as synced once its subtitle files have been synced; tracks still inside the video file are left out of that unless you ask for them, since nothing automatic touches those.
- **Stop** - any running job, whether it is a whole library, a series or a scheduled run, can be stopped from the progress strip on the dashboard or from the progress toast wherever it was started.
- **Automation** - libraries can pick up new items automatically or sync on a schedule, and unattended runs can sync, convert, translate, or react to a Radarr/Sonarr import webhook. Everything here is off by default; pressing a button yourself always works regardless.
- **Access control** - the menu entries above are admin only by default, but can be opened up to specific users or everyone signed in.
- **Undo** - every recent sync can be reversed with one press, whether that means restoring a backup or deleting the file the run added.
- **Fetching from OpenSubtitles** (experimental) - if an item has no subtitle at all, LAPSE can fetch one before syncing, using your own OpenSubtitles account.

### Subtitle formats

LAPSE reads and writes `.srt`, `.ass`, `.ssa`, `.vtt`, `.sub` (MicroDVD), `.sup` (PGS), `.sbv`, `.idx`/`.sub` (VobSub), `.smi`, `.ttml` and `.dfxp`, and writes each one back in the format it read. Picture-based subtitles (PGS, VobSub) have no text to work with, but their timing still gets moved. alass and ffsubsync only take `.srt`, `.ass`, `.ssa` and `.vtt`; anything else is converted to `.srt` automatically when one of those is the active engine.

## Engines

- **LAPSE** - the engine this plugin is built around, and the one to use. Works out on its own whether a subtitle is early, drifting, or split across a re-cut, and reports how confident it is. Reads the most formats of the three. Builds for Linux, macOS and Windows.
- **alass** - splits the file into sections and times each separately, handy for recordings cut around ad breaks. Linux and Windows, x86_64 only.
- **ffsubsync** - shifts the whole subtitle and can correct framerate mismatches. Linux, Windows and macOS, including Apple silicon.

See the [engine repo](https://github.com/Schwponaco-org/lapse) for details, or the [benchmark writeup](https://github.com/Schwponaco-org/lapse/blob/main/docs/benchmarks.md) comparing all three across 39 films.

LAPSE also ships as a standalone Docker image with a file watcher, for syncing subtitles outside Jellyfin entirely. See the engine repo for setup.

## File output

| Mode | What happens |
|---|---|
| Write a new file | Leaves the original alone. `Movie.en.srt` becomes `Movie.en.shifted.srt`. Default. |
| Write a new file, keep a backup | Same, but an earlier result at that name is kept as `.bak`. |
| Overwrite, keep a backup | Replaces the subtitle, keeps the old one as `.bak`. |
| Overwrite, no backup | Replaces the subtitle and keeps nothing. |

Jellyfin picks up a new file as an extra subtitle track on its next scan.

## License

GPL v3. See [LICENSE](LICENSE).

## Credits

Built by Rasmus Stisen ([rs-jensen](https://github.com/rs-jensen)) and Carl Johan M. Bangsgaard ([cowmuncher](https://github.com/cowmuncher)).

A product of [Schwponaco](https://github.com/Schwponaco-org), where the LAPSE engine this plugin is built around is made.

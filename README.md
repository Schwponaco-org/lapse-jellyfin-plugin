<p align="center">
  <img src="Jellyfin.Plugin.Lapse/Configuration/LAPSE.png" alt="LAPSE" width="420">
</p>

# LAPSE for Jellyfin

Subtitles that show up late, drift out over the runtime, or belong to a different cut of the film. LAPSE listens to the audio, works out where the speech actually is, and moves the subtitle to match. Press Sync on an item and it sorts itself out.

This repo is the Jellyfin plugin. The actual syncing is done by a separate program called the engine, which the plugin downloads for you on first run. Three engines are supported; you only need one.

## Installing

Add the repository in Jellyfin under **Dashboard > Plugins > Repositories**:

```
https://raw.githubusercontent.com/rs-jensen/lapse-jellyfin-plugin/main/manifest.json
```

Find LAPSE in the **Catalog** tab, install it, and restart Jellyfin.

Open the plugin from **Dashboard > Plugins > LAPSE** and install an engine from the **Engines** tab (or the Install button a fresh install shows). A fresh install has no engine on disk and cannot sync anything until you do this.

Requires Jellyfin 10.11.11 or newer.

## Features

Every film, episode and loose video gets these entries in its three dot menu:

- **Sync Subtitles** - the main one. Pick a subtitle if there's more than one, press Sync, done. Advanced options let you pick a different mode, output format, translate at the same time, or nudge the timing by hand.
- **Sync Subtitles to Reference** - for items with several subtitle tracks where one is already correct. Lines every other track up against it, or just the one track you pick. Skips the audio entirely, so it's fast and usually more accurate than syncing each track on its own.
- **Shift Subtitles** - manual millisecond nudging with a live preview, for when a sync is close but not quite right. Works even without an engine installed.
- **Convert Subtitles** - writes a subtitle out as `.srt`, `.vtt`, `.ass` or `.ssa`, leaving the original alone unless you say otherwise. Useful for players that only take one format, or for formats no engine can sync directly (LAPSE converts those on its own when needed, so this is rarely something you have to do by hand).
- **Extract Embedded Subtitles** - writes the subtitle tracks living inside the video out as files beside it. Optionally rebuilds the video without them, which is what gets you direct play: nothing is re-encoded, so the picture and sound come out identical, and a track that couldn't be saved as a file (PGS, VobSub) is never dropped. Replacing the original video is a second, separate opt-in; leave it off and the rebuilt file is written as `.nosubs` for you to check first.
- **Readable Subtitles** - writes a copy with a dyslexia-friendly font, larger text and wider letter spacing set inside the file rather than in a client. You tick which subtitles it applies to, so a Danish track can be made readable while the English and Spanish ones are left alone, and by default the copy is written beside the original rather than over it - both are offered in the player, so one person's readable subtitle doesn't take everyone else's away. Replacing is offered too and keeps the original as a backup; either way, **Back to normal** in the same dialog undoes it. Subtitles that aren't in the Latin alphabet keep the larger text, heavier outline and margin, but get a font that has their letters in it: OpenDyslexic has no Arabic, Hebrew, Thai or CJK glyphs. Letter spacing is dropped for scripts whose letters join up or carry stacked marks, and Arabic and Hebrew are marked as running right to left. The font itself installs from the dashboard under Subtitle appearance.
- **Translate** (experimental) - a separate job from syncing that never touches the original file. Providers include MyMemory (no setup needed), self-hosted LibreTranslate and Lingarr, and DeepL or Google Cloud with a key. Set a default language under Translation and the dialogs start on it, so nothing has to be typed on a TV remote.

Elsewhere in the dashboard:

- **Sync status, Bulk sync, Subtitle to subtitle** - a searchable list of every syncable item, a page to sync a whole library or folder at once, and a page to line up two subtitle files directly without a library item involved. An item counts as synced once its subtitle files have been synced; tracks still inside the video file are left out of that unless you ask for them, since nothing automatic touches those.
- **Stop** - any running job, whether it is a whole library, a series or a scheduled run, can be stopped from the progress strip on the dashboard or from the progress toast wherever it was started.
- **Automation** - libraries can pick up new items automatically or sync on a schedule, and unattended runs can sync, convert, translate, write readable copies, or react to a Radarr/Sonarr import webhook. Readable copies can be added beside every subtitle or replace them, and replacing still keeps the original as a backup. Everything here is off by default; pressing a button yourself always works regardless.
- **Subtitle settings, per user** - **Subtitle settings** appears in the player's own subtitle menu while something is playing: size, letter spacing, colour, background and font, including any font installed on the server. It is saved against the account rather than the device, so it follows the person from the TV to the phone, and it changes nothing for anybody else sharing the library. What an admin sets under Subtitle appearance is the starting point for whoever hasn't set their own.
- **Access control** - the menu entries above are admin only by default, but can be opened up to specific users or everyone signed in.
- **Undo** - every recent sync can be reversed with one press, whether that means restoring a backup or deleting the file the run added.
- **Fetching from OpenSubtitles** (experimental) - if an item has no subtitle at all, LAPSE can fetch one before syncing, using your own OpenSubtitles account.

### Subtitle formats

Subtitles: `.srt`, `.ass`, `.ssa`, `.vtt`, `.sub` (MicroDVD, MPL2 and SubViewer 2), `.mpl2`, `.sup` (PGS), `.sbv`, `.idx` (VobSub, point it at the `.idx` file), `.smi`, `.ttml`, `.dfxp`. Each one is written back in the format it was read in.

Three formats share `.sub` and the name says nothing about which one you have, so LAPSE reads the file instead:

| | Looks like | Needs a frame rate |
|---|---|---|
| MicroDVD | `{450}{487}text` | Yes, frames mean nothing without one |
| MPL2 | `[180][195]text` | No, the numbers are tenths of a second |
| SubViewer 2 | `00:03:00.00,00:03:01.50` on its own line | No |

Picture-based subtitles (PGS, VobSub) have no text to work with, but their timing still gets moved. alass and ffsubsync only take `.srt`, `.ass`, `.ssa` and `.vtt`; anything else is converted to `.srt` automatically when one of those is the active engine.

### Text encoding

A subtitle normally goes back out in whatever encoding it came in as: LAPSE works out what that was and writes the result the same way, so a Windows-1252 file stays Windows-1252 and a UTF-8 file stays UTF-8. Nothing needs setting for that.

For a library that is a mix of UTF-16 and old codepage files, **Write every result in one encoding** under an engine's advanced settings forces every result to `utf8`, `utf8-bom`, `utf16le`, `utf16be` or `latin1` instead. The engine cannot tell one ASCII-compatible codepage from another, so converting one to UTF-8 reads it as ISO-8859-1 - right for Western European subtitles, wrong for Cyrillic and CJK. Picture-based formats hold no text and are left alone either way. Needs an engine build that has `--encoding`; the setting is simply not passed to one that doesn't.

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

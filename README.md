# LAPSE for Jellyfin

Jellyfin plugin that fixes subtitles which have drifted out of time. It lines them up against the item's own audio, or against another subtitle file.

**This repo is only the Jellyfin plugin.** The alignment engines it drives are separate projects and none of them are bundled here. The plugin downloads them for you from their own releases, or you can point it at builds you made yourself. See [Engines](#engines) for what's supported on which platform.

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

**Sync buttons in the three dot menu** on any film, episode or loose video:

- **Sync Subtitles** - press it, press Sync, and it runs. If the item has more than one subtitle file you get asked which one. There is also an Advanced option for picking the alignment mode, translating, or hand tuning the timing.
- **Sync All Subtitles to Reference** - pick the subtitle track that is already correct and every other track on that item gets lined up against it. Subtitle to subtitle alignment skips the audio decoding entirely, so this is much faster than syncing each track to the video, and usually more accurate.

**A dashboard page** with:

- Install, update and auto-update any of the supported engines, pick a default, and see which version of each one is on disk
- Per-library on/off switches, so LAPSE only touches the libraries you want it to
- Per-library schedules - a day and a time for each library
- Every syncable item with its sync status, filterable by library and searchable
- Bulk sync for every enabled library or a single folder, with a progress bar
- Skip flags for items or folders you never want touched
- **Subtitle to subtitle** - line up two subtitle files without involving a library item at all, optionally writing the result to a third file instead of overwriting the input

The settings nobody needs to touch day to day live in one collapsed **Advanced** section at the bottom instead of sitting in the way. Inside it:

- **File output** - overwrite in place or write a separate sidecar file, with or without backups
- **Translation** - Google Translate or self-hosted Lingarr
- **Engine tuning** - split penalties and custom binary paths
- **Engine auto-update** - on by default, and best left that way

**Auto sync for newly added items** in any enabled library.

**Scheduled tasks.** Two of them show up under Dashboard > Scheduled Tasks: *Sync subtitles*, which runs over every enabled library, and *Update sync engines*, which checks GitHub for newer engine releases once a day.

**Manual fine tuning.** If a sync gets close but is still slightly off, the Advanced dialog has a box where you can shift the subtitle by a number of seconds. Minus makes subtitles show up earlier, plus makes them show up later. This one does not involve the engine at all, so it works even if no engine is set up. Handles `.srt` and `.vtt`.

## Libraries

Every library gets a row in the **Libraries** section with an on/off switch and an optional schedule. A library you have never touched counts as on, so nothing stops working after an update.

Turning a library on makes everything in it eligible: films, episodes, home videos, music videos, and anything else with a video file. The skip list still applies on top, per item or per folder.

## Engines

The plugin can drive three different sync engines. Install whichever you want from the dashboard, set one as the default, and the quick Sync button, bulk sync and the scheduled task will use it.

| Engine | Standard | Standard OLS | Split | Builds published |
|---|---|---|---|---|
| [LAPSE](https://github.com/rs-jensen/lapse) (experimental) | yes | yes | yes | Linux amd64 and arm64 |
| [alass](https://github.com/kaegi/alass) | yes | no | yes | Linux x86_64, Windows x64 |
| [ffsubsync](https://github.com/smacke/ffsubsync) | yes | no | no | Linux x86_64/arm64, Windows x64, macOS Intel/Apple silicon |

Modes an engine cannot do are shown greyed out rather than hidden, so it is clear the engine is the reason.

The LAPSE engine's card is marked **(EXPERIMENTAL)**. It is the newest of the three and the only one with OLS, but it has had far less mileage than alass and ffsubsync, so it is worth knowing that before you point a whole library at it.

Engines install into your Jellyfin data folder under `lapse/engines/<engine>`.

### Running on Windows or macOS

The LAPSE engine itself only publishes Linux binaries, so the Install button on its card will tell you there is no build for your server rather than handing you something that cannot run. You have three ways forward, and the dashboard says the same thing on the card:

1. **Use an engine that does ship a build for your platform.** alass and ffsubsync both have Windows builds and ffsubsync has macOS builds. Install one from the dashboard and press **Use by default**. Everything else in the plugin works exactly the same.
2. **Run the engine under WSL or Docker** and point the **Binary path override** in Engine settings at it.
3. **Build it yourself.** The LAPSE engine is C++ and needs FFmpeg's libraries, libfvad and FFTW3; its [README](https://github.com/rs-jensen/lapse#cli) has the compile line. Put the resulting `lapse.exe` anywhere the Jellyfin service account can read and execute, then set the **Binary path override** to it.

A path override always wins over the plugin's own copy, and the auto-updater deliberately leaves overridden engines alone - a binary you built is not the plugin's to replace.

### Engine updates

Each engine card shows the version that is on disk, and says when a newer release is out. **Update** installs it. The daily task does the same thing without asking, for every engine with auto-update left on - which is the default, and the switches for it are tucked at the bottom of **Advanced** because turning them off is not recommended. Engines you pointed at your own binary are never touched by the updater either way.

Versions come from two places: the release tag the plugin recorded when it installed the engine, and `engine --version` for engines that answer it (alass and ffsubsync both do). When neither knows, the card simply doesn't mention a version.

### Engine capabilities

Different builds of the same engine take different flags, so rather than assuming a version, the plugin asks the binary. First it tries `engine --capabilities`, which is expected to print JSON like:

```json
{ "version": "1.3.0", "flags": ["--output", "--no-backup"] }
```

If the binary does not know that call, the plugin runs it with no arguments and reads the flags out of the usage text it prints. If neither works, it sticks to the arguments that have always been there. What it found is on the version line's tooltip rather than the card itself - ffsubsync alone lists about fifty flags.

## File output

Under **File output** you choose what a sync does with the subtitle it fixed:

| Mode | What happens |
|---|---|
| Overwrite, keep a backup | Replaces the subtitle, keeps the old one as `.bak` next to it. The default. |
| Overwrite, no backup | Replaces the subtitle and keeps nothing. |
| Write a new file | Leaves the original alone, writes `Movie.en.srt` as `Movie.en.shifted.srt`. |
| Write a new file, keep a backup | Same, but an earlier result at that name is kept as `.bak`. |

The `.shifted` part is configurable. Jellyfin picks the new file up as an extra subtitle track on its next library scan, so you end up with both the original timing and the fixed one to choose between.

Whatever the mode, the engine always writes to a temporary file first and it is only moved into place once the run succeeded and actually produced something. A failed or interrupted run cannot destroy a working subtitle.

**Subtitle to subtitle** has its own version of this. Tick *Write the result to a new file* and you get a third subtitle instead of the input being overwritten - both timings survive and you can compare them in the player. The name is suggested from the input file using the same sidecar suffix, and it is an ordinary text field, so rename it to whatever you want. It just has to end in a subtitle extension, and it cannot be aimed at the reference.

## Alignment modes

**Standard** finds a single best constant offset for the whole file and shifts everything by it. This is the default, what the quick Sync button uses, and what subtitle to subtitle sync always uses. Every engine supports it.

**Standard OLS** fits a slope and intercept across the whole file instead of a flat offset. LAPSE only.

**Split** breaks the subtitle into sections that each get their own timing, for subtitles that drift unevenly. The penalty controls how eager it is to add splits, higher means fewer. The scale differs per engine, so the dashboard shows each engine's own range and default.

## Translation

Translation is a separate job from syncing. It never touches the engine, never modifies the subtitle it reads, and writes its result as a new file named `Movie.<lang>.translated.srt`. Sync and translation are independent - run either, both, in any order.

**Where to run one from:** press Advanced on an item in the dashboard's Items list and use the Translate box in that dialog. The **Translation** tab under Advanced is only where the provider and its defaults get configured, not where a translation job is started.

Two providers:

- **Google Translate** - needs a Cloud Translation API key in the dashboard.
- **Lingarr** - self-hosted; needs its base URL, plus an API key if that Lingarr has authentication turned on.

Only the dialogue gets sent anywhere. Timings, cue numbers, WebVTT headers and ASS style blocks are left exactly as they were.

**Confidence threshold.** Neither provider reports a confidence number, so rather than pretending otherwise the plugin scores each line on what it can actually see: whether anything came back, whether the text changed at all, whether the length is plausible for a translation, and whether the language the provider detected is the one that was asked for. Lines below the threshold are counted, and optionally left in their original language.

**Metadata header.** Optional comment block at the top of the output naming the provider, the date, the languages and the average confidence. Written as `NOTE` lines for srt/vtt and `;` comments for ass/ssa, both of which players ignore.

### If an engine will not start

The Engines section shows the actual error when a binary is installed but cannot run. The usual cause is a missing shared library. Either use a build with its dependencies statically linked, or install the missing libraries in your container.

The manual fine tuning feature keeps working regardless, since it only rewrites timestamps and never touches an engine.

## License

GPL v3. See [LICENSE](LICENSE).

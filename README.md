# LAPSE

Jellyfin plugin that syncs subtitles that have drifted out of time, either against a movie's own audio or against another subtitle file. It's a thin wrapper around the [LAPSE engine](https://github.com/rs-jensen/lapse).

## Features

- "Sync Subtitles" button in the three-dot menu on movies (standard sync runs instantly, advanced mode lets you pick split alignment and a penalty value)
- Dashboard page to download the engine binary, see sync status for every movie, and bulk sync a whole library or a single folder
- Auto-syncs new movies as they're added to your library
- Subtitle-to-subtitle sync section on the dashboard, for lining up two subtitle files without touching the movie itself

## Building

```
dotnet build
```

The compiled `Jellyfin.Plugin.Lapse.dll` (plus the other files listed in `build.yaml`) goes in your Jellyfin server's plugin folder, in its own `Lapse` subdirectory.

## Engine

LAPSE does not ship the alignment engine itself. On first use, the dashboard page can download the right binary for your server (Linux amd64/arm64) straight from the [LAPSE releases](https://github.com/rs-jensen/lapse/releases/latest). If you're on something else, build the engine yourself and point the dashboard's "binary path override" setting at it.

## License

GPL v3, see [LICENSE](LICENSE).

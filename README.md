# GalleryBrowser 1.5

GalleryBrowser is a Windows desktop application for managing gallery files, attributes, tags, creators, tracking records, bookmarks, notes, and analytics in one place.

## Download

- [Latest Windows x64 release](https://github.com/automutton/GalleryBrowser/releases/latest)
- Current public version: **1.5.0**

The release ZIP contains a clean, self-contained Windows x64 build. Personal settings, databases, credentials, thumbnails, caches, and local paths are not included.

## Highlights

- **Gallery**: thumbnail browsing, Rating/Creator/Title/Character/Tag filters, compound sorting, reverse filters, Random Pick, and external-app handoff
- **Explorer**: tabs, split view, thumbnail/detail layouts, GID management, archive tools, image conversion, folder thumbnails, and Gallery navigation
- **Creators and Creator Tracking**: creator summaries, activity locations, storage paths, work tendencies, subscriptions, purchases, tasks, alerts, index filtering, and wishlists
- **Organization**: filter and tag editors, bookmarks that restore view state, Markdown sticky notes, and the cross-screen Board
- **Planning and analytics**: Calendar, User Metrics, rankings, trends, and experimental regression analysis
- **Optional integrations**: pCloud backup/archive, Discord and LINE notifications, Yahoo-powered romaji search, and an experimental Codex-based AI concierge
- **Customization**: configurable sections, themes and accent colors, keyboard shortcuts, and Japanese/English/Simplified Chinese/Traditional Chinese UI support

Google Calendar synchronization is disabled in the public build. The local Calendar and ICS export remain available.

## Stack

- C# / WPF / WebView2
- Svelte / TypeScript / Vite
- SQLite

## Requirements

- Windows 10/11 x64
- Microsoft Edge WebView2 Runtime
- Node.js and the .NET 10 SDK are required only when building from source

## First launch

GalleryBrowser stores its application data under `%LOCALAPPDATA%\GalleryBrowser` by default. Select the primary SQLite database, cache database, thumbnail cache, gallery sections, and scan folders in Settings before scanning a large collection.

Optional services such as pCloud, Discord, LINE, Yahoo APIs, external tools, and the AI concierge remain disabled until the user configures them.

## Repository layout

```text
GalleryBrowser.sln
src/GalleryBrowser.App/   WPF host app
src/GalleryBrowser.Web/   Svelte UI
data/                     local development settings and cache (not tracked)
docs/                     design notes
scripts/                  release packaging scripts
```

## Build from source

1. Open `GalleryBrowser.sln` in Visual Studio.
2. Restore NuGet packages.
3. In `src/GalleryBrowser.Web`, run `npm install` and `npm run build`.
4. Start `GalleryBrowser.App`.

The app loads the built Svelte UI from `src/GalleryBrowser.Web/dist`.

## Create a release package

Public package with clean first-run settings:

```powershell
.\scripts\Publish-GalleryBrowser.ps1 -Mode Public
```

Personal portable package with the current local JSON settings:

```powershell
.\scripts\Publish-GalleryBrowser.ps1 -Mode Personal -PersonalSettingsSource .\data
```

Packages are written under `artifacts/release/`, which is excluded from Git.

## Data locations

- Source development: the repository's `data/` directory
- Public release package: `%LOCALAPPDATA%\GalleryBrowser` (selected by the packaged `GalleryBrowser.portable` marker)
- Personal portable release: the package's `data/` directory populated with the selected JSON settings
- A manually published build without `GalleryBrowser.portable`: `%LOCALAPPDATA%\GalleryBrowser`
- Optional override: set the `GALLERYBROWSER_DATA_DIR` environment variable

The primary gallery database, cache database, thumbnail cache, and external tools are selected in Settings. User databases, caches, credentials, and personal settings are not committed to this repository.

## Updating an installed release

Replacing only `GalleryBrowser.exe` is sufficient for a host-only update when the release notes confirm that the web UI and package marker are unchanged. GalleryBrowser normally updates both the host and Svelte UI, so the safe default is to replace the complete clean package. User settings and databases remain under `%LOCALAPPDATA%\GalleryBrowser` and must not be overwritten by a release package.

Settings files are version tolerant: existing values take priority, newly introduced values receive application defaults, and unknown storage settings survive a read/write cycle. SQLite schema changes must be implemented as startup migrations with defaults so an existing database can be upgraded in place.

The full public-release baseline and packaging policy are documented in [docs/public-release-baseline.md](docs/public-release-baseline.md).

## Privacy

GalleryBrowser is local-first. The public source and release package do not contain the developer's personal databases, local paths, cached thumbnails, OAuth credentials, webhook URLs, access tokens, or Creator Tracking data. External integrations are opt-in and use the settings supplied by each user.

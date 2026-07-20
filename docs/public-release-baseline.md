# Public release baseline

This document is the source-of-truth baseline for GalleryBrowser public releases. The current public release is version 1.2.0; the baseline policy began with version 1.0.0.

## Build and package

- Build command: `scripts/Publish-GalleryBrowser.ps1 -Mode Public`
- Current package: `artifacts/release/GalleryBrowser-1.2-win-x64`
- Target: Windows x64, .NET self-contained, single executable
- Trimming: disabled
- Debug symbols and XML documentation: excluded
- Required package content:
  - `GalleryBrowser.exe`
  - `GalleryBrowser.portable`, containing `%LOCALAPPDATA%\GalleryBrowser`
  - `webui/index.html`
  - the generated `webui/assets/*.css` and `webui/assets/*.js`
  - an empty `data/` directory
- Excluded content: user settings, SQLite databases, credentials, thumbnails, WebView2 data, logs, temporary files, IDE files, and personal paths

The publish script deletes and recreates the package directory before every build. Do not use a package after launching its executable as the source for distribution; rebuild it first.

## Public first-run defaults

- Application data root: `%LOCALAPPDATA%\GalleryBrowser`
- Main database default: `%LOCALAPPDATA%\GalleryBrowser\gallery_api.sqlite`
- Cache database default: `%LOCALAPPDATA%\GalleryBrowser\gallerybrowser.cache.sqlite`
- WebView2 data: `%LOCALAPPDATA%\GalleryBrowser\webview2`
- Explorer initial folder: the parent of `%USERPROFILE%` (normally `C:\Users`)
- Main navigation: Gallery, Explorer, and Creators start expanded
- Creator Tracking custom metric labels, weights, activity-place candidates, and option lists: unset until configured by the user
- pCloud target folder, OAuth client data, account data, and credentials: unset
- Google Calendar synchronization: disabled in the public build
- Personal database, cache, thumbnail, tool, and folder paths: unset

## Upgrade policy

An in-place upgrade may replace only `GalleryBrowser.exe` when the release notes confirm that `GalleryBrowser.portable` and `webui/` are unchanged. Because most UI updates change the generated Svelte assets, the default distribution procedure is to replace the complete clean application package. Never overwrite or delete `%LOCALAPPDATA%\GalleryBrowser` during a normal update.

Backward compatibility is mandatory:

- Existing setting values must remain authoritative.
- A setting introduced by a newer release must receive its declared default when absent from an older file.
- Unknown storage JSON properties must be preserved when settings are rewritten.
- JSON table snapshots import only columns supported by the current database schema; newly added columns must have database defaults.
- SQLite schema changes must be additive startup migrations where possible. Destructive migrations require an automatic backup and an explicit migration path.
- Removed or renamed settings require a documented one-time migration; they must not be silently discarded.

## Personal package

`scripts/Publish-GalleryBrowser.ps1 -Mode Personal -PersonalSettingsSource .\data` is only for the owner's portable build. It embeds the selected local JSON settings and stores application data in the package's `data/` directory. It must never be published as the public package.

# Architecture

## Goal

GalleryBrowser combines a modern HTML-style gallery UI with local file browser operations.

## Responsibilities

- Web UI: search, filters, thumbnail grid, selection state, command surface.
- WPF host: WebView2 window, native dialogs, lifecycle.
- C# services: SQLite access, thumbnail generation, archive inspection, file operations, macro execution.
- Cache folders: generated thumbnails and transient working files.

## Initial Milestone

1. Load the Svelte UI inside WebView2.
2. Add a typed bridge between TypeScript and C#.
3. Read gallery items from SQLite.
4. Render thumbnail grid.
5. Add file open / reveal / rename / delete / copy / move commands.


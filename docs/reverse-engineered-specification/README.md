# Photo Screener — core implementation specification

Updated 2026-11-06. This is the intended rewrite, superseding the legacy use cases and the earlier database/snapshot-export design. The reviewer has authorized destructive operations **inside designated export folders**. Source photos remain read-only.

Implementation details: [export format and legacy compatibility](legacy-export-format.md), [contract](implementation-contract.md), [architecture](architecture-plan.md), [roadmap](implementation-roadmap.md), [acceptance criteria](acceptance-criteria.md), [resolved decisions](rewrite-decisions.md), and [macOS details](macos-support.md).

## The problem and priorities

Review photos at the speed of keyboard decisions, copy chosen photos into an export folder, and resume by comparing source and export contents.

1. **Performance:** Left/Right and Space give immediate feedback. Show a low-resolution preview quickly and load full quality in the background. Decoding, folder comparison, copying, deletion and metadata writes stay off the UI thread.
2. **Consistency:** Source photos are never changed. The active export folder is editable: picking copies a photo into it; unpicking removes that exported copy. File operations must act on the correct associated photo and remain within that export boundary.
3. **Resume:** Completed export files are the source of truth for picks. Selections must be recoverable without a database or mandatory review-state file. Pending operations and optional review-position metadata are separate from completed picks.

The implementation targets Windows and macOS using a shared Avalonia UI. Both platforms must prove the performance targets; macOS support is not a reason to weaken source protection or correct file association.

## Five core workflows

### C-01 — Open or resume a source/export pair

Choose a source folder. Its default target is `<folder>/export`, matching the old application; reuse it to resume, and create it only when first picking. A custom target can be explicitly chosen and remains bound to that source folder when returning to it. Other folders use their own default or explicit binding. Export roots cannot equal or contain protected sources. Export subfolders are excluded from all source discovery; source photos outside those boundaries remain protected.

Write directly into the effective target as `<original-stem>.<hash4><original-extension>`, using the old MD5/Base64 four-character suffix exactly. Preserve original bytes and the flat layout; do not migrate or rename old exports. Default per-folder targets separate same-named photos. Load immediate folder metadata and its export comparison incrementally; do not wait for a recursive library scan. Restore position/filter from optional review metadata when available; otherwise show the first available photo. Missing drives show Retry/Choose source/export without erasing existing copies.

On folder open, load the focused image's quick preview and thumbnails currently visible on screen first. Show placeholders for everything else. Once the first screen is usable, lazily warm the remaining folder thumbnails with relaxed background priorities and bounded work. Never wait for the whole folder to decode. Scrolling or keyboard movement immediately promotes newly visible/focused photos; changing folders cancels obsolete warming.

Lazy loading the rest means small thumbnails and metadata, not retaining every image or loading every full preview. Full-quality refinement is for the current photo. Background work must yield to input, drawing, visible previews and actual review activity; it may pause without affecting the first response.

### C-02 — Review with the keyboard and a large preview

Focused/current means the displayed photo; picked means a confirmed associated export copy exists. Left/Right move through the visible list without changing picks and stop at the ends. Space inverts the current requested pick status without advancing. Ignore Space auto-repeat; keep review keyboard focus after thumbnail/button actions and do not intercept text entry or dialogs.

Give the image the largest available rectangle, with compact controls and a collapsible thumbnail strip. Preserve its complete composition, aspect ratio, and EXIF orientation, including mirrored variants. Show a low-resolution preview first, then automatically improve it from the original while the same photo remains focused. Never show another photo under the current filename or let an obsolete decode replace the current image.

Provide All, Picked and Unpicked filters. An explicit filter change chooses the next matching photo, then the previous, otherwise a coherent empty state. If a Space toggle would hide the focused photo, reveal it in its folder's All view while keeping that photo and preview. A project-wide Picked view combines the folders' effective export indexes rather than using a database or one last-active target.

### C-03 — Pick or unpick asynchronously

On pick, update the requested status immediately and show Copying. Copy original bytes and calculate the legacy filename in the background using an owned staging file inside the export root. Verify it, then atomically publish the completed file directly into the target. Reuse an existing destination only when its full content matches; report collisions without overwriting. Only then confirm Picked.

On unpick, show Removing and delete only confirmed associated current-version export copies in the job's designated root. Only after successful removal confirm Unpicked. Sources are never deleted or rewritten. Older exported versions remain untouched when the source changes; a current-version pick uses its deterministic legacy filename.

Rapid toggles target the same photo and converge on the latest request. If a copy is still pending when unpicked, cancel/discard its temporary output where possible. Failures show the requested change as unsuccessful and retain the actual observed status; never pretend a failed deletion or partial copy succeeded. Left/Right and previews stay responsive throughout.

### C-04 — Recover completed selections

Compare immediate source/export files using legacy filename candidates and full-content verification in the background. The same format supports both old picks and new writes. Internal metadata/staging files do not count; ambiguous files cannot be automatically deleted or replaced. Same basenames in different default export folders stay independent. If folders explicitly share a flat target, identical original names and bytes share one pick association, and all associated rows reflect it; different content never shares a pick merely because its name/length matches.

Existing matching copies restore picks after restart even with all application settings/review metadata removed. Absent copies restore Unpicked. Checking, mismatched copies, missing sources and unavailable export folders are explicit states; unavailable storage must not be interpreted as an empty export.

Show picked/total counts as comparison becomes known. An optional `.photoscreener/review.json` can remember per-folder position and reviewed history. Missing/corrupt metadata loses those conveniences, not the exported picks. Without reviewed history, assessment progress is Unknown; the app must not claim that all unpicked photos have been reviewed. Review completion and selection/export status remain distinct.

### C-05 — Manage pending work and the export boundary

The active export folder is the current selection set, not an immutable historical snapshot. Copying/removal begins on Space; there is no mandatory later batch-export step. Completed files are ordinary photos usable by other applications.

Display queued/active/failed operations and provide retry/cancel for pending work. Folder navigation remains available while jobs finish against their original bindings. Before closing or changing a folder's binding, finish or explicitly cancel the affected pending changes. Jobs always retain their original source/export context; they cannot be redirected to a newly selected folder. Canceling pending work does not undo completed changes. After an interruption, rebuild picks from completed files and identify/clean only app-owned incomplete staging files.

The app never bulk-clears an export root as part of resume, pairing changes or retry. Destructive permission applies to targeted export changes and owned temporary files; it does not authorize changing sources, following links outside the export boundary, or deleting unrelated unmatched files.

## Scope

Core: keyboard review, progressive JPEG/PNG previews, three filters, folder comparison, asynchronous copy/remove, resume from exports and optional position metadata. Directory discovery and caches are incremental, bounded and disposable. AI selection/training and TV output remain excluded. Defer RAW companions, GPS/maps, editing/zoom, ratings, elaborate settings/log panels and cloud synchronization.

The same copied bytes, flat layout and exact hash filenames support Windows/macOS use and reuse of old exports. App metadata may supply binding/source-root hints, but the reviewer must explicitly associate relocated or custom roots when those hints are unavailable. Supporting two operating systems does not imply synchronized simultaneous editing of the same export folder.

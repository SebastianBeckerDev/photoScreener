# Resolved decisions and consistency review

Updated 2026-11-06. Destructive changes are allowed within designated exports. New writes must preserve the old app's export format so old folders work directly. The following decisions supersede conflicting legacy and earlier planning rules.

| Issue | Resolution |
| --- | --- |
| Database dependence for remembering picks | No database. Completed associated export files determine observed picks; indexes/caches are reconstructible. |
| Unpick could not remove a copy under the prior safety rule | Unpick deletes verified current-version associated copies inside its designated export root. Source protection remains absolute. |
| Immediate copies used to block picks/navigation | Immediate requested-status feedback; copy/verify/remove run in a bounded background worker. No fixed delay or disk work in key handlers. |
| Earlier explicit snapshot export separated picks from folder contents | Remove that workflow. The active export folder is the live selection set; copy/remove starts on Space. |
| Manual target overwritten by per-folder `export` defaults | Retain default `<folder>/export` compatibility, with an explicit override per source folder that loading/navigating cannot silently reset. |
| Proposed source-relative export tree broke old-folder interoperability | Write flat `<stem>.<hash4><extension>` files using the exact legacy MD5/Base64 suffix. No migration/rename; source-relative paths are internal IDs only. See the [format contract](legacy-export-format.md). |
| Same basename/size or short hash suffix could identify the wrong photo | Keep all candidates and verify full content in background. Reject different-content collisions and unresolved identities; never use the old first-match index as deletion authority. |
| Flat shared targets cannot encode the origin of identical name/content | Default per-folder exports keep picks independent; explicit shared targets share the verified association and state. Different content never merges based on size alone. |
| Duplicated matching export files could resurrect an unpick | Unpick removes all verified current-version copies within its bound target; remaining/failed removals retain truthful observed state. |
| Replacing outdated mapped files conflicted with deterministic legacy names | Add the current-version hashed copy and retain older export bytes; a conflicting exact name is an error without overwrite or alternative suffix. |
| Partial copies could falsely imply a pick | Stage/verify inside reserved metadata area, then atomically publish. Only final complete associated files count. |
| Rapid Space toggles race with physical work | Separate requested/observed status, serialize per-association changes and converge on latest intent with request/generation checks. |
| Per-folder target changes could block fast navigation or redirect queued work | Normal navigation leaves captured jobs in their original targets; drain/cancel affected work only for binding changes, project replacement or close. Retain writer ownership while jobs remain. |
| Reviewed history cannot be inferred from missing copies | Make cursor/history optional JSON; missing history means Unknown. Absent valid copy means Unpicked only when export comparison is known. |
| Filter/removal could shift focus incorrectly | Focus by photo identity. Space stays on the photo in All if needed; explicit filters use deterministic fallback/empty state. |
| Whole-library scans and full-image work hurt responsiveness | Incremental indexes, virtualized rows, bounded decode/cache, low-first/full-next and preview-priority background I/O. |
| Folder opening could prioritize adjacent/full work before the visible strip or queue the whole folder | Focused quick preview and visible thumbnails first; gradually warm the rest at relaxed priority, reprioritize on scrolling, and keep full refinement fair and memory-bounded. |
| WPF did not support macOS | Shared Avalonia/Skia-based UI and platform file adapters. Benchmark both OSes and native codec alternatives where needed. |
| Export destructive permission could be applied too broadly | Only associated designated-export targets and known owned staging/metadata are mutable; no sources, outside links, unmatched files or automatic root clearing. |

## Evidence and limits

Legacy inspection identified synchronous pick hashing/copying in `MainWindow.PickWorkflow.cs`/`Services.PickService.cs`, basename/size matching and exact MD5/Base64 filenames in `PhotoFileService`, the default `export` layout and target override in `MainWindow.PhotoLoading.cs`, and image-cache/cancellation issues in `ThumbnailService`/`MainWindow.PhotoSelection.cs`. The plan preserves the on-disk format, default layout and keyboard workflow, while replacing blocking work, false-positive matching and the explicit-target override bug.

The absence of a database does not make file I/O instantaneous. Completed copies/removals are recoverable; an in-memory operation still marked pending can be lost on interruption. Optional position/history is also lost if its metadata is removed, while picks survive as photos. These are explicit recovery semantics rather than hidden fallback guesses.

The unchanged legacy filename/index helper passed two byte-fixture round trips on 2026-11-06, recorded in the format contract. No replacement code, native platform test or benchmark has been executed. Implement in [roadmap](implementation-roadmap.md) order and prove the [acceptance criteria](acceptance-criteria.md).

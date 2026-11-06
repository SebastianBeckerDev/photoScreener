# Implementation roadmap

Follow the [architecture](architecture-plan.md), [contract](implementation-contract.md), [legacy-compatible export format](legacy-export-format.md) and [acceptance criteria](acceptance-criteria.md). The database and immutable snapshot-export packages are replaced by folder comparison and background mutations. Legacy compatibility is part of the core reads/writes, not a later import feature.

| Package | Deliverable | Dependency / exit evidence |
| --- | --- | --- |
| P-01 | Shared application foundation and measurement harness | New Avalonia app builds; versions locked; source/export interfaces enforce boundaries; legacy naming vectors pass; Windows/Mac build profiles defined. |
| P-02 | Keyboard/progressive-preview vertical slice | P-01; low/full stages, Space/arrow semantics, virtualized list, cache/generations and initial timing/memory gates demonstrated. |
| P-03 | Folder comparison and old/new completed-pick recovery | P-02; per-folder bindings, flat legacy-format indexes, verified associations, incremental counts and no-metadata resume work. |
| P-04 | Legacy-compatible background copy/remove and latest-intent convergence | P-03; exact legacy writes/read-back, safe staging/publication/deletion, errors/cancel/retry, root locks and race/crash tests pass. |
| P-05 | Optional position/history and external changes | P-03/P-04; optional JSON recovery, watchers/reconciliation and source/version changes work. |
| P-06 | Platform integration, package and full acceptance run | P-02–P-05; source safety, file-based resume and mixed-load performance gates pass on both platforms. |

## P-01 — Foundation

Create Core, Infrastructure and Desktop projects plus focused tests/performance runner. Pin .NET/Avalonia/SkiaSharp/toolkit versions and lock dependencies. Define read-only source operations, bounded associated-export mutation APIs, internal photo/relative-path identities, per-folder binding resolver, exact legacy filename helper, metadata interfaces and native platform adapters. Do not add a database dependency. Prove E-01 naming vectors against the old helper before implementing writes.

Build deterministic path/image/orientation/error fixtures and a real photographic JPEG/PNG benchmark corpus. Instrument key input, low/full publication, UI frames, operation queues, copying/deletion and working set. Define reproducible Release benchmark commands and Mac runner requirements.

## P-02 — Prove immediate review

Create the large fit-to-space preview, compact controls and collapsible virtualized strip. Implement Left/Right navigation, Space requested-status toggle without advance, repeat suppression and focus/filter rules in a deterministic in-memory session. No disk-completion label is shown for this prototype's in-memory decisions.

Add scaled low/full decoding, visible-range tracking, two bounded workers, visible-first thumbnail requests, adjacent prefetch, byte-budget cache and stale-result rejection. Add a gradual relaxed-priority folder-thumbnail warmer and disposable compressed disk cache; full previews remain focused-photo-only. Verify correct EXIF/mirroring, resize/DPI, corrupt files and actual virtualization with 10,000 photos. Run ≥1000 actions and 20 actions/second, plus L-01–L-05 for cold open, scrolling, folder changes, starvation and cache loss. Resolve decoder/dispatch/render bottlenecks before expanding the app; native adapters are introduced only where measurements justify them.

## P-03 — Recover picks from folders

Add default `<folder>/export` bindings with retained explicit overrides, protected-source/export boundary validation, export-subtree exclusion and background comparison of immediate flat target files. Stream folder data/export counts and keep unavailable/unknown/mismatched states explicit. Retain all legacy-name/length candidates and verify full content; cache associations by revisions in memory. Resolve duplicate/shared current-version associations explicitly. Project Picked view combines each folder's effective target index.

Prove R-01/R-03/R-04/R-05/R-07/R-09, S-03/S-04 and E-03/E-04/E-06/E-07: old/new resume with metadata removed, default/custom/shared targets, duplicate basenames, case/Unicode collisions, natural dotted names and missing storage. Measure new/visited folder opening without waiting for a full project scan. Keep source files read-only throughout.

## P-04 — Make Space update export files

Implement one serialized worker and a bounded latest-intent map, serializing shared associations by request sequence. Stage copies inside the captured export root; compute naming MD5 and verification SHA-256 in the same streamed source pass, flush/verify and atomically publish the exact flat legacy name without clobbering. Reuse only verified identical destinations and report short-suffix collisions; retain older exported versions. Unpick removes all verified current-version associated copies. Clean only owned staging. Capture each job's pairing and generation; retain writer ownership while pending jobs run across folder navigation, and cancel/drain affected work before changing its binding or closing/replacing the project.

Test rapid toggles during copy/delete, permission/lock/full-disk errors, source changes, substituted targets, aliases, cancellation and child-process interruption. Prove all source bytes/paths remain unchanged. Prove E-02 and E-05–E-09: new writes read back through the old index, collision protection, duplicate/shared unpick convergence, old-version retention and off-UI one-pass naming. Recover final picked/unpicked state solely from files. Repeat keyboard/preview performance under simultaneous mutations and verification reads.

## P-05 — Resume conveniences and external changes

Add optional atomic JSON position/filter/review history, binding hints and cache hints. Its absence/corruption cannot prevent pick recovery after default or explicit target resolution. Unknown reviewed history is not guessed from absent exports. Legacy reads/writes and actual associated paths already work in P-03/P-04; do not introduce a migration step.

Implement targeted watchers plus open/Refresh/overflow/reconnection reconciliation. Prove stale scans cannot reset newer requested changes and that external changes are observed. Test all remaining recovery cases, both metadata layouts if sharding is needed, and no blanket export cleanup.

## P-06 — Integrate and deliver

Run all functional/performance cases on Windows and Apple Silicon macOS, including equivalent physical-pixel preview sizes, Retina workload, removable drives, native permissions and process locking. Publish Windows self-contained output and a signed/notarized Mac `.app` with matching native image libraries.

Produce actual timing/percentile, working-set, operation-throughput, source-safety and no-metadata recovery evidence. Switch the regular launch path only after gates pass. Keep AI/TV and deferred features out of the replacement build. No speed or source-safety claim is established by documentation checks alone.

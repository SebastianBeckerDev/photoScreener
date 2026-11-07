# Implementation prompt

Use the following prompt to start implementation in this repository.

---

Implement the Photo Screener rewrite end to end in the current repository. Deliver a working, exceptionally responsive Windows/macOS desktop application that reviews photos by keyboard, copies picks into legacy-compatible export folders, and restores completed picks from those folders without a database.

Proceed directly with implementation. Make routine engineering decisions yourself, resolve ordinary issues, and continue through the work packages without asking for confirmation between steps. Keep working through implementation, debugging, validation and packaging; finish the work authorized here. Respect tool-enforced permissions and report any genuine external dependency you cannot resolve. Keep progress updates concise.

Read and follow these documents under `docs/reverse-engineered-specification/` before editing:

- `README.md`
- `implementation-contract.md`
- `legacy-export-format.md`
- `architecture-plan.md`
- `implementation-roadmap.md`
- `acceptance-criteria.md`
- `rewrite-decisions.md`
- `macos-support.md`

Treat them as the implementation contract. The legacy export format supersedes the earlier proposal to mirror source-relative paths into exports. Apply the resolved decisions when legacy code differs from the intended behavior. Inspect existing code for useful evidence and compatibility fixtures; preserve existing user work and the legacy application while building the replacement alongside it.

Priorities, in order:

1. **Performance:** Immediate keyboard feedback; visible-first, incremental folder loading; low-resolution focused preview followed by full-quality refinement. Bound workers, queues and caches, discard stale results, and keep enumeration, decoding, hashing, copying, deletion and metadata I/O off the UI thread. Warm remaining thumbnails gradually at relaxed priority. Never wait for a whole-folder scan or decode before showing the first viewport.
2. **Consistency:** Keep source photos read-only. Picking copies original bytes; unpicking removes only verified current-version associated export copies within the captured designated target. Stage and verify before atomic no-clobber publication. Separate observed state from requested state, coalesce rapid toggles to latest intent, and show pending/error states truthfully. Preserve unrelated files and older exported versions.
3. **Resume:** Completed export photos determine picks. No database or mandatory selection ledger. Optional JSON may preserve position, history, bindings and cache hints; losing it must not lose completed picks after default or explicit target resolution. Missing exports do not prove a photo was reviewed, and unavailable storage is not an empty folder.

Required behavior:

- Left/Right navigate without changing picks. Space inverts the current requested pick status without advancing; suppress auto-repeat and retain review focus. Handle filters and unknown association as specified.
- Give the focused image the largest available fit-to-window preview, preserving composition, aspect ratio and all EXIF orientations. Refine the same photo in background immediately; stale work must never replace the current preview.
- Default each folder to its own `export` child; retain explicit per-folder overrides. Write flat `<originalStem>.<hash4><originalExtension>` copies. `hash4` is the first four standard Base64 characters of the original bytes' MD5 digest, replacing `+` with `-` and `/` with `_`. Compute naming MD5 and verification SHA-256 during one background streamed source copy. Preserve original bytes and filename case. Resume old exports directly, verify content rather than trusting name/length or the short suffix, and reject different-content collisions without overwrite or alternative naming. Implement the contract's duplicate/shared-target association rules.
- Folder navigation remains responsive while mutations retain their original bindings. Protect source/export boundaries, reject unsafe aliases, retain writer ownership for pending work, and drain/cancel affected work on binding changes or close.
- Use C#/.NET 10, Avalonia, CommunityToolkit.Mvvm and scaled SkiaSharp decoding initially. Pin compatible dependencies. Isolate Windows/macOS filesystem adapters and introduce native decoding alternatives only when measurements justify them.
- Keep AI selection/training, TV output, and the documented deferred features outside the rewrite.

Execute P-01 through P-06 in dependency order. Prove the keyboard/preview slice early, then implement old/new folder comparison, verified background mutations, recovery, optional metadata, external-change reconciliation and platform packaging. Add meaningful tests for source protection, compatibility, concurrency, collisions, interruption and recovery, plus a real-window performance harness. Fix failures and measured bottlenecks before claiming their gates pass.

Use the acceptance document's exact scenarios and thresholds. In particular, demonstrate input-feedback p95 ≤16 ms/p99 ≤50 ms, uncached local-SSD low preview p95 ≤100 ms, full refinement p95 ≤1 second, and steady-state working set ≤512 MiB on the documented reference workload. Measure mixed-load behavior with mutations, hashing and thumbnail warming active. Record actual hardware, corpus, commands and results; do not infer UI performance from unit tests.

Provide reproducible build, test, benchmark and publish commands, Windows self-contained output, and macOS packaging/signing support. Run available Windows and macOS checks. If a Mac runner, representative corpus or signing credentials are unavailable, complete the independent implementation and validation work, provide the runnable scripts/configuration, and clearly mark the corresponding evidence or signed artifact as outstanding. Do not report unexecuted platform/performance checks as passing. Change the regular launch path only after its applicable acceptance gates pass.

Track progress and evidence in a concise repository document. Commit coherent implementation milestones locally, staging only relevant files and excluding IDE state, generated build output, private photos and credentials. Never push or publish externally unless separately authorized.

Finish with the implemented behavior, local commit IDs, launch instructions, validation results and any specific remaining external blockers. The goal is the functioning application and verified plan coverage; scaffolding or another plan does not complete it. Begin implementing now.

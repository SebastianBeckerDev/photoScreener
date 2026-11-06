# Acceptance criteria

Applies to the [core specification](README.md), [implementation contract](implementation-contract.md) and [legacy-compatible export format](legacy-export-format.md). These are replacement-app targets/tests, not executed results. The format document separately records two executed legacy-helper fixtures. Picks are recovered from export files, and destructive operations are confined to designated exports.

## 1. Performance

Windows reference: six or more CPU cores, 16 GiB RAM, local SSD, Release build, 1920 × 1080 physical preview area. Add an Apple Silicon/macOS profile with comparable physical pixel workload; measure larger Retina workloads separately. Record actual OS/hardware/build. Corpus: 10,000 JPEGs in a folder, 100,000 files across a source tree, 12–45 MP up to 40 MiB; test PNG separately.

| Measurement | Initial gate |
| --- | --- |
| Input to focus/counter/requested-pick feedback | p95 ≤16 ms; p99 ≤50 ms. |
| Prefetched neighbor to low preview | p95 ≤50 ms; p99 ≤100 ms. |
| Uncached local-SSD photo to low preview | p95 ≤100 ms; input stays responsive. |
| Stable focus to full-quality original-derived preview | p95 ≤1 second; never delays low preview or Space. |
| Visited folder to usable restored focus/preview | p95 ≤300 ms with a warm in-memory index; comparison can continue. |
| New folder to first usable preview | p95 ≤1 second; discovery continues independently. |
| Initially visible thumbnail region on the local-SSD corpus | p95 ≤1 second for readable tiles; placeholders/errors for slow/broken files never block other visible tiles or current refinement. |
| Steady-state working set during large-folder review | ≤512 MiB; no library-sized bitmap accumulation. |

Measure ≥1000 actions and report cold/warm cases separately. Navigate at 20 actions/second for 60 seconds, with copying, deletion, comparison hashing and optional JSON writes active: no stale preview, wrong-photo action, growing obsolete preview queue, or degradation of warm feedback gates. Report pending mutations and bytes/second separately; there is no promise that a 40 MiB copy completes in 16 ms. Test slow/removable/network storage for responsiveness, truthful status and cancellation rather than fixed image/copy availability times.

Add a cold-folder lazy-loading trace: only the focused quick preview and visible tiles receive first-image priority; hidden thumbnails begin warming only after the initial viewport is usable and resources permit. Re-run mixed-load measurements with warming and disk-cache writes enabled. At rest, remaining thumbnails make incremental progress within fixed queue/RAM/cache budgets, without decoding all full previews.

| ID | Lazy-loading scenario | Required result |
| --- | --- | --- |
| L-01 | Open a cold 10,000-photo folder. | Focused low preview and visible tiles load first; no full-folder decode/compare barrier, UI remains responsive and bounds hold. |
| L-02 | Scroll far away or navigate during warming. | New visible/focused work is promoted; old urgent offscreen requests are removed; no stale-image publication. |
| L-03 | Change folders while metadata/thumbnail jobs are pending. | Old warming stops and results cannot enter the new folder's UI. |
| L-04 | One visible image is slow/broken; leave the current image open. | Other visible tiles and current full refinement continue; no starvation or blocked first response. |
| L-05 | Leave the folder idle, then revisit with cache missing/corrupt. | Remaining small thumbnails warm gradually; cache loss regenerates lazily and never changes recovered picks or causes unbounded RAM use. |

## 2. Source safety and export consistency

| ID | Scenario | Required result |
| --- | --- | --- |
| S-01 | Pick/unpick repeatedly, pick a changed source version, cancel/retry, restart. | Source photo paths/bytes remain unchanged; only associated designated-export files and owned metadata/staging change; older exported versions remain. |
| S-02 | Pick, wait for completion, unpick, wait for completion. | A complete byte-identical flat legacy-named copy appears, then all its verified current-version associations are removed; source remains. Resume shows Unpicked. |
| S-03 | Same basename/length in different source folders; natural dotted names. | Independent picks in separate per-folder targets. Shared targets distinguish different content; identical name/content has an explicit shared association. Suffix parsing alone never establishes identity. |
| S-04 | Export equals/contains source, a path alias/link escapes protection, unrepresentable case collision, ambiguous legacy match or unrelated target. | Explicit failure; no source/outside-root mutation or silent wrong-file replacement. A designated nested export is excluded from the source scan and may be used safely. |
| S-05 | Toggle rapidly during a copy/delete, navigate away, change binding/project after finishing/canceling affected work. | Navigation stays responsive while captured jobs finish in their original targets; serialized mutations converge on latest intent, including shared associations; callbacks cannot enter the wrong context. |
| S-06 | Full disk, locked target, source edit, verification/publish failure, cancellation or process kill. | No partial final file classified Picked; failed deletion remains observed Picked; owned staging is identified; no false completion. |
| S-07 | Rapid navigation/late full decode; broken or unsupported image. | Preview, filename and focused ID agree; no stale result or wrong-photo Space action. |
| S-08 | Filter excludes current photo, including last Picked item. | Explicit filter changes give consistent fallback/empty states. Space keeps the current photo in All. |
| S-09 | Left/Right, Space twice, held Space, text/dialog input. | Arrows change focus only; distinct Space presses invert requested status on the same photo; repeat suppressed and text/dialog keys preserved. |
| S-10 | Resize/maximize/collapse strip; portrait/landscape/mirrored EXIF. | Largest complete aspect-preserving preview, consistent low/full orientation and stable focus. |
| S-11 | External export add/remove/replace, watcher overflow or disconnect. | Reconciliation reflects actual copies; unavailable root never appears as empty; pending intent is not reset by a stale scan. |
| S-12 | Resume/switch/retry with unrelated files in export root or other exports. | No blanket cleanup or mutation of unmatched files/other roots. Only verified current-version associated targets and owned staging may be removed. |

## 3. Recovery without a database

| ID | Scenario | Required result |
| --- | --- | --- |
| R-01 | Complete 8 picks, remove all app settings and `.photoscreener` metadata, reopen the source/export pair. | Recover those 8 picks from completed export files alone. |
| R-02 | Close/restart after completed pick/unpick; kill during a staged copy. | Completed file state is recovered; interrupted staging is not a pick; no guarantee is claimed for an unfinished request. |
| R-03 | Missing/corrupt/read-only optional JSON. | Picks remain recoverable; position/history fall back with explicit Unknown status, never guessed reviewed completion. |
| R-04 | Disconnect/reconnect either root; add/replace a source revision. | Preserve existing export bytes, recheck association/versions, and report unavailable/outdated state correctly. |
| R-05 | Old/new hash-suffixed or verified unsuffixed copies; newer pending unpick during verification. | Full-content association works without a ledger or migration; unresolved identity needs a choice, duplicate/shared associations follow the format contract, and late scans cannot undo newer intent. |
| R-06 | Optional position/history file works; alternate folders and reopen. | Restore each folder's position and known reviewed history independently of pick inference. |
| R-07 | Project-wide Picked view before the full source tree is scanned. | Combine each discovered folder's effective target index progressively; never use the last-active target for every folder. Unknown totals and missing-source items are explicit. |
| R-08 | Open the same export root in two app instances. | Only one instance can perform mutations; read-only/activation behavior is explicit. |
| R-09 | Resume an existing nested `export` directory without settings/metadata. | Automatically resolve the source folder's `export` child, restore verified associations without scanning exports as sources, and keep mutations inside that target. |

No-copy means Unpicked only when the relevant export enumeration/association is known. It does not prove the photo was assessed. Pending/error statuses are tested separately from observed file state.

## 4. Legacy-format read/write compatibility

| ID | Scenario | Required result |
| --- | --- | --- |
| E-01 | Golden byte fixtures `abc` and `message digest`; dotted/Unicode stems, uppercase extensions, suffixes containing `+` or `/` before substitution. | Exact legacy MD5/Base64-first-four output, preserving stem/extension case; filename-safe `-`/`_` substitutions. Same bytes/name produce the same result on Windows and macOS. |
| E-02 | Pick in the rewrite, build the unchanged legacy target index, unpick, then re-pick. | The old filename/length lookup recognizes the new completed copy; unpick removes the association; re-pick recreates the exact same name. No migration or metadata dependency. |
| E-03 | Resume real old exports, with all settings/metadata absent; include bare copies, natural four-character dotted stems and equal-length different content. | Verify current-content matches in background and retain actual filenames. Reject false name/length positives; if different original names with identical content fit one target, require explicit association before deleting. Leave unrelated files untouched. |
| E-04 | Open two source folders with existing `export` children; select a custom target for one, leave/return, restart without metadata. | Default per-folder flat layouts work; no source-relative target subdirectories. Explicit overrides stick in-session; after metadata loss the custom target is selectable again. Opening a folder never creates an empty target. |
| E-05 | Destination already has identical bytes, different bytes at the expected name, or two sources collide in the short suffix. | Reuse only a verified identical destination. Different content gives a collision error with both source/existing bytes unchanged; no overwrite or alternative suffix. |
| E-06 | Explicitly share a flat target across folders with identical original names, then with equal-length different content. | Identical bytes share observed/requested state and serialize by latest request sequence. Different content has separate associations/legacy names when suffixes differ; same length never merges picks. |
| E-07 | Several verified copies of a current source revision; unpick, including one locked duplicate. | Remove only verified current-version copies; successful unpick stays Unpicked on restart. A remaining locked copy yields truthful observed Picked/error, not false completion. |
| E-08 | Source changes after a completed export; pick the current version and then unpick it. | New current-version legacy copy appears when its name is available; old exported bytes remain. Unpick removes only the current-version association. A short-suffix collision follows E-05. |
| E-09 | Trace pick while navigation, first preview, warming and verification run. | MD5 naming and SHA-256 run during one background streamed source copy, with staged-output verification separately; no filename-hashing pass in the key handler or whole-folder startup. Input/preview performance gates still pass. |

Execute legacy-helper read-back in a Windows test harness or use checked-in golden fixtures if the helper cannot run on the Mac runner. Record replacement-app results separately from the already verified legacy byte fixtures.

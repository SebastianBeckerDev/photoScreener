# Implementation contract

Implements the [core specification](README.md) and [legacy-compatible export format](legacy-export-format.md). Current choices are C#/.NET 10, Avalonia, progressive native-backed image decoding, **folder-authoritative picks**, and no database. The [architecture](architecture-plan.md) supplies initial resource allocations; [acceptance criteria](acceptance-criteria.md) define the gates.

## Source/export mapping and authority

- Each reviewed source folder has an effective export binding: `<folder>/export` by default, or an explicit custom target retained for that folder. Reuse existing targets and create missing ones only on the first pick. Reject an export equal to or containing protected sources. Exclude all `export` subtrees and designated custom targets from source discovery before enabling mutations; protected source-photo paths and export mutation paths must be disjoint.
- Destination path is `exportRoot / (originalStem + "." + hash4 + originalExtension)`, with the exact MD5/Base64 algorithm in the format contract. Writes are flat and preserve original bytes, stem and extension case. Source-relative paths remain internal photo IDs, not destination directories. Respect filesystem case/Unicode behavior; unrepresentable or different-content collisions are explicit errors.
- Source and export indexes are background-built in-memory metadata, not durable truth. Confirmed picked means a complete matching associated export exists; pending requests do not make a photo confirmed picked. Failed/unavailable enumeration cannot establish Unpicked.
- First association/content uncertainty is resolved in the background. Original filename (including a possible legacy suffix removal) and length narrow candidates; retain every candidate rather than the old first-match lookup. Full checksums establish ambiguous or unverified content matches, including verified unsuffixed older copies. Cache verified matches by source/export revision (file identity where available, length and modification time). A changed revision invalidates the cache and becomes Checking/outdated, not a silent current-version match. Never use the short suffix alone as identity.
- Default per-folder targets keep equal basenames independent. In an explicitly shared flat target, equal original names and identical content share an association and pick state; serialize mutations by that association and keep all associated rows consistent. Different content requires separate associations. Unpick removes every verified current-version copy of its association within the designated target; it never deletes older versions or unresolved matches.
- Comparison snapshots carry project/folder/index generations. Apply observations only to the right context, and never let a late scan overwrite a newer pending user intent. Successful copy/delete immediately refreshes its affected index entry; do not rescan the entire export root after each action.
- Watchers are hints, not a guarantee. Reconcile on open, explicit Refresh, after mutations, and after notification overflow/reconnection. External export changes update observed picks after reconciliation without silently fighting the external editor.

## Interactive path

- Left/Right changes focus; Space changes requested pick status for the photo captured at keypress and preserves focus. No filesystem work occurs in the key handler. Ignore Space repeat until release, and scope shortcuts to the review surface.
- Keep virtualized thumbnails and lightweight folder metadata. Never allocate a bitmap/control/task for each file in a library. Update the changed row and counts instead of refreshing all rows on a pick.
- Publish the focused low preview first and prioritize currently visible thumbnails before offscreen work. Then prefetch adjacent low previews and refine the current photo from its original while it remains current. Use the same aspect-preserving fit/EXIF policy at both stages. A fit-to-screen full-quality render need not retain a native-size bitmap beyond the display's needs.
- Cache by photo/revision/stage/target size/rendering policy, evict by bytes, refresh recency on hits, and bound transient decode allocations.
- Cancel/coalesce obsolete preview requests and check photo ID, revision, view context and request generation before preview publication/display. Mutation publication validates its captured binding and operation generation, not whichever folder is currently visible. Native calls may not cancel immediately; fixed workers prevent accumulation of abandoned tasks/threads.
- Prioritize focused low preview and visible thumbnails, then current refinement/near-neighbor previews, over remaining-folder warming and comparison hashing/copying. Background file work yields between bounded chunks when foreground work is pending. There are no fixed delays on navigation or pick feedback.

## Folder startup and lazy loading

1. Publish lightweight discovered rows in small dispatcher batches and keep unknown counts explicit. Do not read EXIF or decode every source image while enumerating the folder. The first image/viewport must not wait for complete source/export enumeration, checksum matching, optional JSON or thumbnail warming.
2. Compute the current visible range after layout, including the focused photo. Request its quick preview and those visible thumbnails; other rows remain placeholders. Use cached artifacts when valid, without synchronously reading cache files on the UI thread.
3. After the first viewport is usable, continue discovery and lazy thumbnail generation for the remaining folder at low background priority. Walk a bounded work cursor instead of enqueueing a task/request for every image.
4. Scrolling, resize or focus movement replaces the visible-range generation, promotes the new viewport and removes obsolete offscreen requests from the urgent queue. A small near-viewport margin can be prefetched; it must not outrank visible items.
5. Cache small compressed thumbnail artifacts in disposable local application cache storage if needed to retain warmed results beyond the RAM budget. Keep decoded bitmaps byte-bounded. Cache absence/corruption does not affect authoritative picks, and cache creation never changes source photos.
6. Cancel warming on folder/context change, skip obsolete generations, and pause it when interactive, CPU, memory or storage pressure would threaten latency gates. Relaxed scheduling includes lower internal priority, bounded concurrency, nonblocking UI publication and platform background priority where supported. Do not wait or synchronously invoke the UI from a decoder worker.

Visible-thumbnail completion cannot indefinitely delay current full-quality refinement; use bounded/fair scheduling after the initial viewport so the existing refinement timing gate still holds. Remaining-folder warming never loads all full-resolution previews or requires the whole folder to remain decoded in memory.

## Operation state machine

Keep `ObservedPicked`, `RequestedPicked`, operation generation and status separately. Space toggles the latest requested status, including during Copying/Removing, rather than reading a stale observed flag. Once the latest request finishes, requested and observed status agree. During unknown association, retain toggle parity against that photo and resolve it asynchronously: two Space presses cancel out, and one inverts the resolved observation. Do not guess a destructive legacy-file target.

| State | Meaning |
| --- | --- |
| Checking | Export contents/association are not yet established. |
| Picked / Unpicked | Confirmed observed state after comparison or completed mutation. |
| Copying / Removing | Requested change is pending; physical state has not yet converged. |
| Error / Outdated / Unavailable | Requested operation or association needs attention; actual copies are retained and reported. |

Serialize mutations for each export association; begin with one mutation worker overall. Keep requests against photo IDs while comparison is unknown, then resolve shared associations using a monotonic request sequence so the latest intent across associated rows wins. Coalesce pending requests to their latest desired state instead of queueing repeated copies/removals. Bound the pending-photo set. If it cannot retain a request, reject it explicitly while navigation remains available. A slow disk can cause pending work; feedback latency and copy-completion latency are different measurements.

On error, refresh actual state, keep the failed requested target visible for retry, and never show a completed result without checking operation generation. A newer request may require a follow-up action if an older atomic publish/delete had already started. Do not apply an older completion as the latest requested result.

## File mutation protocol

1. Capture source folder/root, its effective export root, internal photo ID, revision, association and operation/request generation. Pin/validate the canonical boundaries. Only export-adapter operations may mutate files; browsing another folder cannot redirect a captured job.
2. For a pick, create a unique owned staging file in `exportRoot/.photoscreener/staging` using create-new semantics. Metadata/staging reside below the flat photo target and cannot count as picks. Never write through an existing target handle.
3. Read the source consistently and stream-copy in background chunks, computing both MD5 for the legacy name and SHA-256 for verification in that same source pass. On Windows exclude concurrent writes using supported read-handle sharing; on macOS revalidate identity/revision around the copy and fail on detected edits rather than claiming sharing flags forbid another writer. Flush and verify staging bytes before publication; no separate source pass is needed just to calculate the filename.
4. If the latest request is still picked, atomically publish on the same filesystem directly to the exact legacy filename with no-clobber semantics. Reuse a concurrently existing destination only after full-content verification and identity checks prove it is the same photo. A different-content/short-suffix collision is an explicit error: no overwrite or alternative naming. A newer source version does not automatically replace or delete older exported versions.
5. If the request became unpicked, discard owned staging output. If publication already occurred, the serialized next action removes the associated copy and converges on the latest request.
6. For an unpick, verify every associated current-version target remains a regular file inside the job's designated root and has not been substituted since association. Delete only those verified copies. Already absent files are successful removals; confirm Unpicked only when no matching copy remains. A locked, replaced or ambiguous target is an explicit error/recheck, and any remaining copies stay observed Picked.
7. Reconcile the affected export entry and report completion only for the applicable generation. Cleanup may delete app-owned staging files; never recursively remove roots/directories or unmatched photos.

Do not follow source/export symlinks, junctions or directory links in the core release. Reject aliases that route mutations into source/outside paths and export files aliasing the source file itself. Use platform path/handle validation to guard substitutions. Photo publication uses a new file with no-clobber semantics, never in-place truncation that could modify an aliased source.

Only final associated photo files count as picks; `.photoscreener` contents never do. A crash during copying leaves staging, not a partial file falsely classified Picked. After interruption, verified final files restore picks; inspect and remove only identifiable owned staging files. Pending uncompleted requests are not promised to survive a crash without metadata: file contents determine the recovered state.

## Optional metadata and recovery

`.photoscreener/review.json` may contain schema version, source-root/binding hints, active view/filter, folder cursors, reviewed-revision keys and verified-match cache. It must not be an authoritative picked/not-picked ledger. Atomic background JSON replacement is permitted inside the export root; a failed write reports loss of position/history persistence but does not disable already confirmed selection recovery. Default folder targets are rediscoverable without hints; an unknown custom target must be explicitly selected again.

Missing/corrupt metadata means unknown reviewed history and default focus, followed by export comparison. Verified existing copies imply prior picks; absence only means Unpicked, not reviewed/rejected. Initial folder comparison and low preview availability never wait for optional metadata reconstruction or a recursive project scan.

Old and new exports use the same flat hash-suffixed write format and association path. Keep actual verified filenames for targeted unpick; do not migrate old files. Outside-root exports may be read as recovery inputs but cannot be mutated until explicitly designated; unresolved identity requires a reviewer choice. Late association must not overwrite newer pending actions. The old app's weaker name/length matcher is not reproduced as a deletion authority.

Normal folder navigation leaves captured mutations running and releases obsolete preview/thumbnail work. Close, project replacement or a binding change drains or explicitly cancels the affected pending mutations; remain open on an unresolved operation error unless the reviewer chooses to cancel/leave it. Operations already completed remain completed. Retain each designated export's writer ownership while it has accepted pending work. One rewrite writer instance per export root prevents rewrite-app races; the old app and external editors do not participate in those locks and still require identity checks/reconciliation.

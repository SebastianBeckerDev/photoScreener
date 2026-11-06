# Windows and macOS platform details

The [architecture](architecture-plan.md) now targets both systems with a shared Avalonia UI, C#/.NET 10 and folder-authoritative picks using the [exact legacy export format](legacy-export-format.md). No database or native SQLite dependency is required. Optional review JSON is stored inside an export's `.photoscreener` area and is portable; matching exports recovers picks without it.

## Shared behavior

Left/Right navigation, Space inversion without advance, preview fit/orientation, low-first/full-next loading, requested-versus-observed state, comparison, internal source IDs, per-folder export bindings and optional history are shared. Default target is `<folder>/export`; new writes are flat `<stem>.<hash4><extension>` copies. MD5/Base64 naming is identical across platforms and runs with SHA-256 during background copying. Start the image benchmark with scaled SkiaSharp JPEG/PNG decoding; introduce WIC/ImageIO adapters only if a specific platform needs them for the performance gates.

| Platform concern | Windows | macOS |
| --- | --- | --- |
| App/rendering | Avalonia with matching native rendering/image dependencies | Same UI; test Retina physical-pixel sizes and keyboard focus. |
| File identity | Handle-based volume/file identity where available | Device/inode identity where available; never assume identities are portable between OSes. |
| Source reads | Supported read-handle sharing can exclude writes | Revalidate source revision before/after copying; ordinary sharing flags do not prevent arbitrary external edits. Fail on detected changes. |
| Atomic export publication | Native same-volume no-clobber photo publication; atomic optional-metadata replacement | Same semantics with collision/no-follow checks; never expose partial final photos. |
| Target deletion | Verified current-version associated regular files in the job's designated export only | Same bounded operation, including duplicate/shared associations; do not follow symlinks or recursively clear roots. |
| Case/Unicode | Respect actual source/export volume behavior | Test case-sensitive and case-insensitive APFS and Unicode naming; preserve names and flag unrepresentable collisions. |
| Instance ownership | Platform writer ownership for each designated target with accepted pending work | Same ownership among rewrite processes; the old app/external editors do not participate and still need reconciliation. |
| Folder access | Native pickers and filesystem errors | Native pickers and access-denied/revoked handling. Sandboxed/App Store distribution additionally needs persistent security-scoped bookmarks. |
| Local logs/cache | Per-user local app-data directory | Per-user Application Support/cache directory; not authoritative pick storage. |
| Delivery | Self-contained `win-x64` output | `.app` initially `osx-arm64`, signed/notarized for distribution. Add Intel `osx-x64` only with its own build/test profile. |

Keep protected source-photo and export mutation paths disjoint, reject links/aliases that escape protection, and stage on the destination filesystem so completed publication is atomic. A designated nested export is excluded from all source discovery; an export equal to or containing the source root is rejected. Treat copy failure, deletion failure and unavailability identically in the UI across platforms. Completed picks are derived from files; no unsupported cross-platform locking guarantee should be presented as a durable state acknowledgement.

When moving old/new export folders between systems, choose the corresponding source folder and target (or retain each folder's default `export` child). Original names, copied bytes and deterministic hash filenames retain selections without conversion. Optional cursor/cache/binding metadata may need revalidation against local filesystem identities; it cannot override file contents. Unrepresentable destination names/collisions fail explicitly. Simultaneous/cloud-synchronized editing is outside the core scope.

## Validation and packaging

P-01 must pin mutually compatible .NET/Avalonia/SkiaSharp versions, determine their minimum supported macOS release and prove exact legacy naming vectors. P-02 must prove the same real keyboard/preview pipeline on a Mac runner, not extrapolate Windows timings. P-03/P-04 must test existing Windows exports on macOS, exact new write filenames, default/custom bindings, native stage/publication/delete, source edits, aliases, locking, permissions and interruptions on both systems. P-06 must package the correct native image libraries and exercise an installed application with no separately installed .NET runtime.

Compare performance at equivalent physical preview resolution and report additional Retina workloads separately. No Mac environment, package signing or platform performance has been exercised in this Windows working copy.

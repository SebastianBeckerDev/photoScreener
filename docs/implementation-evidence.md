# Rewrite implementation and evidence

The replacement is in `src/`. On 2026-11-06 the user explicitly requested removal of the old WPF version, superseding its original preservation requirement. The WPF project, old solution/launcher and its IDE cache have been removed; the isolated compatibility fixture remains. Start the rewrite with `buildAndRunRewrite.bat`. Contract: `reverse-engineered-specification/`.

| Package | Implemented deliverable | Executed evidence |
| --- | --- | --- |
| P-01 | Independent projects, pinned SDK/packages, locks for Windows x64/Mac ARM64, read-only sources and native identities | Locked Release builds/tests; unchanged legacy-helper snapshot and filename vectors |
| P-02 | Keyboard review; incremental low/full fit preview; eight EXIF orientations; virtualized strip; fixed workers; 64 MiB decoded cache; disposable disk cache and relaxed warming; pointer zoom/pan | Real Windows window runs; 10,000-row/recycled-tile checks; low/full quadrant-pixel orientation tests; anchored wheel/pan and higher-resolution zoom tests |
| P-03 | Default/custom per-folder targets; full-content old/new/bare-copy associations; duplicate/shared rules; ownership dialog; progressive project picks | Eight-of-ten no-metadata recovery; false-positive/ambiguity/version/default/custom-target cases |
| P-04 | Bounded serialized latest-intent worker; captured targets; one-pass source MD5/SHA-256; verified staging/publication/deletion; ownership; cancel/drain/retry | Legacy read-back; real short-suffix collision; shared convergence; substitution/hardlinks/locked duplicates; four child-process kill/recovery phases |
| P-05 | Optional atomic cursor/filter/history; watcher hints and Refresh; unavailable/empty distinction; revision/stale-result guards | Missing/corrupt/null JSON; maintenance ownership; external changes; saved-filter/cursor restoration; retained focus on first export creation |
| P-06 | Self-contained Windows/Mac payloads; bundle/plist/entitlements/signing scripts; platform CI | Current Windows package build/start smoke passed; an earlier package passed 1,200-action mixed load, and current framework-dependent code passed the 100,000-row diagnostic below. Current Mac cross-publish/bundle inspection passed for 18 ARM64 native binaries (maximum required macOS 12, profile 14+); Mac execution/signing and reference acceptance remain outstanding |

## Windows verification

Latest Release tests: 50 core/infrastructure and 25 Avalonia headless cases pass (75 total), using the same solution-wide command as CI. Debug desktop build is refreshed. Windows-only native sharing/rename cases are not evidence for macOS. Full disk, physically unplugged devices and network volumes have not been exercised.

Single-photo mutation checks now index only that original's bare/legacy-suffixed candidates, including before deletion and during failure recovery; folder open/Refresh still builds the complete index. A real filesystem test locks an unassociated file with the same stem prefix and completes pick/unpick without opening it. Full/association scan and candidate-revision counters are included in benchmark reports. Verified shared associations are visible in photo status/tooltips and retain their known relationship across an empty-export reconciliation when a native source change token is available. The UI regression covers two identical originals, unpick/repick, then an external edit in the inactive folder: the worker's fresh source census cancels stale optimistic propagation and reopening rebuilds that folder's rows.

The first public GitHub Windows job failed only during fixture disposal after a killed process, with a transient sharing violation on the source fixture. Cleanup now retries only Windows sharing/lock violations for at most three seconds, after checking the temporary root boundary. A subsequent job encountered a transient writer-lock hold after confirmed child exit; the recovery fixture now waits up to three seconds for ownership before running cleanup and assertions once. All four kill/recovery cases pass locally. Recovery/source-safety assertions are never retried. Windows run [42419080908](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42419080908) passed the full 75 tests and self-contained publish.

CI uses the standard GitHub `macos-15` ARM64 runner ([available runner images](https://github.com/actions/runner-images#available-images)). Its first native execution exposed the Darwin ARM64 variadic `openat` ABI mismatch: creation permissions were being passed in a register instead of the required stack location. Commit `b91dcb1` corrects that call. The Mac job in [42419921630](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42419921630) passes all 50 infrastructure and 25 desktop tests, ARM64 verification, self-contained publish, plist validation and unsigned bundle assembly. Signing/notarization and native reference performance remain unverified. A short actual-bundle window/runtime smoke is now configured separately; it is pending execution.

The 2026-11-07 binding safety follow-up checks actual directory identity before using warm folder rows or mutating a captured target, and again after first creating a missing export root. Different path keys for the same physical directory are rejected with a consistent-spelling message. Windows paths containing trailing dots/spaces and case-sensitive directories that the session's case-insensitive keys cannot represent fail explicitly. Native tests use an extended-path alias, a trailing-dot alias, and an actual NTFS case-sensitive directory with two distinct `a.JPG`/`A.JPG` originals and `one`/`One` subdirectories. Rejected requests preserve the sources and existing export copy; library discovery reports the folder collision. Ordinary Windows case variants remain valid. The Mac case-sensitive/insensitive branch is runnable but not executed here. The Debug app and both platform payloads are rebuilt with these changes and the subsequent folder-list fix.

Latest Windows self-contained functional smoke: `artifacts/performance/windows-package-safety-smoke.json`, bundled .NET 10.0.11 with installed-runtime lookup disabled, 40 handled keys, eight completed mutations, four full refinements, no ignored keys or export/maintenance errors. This short cold-start run is not a percentile acceptance run (its p99 feedback is 53.899 ms). The full framework-dependent tree-scale run below supplies the latest mixed-load diagnostic evidence. The refreshed Mac bundle has 18 ARM64 native binaries with a maximum macOS 12 minimum, within its macOS 14+ profile; execution/signing on a Mac remain unverified. Git Bash syntax validation of the Mac packaging/benchmark scripts passed.

Tests perform actual filesystem mutations and child-process interruption, rather than simulating a pick database. The unchanged legacy helper recognizes new completed exports. Kill/recovery phases cover staging, completed publication, before deletion and completed deletion. Cleanup preserves an unrelated staging file.

The UI offers pending/error states, retry/cancel, a work panel and tooltips on every button. Optional JSON never establishes picks. Its saved filter/cursor applies after association checks and only if navigation has not already begun. Creating a default export does not reset the focused photo.

## Windows measurements

First real photographic run: `artifacts/performance/private-real-camera-usb.json`, 2026-11-07. The user-provided archive contains 116,716 JPEG and 94 PNG filenames across 776 directories (about 557.7 GiB). The selected camera folder contains 3,435 JPEG filenames; 99 of 100 evenly spaced header samples are readable 20.668 MP images, with one unreadable 4 KiB file. Sampled files average 8.25 MB. This is a real photographic workload, but it does not supply the required single 10,000-image folder. The source is a Samsung T7 over USB with exFAT; exports/cache are in a new user-temporary directory outside the repository. Private folder/file names and raw reports remain ignored local artifacts.

| Real-camera USB run | Result |
| --- | --- |
| Actual physical-pixel preview surface | 1919 × 1080; requested 1920 × 1080 |
| Inputs / ignored keys | 1,200 in 60.005 seconds / zero |
| Input feedback p95 / p99 | 1.002 / 2.184 ms |
| Drawing submission p95 / p99 | 8.075 / 12.013 ms |
| First usable preview | 191.076 ms; one opening sample |
| Stable decoded-cache cold low / full p95 | 544.375 / 1558.913 ms; 100 samples |
| Peak sampled working set / decoded cache | 356.980 / 63.959 MiB |
| Image queue / pending mutations / realized tiles maxima | 21 / 2 / 15 |
| Completed mutation results / copied bytes / hash-read bytes | 240 / 903,936,999 / 2,922,191,437 |
| Full / association export scans; candidate revision reads | 3 / 360; 120 |
| Review JSON writes / thumbnail disk writes | 712 / 0 |
| Export or maintenance errors | None |

Feedback and RAM meet their numerical limits in this run. Preview latency does not meet the low/full limits; the source is removable exFAT, and decoded-cache cold does not mean uncached physical storage. Without a reliable native change token, existing cache hints are deliberately not trusted. A bounded 100-image copy (779.2 MiB) on the internal NTFS SSD is prepared to separate storage/cache behavior from decoding. Its comparison results are pending. The first USB attempt lost window activation during input and correctly rejected 587 ignored keys; `private-real-camera-usb-invalid-focus.json` is invalid evidence.

The benchmark now supports `-PrivateSource`, keeping photo copies/cache outside the repository, and requests a configurable physical-pixel preview before timing. Reports retain the actual platform-constrained size and source change-token availability. The header-inspection tool reads at most 100 encoded headers without decoding or changing originals. No private photos are committed or bundled.

Earlier full self-contained package run, including photo information and centered thumbnails: `artifacts/performance/windows-package-information.json`, 2026-11-07. The app used its bundled .NET 10.0.11 with installed-runtime lookup disabled. The synthetic mixed folder contained 10,000 files; the information sidebar was visible, giving a 1209 × 647 physical preview area. The current package smoke above and current tree-scale run below also include the subsequent binding/list fixes.

| Measurement | Latest package result |
| --- | --- |
| Inputs | 1,200 in 60.007 seconds |
| Input feedback p95 / p99 | 1.253 / 3.782 ms |
| Drawing submission p95 / p99 | 16.849 / 18.198 ms |
| First usable preview | 413.562 ms; one opening sample |
| Stable decoded-cache cold low/full p95 | 7.080 / 19.917 ms; 100 samples |
| Peak sampled working set | 378.297 MiB |
| Decoded cache / image queue / realized thumbnails maxima | 63.942 MiB / 12 / 11 |
| Mutation results / copied bytes / hash-read bytes | 240 / 202,584,237 / 238,036,701 |
| Optional review JSON writes | 1,138 |
| Export and maintenance errors | None |

These diagnostics do not establish representative photo-corpus, uncached OS/SSD, physical-display or Mac acceptance. Historical reports document the code at the time of each build; the current package smoke and tree-scale measurements identify the latest verified scope.

The latest framework-dependent tree-scale run is `artifacts/performance/metadata-tree-window-fixed.json` (2026-11-07, .NET 10.0.14). The initial mixed 10,000-file folder produced a usable preview in 264.305 ms (one opening sample). The runner then loaded and retained 100,000 actual source rows across 100 image folders plus their empty project root, recovered 800 fixture exports in 47.463 seconds, and returned to the mixed folder for review. The tree files are 64 × 48 JPEG diagnostics, not the photographic reference workload.

| Tree-scale mixed review | Result |
| --- | --- |
| Inputs / ignored keys | 1,200 in 60.011 seconds / zero |
| Input feedback p95 / p99 | 1.003 / 3.020 ms |
| Drawing submission p95 / p99 | 15.679 / 17.014 ms |
| Peak sampled working set, including tree scan and review | 486.660 MiB |
| Cache / image queue / realized-thumbnail maxima | 63.883 MiB / 14 / 11 |
| Mutation results / copied bytes / hash-read bytes | 240 / 202,584,237 / 239,145,501 |
| JSON writes / export or maintenance errors | 406 / none |

The first tree run (`metadata-tree-window.json`) exposed duplicate visible rows when a programmatic All-filter reset repopulated an old cached folder during reopening. It also recorded fewer processed review keys than injected keys. That report is invalid as a mixed-input acceptance run. Folder initialization now suppresses the filter callback, a headless regression checks unique rows and immediate arrow input after returning from Project Picked, and the runner verifies the All list against its folder collection. It activates the review window at injection start and rejects any run containing ignored keys, reporting the first ignored focus contexts. The fixed run above has exactly 10,000 visible rows and all 1,200 keys handled. No representative-corpus or Mac gate is inferred.

Hardware: Core i5-1345U, 10 physical/12 logical cores; 16,849,256,448 bytes RAM. D: is a Samsung PM9B1 1 TB NVMe SSD. Windows 10.0.26300; SDK 10.0.303; framework-dependent runs use .NET 10.0.14. Physical preview 1439 × 647, below the required 1920 × 1080.

Diagnostic corpora: 100 generated 4000 × 3000 JPEGs (12 MP, roughly 1.7 MB each); a 10,000-file folder containing those 100 plus 9,900 64 × 48 JPEGs; and the 100,000-file small-image tree described above. Seed 813, JPEG quality 90. These are synthetic diagnostics, not the required 12–45 MP photographic corpus/tree. The mixed folder's original generator metadata describes its small images; its actual mixed composition is recorded here.

`artifacts/performance/final-window.json`: 1,200 routed-key actions in 60.005 seconds (19.998/s), with copies, deletion, hashing, warming and JSON writes:

| Measurement | Result |
| --- | --- |
| Arrival to requested-state/focus feedback | p95 5.961 ms; p99 16.471 ms |
| Key handler | p95 1.808 ms; p99 3.246 ms |
| Drawing submission | p95 28.648 ms; p99 49.112 ms |
| First usable preview | 335.102 ms; one opening sample |
| Stable decoded-cache cold low/full, 100 samples | p95 21.785 / 48.105 ms |
| Navigating low/full publication | p95 80.738 / 107.133 ms; not separately classified prefetched neighbors |
| Peak sampled working set | 437.65 MiB |
| Maximum image queue / realized tile controls | 16 / 10 |
| Mutation results / copied bytes / hash-read bytes | 240 / 202,584,237 / 238,036,701 |
| Successful JSON writes | 1,107 |

That older run exposed one optional JSON replacement/read-sharing conflict. Readers now permit atomic replacement. A later short run (`tiles-metadata-final.json`) completed 51 JSON writes with no maintenance/export errors; the rendered viewport's tiles were visually checked. The latest full package run above also completed without these errors.

The first 10,000-file run failed latency despite bounded memory. Profiling found a source array rebuilt for every compared photo and synchronous strip layout inside the key handler. Sources are now grouped once, comparisons publish bounded batches, and strip scrolling is coalesced outside the handler. Failed reports remain under artifacts.

Input feedback is separate from drawing submission and physical display. Synthetic input results meet the stated input thresholds but do not establish reference-workload/macOS acceptance. Decoded-cache cold is not cold OS/SSD data. Repeated warm-folder, tile, prefetched-neighbor and cold-folder samples on the photographic reference workload remain outstanding.

## Reproduction and external evidence

```powershell
dotnet restore PhotoScreener.Rewrite.sln --locked-mode
dotnet test PhotoScreener.Rewrite.sln -c Release --no-restore --disable-build-servers
dotnet run --project tests/PhotoScreener.Performance -c Release -- artifacts/corpus 100 4000 3000
./scripts/Benchmark-Rewrite.ps1 -SourceFolder artifacts/corpus -Actions 1200 -StableSamples 100
./scripts/Publish-Rewrite.ps1 -Runtime win-x64
./scripts/Publish-Rewrite.ps1 -Runtime osx-arm64
python scripts/Assemble-MacBundle.py
```

Benchmark scripts use fresh isolated exports and thumbnail caches under artifacts. Reports record input/drawing/preview timings, queue/cache/control counts, working set, mutation/hash throughput, successful disk/JSON writes and errors. Generated photos, caches, reports and packages are ignored by git.

Mac inspection found ARM64 slices in all 18 native binaries; the highest embedded minimum was macOS 12.0. The deployment profile remains macOS 14.0+. Cross-publishing/Mach-O inspection does not prove native operation, Gatekeeper acceptance or filesystem race behavior. On an Apple Silicon Mac run the locked suite, `scripts/benchmark-macos.sh` and `scripts/package-macos.sh`. Signing uses `SIGNING_IDENTITY`; explicitly requested notarization additionally uses a configured `NOTARY_PROFILE`. CI is configured for a self-hosted Mac ARM64 runner; no job was dispatched.

Remaining external evidence: the photographic JPEG corpus and separate PNG/removable-volume cases at the reference pixels; repeated cold/warm/tile/neighbor/folder traces; an Apple Silicon runner for native permission, case/alias, locking, interruption and Retina checks; signing/notarization credentials. No signed Mac artifact or passing unexecuted gate is claimed.

## Startup and folder focus follow-up

Folder opening now establishes review keyboard focus independently of filter changes, and initial window layout restores review focus after automatic toolbar focus assignment. Cold and cached folder navigation tests send actual headless keyboard input without manually focusing the photo surface.

User-local optional session settings remember the last successfully opened folder. Startup probes its accessibility on a worker before loading; missing/disconnected storage leaves a usable empty review with a reconnect/open hint and preserves the preference. Explicit command-line folders take precedence, and benchmarks do not read or change the user's session preference. Optional preference writes run outside the UI thread, coalesce through idle maintenance and flush after draining on close.

Release suites: 17 desktop and 36 core/infrastructure tests passed (53 total), including available-folder startup, immediate arrow navigation, simulated disconnected storage, alternate-folder selection, preference persistence and malformed/oversized preferences. Desktop validation used `-p:OutputPath=D:/Git/local/photoScreener/artifacts/focus-check/` because the user's running app locks the normal Release files. These changes require rebuilding/relaunching the app; the existing self-contained packages predate this follow-up.

## Export and zoom follow-up

Mutations enumerate source names in all known folders sharing the captured target before ownership decisions, including originals not yet published by incremental UI enumeration. Metadata is read only for relevant candidate names. Each mutation uses a fresh export index, and successful deletion is followed by another authoritative comparison. Tests cover an undiscovered natural dotted-name owner, an externally added duplicate between requests, and a duplicate created during deletion; unsafe ownership remains unresolved and remaining copies remain Picked with an error.

File revisions now include native change time (Windows `FILE_BASIC_INFO.ChangeTime`, macOS `st_ctimespec`). Cached content cannot be reused merely because file size, identity and modification time match. The Windows test changes bytes in place and restores the modification time, verifies the new revision and preserves the older export. Filesystems without a supported change token bypass digest/preview cache hits. Native macOS execution remains outstanding.

Pointer-centered wheel zoom, bounded panning, double-click/Fit reset and reset on photo navigation are implemented. Zoom retains the visible image while requesting a higher-resolution refinement; percentages are relative to Fit, and the existing 3840-pixel longest-side decode limit remains. Tests send wheel/drag input, verify the anchored image coordinates, pan bounds, increased decoded resolution and immediate keyboard navigation. This is magnification of a bounded full-image preview; native-resolution cropped inspection beyond that limit is not yet implemented.

A real Windows window smoke run (`artifacts/performance/zoom-window.json`, 40 actions and four stable samples) produced a rendered 300%-of-Fit screenshot with no export/maintenance errors. Sampled working set was 333.43 MiB during its diagnostic workload. The screenshot was inspected for image clipping, the full toolbar and virtualized strip. This short run is a rendering check, not reference-corpus performance acceptance.

## Project, strip and photo-information follow-up

Project scans publish the complete source roster for each finished folder. Changed revisions replace earlier rows/counts; missing originals leave the source count and their unmatched/older exports appear as preserved read-only entries, with Space disabled. Failed operations from older revisions retain their own completion/error row and can be superseded by a new explicit request. Source revision is checked again after staging verification, immediately before publication. Tests cover source replacement during a paused verification, rescan of changed/deleted originals, preserved exports and truthful failed work.

The virtualized strip centers the selected thumbnail through deferred scroll adjustment, clamped to its extent. Tests verify middle/beginning/end placement and bounded realized controls. Resize/show-strip operations re-center the current item.

Photo information uses pinned MetadataExtractor 2.9.2/XmpCore dependencies. One bounded background reader with a 256-entry cache reads EXIF capture time, focal length, exposure, ISO, aperture, camera/lens and GPS coordinates. An 8 MiB metadata read budget and 256-character field limits bound parsing/display work; missing/malformed data remains Unknown/unavailable. Read-only metadata access allows external writes/deletion and checks native identity/revision before/after reading. JPEG and PNG-after-IDAT fixtures verify exact capture values, signed GPS coordinates and unchanged source bytes; the worker survives a deleted cached source.

The existing decode workers compute a sampled 256-bin preview-luminance histogram after publishing the focused image. It is not a RAW or full-resolution histogram. Information/histogram callbacks reject old photos/generations, and tests verify that missing fields clear and the histogram follows a different-colored next photo.

`artifacts/performance/photo-information-window.json` is an executed real Windows window smoke (40 actions, four stable samples, eight generated 12 MP JPEGs with explicit synthetic EXIF/GPS fixtures). The rendered screenshot was inspected: capture values, all toolbar buttons, histogram, fitted image and strip are visible. No export/maintenance errors; sampled working set 253.35 MiB. This short run is not reference-workload performance evidence. Reproduce the fixture with `dotnet run --project tests/PhotoScreener.Performance -c Release -- artifacts/photo-info-corpus 8 4000 3000 --exif`.

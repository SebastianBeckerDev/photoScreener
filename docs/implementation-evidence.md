# Rewrite implementation and evidence

The replacement is in `src/`. On 2026-11-06 the user explicitly requested removal of the old WPF version, superseding its original preservation requirement. The WPF project, old solution/launcher and its IDE cache have been removed; the isolated compatibility fixture remains. Start the rewrite with `buildAndRunRewrite.bat`. Contract: `reverse-engineered-specification/`.

| Package | Implemented deliverable | Executed evidence |
| --- | --- | --- |
| P-01 | Independent projects, pinned SDK/packages, locks for Windows x64/Mac ARM64, read-only sources and native identities | Locked Release builds/tests; unchanged legacy-helper snapshot and filename vectors |
| P-02 | Keyboard review; incremental low/full fit preview; eight EXIF orientations; virtualized strip; fixed workers; 64 MiB decoded cache; disposable disk cache and relaxed warming; pointer zoom/pan | Real Windows window runs; 10,000-row/recycled-tile checks; low/full quadrant-pixel orientation tests; anchored wheel/pan and higher-resolution zoom tests |
| P-03 | Default/custom per-folder targets; full-content old/new/bare-copy associations; duplicate/shared rules; ownership dialog; progressive project picks | Eight-of-ten no-metadata recovery; false-positive/ambiguity/version/default/custom-target cases |
| P-04 | Bounded serialized latest-intent worker; captured targets; one-pass source MD5/SHA-256; verified staging/publication/deletion; ownership; cancel/drain/retry | Legacy read-back; real short-suffix collision; shared convergence; substitution/hardlinks/locked duplicates; four child-process kill/recovery phases |
| P-05 | Optional atomic cursor/filter/history; watcher hints and Refresh; unavailable/empty distinction; revision/stale-result guards | Missing/corrupt/null JSON; maintenance ownership; external changes; saved-filter/cursor restoration; retained focus on first export creation |
| P-06 | Self-contained Windows/Mac payloads; bundle/plist/entitlements/signing scripts; platform CI | Windows and native ARM64 Mac tests/publish pass together. The actual self-contained Mac `.app` passes its window/runtime smoke. Windows package and 100,000-row diagnostics are recorded below. Mac signing/notarization and complete reference acceptance remain outstanding |

## Windows verification

Latest Release tests: 50 core/infrastructure and 26 Avalonia headless cases pass (76 total), using the same solution-wide command as CI. Commit `a260bc2` passes both Windows and native Apple Silicon jobs in [42522274599](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42522274599), including publish and the actual self-contained Mac `.app` smoke. Debug desktop and the local Windows self-contained package are refreshed. Windows-only native sharing/rename cases are not evidence for macOS. Full disk, physically unplugged devices and network volumes have not been exercised.

On 2026-11-08 the user accepted higher RAM use, so the original 512 MiB figure is retained as a reference rather than a hard current acceptance ceiling. Workers, pending requests and decoded-cache size remain bounded. The Windows apphost has PE machine `0x8664` (x64); native Mac packaging uses ARM64. Both are 64-bit processes. Diagnostic reports now include process architecture explicitly.

Single-photo mutation checks now index only that original's bare/legacy-suffixed candidates, including before deletion and during failure recovery; folder open/Refresh still builds the complete index. A real filesystem test locks an unassociated file with the same stem prefix and completes pick/unpick without opening it. Full/association scan and candidate-revision counters are included in benchmark reports. Verified shared associations are visible in photo status/tooltips and retain their known relationship across an empty-export reconciliation when a native source change token is available. The UI regression covers two identical originals, unpick/repick, then an external edit in the inactive folder: the worker's fresh source census cancels stale optimistic propagation and reopening rebuilds that folder's rows.

The first public GitHub Windows job failed only during fixture disposal after a killed process, with a transient sharing violation on the source fixture. Cleanup now retries only Windows sharing/lock violations for at most three seconds, after checking the temporary root boundary. A subsequent job encountered a transient writer-lock hold after confirmed child exit; the recovery fixture now waits up to three seconds for ownership before running cleanup and assertions once. All four kill/recovery cases pass locally. Recovery/source-safety assertions are never retried. Windows run [42419080908](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42419080908) passed the full 75 tests and self-contained publish.

CI uses the standard GitHub `macos-15` ARM64 runner ([available runner images](https://github.com/actions/runner-images#available-images)). Its first native execution exposed the Darwin ARM64 variadic `openat` ABI mismatch: creation permissions were being passed in a register instead of the required stack location. Commit `b91dcb1` corrects that call. The Mac job in [42419921630](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42419921630) passed all 50 infrastructure and 25 desktop tests, ARM64 verification, self-contained publish, plist validation and unsigned bundle assembly. The later [42499062188](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42499062188), commit `8c63f1d`, passes all 75 tests and publish on both platforms together, plus the actual Mac `.app` window/runtime smoke (40 routed keys, four stable refinements, installed-runtime lookup disabled). Signing/notarization and native reference performance remain unverified. One intervening Mac run failed the external-copy-during-deletion test; the assertions now identify the failed phase/observation, and the latest native run passes it without retries or relaxed assertions. Its intermittent cause is not established.

The 2026-11-07 binding safety follow-up checks actual directory identity before using warm folder rows or mutating a captured target, and again after first creating a missing export root. Different path keys for the same physical directory are rejected with a consistent-spelling message. Windows paths containing trailing dots/spaces and case-sensitive directories that the session's case-insensitive keys cannot represent fail explicitly. Native tests use an extended-path alias, a trailing-dot alias, and an actual NTFS case-sensitive directory with two distinct `a.JPG`/`A.JPG` originals and `one`/`One` subdirectories. Rejected requests preserve the sources and existing export copy; library discovery reports the folder collision. Ordinary Windows case variants remain valid. These Windows fixtures do not prove behavior on other Mac filesystems; native Mac results are reported above. Both platform payloads include the binding and subsequent folder-list fixes.

Earlier Windows self-contained functional smoke: `artifacts/performance/windows-package-safety-smoke.json`, bundled .NET 10.0.11 with installed-runtime lookup disabled, 40 handled keys, eight completed mutations, four full refinements, no ignored keys or export/maintenance errors. This short cold-start run is not a percentile acceptance run (its p99 feedback is 53.899 ms). It predates the latest JPEG-buffer changes; a new local package benchmark was not run after the user declined that measurement. The Mac cross-bundle inspection found 18 ARM64 native binaries with a maximum macOS 12 minimum, within its macOS 14+ profile. Native bundle execution now passes in CI; signing remains unverified. Git Bash syntax validation of the Mac packaging/benchmark scripts passed.

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

Feedback and RAM meet their numerical limits in this run. Preview latency does not meet the low/full limits; the source is removable exFAT, and decoded-cache cold does not mean uncached physical storage. Without a reliable native change token, existing cache hints are deliberately not trusted. The first USB attempt lost window activation during input and correctly rejected 587 ignored keys; `private-real-camera-usb-invalid-focus.json` is invalid evidence.

A bounded 100-image copy (779.2 MiB) on the internal NTFS SSD separates storage/cache behavior from decoding. In `private-real-camera-ntfs.json`, 1,200/1,200 keys are handled; feedback p95/p99 is 1.138/3.415 ms, low/full decoded-cache-cold p95 is 128.206/387.762 ms, working set 398.570 MiB, 240 mutations and 99 disk thumbnail writes. The source change token is available. This subset does not prove the full reference workload or uncached SSD latency.

Commit `8c63f1d` uses the lowest native JPEG DCT scale for the brief quick view; original-derived refinement keeps its requested resolution. All eight quick/full orientation pixel checks pass. The follow-up `private-real-camera-ntfs-quick.json` handles 1,200 keys in 60.020 seconds, with feedback p95/p99 1.113/2.532 ms, decoded-cache-cold low/full p95 122.458/314.931 ms, 397.137 MiB sampled working set, 240 mutations, 817 JSON writes, 98 thumbnail writes and no export/maintenance errors. The low-preview change is modest and still exceeds 100 ms; worker-stage profiling is added to identify the remaining cost. No representative low-preview gate is claimed.

The subsequent worker profile (`private-real-camera-ntfs-worker-profile.json`, 100 stable samples and 40 input actions) attributes quick-worker p95 of 110.573 ms mainly to decoding (109.905 ms); native source-revision checks are 0.430 ms. A native encoded JPEG buffer improves the short profile to 94.791 ms end-to-end low p95, but its full 1,200-action run (`private-real-camera-ntfs-native-mixed.json`) reaches 588.020 MiB with low/full decoded-cache-cold p95 128.252/300.480 ms. Feedback p95/p99 is 1.102/4.119 ms, with all keys handled, 240 mutation results and no export/maintenance errors. The short profile is not mixed-load acceptance evidence.

The final implementation replaces varying large encoded allocations with two reusable native buffers of at most 40 MiB each, held only until borrowed codec data is released. Larger JPEGs and PNGs retain the streamed path. The decoder checks the byte bound again before filling a buffer. A concurrent JPEG regression retains prior decoded pixels while both encoded slots are reused and verifies different photos stay independent; all 26 desktop tests pass. The planned local mixed benchmark for this pooled variant was declined, so its photographic RAM/latency effect has not been measured or claimed. The package smoke script supports private source data outside the repository. The user has relaxed the former 512 MiB ceiling; no library-sized bitmap cache or unbounded job queue is introduced.

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

Mac inspection found ARM64 slices in all 18 native binaries; the highest embedded minimum was macOS 12.0. The deployment profile remains macOS 14.0+. Cross-publishing/Mach-O inspection does not prove native operation or Gatekeeper acceptance. Native tests, packaging and the actual bundle smoke now execute on the standard hosted `macos-15` ARM64 runner. On an Apple Silicon Mac run the locked suite, `scripts/benchmark-macos.sh`, `scripts/package-macos.sh` and `scripts/test-macos-package.sh`. Signing uses `SIGNING_IDENTITY`; explicitly requested notarization additionally uses a configured `NOTARY_PROFILE`.

Remaining reference evidence: a complete 10,000-photo JPEG folder at the specified sizes, the full photographic 100,000-file tree workload, separate PNG and physically disconnected/slow-volume scenarios, repeated classified cold/warm/tile/neighbor/folder traces, and Apple Silicon/Retina reference performance. The supplied archive and real 100-photo NTFS subset support the partial measurements above; native Windows/Mac functional checks now run. Signing/notarization credentials remain unavailable. No signed Mac artifact or passing unexecuted gate is claimed.

## Startup and folder focus follow-up

### Portable review metadata (2026-11-08)

Optional `review.json` schema 2 stores source-folder keys relative to the metadata directory, with `/` separators. Schema 1 absolute keys remain readable and migrate on the next successful write, preserving all shared-target folder entries. Separate Windows volumes retain absolute keys. Metadata never selects a mutation binding. Cursor restoration uses filename/length/mtime so copied files' new native identities do not prevent position recovery; actual picks and mutations retain their existing identity/content checks. See [portable review metadata](portable-review-metadata.md) for migration and relocation limits.

Executed Windows Release suites: **52 core/infrastructure + 27 desktop tests passed (79 total)**. New cases copy/rename a deep `Export/Luminar/Again Exported` tree, delete the original, recover filter/history and verified picks, and preserve shared-folder history during schema migration. A headless UI test verifies that a copied folder restores its non-first cursor and Picked observation despite a different native file identity. Desktop output was isolated at `artifacts/relative-path-check` while the user's Debug app remained running. Targeted build: zero warnings/errors. No new photographic benchmark, Mac execution, or installed-package claim is made for this change; running Debug/package binaries require a later rebuild.

### LumaPick branding and startup (2026-11-08)

The user chose **LumaPick**. A geometric aperture/check logo is committed as SVG and PNG, with multi-size Windows ICO and macOS ICNS. `scripts/Generate-BrandAssets.py` regenerates all four from the same geometry (Pillow is needed only for regeneration). Windows EXE product metadata, window/taskbar icon, empty review view and lightweight startup splash carry the new name/mark. The splash adopts and shows the main window before closing, with no fixed minimum duration. Native initialization still uses the existing asynchronous folder restoration. Closing the splash before handoff prevents a late new window. Existing project names, export metadata, local session paths and the Mac bundle identifier are retained.

Executed local Release desktop suite: **29 tests passed**, including handoff/focus, icon resource loading and closing during startup. The splash was rendered at 1040×700 (`artifacts/lumapick-splash.png`) and inspected. The current Windows self-contained package is `artifacts/LumaPick-win-x64.zip`. Mac packaging now assembles `LumaPick.app` with its Finder/Dock icon; shell syntax checks passed. [macOS distribution](macos-distribution.md) documents Developer ID signing/notarization and the separate, unimplemented App Store sandbox/submission requirements.

The preceding metadata commit's CI run `42524635576` passed Windows tests/publish and all 52 native Mac filesystem/core tests, but one Mac desktop test failed: `BrokenFocusedImageDoesNotBlockOtherVisibleTiles` assumed that a good tile's completion implied the focused error callback had completed. Its assertion ran after 83 ms while the preview still displayed Loading. The test now waits, within its original bound, for both independent outcomes; the corrected targeted test passes locally. This changes test synchronization, not the image worker count or native scheduling.

Fresh [CI run 42525563997](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42525563997) for `e0fffbb` passed **52 core/infrastructure + 29 desktop tests on each platform (81 each)**, Windows self-contained publishing, native Mac packaging and the actual `LumaPick.app` 40-key/four-refinement functional smoke with installed-runtime lookup disabled. Both jobs completed successfully; their test totals and bundle success were checked in downloaded logs. Local Windows/Mac payloads were also republished, and `scripts/Assemble-MacBundle.py` assembled `artifacts/LumaPick-osx-arm64-unsigned.zip`: all 18 native binaries contain ARM64, their highest embedded minimum is macOS 12 within the macOS 14 deployment profile, and the ICNS is present in Resources. Windows EXE ProductName/FileDescription are LumaPick. Signing, photographic reference performance and a new local real-window benchmark remain unexecuted.

### Retained preview and active decode cancellation (2026-11-08)

At the user's request, navigation retains the last bitmap until the newly selected photo's quick/full preview arrives. A dimmed image and loading badge explicitly name the selected target and still-displayed previous photo. Replacement assigns the new bitmap before releasing the previous lease, avoiding a blank frame. Failed decoding keeps the previous image with an error and stops the progress indicator. Same-photo zoom/refinement continues to retain detail; a previous photo's full-quality flag never suppresses the next photo's quick preview. Keyboard actions capture the newly selected row, while image pointer inspection is paused during the marked mismatch. Empty views release the retained bitmap.

Both fixed image workers now own per-active-request cancellation tokens. Focus changes cancel obsolete quick/full and relaxed requests; context changes cancel all old active work; detached/rebound thumbnail owners cancel their active decode; clearing focus cancels old focused work. Checks fence native decoding, caching, publication and later histogram/disk-cache work. Cancelled native-return bitmaps are disposed, interrupted idle thumbnails remain eligible for a later retry, and foreground-priority checks include active work. No replacement decoder tasks/threads are spawned. Already-running synchronous native/filesystem calls may delay observing cancellation; this does not claim an interruptible OS read or the slow-volume L-04 timing gate.

Executed local Release desktop suite: **39 tests passed**. Four original deterministic blocked-decode scenarios first failed by timeout and then passed after the fix. Eight cancellation cases cover active quick/full/warming/thumbnail work, clearing focus, pre-cancelled reads, idle retry, and a simulated uninterruptible native call with 100 coalesced focus changes, at most two workers and no stale bitmap in the cache. Two rendered-window cases cover retained-image identity/marker, skipping a blocked next photo, errors and navigation back. The 1700×950 headless render (`artifacts/lumapick-retained-preview.png`, generated solid-color JPEG fixtures) was inspected. It is UI/ordering evidence, not a photographic latency or physical-display benchmark. Fresh Windows/Mac CI and refreshed packages remain to be recorded for this change.

First CI run `42528563308` passed all 91 Windows tests/publish and 52 core + 37 desktop tests on Mac. Both new retained-preview cases failed while waiting for their initial `a.JPG` full preview: the fixture assumed file enumeration starts with that name, while production intentionally publishes the first available row without a complete sort/scan barrier. The tests now explicitly arrange their visible fixture sequence and select its first row; the targeted pair passes locally. No production sorting/preview behavior was changed to satisfy that assumption. Both platform payloads have been republished; final native CI for the fixture correction remains pending.

The corrected fixture run `42529244332` passed all **39 native Mac desktop tests**, including retained previews and active cancellation. Its core suite exposed an intermittent export ownership conflict after a disposed writer. Native descriptors lacked close-on-exec: a concurrently spawned unrelated child could retain the shared open-file description and its `flock`. macOS opens now set `O_CLOEXEC` atomically; writer disposal explicitly unlocks before closing, is idempotent, and preserves cleanup on errors. Interrupted `flock` calls retry `EINTR`. A regression keeps an unrelated staging child alive while releasing/reacquiring the parent's export ownership and writing metadata; on Mac it also checks `FD_CLOEXEC`. Source bytes remain unchanged. Local Windows Release suites: **53 core + 39 desktop passed (92 total)**, with isolated desktop output while the user's Debug app remained running. Native execution of this descriptor/ownership regression remains pending.

Final [CI run 42530498026](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42530498026), commit `1ba8605`, passed **53 core/infrastructure + 39 desktop tests on each platform (92 each)**, including native `FD_CLOEXEC`/unrelated-child ownership and retained-image transitions. Windows self-contained publishing, native Mac packaging and the actual `LumaPick.app` 40-key/four-refinement smoke all passed. Downloaded job logs confirm both totals and the bundle result. Local packages were refreshed from the same production commit: Windows EXE ProductVersion `1.0.0+1ba86055e5d84abcddf3ed03897dec09ac5ef99a`, `artifacts/LumaPick-win-x64.zip` (47,545,552 bytes), and `artifacts/LumaPick-osx-arm64-unsigned.zip` (45,979,303 bytes). Cross-assembled Mac payload inspection confirms all 18 native ARM64 binaries and the icon. Signing/notarization and photographic reference performance remain outside this executed evidence.

Folder opening now establishes review keyboard focus independently of filter changes, and initial window layout restores review focus after automatic toolbar focus assignment. Cold and cached folder navigation tests send actual headless keyboard input without manually focusing the photo surface.

User-local optional session settings remember the last successfully opened folder. Startup probes its accessibility on a worker before loading; missing/disconnected storage leaves a usable empty review with a reconnect/open hint and preserves the preference. Explicit command-line folders take precedence, and benchmarks do not read or change the user's session preference. Optional preference writes run outside the UI thread, coalesce through idle maintenance and flush after draining on close.

Release suites: 17 desktop and 36 core/infrastructure tests passed (53 total), including available-folder startup, immediate arrow navigation, simulated disconnected storage, alternate-folder selection, preference persistence and malformed/oversized preferences. Desktop validation used `-p:OutputPath=D:/Git/local/photoScreener/artifacts/focus-check/` because the user's running app locks the normal Release files. These changes require rebuilding/relaunching the app; the existing self-contained packages predate this follow-up.

## Export and zoom follow-up

### JPEG/NEF companions (2026-11-09)

The user explicitly requested RAW+JPEG support, superseding the initial RAW-companion deferral for Nikon NEF. JPEGs remain the reviewed/counted/authoritative selected images. Same-stem NEFs in the captured source folder are copied unchanged with the existing one-pass MD5/SHA-256 stage/verify/no-clobber protocol and their own exact legacy suffix. NEFs are never decoded as previews or added to source/project review rows. PNG behavior is unchanged.

Complete current NEFs publish before the authoritative JPEG; source/export RAW revisions, current intent and cancellation are checked again immediately before JPEG publication. A complete orphan NEF after interruption is preserved/reused and cannot establish a pick. Unpick verifies/removes all current companion copies before the JPEG and rechecks for externally inserted copies; a RAW error keeps the JPEG picked for retry. Older/missing-original/ambiguous/unrelated RAW copies remain preserved. Shared JPEG associations copy each folder's current NEF, including different RAW contents. An explicit pick/Retry also repairs a missing companion of an existing JPEG pick; folder open performs no bulk RAW export. See [JPEG/NEF behavior](raw-jpeg-companions.md).

Executed Windows Release infrastructure suite: **70 passed**. Seventeen NEF cases cover unchanged streamed bytes/case, no-metadata JPEG-only selection recovery, orphan RAW, PNG exclusion, collisions, current/older/duplicate RAW versions, source/export edits before JPEG publication, cancellation/latest intent, missing originals, ambiguous dotted ownership, shared targets, missing-companion repair, a locked export and a native hard-linked RAW source alias. Release desktop suite: **55 passed**, including actual Space events on a JPEG+NEF window, one review row, stable preview/focus, both exports and unchanged source hashes. Desktop output remains isolated from the user's running app. Native Mac CI and refreshed packages for this change remain pending.

Initial [NEF CI run 42578157542](https://github.com/SebastianBeckerDev/photoScreener/actions/runs/42578157542), `0403a65`, passed **70 core + 55 desktop tests on Apple Silicon**, native packaging and the actual bundle smoke, including the new PNG orientation cases. Windows passed all 55 desktop tests and 69 core cases, including all NEF cases, but the existing post-deletion kill fixture failed while removing optional test metadata: `fault-reached.txt` had a sharing violation after child exit. Metadata removal now uses the same validated temporary-fixture boundary and bounded three-second Windows sharing/lock-violation disposal path as final cleanup. Recovery/source assertions remain unchanged and run once. All five interruption/ownership cases pass locally after this test-only correction; fresh platform CI remains pending.

### PNG orientation follow-up (2026-11-09)

Native PNG fixtures exposed that the pinned Skia PNG codec ignores `eXIf` orientation: seven non-default orientations failed before the fix. The preview decoder now inspects PNG chunk headers and a CRC-verified TIFF orientation field off the UI thread, seeks past pixel data, restores the codec stream position, and applies the same orientation matrix to quick/full/tile output. Inspection is bounded to 4,096 chunks and a 64 KiB EXIF payload; malformed/oversized metadata is optional, and cancellation is checked between reads/chunks. Cache rendering policy is now `oriented-v2` so old incorrectly oriented thumbnails regenerate.

Local isolated Release desktop suite: **54 passed**, without build warnings. Fifteen new cases cover all eight actual PNG orientation transforms at both stages, big-endian EXIF after IDAT with fewer than 128 metadata bytes read, invalid offsets/counts/orientation/CRC, oversized metadata, cancellation/stream restoration, and concurrent quick/full 12 MP RGBA PNG decoding with premultiplied transparency and unchanged source hashes. This is native functional evidence; separate photographic PNG latency and Mac execution remain pending.

Mutations enumerate source names in all known folders sharing the captured target before ownership decisions, including originals not yet published by incremental UI enumeration. Metadata is read only for relevant candidate names. Each mutation uses a fresh export index, and successful deletion is followed by another authoritative comparison. Tests cover an undiscovered natural dotted-name owner, an externally added duplicate between requests, and a duplicate created during deletion; unsafe ownership remains unresolved and remaining copies remain Picked with an error.

File revisions now include native change time (Windows `FILE_BASIC_INFO.ChangeTime`, macOS `st_ctimespec`). Cached content cannot be reused merely because file size, identity and modification time match. The test changes bytes in place and restores the modification time, verifies the new revision and preserves the older export. Filesystems without a supported change token bypass digest/preview cache hits. Native macOS execution passes in the CI runs recorded above.

Pointer-centered wheel zoom, bounded panning, double-click/Fit reset and reset on photo navigation are implemented. Zoom retains the visible image while requesting a higher-resolution refinement; percentages are relative to Fit, and the existing 3840-pixel longest-side decode limit remains. Tests send wheel/drag input, verify the anchored image coordinates, pan bounds, increased decoded resolution and immediate keyboard navigation. This is magnification of a bounded full-image preview; native-resolution cropped inspection beyond that limit is not yet implemented.

A real Windows window smoke run (`artifacts/performance/zoom-window.json`, 40 actions and four stable samples) produced a rendered 300%-of-Fit screenshot with no export/maintenance errors. Sampled working set was 333.43 MiB during its diagnostic workload. The screenshot was inspected for image clipping, the full toolbar and virtualized strip. This short run is a rendering check, not reference-corpus performance acceptance.

## Project, strip and photo-information follow-up

Project scans publish the complete source roster for each finished folder. Changed revisions replace earlier rows/counts; missing originals leave the source count and their unmatched/older exports appear as preserved read-only entries, with Space disabled. Failed operations from older revisions retain their own completion/error row and can be superseded by a new explicit request. Source revision is checked again after staging verification, immediately before publication. Tests cover source replacement during a paused verification, rescan of changed/deleted originals, preserved exports and truthful failed work.

The virtualized strip centers the selected thumbnail through deferred scroll adjustment, clamped to its extent. Tests verify middle/beginning/end placement and bounded realized controls. Resize/show-strip operations re-center the current item.

Photo information uses pinned MetadataExtractor 2.9.2/XmpCore dependencies. One bounded background reader with a 256-entry cache reads EXIF capture time, focal length, exposure, ISO, aperture, camera/lens and GPS coordinates. An 8 MiB metadata read budget and 256-character field limits bound parsing/display work; missing/malformed data remains Unknown/unavailable. Read-only metadata access allows external writes/deletion and checks native identity/revision before/after reading. JPEG and PNG-after-IDAT fixtures verify exact capture values, signed GPS coordinates and unchanged source bytes; the worker survives a deleted cached source.

The existing decode workers compute a sampled 256-bin preview-luminance histogram after publishing the focused image. It is not a RAW or full-resolution histogram. Information/histogram callbacks reject old photos/generations, and tests verify that missing fields clear and the histogram follows a different-colored next photo.

`artifacts/performance/photo-information-window.json` is an executed real Windows window smoke (40 actions, four stable samples, eight generated 12 MP JPEGs with explicit synthetic EXIF/GPS fixtures). The rendered screenshot was inspected: capture values, all toolbar buttons, histogram, fitted image and strip are visible. No export/maintenance errors; sampled working set 253.35 MiB. This short run is not reference-workload performance evidence. Reproduce the fixture with `dotnet run --project tests/PhotoScreener.Performance -c Release -- artifacts/photo-info-corpus 8 4000 3000 --exif`.

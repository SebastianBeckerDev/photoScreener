# LumaPick

![LumaPick aperture and selection mark](src/PhotoScreener.Desktop/Assets/lumapick.svg)

**Find your keepers.** LumaPick is the PhotoScreener rewrite: keyboard-first photo review with verified export copies. Its logo appears in the startup splash, empty review view, window/taskbar icon and platform packages. The splash hands directly to the review window without a minimum display delay. Existing settings/export metadata and the repository's technical project names retain compatibility.

The .NET 10 / Avalonia app lives in `src/`. The old WPF project, solution and launcher were removed at the user's request. The isolated legacy-format fixture helper remains in the tests.

```
dotnet restore PhotoScreener.Rewrite.sln --locked-mode
dotnet build PhotoScreener.Rewrite.sln -c Release --disable-build-servers
dotnet test PhotoScreener.Rewrite.sln -c Release --disable-build-servers
dotnet run --project src/PhotoScreener.Desktop -c Release -- "D:\Photos\Trip"
```

Left/Right navigate. Space picks/unpicks without advancing; held Space is suppressed. The left panel switches between source folders and export candidates, with ten recent directories per mode, a lazy directory tree and Refresh. Click a source to open it; apply an export candidate explicitly with **Use as export target**. Unavailable directories remain listed. Collapse directory navigation, photo information or thumbnails using their edge handles; panel state and widths are remembered locally. The strip footer keeps All/Picked/Unpicked, source/export links and contextual pending/error recovery visible. Directory context menus and footer links open Explorer/Finder. Project picks remains available in the source link's context menu.

With RAW+JPEG captures, review stays on the JPEG. A same-stem Nikon NEF in the same folder is copied unchanged with the pick and removed as a verified current companion on unpick. See [JPEG/NEF pairing and recovery](docs/raw-jpeg-companions.md).
During navigation the previous photo stays visible, slightly dimmed, until the newly selected photo's preview is ready. A loading badge names both the requested photo and the retained image; a failed decode shows an error there. Zoom/panning resumes when the selected photo arrives. Keyboard actions still apply to the selected row, and superseded image work is cancelled within the two fixed decoder workers.
See the [illustrated image-selection and PickStatus guide](docs/image-selection.md) for every state, verification, pending requests, failures, and shared picks.
Hover any button for an explanation; the filter dropdown also has help text. Space opens an ownership choice when an ambiguous legacy export fits different original filenames with identical bytes. The photo context menu can repair a missing NEF companion, and the footer activity popover provides failed-request retry and pending cancellation.

Click a fitted photo to request its available detailed preview at 100% around the pointer; click again to return to Fit. Use the mouse wheel to zoom around the pointer, drag a zoomed photo to pan, or use −, + and Fit at the image's lower-right corner. Percentages refer to physical decoded preview pixels, with a 3840-pixel longest-side limit and maximum 16× Fit. Changing photos resets zoom. The selected photo has priority over unrelated thumbnails; following previews load in the latest navigation direction. Detail requests retain the current image and bypass queued thumbnails and duplicate quick-preview reads. A first detail decode still requires source access; storage without a supported native change token bypasses preview cache reuse.

The selected thumbnail stays centered when the strip can scroll; scrolling stops at the beginning/end without adding blank space. The right photo-information panel shows embedded capture time, focal length, exposure, ISO, aperture, camera/lens and available GPS coordinates. Its histogram describes sampled preview luminance. Missing embedded values show Unknown; capture time is never inferred from the file date. Collapse the panel to enlarge the preview.

Project picks also displays unmatched/older exports as preserved, read-only entries. A rescan replaces changed source revisions and removes missing originals from source counts while retaining those export files.

Launching without a folder argument automatically opens the last successfully opened folder. Its path is saved in the user's local application data (`PhotoScreener/session-v1.json`), so it remains available when an external SSD is disconnected. An unavailable folder leaves the app ready to open another folder and displays a reconnect hint; the remembered path is retained. An explicit folder argument takes precedence. Startup and folder opening give the photo review area keyboard focus immediately.

Each folder defaults to its existing `export` child, created only on its first pick. An explicit target applies to that folder for the session; use `--export PATH` for an explicit launch binding. Old/new flat exports restore completed picks by full-content verification. No database or review JSON is required for picks. Optional history and cursor JSON live under the chosen target's `.photoscreener` directory.

Review metadata uses relative source-folder paths, so moving/copying a photo tree together with its exports retains cursor, filter and history. Existing absolute-path metadata is upgraded on its next save. See [portable review metadata](docs/portable-review-metadata.md) for custom targets, migration and separate-volume limits.

```
pwsh -File scripts/Publish-Rewrite.ps1
pwsh -File scripts/Benchmark-Rewrite.ps1 -SourceFolder artifacts/corpus
dotnet run --project tests/PhotoScreener.Performance -c Release -- artifacts/corpus 100 4000 3000
```

For metadata scaling diagnostics, generate a small-image tree and preload its actual project rows before mixed-load review:

```
dotnet run --project tests/PhotoScreener.Performance -c Release --no-restore -- artifacts/corpus-tree-100000 100000 64 48 --folders 100 --export-samples 8
pwsh -File scripts/Benchmark-Rewrite.ps1 -SourceFolder artifacts/corpus-10000 -MetadataTree artifacts/corpus-tree-100000 -Actions 1200 -StableSamples 100 -Report artifacts/performance/metadata-tree-window.json
```

The report includes scanned file/folder/pick counts and memory sampled during both the scan and subsequent review. This small-image tree tests metadata scaling; the photographic reference corpus remains a separate acceptance workload. Benchmark builds use `artifacts/benchmark-build` so they can run alongside the development app.

Windows self-contained output: `artifacts/publish/win-x64/PhotoScreener.Desktop.exe`; zip: `artifacts/LumaPick-win-x64.zip` (EXE product name/icon: LumaPick).
For the development launcher, run `buildAndRunRewrite.bat "D:\Photos\Trip"`.
On an Apple Silicon Mac, run `bash scripts/package-macos.sh`; the initial deployment profile is macOS 14+. Set `SIGNING_IDENTITY` to sign; pass `--notarize` with an explicitly configured `NOTARY_PROFILE` to submit to Apple. No credentials are stored in this repository.
The bundle is `artifacts/LumaPick.app`, with its Dock/Finder icon; the ZIP is `artifacts/LumaPick-osx-arm64.zip`. See [macOS distribution](docs/macos-distribution.md) for certificate/keychain setup and the separate Mac App Store requirements.
Run `bash scripts/test-macos-package.sh` for a short functional check of the assembled bundle with installed-runtime lookup disabled. It creates synthetic fixtures under ignored `artifacts/`, starts the actual app window, checks all injected keys and export errors, and verifies the bundled runtime. It does not establish representative photo performance or signed installation acceptance.
From Windows, `pwsh -File scripts/Publish-Rewrite.ps1 -Runtime osx-arm64` followed by `python scripts/Assemble-MacBundle.py` produces an unsigned `.app` zip with native architecture/minimum-version inspection. It still requires execution and signing checks on a Mac.

Benchmark scripts use a fresh explicit export target under `artifacts/benchmark-exports`, preserving an existing review's export selection. Keep representative photos outside generated build output. The real-window runner records fixed-rate keyboard arrival/feedback, drawing submission, stable low/full timings, queue/cache/control counts, working set, copied/hash bytes and errors. Cold decoded-cache samples do not establish cold OS/SSD measurements.

For a private photographic corpus, add `-PrivateSource` to keep exported copies and thumbnail cache in a fresh user-temporary directory outside the repository. Reports still contain local source/target paths; keep them under ignored `artifacts/` and publish only aggregate results. Request reference-sized rendering with `-PreviewWidth 1920 -PreviewHeight 1080`; the report records the actual physical-pixel surface, which the window manager may constrain. Header inspection reads at most 100 evenly spaced image headers without decoding or changing originals:

```
dotnet run --project tests/PhotoScreener.Performance -c Release -- --inspect "E:\Photos\Trip"
pwsh -File scripts/Benchmark-Rewrite.ps1 -SourceFolder "E:\Photos\Trip" -PrivateSource -PreviewWidth 1920 -PreviewHeight 1080 -Actions 1200 -StableSamples 100 -Report artifacts/performance/private-window.json
```

See [implementation evidence](docs/implementation-evidence.md) for executed results and outstanding acceptance gates. Windows/macOS reference-corpus performance and the Mac installed/signed checks require the documented hardware/corpus and a Mac runner.

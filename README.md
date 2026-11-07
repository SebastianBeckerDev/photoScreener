# Photo Screener rewrite

The .NET 10 / Avalonia app lives in `src/`. The old WPF project, solution and launcher were removed at the user's request. The isolated legacy-format fixture helper remains in the tests.

```
dotnet restore PhotoScreener.Rewrite.sln --locked-mode
dotnet build PhotoScreener.Rewrite.sln -c Release --disable-build-servers
dotnet test PhotoScreener.Rewrite.sln -c Release --disable-build-servers
dotnet run --project src/PhotoScreener.Desktop -c Release -- "D:\Photos\Trip"
```

Left/Right navigate. Space picks/unpicks without advancing; held Space is suppressed. Buttons offer All/Picked/Unpicked, project picks, folder navigation, an export override, Refresh, pending work, Retry and Cancel. Collapse folders/strip to give the complete oriented preview more space.
Hover any button for an explanation; the filter dropdown also has help text. Retry opens an ownership choice when a legacy export fits different original filenames with identical bytes.

Use the mouse wheel over a photo to zoom around the pointer, drag with the left button to move a zoomed photo, and double-click or press Fit to return to the complete image. Zoom requests a higher-resolution preview in the background while retaining the visible image. Percentages are relative to the fit view; changing photos resets zoom to Fit.

The selected thumbnail stays centered when the strip can scroll; scrolling stops at the beginning/end without adding blank space. Info shows embedded capture time, focal length, exposure, ISO, aperture, camera/lens and available GPS coordinates. Its histogram describes sampled preview luminance. Missing embedded values show Unknown; capture time is never inferred from the file date. Info can be hidden to enlarge the preview.

Project picks also displays unmatched/older exports as preserved, read-only entries. A rescan replaces changed source revisions and removes missing originals from source counts while retaining those export files.

Launching without a folder argument automatically opens the last successfully opened folder. Its path is saved in the user's local application data (`PhotoScreener/session-v1.json`), so it remains available when an external SSD is disconnected. An unavailable folder leaves the app ready to open another folder and displays a reconnect hint; the remembered path is retained. An explicit folder argument takes precedence. Startup and folder opening give the photo review area keyboard focus immediately.

Each folder defaults to its existing `export` child, created only on its first pick. An explicit target applies to that folder for the session; use `--export PATH` for an explicit launch binding. Old/new flat exports restore completed picks by full-content verification. No database or review JSON is required for picks. Optional history and cursor JSON live under the chosen target's `.photoscreener` directory.

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

Windows self-contained output: `artifacts/publish/win-x64/PhotoScreener.Desktop.exe`; zip: `artifacts/PhotoScreener-win-x64.zip`.
For the development launcher, run `buildAndRunRewrite.bat "D:\Photos\Trip"`.
On an Apple Silicon Mac, run `bash scripts/package-macos.sh`; the initial deployment profile is macOS 14+. Set `SIGNING_IDENTITY` to sign; pass `--notarize` with an explicitly configured `NOTARY_PROFILE` to submit to Apple. No credentials are stored in this repository.
From Windows, `pwsh -File scripts/Publish-Rewrite.ps1 -Runtime osx-arm64` followed by `python scripts/Assemble-MacBundle.py` produces an unsigned `.app` zip with native architecture/minimum-version inspection. It still requires execution and signing checks on a Mac.

Benchmark scripts use a fresh explicit export target under `artifacts/benchmark-exports`, preserving an existing review's export selection. Keep representative photos outside generated build output. The real-window runner records fixed-rate keyboard arrival/feedback, drawing submission, stable low/full timings, queue/cache/control counts, working set, copied/hash bytes and errors. Cold decoded-cache samples do not establish cold OS/SSD measurements.

See [implementation evidence](docs/implementation-evidence.md) for executed results and outstanding acceptance gates. Windows/macOS reference-corpus performance and the Mac installed/signed checks require the documented hardware/corpus and a Mac runner.

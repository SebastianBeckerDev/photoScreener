# Rewrite implementation and evidence

The replacement lives in `src/`; the existing WPF app and regular launch path are preserved.
Contract: `reverse-engineered-specification/`. No performance/platform gate is inferred from unit tests.

| Package | Implementation | Executed evidence |
| --- | --- | --- |
| P-01 | Shared projects, pinned/locked dependencies, platform identities, source/export guards, isolated legacy fixture helper | Release build; 9 core/infrastructure tests pass |
| P-02 | Keyboard/progressive-preview slice, fixed workers, 32-entry secondary queue, 128 MiB decoded cache, virtualized strip, EXIF transforms, real-window harness | 9 desktop tests pass; preliminary Windows diagnostic below; reference gates pending |
| P-03 | In progress: full-content legacy comparison, ambiguity handling, bounded comparison worker | Pending |
| P-04 | Pending | Pending |
| P-05 | Pending | Pending |
| P-06 | Pending | Windows publish, Mac runner/signing, reference photographic corpus pending |

Machine available: Windows x64, .NET SDK 10.0.303. Mac runner and signing credentials have not been supplied.

## Preliminary P-02 diagnostic (2026-11-06)

Release, Windows 10.0.26300, .NET 10.0.14; Intel family 6/model 186, 12 logical processors.
100 synthetic JPEGs, 4000×3000, approximately 1.7 MB each, seed 813. Preview 841×631 physical pixels.
1,200 routed-key actions in a real Avalonia window; 88.49 seconds (timer cadence below the required 20/s).
Feedback p95 8.084 ms / p99 18.228 ms; drawing submission p95 9.845 ms / p99 19.827 ms.
Peak sampled working set 391.66 MiB; maximum secondary queue 32; cache 127.95 MiB.
The initial preview timing collector resets on Space and is being corrected; its low/full values are not acceptance evidence.
These measurements do **not** pass the reference-corpus, 1920×1080, mixed-load, uncached or macOS gates.
Generated corpus and raw report are excluded from git under `artifacts/`.

Commands:
```
dotnet build PhotoScreener.Rewrite.sln -c Release --disable-build-servers
dotnet test PhotoScreener.Rewrite.sln -c Release --disable-build-servers
dotnet run --project tests/PhotoScreener.Performance -c Release -- artifacts/corpus 100 4000 3000
src/PhotoScreener.Desktop/bin/Release/net10.0/PhotoScreener.Desktop.exe artifacts/corpus --benchmark --actions 1200 --report artifacts/performance/p02-window.json
```

Tests include an unchanged snapshot of the legacy filename/index helper so the replacement builds independently of the untracked legacy application.

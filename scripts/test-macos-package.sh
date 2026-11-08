#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
app="$root/artifacts/LumaPick.app/Contents/MacOS"
test -f "$root/artifacts/LumaPick.app/Contents/Resources/lumapick.icns"
test "$(uname -m)" = arm64
for component in PhotoScreener.Desktop libcoreclr.dylib libhostfxr.dylib System.Private.CoreLib.dll libSkiaSharp.dylib; do
  test -f "$app/$component" || { echo "Missing bundle component: $component" >&2; exit 1; }
done
corpus="$root/artifacts/macos-package-corpus"
dotnet run --project "$root/tests/PhotoScreener.Performance" -c Release --no-restore -- "$corpus" 40 4000 3000
python3 - "$root" "$app" "$corpus" <<'PY'
import json, os, pathlib, subprocess, sys, uuid
root, app, corpus = map(pathlib.Path, sys.argv[1:])
run = uuid.uuid4().hex
report = root / 'artifacts/performance/macos-package.json'
report.parent.mkdir(parents=True, exist_ok=True)
environment = dict(os.environ, DOTNET_ROOT=str(root / 'artifacts/no-installed-runtime'), DOTNET_MULTILEVEL_LOOKUP='0')
environment['DOTNET_ROOT_ARM64'] = environment['DOTNET_ROOT']
subprocess.run([str(app / 'PhotoScreener.Desktop'), str(corpus), '--export', str(root / 'artifacts/package-test-exports' / run),
                '--benchmark', '--cache-dir', str(root / 'artifacts/package-test-cache' / run), '--actions', '40', '--stable', '4', '--report', str(report)],
               env=environment, timeout=120, check=True)
result = json.loads(report.read_text())
if result.get('error') or result.get('errors') or result.get('maintenanceErrors'):
    raise RuntimeError(result.get('error') or 'Bundle recorded export/metadata failures.')
if result.get('actions') != 40 or result.get('ignoredKeys') != 0:
    raise RuntimeError('Bundle did not process every routed review key.')
if not pathlib.Path(result['runtimeAssembly']).is_relative_to(app):
    raise RuntimeError('Bundle used an external .NET runtime.')
print(json.dumps({key: result[key] for key in ('framework', 'actions', 'ignoredKeys', 'totalOperations', 'feedback', 'peakSampledWorkingSetMiB')}, indent=2))
print('Self-contained Apple Silicon bundle functional smoke passed; synthetic timing is not reference acceptance.')
PY

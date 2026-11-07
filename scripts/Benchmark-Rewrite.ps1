param([Parameter(Mandatory)][string]$SourceFolder, [int]$Actions = 1200, [int]$StableSamples = 30, [string]$Report = 'artifacts/performance/window.json', [string]$Screenshot)
$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $rewriteRoot 'src/PhotoScreener.Desktop/PhotoScreener.Desktop.csproj') -c Release --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
$target = Join-Path $rewriteRoot ('artifacts/benchmark-exports/' + [Guid]::NewGuid().ToString('N'))
$cache = Join-Path $rewriteRoot ('artifacts/benchmark-cache/' + [Guid]::NewGuid().ToString('N'))
$reportPath = if ([IO.Path]::IsPathRooted($Report)) { $Report } else { Join-Path $rewriteRoot $Report }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $rewriteRoot 'src/PhotoScreener.Desktop/bin/Release/net10.0/PhotoScreener.Desktop.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @([IO.Path]::GetFullPath($SourceFolder), '--export', $target, '--benchmark', '--cache-dir', $cache, '--actions', "$Actions", '--stable', "$StableSamples", '--report', $reportPath)) { $start.ArgumentList.Add($argument) }
if ($Screenshot) { $start.ArgumentList.Add('--screenshot'); $start.ArgumentList.Add([IO.Path]::GetFullPath($Screenshot)) }
$benchmarkProcess = [Diagnostics.Process]::Start($start)
$benchmarkProcess.WaitForExit()
if ($benchmarkProcess.ExitCode -ne 0) { throw "Window failed: $($benchmarkProcess.ExitCode)" }
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Window did not produce a report.' }
Get-Content -Raw -LiteralPath $reportPath

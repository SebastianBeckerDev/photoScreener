param([Parameter(Mandatory)][string]$SourceFolder, [int]$Actions = 40, [int]$StableSamples = 4, [string]$Report = 'artifacts/performance/windows-package.json')
$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path -Parent $PSScriptRoot
$published = Join-Path $rewriteRoot 'artifacts/publish/win-x64'
foreach ($required in @('PhotoScreener.Desktop.exe', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'libSkiaSharp.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Missing self-contained component: $required" }
}
$reportPath = if ([IO.Path]::IsPathRooted($Report)) { $Report } else { Join-Path $rewriteRoot $Report }
$runId = [Guid]::NewGuid().ToString('N')
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $published 'PhotoScreener.Desktop.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.Environment['DOTNET_ROOT'] = Join-Path $rewriteRoot 'artifacts/no-installed-runtime'
$start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
foreach ($argument in @([IO.Path]::GetFullPath($SourceFolder), '--export', (Join-Path $rewriteRoot "artifacts/package-test-exports/$runId"), '--benchmark', '--cache-dir', (Join-Path $rewriteRoot "artifacts/package-test-cache/$runId"), '--actions', "$Actions", '--stable', "$StableSamples", '--report', $reportPath)) { $start.ArgumentList.Add($argument) }
$packageProcess = [Diagnostics.Process]::Start($start)
$packageProcess.WaitForExit()
if ($packageProcess.ExitCode -ne 0) { throw "Package process failed: $($packageProcess.ExitCode)" }
$result = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
if ($result.error) { throw $result.error }
if (-not $result.runtimeAssembly.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'App used an external runtime.' }
if ($result.errors.Count -gt 0 -or $result.maintenanceErrors.Count -gt 0) { throw 'Package smoke check recorded export or optional metadata failures; inspect the report.' }
Get-Content -Raw -LiteralPath $reportPath

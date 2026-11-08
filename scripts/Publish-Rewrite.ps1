param([ValidateSet('win-x64','osx-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$rewriteRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $rewriteRoot "artifacts/publish/$Runtime"
dotnet publish (Join-Path $rewriteRoot 'src/PhotoScreener.Desktop/PhotoScreener.Desktop.csproj') -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true --disable-build-servers -o $output
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
if ($Runtime -eq 'win-x64') {
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $rewriteRoot 'artifacts/LumaPick-win-x64.zip') -Force
}
Write-Output "Published: $output"

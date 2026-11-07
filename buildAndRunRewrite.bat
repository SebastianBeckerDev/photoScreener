@echo off
dotnet run --project "%~dp0src\PhotoScreener.Desktop\PhotoScreener.Desktop.csproj" -c Release -- %*

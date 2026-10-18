# Builds the release files into dist\:
#   SawnTask-<version>-portable.exe   one self-contained exe, no install, no .NET needed
#   SawnTask-<version>-setup.exe      Inno Setup installer of the same exe
#   SHA256SUMS.txt                    checksums of both
# Usage (from the repository root):  powershell -File tools\build-release.ps1
# Needs the .NET 9 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\SawnTask\SawnTask.csproj"
$dist = Join-Path $root "dist"

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
Write-Host "SawnTask $version"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
# IncludeNativeLibrariesForSelfExtract: WPF's native DLLs go inside the exe too, so the
# exe runs on its own (without it, the exe fails to start when copied anywhere alone).
dotnet publish $project -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $dist "portable")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$portable = Join-Path $dist "SawnTask-$version-portable.exe"
Copy-Item (Join-Path $dist "portable\SawnTask.exe") $portable

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$version" (Join-Path $root "installer\SawnTask.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Remove-Item (Join-Path $dist "portable") -Recurse -Force
Get-ChildItem $dist -Filter "SawnTask-$version-*.exe" | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $dist "SHA256SUMS.txt") -Encoding ascii
Get-Content (Join-Path $dist "SHA256SUMS.txt")

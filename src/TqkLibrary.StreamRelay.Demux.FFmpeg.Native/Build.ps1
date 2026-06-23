$ErrorActionPreference = 'Stop'
$nativeSrc = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $nativeSrc '..\..')).Path
Set-Location $repo

# ---------------------------------------------------------------------------
# FFmpeg native package versions (one NuGet package per RID).
# ---------------------------------------------------------------------------
$ffmpegVersions = @{
    'win-x64'   = '8.0.1.48'
    'win-x86'   = '8.0.1.48'
    'win-arm64' = '8.0.1.48'
}
$nugetCache = Join-Path $env:USERPROFILE '.nuget\packages'

# ---- Locate CMake (prefer the one bundled with Visual Studio) -------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$cmake = $null
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
    $candidate = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (Test-Path $candidate) { $cmake = $candidate }
}
if (-not $cmake) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source }
if (-not $cmake) { throw "cmake not found (install Visual Studio C++ CMake tools or cmake on PATH)" }
Write-Host "CMake: $cmake"

# ---- Version via GitVersion ----------------------------------------------
if (-not (Get-Command dotnet-gitversion -ErrorAction SilentlyContinue)) {
    dotnet tool install -g GitVersion.Tool | Out-Host
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
$verMajor = 0; $verMinor = 0; $verBuild = 0
try {
    $gv = dotnet-gitversion /output json | ConvertFrom-Json
    $verMajor = [int]$gv.Major; $verMinor = [int]$gv.Minor; $verBuild = [int]$gv.CommitsSinceVersionSource
} catch { Write-Warning "GitVersion failed; using 0.0.0" }
Write-Host "Version: $verMajor.$verMinor.$verBuild"

$libProj = Join-Path $repo 'src\TqkLibrary.StreamRelay.Demux.FFmpeg\TqkLibrary.StreamRelay.Demux.FFmpeg.csproj'
$artifacts = Join-Path $repo 'native-artifacts'

# Restore so the FFmpeg native packages are present in the NuGet cache.
dotnet restore $libProj | Out-Host
if ($LASTEXITCODE -ne 0) { throw "restore failed" }

Remove-Item -Recurse -Force $artifacts -ErrorAction SilentlyContinue

$archMap = @{ 'win-x64' = 'x64'; 'win-x86' = 'Win32'; 'win-arm64' = 'ARM64' }

function Build-NativeWin([string]$rid) {
    $ver = $ffmpegVersions[$rid]
    $pkg = Join-Path $nugetCache "tqklibrary.ffmpeg.native.$($rid.Replace('-','.'))\$ver"
    if (-not (Test-Path $pkg)) { throw "Package not restored: $pkg" }
    $arch = $rid.Substring(4)   # x64 / x86 / arm64
    $vsArch = $archMap[$rid]
    $bld = Join-Path $repo "native-build\$rid"
    Remove-Item -Recurse -Force $bld -ErrorAction SilentlyContinue

    # Build the argument list explicitly so PowerShell expands every value before
    # handing them to cmake (a backtick-continued inline form mis-tokenises the
    # -D<name>=<value> pairs on some hosts, passing the literal '$verMajor').
    $cmakeArgs = @(
        '-A', $vsArch,
        "-DFFMPEG_INCLUDE_DIR=$pkg\build\native\include",
        "-DFFMPEG_LIB_DIR=$pkg\build\native\win\$arch\lib",
        "-DVER_FILE_MAJOR=$verMajor",
        "-DVER_FILE_MINOR=$verMinor",
        "-DVER_FILE_BUILD=$verBuild",
        '-S', $nativeSrc,
        '-B', $bld
    )
    & $cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($rid)" }
    & $cmake --build $bld --config Release
    if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($rid)" }

    $dstDir = Join-Path $artifacts "runtimes\$rid\native"
    New-Item -ItemType Directory -Force $dstDir | Out-Null
    Get-ChildItem -Recurse $bld -Include '*.dll', '*.exe', '*.pdb' |
        Where-Object { $_.Name -like 'TqkLibrary.StreamRelay.*' } |
        ForEach-Object { Copy-Item $_.FullName $dstDir -Force }
    Write-Host "Native built -> $dstDir"
}

Build-NativeWin 'win-x64'
Build-NativeWin 'win-x86'
try { Build-NativeWin 'win-arm64' }
catch { Write-Warning "win-arm64 native skipped: $($_.Exception.Message). Linux + full matrix are produced by CI." }

Write-Host "Done. Native artifacts under $artifacts"

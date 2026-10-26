# Builds bin\RadialMenu.exe with the C# compiler that ships with Windows (.NET Framework 4.8) — no SDK needed.
#   .\build.ps1          build
#   .\build.ps1 -Run     build, then (re)start the app
param([switch]$Run)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "Nu găsesc compilatorul C#: $csc (.NET Framework 4.8 lipsește?)" }

$icon = Join-Path $root 'assets\app.ico'
if (-not (Test-Path $icon)) {
    & powershell -STA -NoProfile -File (Join-Path $root 'tools\make-icon.ps1') -Out $icon
}

# The running app keeps the exe locked.
$running = Get-Process RadialMenu -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Opresc instanța care rulează...'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
}

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$out = Join-Path $bin 'RadialMenu.exe'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warn:4',
    "/out:$out",
    "/win32icon:$icon",
    "/win32manifest:$(Join-Path $root 'src\app.manifest')",
    "/resource:$(Join-Path $root 'src\default-config.json'),default-config.json",
    "/resource:$icon,app.ico",
    "/lib:$(Join-Path $fw 'WPF')",
    '/r:PresentationCore.dll', '/r:PresentationFramework.dll', '/r:WindowsBase.dll', '/r:System.Xaml.dll',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
    '/r:Microsoft.CSharp.dll', '/r:System.Core.dll', '/r:System.dll'
) + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilarea a eșuat (cod $LASTEXITCODE)." }
Write-Host "OK: $out" -ForegroundColor Green

if ($Run) { Start-Process $out }

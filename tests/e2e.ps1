# End-to-end test: drives the real app with synthesized mouse input on a throwaway text box window.
# It MOVES THE MOUSE and uses the clipboard for ~10 s — don't use the mouse while it runs.
#
# Safety: before every click the script checks which process owns the window under the cursor
# (the test window, or the radial menu itself) and aborts otherwise, so a stray window can never
# receive the test's Ctrl+A / Ctrl+C / Ctrl+V. Every "move + click" is a single atomic SendInput call.
#   powershell -STA -NoProfile -File tests\e2e.ps1 [-OutDir <folder for screenshots>]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\RadialMenu.exe'),
    [string]$OutDir = $env:TEMP
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class U {
    [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; public int pad1, pad2; }
    [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public MI mi; }
    [StructLayout(LayoutKind.Sequential)] public struct INK { public uint type; public KI ki; }
    [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INK[] i, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(P p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();

    public static uint OwnerAt(int x, int y) {
        var p = new P(); p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        IntPtr root = GetAncestor(h, 2);
        uint pid; GetWindowThreadProcessId(root == IntPtr.Zero ? h : root, out pid);
        return pid;
    }

    // Absolute move to (x, y), then up to two button events (0 = none), all in one atomic SendInput call.
    public static void At(int x, int y, uint b1, uint b2) {
        int vl = GetSystemMetrics(76), vt = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var list = new List<IN>();
        var m = new IN();
        m.mi.dx = (int)Math.Round((x - vl) * 65535.0 / (vw - 1));
        m.mi.dy = (int)Math.Round((y - vt) * 65535.0 / (vh - 1));
        m.mi.flags = 0x0001 | 0x8000 | 0x4000; // MOVE | ABSOLUTE | VIRTUALDESK
        list.Add(m);
        foreach (var b in new[] { b1, b2 }) {
            if (b == 0) continue;
            var i = new IN();
            i.mi.flags = b;
            list.Add(i);
        }
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(IN)));
    }

    public static void Key(ushort vk) {
        var a = new INK[2];
        a[0].type = 1; a[0].ki.vk = vk;
        a[1].type = 1; a[1].ki.vk = vk; a[1].ki.flags = 2;
        SendInput(2, a, Marshal.SizeOf(typeof(INK)));
    }

    public static void Shot(int x, int y, int w, int h, string path) {
        using (var b = new System.Drawing.Bitmap(w, h)) {
            IntPtr dc = GetDC(IntPtr.Zero);
            using (var g = System.Drawing.Graphics.FromImage(b)) {
                IntPtr hdc = g.GetHdc();
                BitBlt(hdc, 0, 0, w, h, dc, x, y, 0x00CC0020 | 0x40000000);
                g.ReleaseHdc(hdc);
            }
            ReleaseDC(IntPtr.Zero, dc);
            b.Save(path);
        }
    }
}
"@
[void][U]::SetProcessDpiAwarenessContext([IntPtr]-4)   # per-monitor v2: coordinates in physical pixels

$RDOWN = 0x08; $RUP = 0x10; $LDOWN = 0x02; $LUP = 0x04; $VK_ESCAPE = 0x1B
$scale = [U]::GetDpiForSystem() / 96.0
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$cx = [int]($screen.Left + $screen.Width / 2)
$cy = [int]($screen.Top + $screen.Height / 2)
$half = [int](380 * $scale)

function Pt([double]$angle, [double]$radius) {
    $a = $angle * [Math]::PI / 180
    , @([int]($cx + $radius * $scale * [Math]::Sin($a)), [int]($cy - $radius * $scale * [Math]::Cos($a)))
}
# Refuses to go on unless the window at (x, y) belongs to the expected process.
function Expect([int]$x, [int]$y, $process, [string]$what) {
    $owner = [U]::OwnerAt($x, $y)
    if ($owner -ne $process.Id) { throw "ABORT: at ($x,$y) expected $what (pid $($process.Id)) but found pid $owner — nothing was clicked." }
}
function Hover([double]$angle, [double]$radius) { $p = Pt $angle $radius; [U]::At($p[0], $p[1], 0, 0); Start-Sleep -Milliseconds 250 }
function OpenMenu {
    Expect $cx $cy $target 'the test window'
    [U]::At($cx, $cy, $RDOWN, $RUP); Start-Sleep -Milliseconds 500
    Expect $cx $cy $app 'the radial menu'
}
function Pick([double]$angle, [double]$radius) {
    $p = Pt $angle $radius
    Expect $p[0] $p[1] $app 'the radial menu'
    [U]::At($p[0], $p[1], $LDOWN, $LUP); Start-Sleep -Milliseconds 500
}
function Shot([string]$name) {
    $path = Join-Path $OutDir $name
    [U]::Shot($cx - $half, $cy - $half, 2 * $half, 2 * $half, $path)
    Write-Host "  screenshot: $path"
}
function SelectAllAndCopy {
    OpenMenu; Hover 90 106; Pick 90 197     # Editare > Selectează tot
    OpenMenu; Pick 0 106                    # Copiază
    Start-Sleep -Milliseconds 300
    Get-Clipboard -Raw
}

$results = [ordered]@{}
$oldClip = Get-Clipboard -Raw -ErrorAction SilentlyContinue
Set-Clipboard -Value 'inainte'

$running = Get-Process RadialMenu -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500 }
$env:RADIALMENU_ACCEPT_INJECTED = '1'
$app = Start-Process $Exe -PassThru
$target = Start-Process powershell -PassThru -ArgumentList '-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-File',
    "`"$(Join-Path $PSScriptRoot 'target-window.ps1')`"", '-CenterX', $cx, '-CenterY', $cy
Start-Sleep -Seconds 3

try {
    Write-Host '1. Right-click opens the radial menu, hovering Editare opens its submenu'
    OpenMenu
    Shot 'e2e-1-open.png'
    Hover 90 106
    Hover 90 197
    Shot 'e2e-2-submenu.png'
    Pick 0 0                  # centre → original context menu of the text box
    Start-Sleep -Milliseconds 300
    Shot 'e2e-3-classic.png'
    [U]::Key($VK_ESCAPE)      # closes the text box's own context menu (it has the focus now)
    Start-Sleep -Milliseconds 300

    Write-Host '2. Editare > Selectează tot, then Copiază'
    $clip = SelectAllAndCopy
    $results['select all + copy'] = if ($clip -and $clip.Trim() -eq 'salut radial') { 'OK' } else { "FAIL (clipboard: '$clip')" }

    Write-Host '3. Press-drag-release: hold right, drag to Lipește, release'
    $endX = $cx + [int](400 * $scale)
    Expect $endX $cy $target 'the test window'
    [U]::At($endX, $cy, $LDOWN, $LUP)        # caret after the text, so the paste appends
    Start-Sleep -Milliseconds 300
    Expect $cx $cy $target 'the test window'
    [U]::At($cx, $cy, $RDOWN, 0)
    Start-Sleep -Milliseconds 350
    $p = Pt 45 106
    Expect $p[0] $p[1] $app 'the radial menu'
    [U]::At($p[0], $p[1], 0, 0)
    Start-Sleep -Milliseconds 200
    [U]::At($p[0], $p[1], $RUP, 0)
    Start-Sleep -Milliseconds 500
    $clip = SelectAllAndCopy
    $results['drag-release paste'] = if ($clip -and $clip.Trim() -eq 'salut radialsalut radial') { 'OK' } else { "FAIL (text box: '$clip')" }

    Write-Host '4. Esc closes the menu'
    OpenMenu
    [U]::Key($VK_ESCAPE)      # swallowed by the app's keyboard hook while the menu is open
    Start-Sleep -Milliseconds 450
    $results['esc closes'] = if ([U]::OwnerAt($cx, $cy) -eq $target.Id) { 'OK' } else { 'FAIL (menu still open)' }
}
finally {
    Start-Sleep -Milliseconds 300
    $target | Stop-Process -Force -ErrorAction SilentlyContinue
    $app | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($oldClip) { Set-Clipboard -Value $oldClip }
}

Write-Host ''
$results.GetEnumerator() | ForEach-Object { Write-Host ("{0,-20} {1}" -f $_.Key, $_.Value) }

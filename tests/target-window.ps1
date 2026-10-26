# A throwaway window with a text box, used as the right-click target by e2e.ps1.
# It is placed explicitly at the centre of the PRIMARY monitor (CenterScreen would follow the mouse
# to another monitor, and the test would then act on whatever window sits at the screen centre).
param([int]$CenterX, [int]$CenterY)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx); }
"@
[void][Dpi]::SetProcessDpiAwarenessContext([IntPtr]-4)   # same physical-pixel coordinates as e2e.ps1

$form = New-Object System.Windows.Forms.Form
$form.Text = 'RadialMenu test target'
$form.StartPosition = 'Manual'
$form.FormBorderStyle = 'FixedToolWindow'
$form.TopMost = $true
$form.Bounds = New-Object System.Drawing.Rectangle ($CenterX - 450), ($CenterY - 60), 900, 120

$box = New-Object System.Windows.Forms.TextBox
$box.Dock = 'Fill'
$box.Multiline = $false
$box.Font = New-Object System.Drawing.Font 'Segoe UI', 28
$box.Text = 'salut radial'
$form.Controls.Add($box)

$form.Add_Shown({ $form.Activate(); $box.Focus(); $box.Select($box.TextLength, 0) })
[System.Windows.Forms.Application]::Run($form)

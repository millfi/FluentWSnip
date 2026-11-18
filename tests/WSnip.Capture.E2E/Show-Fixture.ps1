param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][int]$ParentId
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'Native.cs')
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[CaptureTestNative]::UsePhysicalPixels()

function New-FixtureWindow([string]$Title, [int]$X, [int]$Y, [int]$Width, [int]$Height,
                           [Drawing.Color]$Color, [Drawing.Color]$Marker) {
    $image = [Drawing.Bitmap]::new($Width, $Height)
    $graphics = [Drawing.Graphics]::FromImage($image)
    $brush = [Drawing.SolidBrush]::new($Marker)
    try {
        $graphics.Clear($Color)
        $graphics.FillRectangle($brush, 25, 25, 100, 90)
        $image.Save((Join-Path $Directory "$Title.png"))
    }
    finally {
        $brush.Dispose()
        $graphics.Dispose()
    }

    $form = [Windows.Forms.Form]::new()
    $form.Text = "FluentWSnip E2E $Title"
    $form.AutoScaleMode = 'None'
    $form.FormBorderStyle = 'None'
    $form.StartPosition = 'Manual'
    $form.TopMost = $true
    $form.Bounds = [Drawing.Rectangle]::new($X, $Y, $Width, $Height)
    $form.BackgroundImage = $image
    $form.BackgroundImageLayout = 'None'
    $form.Show()
    $form.Refresh()
    return $form
}

$screen = [Windows.Forms.Screen]::PrimaryScreen
if ($screen.WorkingArea.Width -lt 650 -or $screen.WorkingArea.Height -lt 550) {
    throw 'The primary display must have at least 650 x 550 usable physical pixels.'
}
$x = $screen.WorkingArea.Left + [int](($screen.WorkingArea.Width - 550) / 2)
$y = $screen.WorkingArea.Top + [int](($screen.WorkingArea.Height - 370) / 2)
$back = New-FixtureWindow Back $x $y 420 320 Blue Red
$front = New-FixtureWindow Front ($x + 210) ($y + 120) 340 250 Lime Yellow

$ready = Join-Path $Directory 'fixture.json'
@{
    Back = [CaptureTestNative]::FrameBounds($back.Handle)
    Front = [CaptureTestNative]::FrameBounds($front.Handle)
    Monitor = $screen.Bounds | Select-Object Left,Top,Right,Bottom,Width,Height
} | ConvertTo-Json | Set-Content -LiteralPath "$ready.tmp" -Encoding utf8
[IO.File]::Move("$ready.tmp", $ready)

$parentCheck = [Windows.Forms.Timer]::new()
$parentCheck.Interval = 500
$parentCheck.Add_Tick({
    if (!(Get-Process -Id $ParentId -ErrorAction SilentlyContinue) -or
        (Test-Path -LiteralPath (Join-Path $Directory 'close-fixture'))) {
        [Windows.Forms.Application]::ExitThread()
    }
})
$parentCheck.Start()
try {
    [Windows.Forms.Application]::Run()
}
finally {
    $parentCheck.Dispose()
    foreach ($form in $front, $back) {
        $image = $form.BackgroundImage
        $form.Dispose()
        $image.Dispose()
    }
}

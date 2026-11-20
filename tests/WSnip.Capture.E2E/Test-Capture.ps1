#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$layoutRoot = Join-Path $repo 'artifacts/capture-e2e'
$runId = [guid]::NewGuid().ToString('N')
$output = [IO.Directory]::CreateDirectory((Join-Path $layoutRoot "run-$runId")).FullName
$stagingPath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "FluentWSnip.CaptureE2E-$runId"))
$screenshots = [IO.Directory]::CreateDirectory((Join-Path $output 'saved')).FullName
$reports = [Collections.Generic.List[object]]::new()
$workflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$script:app = $null
$script:editorWindow = $null
$fixture = $null
$stage = 'Setup'
$packageName = 'LightStudio.FluentWSnip.CaptureE2E'
$packageTitle = 'FluentWSnip Capture E2E'

function Invoke-WinApp([string[]]$Arguments) {
    $response = @(& winapp @Arguments --json 2>&1)
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed ($code). $($response -join [Environment]::NewLine)"
    }
    ($response -join [Environment]::NewLine) | ConvertFrom-Json
}

function Wait-For([string]$Description, [scriptblock]$Condition, [int]$Seconds = 15) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    } while ($deadline.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out waiting for $Description."
}

function Get-CaptureWindows {
    if ($script:app.HasExited) { throw "FluentWSnip exited early ($($script:app.ExitCode)). Check the app log in the report." }
    Invoke-WinApp @('ui', 'list-windows', '-a', "$($script:app.Id)") |
        Where-Object { [CaptureTestNative]::IsRevealed([IntPtr][long]$_.hwnd) }
}

function Get-CaptureView([bool]$Live) {
    $windows = @(Get-CaptureWindows)
    $appWindows = @($windows | Where-Object className -eq 'WinUIDesktopWin32WindowClass')
    $bar = $appWindows | Where-Object { $_.width -gt 100 -and $_.height -lt 240 } | Select-Object -First 1
    $background = $appWindows | Where-Object { $_.width -eq $geometry.Monitor.Width -and $_.height -eq $geometry.Monitor.Height } |
        Where-Object {
            $bounds = [CaptureTestNative]::FrameBounds([IntPtr][long]$_.hwnd)
            $bounds.Left -eq $geometry.Monitor.Left -and $bounds.Top -eq $geometry.Monitor.Top
        } | Select-Object -First 1
    $highlight = $windows | Where-Object className -eq 'FluentWSnip.PickerBorder' | Select-Object -First 1
    if ($bar -and (($Live -and $highlight -and !$background) -or (!$Live -and $background -and !$highlight))) {
        return [pscustomobject]@{ Bar = "$($bar.hwnd)"; Background = "$($background.hwnd)" }
    }

    if ($bar -and !$Live -and !$background) {
        # A newly registered package can ask whether Windows may hide its capture border.
        $prompt = Invoke-WinApp @('ui', 'list-windows') |
            Where-Object { $_.className -eq 'Shell_SystemDialog' -and $_.processName -eq 'PickerHost' -and
                           $_.title.Contains($packageTitle) -and $_.isForeground } |
            Select-Object -First 1
        if ($prompt) {
            Invoke-WinApp @('ui', 'invoke', 'Button0', '-w', "$($prompt.hwnd)") | Out-Null
        }
    }
}

function Start-Capture {
    if ($script:editorWindow) {
        Invoke-WinApp @('ui', 'click', 'NewButton', '-w', $script:editorWindow) | Out-Null
        return
    }
    if (Get-Process -Name FluentWSnip -ErrorAction SilentlyContinue) {
        throw 'Close other FluentWSnip processes before this test. The app uses a shared single-instance mutex.'
    }
    $processId = [CaptureTestNative]::ActivatePackage("$($package.PackageFamilyName)!App", '--snip Rectangle')
    $script:app = Get-Process -Id $processId
    if ($script:app.Path -ne (Join-Path $deployment 'FluentWSnip.exe')) {
        $script:app = $null
        throw 'Windows did not launch the isolated test package.'
    }
    # Keep the process handle open so Get-Process can report the exit code after shutdown.
    $null = $script:app.Handle
}

function Wait-ForToolbarFocus([string]$Window) {
    $foreground = Get-CaptureWindows | Where-Object isForeground
    if ("$($foreground.hwnd)" -eq $Window) { return }
    Write-Host 'Waiting for the capture toolbar to be foreground. Click its empty margin if needed.'
    $null = Wait-For 'the capture toolbar to become foreground' {
        Get-CaptureWindows | Where-Object { "$($_.hwnd)" -eq $Window -and $_.isForeground }
    } 40
}

function Find-TrayIcon($Windows) {
    foreach ($window in $Windows) {
        $inspection = Invoke-WinApp @('ui', 'inspect', '-w', "$($window.hwnd)", '--interactive')
        $icon = $inspection.windows.elements |
            Where-Object { $_.name -like 'FluentWSnip*' -and $_.automationId -eq 'NotifyItemIcon' -and !$_.isOffscreen } |
            Select-Object -First 1
        if ($icon) {
            return [pscustomobject]@{ Window = "$($window.hwnd)"; Selector = $icon.selector }
        }
    }
}

function Exit-FromTray {
    $taskbar = Invoke-WinApp @('ui', 'list-windows') |
        Where-Object className -eq 'Shell_TrayWnd' | Select-Object -First 1
    if (!$taskbar) { throw 'Windows did not expose the taskbar.' }
    $icon = Find-TrayIcon @($taskbar)
    if (!$icon) {
        # Win+B focuses the overflow button when Windows hides notification-area icons.
        Invoke-WinApp @('ui', 'send-keys', 'win+b enter', '-w', "$($taskbar.hwnd)", '--via', 'send-input', '--allow-system-keys') | Out-Null
        $icon = Wait-For 'the FluentWSnip icon in the notification area' {
            $overflow = Invoke-WinApp @('ui', 'list-windows') |
                Where-Object { $_.className -in 'TopLevelWindowForOverflowXamlIsland', 'NotifyIconOverflowWindow' }
            Find-TrayIcon @($overflow)
        }
    }
    Invoke-WinApp @('ui', 'click', $icon.Selector, '-w', $icon.Window, '--right') | Out-Null

    $menu = Wait-For 'the FluentWSnip tray menu' {
        Get-CaptureWindows | Where-Object className -eq '#32768' | Select-Object -First 1
    }
    $exitLabels = Get-ChildItem -LiteralPath (Join-Path $repo 'src/WSnip.Core/Strings') -Filter '*.resx' |
        ForEach-Object { ([xml](Get-Content -LiteralPath $_.FullName -Raw)).root.data.Where({ $_.name -eq 'Exit' }).value }
    $inspection = Invoke-WinApp @('ui', 'inspect', '-w', "$($menu.hwnd)", '--interactive')
    $exitItem = $inspection.windows.elements |
        Where-Object { $_.type -eq 'MenuItem' -and $_.name -in $exitLabels } | Select-Object -First 1
    if (!$exitItem) { throw 'The notification-area menu did not offer Exit.' }
    $exitTimeout = 15000
    try {
        Invoke-WinApp @('ui', 'click', $exitItem.selector, '-w', "$($menu.hwnd)") | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch '"code"\s*:\s*"(no_interactive_desktop|foreground_not_target)"') { throw }
        # The native popup may have a hidden foreground owner. Let the user select the menu item
        # when WinApp's input guard rejects the click.
        Write-Host "WinApp could not click '$($exitItem.name)'. Select it directly from the FluentWSnip notification-area menu."
        $exitTimeout = 60000
    }

    if (!$script:app.WaitForExit($exitTimeout)) { throw 'FluentWSnip did not exit from the notification-area menu.' }
    if ($script:app.ExitCode -ne 0) { throw "FluentWSnip exited with code $($script:app.ExitCode)." }
}

function Wait-ForCapturedEditor([string]$Saved) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Saved)
    Wait-For 'the editor with the saved capture' {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw 'FluentWSnip exited before showing the capture.' }
        if ($script:app.MainWindowTitle.Contains($name)) {
            return "$($script:app.MainWindowHandle.ToInt64())"
        }
    }
}

function Assert-Pixel([Drawing.Bitmap]$Image, [int]$X, [int]$Y, [string]$Expected) {
    $pixel = $Image.GetPixel($X, $Y)
    $matches = if ($Expected -eq 'Transparent') {
        $pixel.A -le 8
    } else {
        $channel = [string]$Expected[0]
        $otherChannels = 'R', 'G', 'B' | Where-Object { $_ -cne $channel }
        $pixel.A -ge 245 -and $pixel.$channel -ge 100 -and
            $pixel.$channel -ge ($pixel.($otherChannels[0]) + 65) -and
            $pixel.$channel -ge ($pixel.($otherChannels[1]) + 65)
    }
    if (!$matches) { throw "Expected $Expected at ($X,$Y), got $pixel." }
}

function Assert-Image([string]$Path, [int]$Width, [int]$Height, [object[]]$Samples, [int]$Tolerance) {
    $image = [Drawing.Bitmap]::new($Path)
    try {
        if ([Math]::Abs($image.Width - $Width) -gt $Tolerance -or [Math]::Abs($image.Height - $Height) -gt $Tolerance) {
            throw "Expected $($Width)x$Height (±$Tolerance) in $Path, got $($image.Width)x$($image.Height)."
        }
        foreach ($sample in $Samples) { Assert-Pixel $image $sample[0] $sample[1] $sample[2] }
    }
    finally { $image.Dispose() }
}

function Test-CaptureMode($Case) {
    $before = @(Get-ChildItem -LiteralPath $screenshots -Filter '*.png' -File | ForEach-Object FullName)
    $sequence = [CaptureTestNative]::GetClipboardSequenceNumber()
    Start-Capture
    $view = Wait-For 'the revealed frozen overlay and toolbar' { Get-CaptureView $false }
    $button = "SnipMode$($Case.Mode)"
    Wait-ForToolbarFocus $view.Bar
    Invoke-WinApp @('ui', 'hover', '--at', $pickPoint, '-w', $view.Bar, '--dwell-time', '200') | Out-Null
    Invoke-WinApp @('ui', 'hover', $button, '-w', $view.Bar, '--dwell-time', '750') | Out-Null
    $hovered = Get-CaptureView $false
    if (!$hovered -or $hovered.Bar -ne $view.Bar -or $hovered.Background -ne $view.Background) {
        throw 'Hovering over a mode button did not preserve the same overlay and toolbar.'
    }

    Invoke-WinApp @('ui', 'click', $button, '-w', $view.Bar) | Out-Null
    $toggle = Invoke-WinApp @('ui', 'wait-for', $button, '-w', $view.Bar, '-p', 'ToggleState', '--value', 'On', '-t', '3000')
    if (!$toggle.found) { throw "The toolbar did not select $($Case.Mode)." }

    if ($Case.Mode -eq 'WholeWindow') {
        $live = Wait-For 'the live window picker' { Get-CaptureView $true }
        if ($live.Bar -ne $view.Bar) { throw 'Switching to the live picker replaced the toolbar.' }
        Invoke-WinApp @('ui', 'click', '--at', $pickPoint, '-w', $live.Bar) | Out-Null
    } else {
        # Use the active toolbar as WinApp's input target and give it physical overlay coordinates.
        switch ($Case.Mode) {
            'Rectangle' { Invoke-WinApp @('ui', 'drag', $rectangleStart, $rectangleEnd, '-w', $view.Bar) | Out-Null }
            'Freeform' { Invoke-WinApp @('ui', 'pen', '-w', $view.Bar, '--path', $outline, '--duration-ms', '150') | Out-Null }
            default { Invoke-WinApp @('ui', 'click', '--at', $pickPoint, '-w', $view.Bar) | Out-Null }
        }
    }

    $saved = Wait-For 'the saved PNG' {
        $newFiles = @(Get-ChildItem -LiteralPath $screenshots -Filter '*.png' -File | Where-Object FullName -NotIn $before)
        if ($newFiles.Count -gt 1) { throw "Expected one new saved PNG, got $($newFiles.Count)." }
        if ($newFiles.Count -eq 1) { return $newFiles[0].FullName }
    }
    $script:editorWindow = Wait-ForCapturedEditor $saved
    $copied = Join-Path $output "$($Case.Mode)-clipboard.png"
    $null = Wait-For 'a new clipboard PNG' {
        if ([CaptureTestNative]::GetClipboardSequenceNumber() -ne $sequence) {
            $bytes = [CaptureTestNative]::TryReadClipboardPng()
            if ($bytes) {
                [IO.File]::WriteAllBytes($copied, [byte[]]$bytes)
                return $true
            }
        }
    }
    foreach ($path in $saved, $copied) {
        Assert-Image $path $Case.Width $Case.Height $Case.Samples $Case.Tolerance
    }
    $reports.Add(@{ case = $Case.Mode; status = 'PASS'; saved = $saved; clipboard = $copied })
    $script:lastSaved = $saved
    Write-Host "PASS $($Case.Mode)"
}

try {
    Add-Type -Path (Join-Path $PSScriptRoot 'Native.cs')
    Add-Type -AssemblyName System.Drawing.Common
    [CaptureTestNative]::UsePhysicalPixels()

    if (Get-Process -Name FluentWSnip -ErrorAction SilentlyContinue) {
        throw 'Close FluentWSnip before starting the test. Test and production instances share a mutex.'
    }
    if (!$NoBuild) {
        $project = Join-Path $repo 'src/WSnip.App/WSnip.App.csproj'
        & dotnet build $project -c $Configuration "-p:Platform=$Architecture"
        if ($LASTEXITCODE -ne 0) { throw "The $Configuration build failed ($LASTEXITCODE)." }
    }

    $rid = "win-$($Architecture.ToLowerInvariant())"
    $build = Join-Path $repo "artifacts/bin/FluentWSnip/$($Configuration.ToLowerInvariant())_$rid"
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $build 'AppxManifest.xml') -Raw
    # A workspace can have a low integrity label, which prevents the test app from
    # capturing the desktop or talking to Explorer's notification area.
    $deployment = [IO.Directory]::CreateDirectory($stagingPath).FullName
    $manifest.Package.Identity.SetAttribute('Name', $packageName)
    $manifest.Package.Properties.DisplayName = $packageTitle
    $application = $manifest.Package.Applications.Application
    $extensions = $application.Extensions
    foreach ($extension in @($extensions.Extension)) {
        if ($extension.Category -in 'windows.protocol', 'windows.appExecutionAlias') {
            $null = $extensions.RemoveChild($extension)
        }
    }
    $application.VisualElements.SetAttribute('DisplayName', $packageTitle)
    Get-ChildItem -LiteralPath $build | Where-Object Name -NotIn @('AppX', 'AppxManifest.xml', $rid) |
        Copy-Item -Destination $deployment -Recurse
    $manifestPath = Join-Path $deployment 'AppxManifest.xml'
    $manifest.Save($manifestPath)
    Add-AppxPackage -Register $manifestPath
    $package = Get-AppxPackage -Name $packageName
    if (!$package -or $package.InstallLocation -ne $deployment) {
        throw 'Windows did not register the isolated test package.'
    }

    $settingsDirectory = [IO.Directory]::CreateDirectory((Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState/FluentWSnip")).FullName
    $appLog = Join-Path $settingsDirectory 'logs/fluentwsnip.log'
    @{
        Hotkey = ''; ModeHotkeys = @{}; CopyToClipboard = $true; AutoSave = $true
        SaveFolder = $screenshots; SdrFormat = 'Png'; HdrFormat = 'Png'
        IncludeCursor = $false; OpenEditorAfterCapture = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $settingsDirectory 'settings.json') -Encoding utf8

    $fixturePath = Join-Path $PSScriptRoot 'Show-Fixture.ps1'
    $fixtureArguments = '-NoProfile -STA -File "{0}" -Directory "{1}" -ParentId {2}' -f $fixturePath, $output, $PID
    $fixture = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -WindowStyle Hidden -PassThru -ArgumentList $fixtureArguments -RedirectStandardError (Join-Path $output 'fixture-errors.txt')
    $geometry = Wait-For 'the two fixture windows' {
        if ($fixture.HasExited) { throw "Fixture exited. See fixture-errors.txt in $output." }
        $ready = Join-Path $output 'fixture.json'
        if (Test-Path -LiteralPath $ready) { Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json }
    }
    $x = $geometry.Back.Left
    $y = $geometry.Back.Top
    $pickPoint = "$($x + 45),$($y + 50)"
    $rectangleStart = "$($x + 16),$($y + 16)"
    $rectangleEnd = "$($x + 178),$($y + 145)"
    $topLeft = $rectangleStart
    $topRight = "$($x + 180),$($y + 16)"
    $bottomLeft = "$($x + 16),$($y + 145)"
    $outline = "$topLeft $topRight $bottomLeft $topLeft"
    $monitorX = $x - $geometry.Monitor.Left
    $monitorY = $y - $geometry.Monitor.Top
    $cases = @(
        @{ Mode = 'Rectangle'; Width = 162; Height = 129; Tolerance = 0; Samples = @(@(29, 34, 'Red'), @(129, 104, 'Blue')) }
        @{ Mode = 'Window'; Width = $geometry.Back.Width; Height = $geometry.Back.Height; Tolerance = 0; Samples = @(@(45, 50, 'Red'), @(270, 235, 'Green')) }
        @{ Mode = 'Fullscreen'; Width = $geometry.Monitor.Width; Height = $geometry.Monitor.Height; Tolerance = 0
           Samples = @(@(($monitorX + 45), ($monitorY + 50), 'Red'), @(($monitorX + 270), ($monitorY + 235), 'Green')) }
        # Windows may coalesce intermediate pen moves near the vertices.
        @{ Mode = 'Freeform'; Width = 165; Height = 130; Tolerance = 24; Samples = @(@(34, 34, 'Red'), @(130, 88, 'Transparent')) }
        @{ Mode = 'WholeWindow'; Width = $geometry.Back.Width; Height = $geometry.Back.Height; Tolerance = 0; Samples = @(@(45, 50, 'Red'), @(270, 235, 'Blue')) }
    )
    foreach ($case in $cases) {
        $stage = $case.Mode
        Test-CaptureMode $case
    }

    $stage = 'Escape'
    $count = @(Get-ChildItem -LiteralPath $screenshots -Filter '*.png' -File).Count
    $sequence = [CaptureTestNative]::GetClipboardSequenceNumber()
    Start-Capture
    $view = Wait-For 'the live window picker to cancel' { Get-CaptureView $true }
    Wait-ForToolbarFocus $view.Bar
    Invoke-WinApp @('ui', 'send-keys', 'escape', '-w', $view.Bar, '--via', 'send-input') | Out-Null
    if ((Wait-ForCapturedEditor $script:lastSaved) -ne $script:editorWindow) {
        throw 'Esc did not return to the editor that showed the previous capture.'
    }
    if (@(Get-ChildItem -LiteralPath $screenshots -Filter '*.png' -File).Count -ne $count -or
        [CaptureTestNative]::GetClipboardSequenceNumber() -ne $sequence) {
        throw 'Esc changed the saved output or clipboard.'
    }
    $reports.Add(@{ case = $stage; status = 'PASS' })
    Write-Host "PASS $stage"
    $stage = 'Tray'
    $null = $script:app.CloseMainWindow()
    $null = Wait-For 'the editor to close while the process stays resident' {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw 'Closing the editor stopped the resident process.' }
        $script:app.MainWindowHandle -eq [IntPtr]::Zero
    }
    Exit-FromTray
    $reports.Add(@{ case = $stage; status = 'PASS' })
    Write-Host "PASS $stage"
}
catch {
    $reports.Add(@{ case = $stage; status = 'FAIL'; detail = $_.ToString() })
    throw
}
finally {
    try {
        if ($script:app -and !$script:app.HasExited) {
            try {
                $windows = @(Get-CaptureWindows)
                $toolbar = $windows | Where-Object {
                    $_.className -eq 'WinUIDesktopWin32WindowClass' -and $_.height -lt 240
                } | Select-Object -First 1
                if ($toolbar) {
                    Invoke-WinApp @('ui', 'invoke', 'CancelSnip', '-w', "$($toolbar.hwnd)") | Out-Null
                    $null = Wait-For 'the capture windows to close' {
                        !(Get-CaptureWindows | Where-Object {
                            $_.hwnd -eq $toolbar.hwnd -or $_.className -eq 'FluentWSnip.PickerBorder'
                        })
                    } 3
                }
            } catch { Write-Warning $_ }
            # Only the isolated process returned by Start-Capture is stored in $script:app.
            if (!$script:app.HasExited) { Stop-Process -InputObject $script:app }
            if (!$script:app.WaitForExit(3000)) {
                Write-Warning "Test process $($script:app.Id) is still running. Close it before the next run."
            }
        }
        if ($fixture -and !$fixture.HasExited) {
            [IO.File]::WriteAllText((Join-Path $output 'close-fixture'), '')
            $null = $fixture.WaitForExit(3000)
        }
        if ($deployment -and (!$script:app -or $script:app.HasExited)) {
            if ($package -and $package.InstallLocation -eq $deployment) {
                if ($appLog -and (Test-Path -LiteralPath $appLog)) { Copy-Item -LiteralPath $appLog -Destination $output }
                Remove-AppxPackage -Package $package.PackageFullName
            }
            $resolvedStagingPath = (Resolve-Path -LiteralPath $deployment).Path
            if ($resolvedStagingPath -eq $stagingPath -and
                !(Get-Item -LiteralPath $resolvedStagingPath).LinkType) {
                Remove-Item -LiteralPath $resolvedStagingPath -Recurse -Force
            }
        }
        Invoke-WinApp @('ui', 'yield') | Out-Null
    }
    catch { Write-Warning $_ }
    finally {
        $env:WINAPP_UI_WORKFLOW_ID = $workflow
        @{ results = $reports.ToArray(); appLog = (Join-Path $output 'fluentwsnip.log') } | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
        Write-Host "Results: $output"
    }
}

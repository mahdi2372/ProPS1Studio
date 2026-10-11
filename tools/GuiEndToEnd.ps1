# GUI end-to-end conversion test.
# Phase 0: real login gate - signs in with the local credentials 1/1 (no bypass).
# Phase 1: launches the published app, navigates to Convert via UI Automation,
# sets an input script, invokes Convert, and verifies a real .exe is produced.
# Phase 2: does the same on the BAT -> EXE page with a trivial .bat file.
# Never executes either produced executable.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\publish\app\ProPS1Studio.exe"),
    [int]$TimeoutSec = 180
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ExePath = (Resolve-Path $ExePath).Path
$work = Join-Path $env:TEMP "props-gui-e2e"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$scriptPath = Join-Path $work "gui-e2e-$stamp.ps1"
$outExe = [System.IO.Path]::ChangeExtension($scriptPath, ".exe")
Set-Content -Path $scriptPath -Value 'Write-Output "hello from GUI e2e"' -Encoding ASCII

# distinct base name so phase 2 does not collide with phase 1's .exe
$batPath = Join-Path $work "gui-e2e-bat-$stamp.bat"
$batOutExe = [System.IO.Path]::ChangeExtension($batPath, ".exe")
Set-Content -Path $batPath -Value "@echo hello from BAT e2e" -Encoding ASCII

# stale outputs from a same-second rerun must not fake a pass
Remove-Item $outExe, $batOutExe -ErrorAction SilentlyContinue

$UIA = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]

function Wait-Until {
    param([scriptblock]$Cond, [int]$Sec, [string]$What)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Sec) {
        $r = & $Cond
        if ($r) { return $r }
        Start-Sleep -Milliseconds 400
    }
    throw "Timeout ($Sec s) waiting for: $What"
}

function ControlCond([System.Windows.Automation.ControlType]$type) {
    New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $type)
}

function IdCond([string]$automationId) {
    New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $automationId)
}

function Set-UiaValue([System.Windows.Automation.AutomationElement]$el, [string]$value, [string]$what) {
    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if (-not $vp) { throw "ValuePattern not supported on $what" }
    $vp.SetValue($value)
}

Write-Output "SCRIPT : $scriptPath"
Write-Output "EXPECT : $outExe"
# No auth bypass and no self-test: exercise the real login gate (username "1" / password "1").
Remove-Item Env:PROPS_AUTH_BYPASS -ErrorAction SilentlyContinue
Remove-Item Env:PROPS_SELFTEST -ErrorAction SilentlyContinue
Remove-Item Env:PROPS_SELFTEST_PAGE -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $ExePath -PassThru
$exitCode = 1
try {
    Wait-Until { $proc.Refresh(); $proc.MainWindowHandle -ne [IntPtr]::Zero } 30 "first window" | Out-Null
    $root = $UIA::FromHandle($proc.MainWindowHandle)
    Write-Output "WINDOW : handle 0x$($proc.MainWindowHandle.ToString('X')) title '$($root.Current.Name)'"

    # ---- Phase 0: login gate (1/1) ------------------------------------------------
    if ($root.Current.Name -like "*Sign in*") {
        $loginHandle = $proc.MainWindowHandle

        $userBox = Wait-Until { $root.FindFirst($TS::Descendants, (IdCond "UsernameInput")) } 30 "login username box"
        $passBox = Wait-Until { $root.FindFirst($TS::Descendants, (IdCond "PasswordInput")) } 15 "login password box"
        Set-UiaValue $userBox "1" "username box"
        Set-UiaValue $passBox "1" "password box"
        Write-Output "LOGIN  : credentials set to 1/1"

        $signIn = Wait-Until { $root.FindFirst($TS::Descendants, (IdCond "SubmitButton")) } 10 "Sign in button"
        $signIn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output "LOGIN  : Sign in invoked"

        # the login window must close and the main window must appear (new handle)
        Wait-Until {
            $proc.Refresh()
            $h = $proc.MainWindowHandle
            return ($h -ne [IntPtr]::Zero -and $h -ne $loginHandle)
        } 30 "main window after login" | Out-Null

        $root = $UIA::FromHandle($proc.MainWindowHandle)
        $afterTitle = $root.Current.Name
        if ($afterTitle -like "*Sign in*") { throw "login window still up after Sign in: '$afterTitle'" }
        Write-Output "LOGIN  : PASS (main window '$afterTitle')"
    }
    else {
        Write-Output "LOGIN  : skipped (login gate not shown - KeyAuth not configured?)"
    }


    # sidebar nav list (screen coords: the ListBox inside the left ~210px of the window)
    $navList = Wait-Until {
        $winRect = $root.Current.BoundingRectangle
        $lists = $root.FindAll($TS::Descendants, (ControlCond $CT::List))
        foreach ($l in $lists) { if ($l.Current.BoundingRectangle.Left -lt ($winRect.Left + 210)) { return $l } }
        return $null
    } 30 "sidebar nav list"

    # nav item for Convert: prefer name match, fall back to known index (order is fixed)
    $itemCond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, "*Convert*")
    $item = $navList.FindFirst($TS::Children, $itemCond)
    if (-not $item) {
        $children = $navList.FindAll($TS::Children, (ControlCond $CT::ListItem))
        if ($children.Count -lt 6) { $children = $navList.FindAll($TS::Children, (ControlCond $CT::Item)) }
        if ($children.Count -lt 6) { throw "sidebar items not found (got $($children.Count))" }
        $item = $children[1]   # dashboard, convert, bat -> exe, setup, settings, diagnostics, help
    }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "NAV    : Convert page selected"

    # Convert page must be live: it has the Input and Output edits
    $edits = Wait-Until {
        $e = $root.FindAll($TS::Descendants, (ControlCond $CT::Edit))
        if ($e.Count -ge 2) { return $e }
        return $null
    } 15 "Convert page edit boxes"
    $vp = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($scriptPath)
    Write-Output "INPUT  : ps1 set via ValuePattern"

    # protection must default to ON in the shipped UI (settings default + VM wiring)
    $protectBox = Wait-Until {
        $boxes = $root.FindAll($TS::Descendants, (ControlCond $CT::CheckBox))
        foreach ($c in $boxes) { if ($c.Current.Name -like "Protect output*") { return $c } }
        return $null
    } 15 "Protect output checkbox"
    $toggle = $protectBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw "Protect output checkbox must default to ON (got $($toggle.Current.ToggleState))"
    }
    Write-Output "PROTECT: checkbox ON (default verified)"

    # main Convert button (unique: the only Button named exactly "Convert")
    $convertBtn = Wait-Until {
        $btns = $root.FindAll($TS::Descendants, (ControlCond $CT::Button))
        foreach ($b in $btns) { if ($b.Current.Name -eq "Convert") { return $b } }
        return $null
    } 10 "Convert button"
    $convertBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "ACTION : Convert invoked"

    # wait for the output exe, then for its size to settle
    Wait-Until { Test-Path $outExe } $TimeoutSec "output exe on disk" | Out-Null
    Wait-Until {
        $a = (Get-Item $outExe -ErrorAction SilentlyContinue).Length
        Start-Sleep -Seconds 1
        $b = (Get-Item $outExe -ErrorAction SilentlyContinue).Length
        return ($a -gt 0 -and $a -eq $b)
    } 30 "output size to settle" | Out-Null

    $fi = Get-Item $outExe
    $head = [System.IO.File]::ReadAllBytes($outExe)[0..1]
    $mz = ($head[0] -eq 0x4D -and $head[1] -eq 0x5A)
    if (-not $mz) { throw "output is not a PE file (missing MZ header)" }

    Write-Output ("RESULT : {0} bytes, MZ={1}" -f $fi.Length, $mz)
    Write-Output "PHASE 1 (ps1 -> exe): PASS"

    # ---- Phase 2: BAT -> EXE page -------------------------------------------------
    Write-Output "SCRIPT : $batPath"
    Write-Output "EXPECT : $batOutExe"

    # re-acquire the nav list (the earlier element may have gone stale)
    $navList = Wait-Until {
        $winRect = $root.Current.BoundingRectangle
        $lists = $root.FindAll($TS::Descendants, (ControlCond $CT::List))
        foreach ($l in $lists) { if ($l.Current.BoundingRectangle.Left -lt ($winRect.Left + 210)) { return $l } }
        return $null
    } 30 "sidebar nav list (phase 2)"

    $children = $navList.FindAll($TS::Children, (ControlCond $CT::ListItem))
    if ($children.Count -lt 7) { $children = $navList.FindAll($TS::Children, (ControlCond $CT::Item)) }
    if ($children.Count -lt 7) { throw "sidebar items not found for bat2exe (got $($children.Count))" }
    $children[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "NAV    : BAT -> EXE page selected"

    # wait for "Add files..." — unique to the BAT page, so the old Convert page
    # cannot still be in the tree when we query the edit boxes
    Wait-Until {
        $btns = $root.FindAll($TS::Descendants, (ControlCond $CT::Button))
        foreach ($b in $btns) { if ($b.Current.Name -like "Add files*") { return $true } }
        return $null
    } 15 "BAT -> EXE page live (Add files button)" | Out-Null

    $batEdits = Wait-Until {
        $e = $root.FindAll($TS::Descendants, (ControlCond $CT::Edit))
        if ($e.Count -ge 2) { return $e }
        return $null
    } 15 "BAT -> EXE page edit boxes"
    $bvp = $batEdits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $bvp.SetValue($batPath)
    Write-Output "INPUT  : bat set via ValuePattern"

    # protection defaults to ON on the BAT page as well
    $batProtect = Wait-Until {
        $boxes = $root.FindAll($TS::Descendants, (ControlCond $CT::CheckBox))
        foreach ($c in $boxes) { if ($c.Current.Name -like "Protect output*") { return $c } }
        return $null
    } 15 "BAT Protect output checkbox"
    $bToggle = $batProtect.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($bToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw "BAT Protect output checkbox must default to ON (got $($bToggle.Current.ToggleState))"
    }
    Write-Output "PROTECT: BAT checkbox ON (default verified)"

    $batBtn = Wait-Until {
        $btns = $root.FindAll($TS::Descendants, (ControlCond $CT::Button))
        foreach ($b in $btns) { if ($b.Current.Name -eq "Convert") { return $b } }
        return $null
    } 10 "BAT Convert button"
    $batBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "ACTION : BAT Convert invoked"

    Wait-Until { Test-Path $batOutExe } $TimeoutSec "bat output exe on disk" | Out-Null
    Wait-Until {
        $a = (Get-Item $batOutExe -ErrorAction SilentlyContinue).Length
        Start-Sleep -Seconds 1
        $b = (Get-Item $batOutExe -ErrorAction SilentlyContinue).Length
        return ($a -gt 0 -and $a -eq $b)
    } 30 "bat output size to settle" | Out-Null

    $bfi = Get-Item $batOutExe
    $bhead = [System.IO.File]::ReadAllBytes($batOutExe)[0..1]
    $bmz = ($bhead[0] -eq 0x4D -and $bhead[1] -eq 0x5A)
    if (-not $bmz) { throw "bat output is not a PE file (missing MZ header)" }
    Write-Output ("RESULT : {0} bytes, MZ={1}" -f $bfi.Length, $bmz)
    Write-Output "PHASE 2 (bat -> exe): PASS"

    # failure surface: read the app status/error text if conversion actually failed
    $statusText = ""
    try {
        $status = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
            $UIA::AutomationIdProperty, "PART_StatusText")))
        if ($status) { $statusText = $status.Current.Name }
    } catch { }
    if ($statusText) { Write-Output "STATUS : $statusText" }

    Write-Output "GUI E2E CONVERSION: PASS"
    $exitCode = 0
}
catch {
    Write-Output "GUI E2E CONVERSION: FAIL - $($_.Exception.Message)"
    # failure surface: read the app status/error text to see why conversion failed
    try {
        if ($root) {
            $status = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
                $UIA::AutomationIdProperty, "PART_StatusText")))
            if ($status -and $status.Current.Name) { Write-Output "STATUS : $($status.Current.Name)" }
        }
    } catch { }
    $exitCode = 1
}
finally {
    # close the app gracefully (never kill - the user may be working)
    if ($proc -and -not $proc.HasExited) {
        try {
            $w = $UIA::FromHandle($proc.MainWindowHandle)
            $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        } catch { try { $proc.CloseMainWindow() | Out-Null } catch { } }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (-not $proc.HasExited -and $sw.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 400 }
        if (-not $proc.HasExited) { $proc.Kill(); Write-Output "WARN   : app did not close gracefully" }
    }
}
exit $exitCode

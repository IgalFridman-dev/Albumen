[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$CheckpointName = 'Toolchain-Evaluation-Base',

    [Parameter()]
    [System.Management.Automation.PSCredential]$Credential,

    # An existing framework-dependent win-x64 publish folder. When omitted the
    # host SDK publishes src\PhotoSift.App into the run folder.
    [Parameter()]
    [string]$PackagePath,

    [Parameter()]
    [ValidateRange(5, 600)]
    [int]$LaunchSeconds = 20,

    # The golden toolchain checkpoint has no machine-wide Desktop runtime; its
    # extracted SDK carries the shared frameworks, so the app is pointed there.
    # Pass an empty string to use only a machine-wide runtime install.
    [Parameter()]
    [string]$GuestDotnetRoot = 'C:\PhotoSift\golden\sdk',

    [Parameter()]
    [ValidateRange(800, 3840)]
    [int]$ScreenWidth = 1600,

    [Parameter()]
    [ValidateRange(600, 2160)]
    [int]$ScreenHeight = 1000,

    # Leave the VM running at the desktop after collection instead of turning
    # it off and restoring the checkpoint, so the app can be inspected by hand.
    [Parameter()]
    [switch]$KeepRunning,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $PSScriptRoot 'PhotoSiftVerification.psm1') -Force
$deadline = [System.DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Invoke-PhotoSiftUiSmoke.ps1 from an elevated PowerShell process.'
}

Assert-PhotoSiftGoldenVmVerificationCheckpoint -CheckpointName $CheckpointName | Out-Null
$resolvedCredential = Get-PhotoSiftGoldenVmCredential -Credential $Credential

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ([string]$vm.State -cne 'Off') {
    throw "VM '$VMName' must be Off before a UI smoke run; it is $($vm.State)."
}
if (@(Get-VMNetworkAdapter -VMName $VMName).Count -ne 0) {
    throw "VM '$VMName' must have zero virtual network adapters."
}
Get-VMSnapshot -VMName $VMName -Name $CheckpointName -ErrorAction Stop | Out-Null

$runId = '{0}-ui-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $repositoryRoot "artifacts\ui-smoke\$runId"
$hostOutputPath = Join-Path $runPath 'output'
[System.IO.Directory]::CreateDirectory($hostOutputPath) | Out-Null
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$sourceDirty = [bool](& git -C $repositoryRoot status --porcelain)

Write-Output "UI smoke run $runId"
Write-Output "Output: $runPath"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $runPath 'package'
    Write-Output 'Publishing src\PhotoSift.App (Release, win-x64, framework-dependent).'
    & dotnet publish (Join-Path $repositoryRoot 'src\PhotoSift.App\PhotoSift.App.csproj') `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $PackagePath `
        -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish exited $LASTEXITCODE."
    }
}
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $PackagePath 'PhotoSift.App.exe'))) {
    throw "PhotoSift.App.exe was not found in $PackagePath."
}

$guestRoot = "C:\PhotoSift\ui-smoke\$runId"
$guestAppPath = "$guestRoot\app"
$guestOutputPath = "$guestRoot\output"
$taskName = 'PhotoSift-UiSmoke'
$session = $null

# Runs inside the guest, in PhotoSiftRunner's interactive desktop session, via
# a scheduled task. PowerShell Direct itself has no desktop to draw on.
$guestLaunchScript = @'
param([string]$AppPath, [string]$OutputPath, [int]$LaunchSeconds, [string]$DotnetRoot)
$ErrorActionPreference = 'Stop'
$log = Join-Path $OutputPath 'guest-launch.log'
function Write-Log([string]$Message) {
    Add-Content -LiteralPath $log -Value ('{0:O} {1}' -f [DateTimeOffset]::Now, $Message)
}
try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
    Add-Type -Namespace PhotoSiftSmoke -Name Native -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
"@
    [PhotoSiftSmoke.Native]::SetProcessDPIAware() | Out-Null

    $exe = Join-Path $AppPath 'PhotoSift.App.exe'
    if ($DotnetRoot) {
        $env:DOTNET_ROOT = $DotnetRoot
        $env:DOTNET_ROOT_X64 = $DotnetRoot
        Write-Log "DOTNET_ROOT=$DotnetRoot"
    }
    Write-Log "Starting $exe"
    $process = Start-Process -FilePath $exe -WorkingDirectory $AppPath -PassThru
    $deadline = [DateTimeOffset]::Now.AddSeconds($LaunchSeconds)
    while ([DateTimeOffset]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { break }
    }

    $state = [ordered]@{
        processId = $process.Id
        hasExited = $process.HasExited
        exitCode = $(if ($process.HasExited) { $process.ExitCode } else { $null })
        mainWindowTitle = $null
        uiaElementCount = 0
    }

    if (-not $process.HasExited) {
        $process.Refresh()
        $state.mainWindowTitle = $process.MainWindowTitle
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            [PhotoSiftSmoke.Native]::ShowWindow($process.MainWindowHandle, 3) | Out-Null  # maximize
            [PhotoSiftSmoke.Native]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            Start-Sleep -Seconds 2

            # Dump the UI Automation tree so missing text is visible as data,
            # not only in the screenshot.
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $all = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $rows = foreach ($element in $all) {
                $current = $element.Current
                [pscustomobject]@{
                    controlType = $current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
                    automationId = $current.AutomationId
                    className = $current.ClassName
                    name = $current.Name
                    isOffscreen = $current.IsOffscreen
                    bounds = $current.BoundingRectangle.ToString()
                }
            }
            $state.uiaElementCount = @($rows).Count
            $rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputPath 'uia-tree.json') -Encoding UTF8
        }
    }

    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $OutputPath 'screen.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()

    $state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputPath 'app-state.json') -Encoding UTF8
    Write-Log 'Completed'
}
catch {
    Write-Log "FAILED: $($_ | Out-String)"
}
finally {
    Set-Content -LiteralPath (Join-Path $OutputPath 'done.flag') -Value 'done'
}
'@

function Wait-PhotoSiftGuestCondition {
    param([scriptblock]$Condition, [string]$Description, [int]$TimeoutSeconds)
    $limit = [System.DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    if ($limit -gt $deadline) { $limit = $deadline }
    while ([System.DateTimeOffset]::Now -lt $limit) {
        try {
            if (& $Condition) { return }
        }
        catch {
            Write-Verbose "$Description not ready: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 3
    }
    throw "Timed out waiting for $Description."
}

function Open-PhotoSiftSession {
    Connect-PhotoSiftPowerShellDirect `
        -VMName $VMName `
        -CheckpointName $CheckpointName `
        -Credential $resolvedCredential `
        -Deadline $deadline
}

$runSucceeded = $false
try {
    Write-Output "Restoring checkpoint '$CheckpointName'."
    Restore-VMSnapshot -VMName $VMName -Name $CheckpointName -Confirm:$false
    Set-VMVideo -VMName $VMName -ResolutionType Single `
        -HorizontalResolution $ScreenWidth -VerticalResolution $ScreenHeight
    Start-VM -Name $VMName
    Wait-VM -Name $VMName -For Heartbeat -Timeout 300
    $session = Open-PhotoSiftSession

    # Autologon lives only in this run: the checkpoint restore at the end
    # discards it, so the password is never kept in the golden checkpoint.
    Write-Output 'Enabling one-shot autologon and restarting the guest.'
    $plainPassword = $resolvedCredential.GetNetworkCredential().Password
    Invoke-Command -Session $session -ScriptBlock {
        param($UserName, $Password)
        $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty $winlogon -Name AutoAdminLogon -Value '1'
        Set-ItemProperty $winlogon -Name DefaultUserName -Value $UserName
        Set-ItemProperty $winlogon -Name DefaultDomainName -Value $env:COMPUTERNAME
        Set-ItemProperty $winlogon -Name DefaultPassword -Value $Password
        # Skip the first-sign-in animation and lock screen so the desktop is reachable.
        $system = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
        Set-ItemProperty $system -Name EnableFirstLogonAnimation -Value 0 -Type DWord
        $personalization = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization'
        New-Item $personalization -Force | Out-Null
        Set-ItemProperty $personalization -Name NoLockScreen -Value 1 -Type DWord
    } -ArgumentList 'PhotoSiftRunner', $plainPassword
    $plainPassword = $null
    Invoke-Command -Session $session -ScriptBlock { Restart-Computer -Force }
    Remove-PSSession $session -ErrorAction SilentlyContinue
    $session = $null
    Start-Sleep -Seconds 15
    Wait-VM -Name $VMName -For Heartbeat -Timeout 300
    $session = Open-PhotoSiftSession

    Wait-PhotoSiftGuestCondition -Description 'the PhotoSiftRunner desktop' -TimeoutSeconds 300 -Condition {
        Invoke-Command -Session $session -ScriptBlock {
            [bool](Get-Process explorer -IncludeUserName -ErrorAction SilentlyContinue |
                Where-Object { $_.UserName -like '*\PhotoSiftRunner' })
        }
    }
    Start-Sleep -Seconds 10

    Write-Output 'Copying the package into the guest.'
    Invoke-Command -Session $session -ScriptBlock {
        param($AppPath, $OutputPath)
        New-Item -ItemType Directory -Path $AppPath, $OutputPath -Force | Out-Null
    } -ArgumentList $guestAppPath, $guestOutputPath
    Copy-Item -Path (Join-Path $PackagePath '*') -Destination $guestAppPath -Recurse -Force -ToSession $session

    Write-Output "Launching PhotoSift on the guest desktop for $LaunchSeconds seconds."
    Invoke-Command -Session $session -ScriptBlock {
        param($Root, $AppPath, $OutputPath, $Seconds, $ScriptText, $TaskName, $DotnetRoot)
        $scriptPath = Join-Path $Root 'launch.ps1'
        Set-Content -LiteralPath $scriptPath -Value $ScriptText -Encoding UTF8
        $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -AppPath "{1}" -OutputPath "{2}" -LaunchSeconds {3} -DotnetRoot "{4}"' -f `
            $scriptPath, $AppPath, $OutputPath, $Seconds, $DotnetRoot
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
        $taskPrincipal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\PhotoSiftRunner" -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $taskPrincipal -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    } -ArgumentList $guestRoot, $guestAppPath, $guestOutputPath, $LaunchSeconds, $guestLaunchScript, $taskName, $GuestDotnetRoot

    Wait-PhotoSiftGuestCondition -Description 'the guest launch script' -TimeoutSeconds ($LaunchSeconds + 180) -Condition {
        Invoke-Command -Session $session -ScriptBlock {
            param($OutputPath) Test-Path -LiteralPath (Join-Path $OutputPath 'done.flag')
        } -ArgumentList $guestOutputPath
    }

    Write-Output 'Collecting results.'
    Invoke-Command -Session $session -ScriptBlock {
        param($OutputPath)
        $appData = 'C:\Users\PhotoSiftRunner\AppData\Local\PhotoSift'
        if (Test-Path -LiteralPath $appData) {
            Copy-Item -LiteralPath $appData -Destination (Join-Path $OutputPath 'localappdata') -Recurse -Force
        }
        $since = (Get-Date).AddMinutes(-30)
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since; Level = 1, 2, 3 } -ErrorAction SilentlyContinue |
            Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath (Join-Path $OutputPath 'application-events.json') -Encoding UTF8
        $os = Get-CimInstance Win32_OperatingSystem
        [ordered]@{
            os = "$($os.Caption) $($os.Version)"
            desktopRuntimes = @(Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Name -ErrorAction SilentlyContinue)
            goldenDesktopRuntimes = @(Get-ChildItem 'C:\PhotoSift\golden\sdk\shared\Microsoft.WindowsDesktop.App' -Name -ErrorAction SilentlyContinue)
            sdkOnPath = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputPath 'guest-environment.json') -Encoding UTF8
    } -ArgumentList $guestOutputPath
    Copy-Item -FromSession $session -Path "$guestOutputPath\*" -Destination $hostOutputPath -Recurse -Force

    $appState = $null
    $appStatePath = Join-Path $hostOutputPath 'app-state.json'
    if (Test-Path -LiteralPath $appStatePath) {
        $appState = Get-Content -LiteralPath $appStatePath -Raw | ConvertFrom-Json
    }
    [ordered]@{
        runId = $runId
        vmName = $VMName
        checkpointName = $CheckpointName
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        packagePath = $PackagePath
        appStillRunning = ($null -ne $appState -and -not $appState.hasExited)
        mainWindowTitle = $(if ($appState) { $appState.mainWindowTitle } else { $null })
        uiaElementCount = $(if ($appState) { $appState.uiaElementCount } else { 0 })
        screenshot = $(if (Test-Path (Join-Path $hostOutputPath 'screen.png')) { 'output\screen.png' } else { $null })
        completedAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'summary.json') -Encoding UTF8

    $runSucceeded = $true
    Get-Content -LiteralPath (Join-Path $runPath 'summary.json')
}
finally {
    if ($null -ne $session) {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }
    if ($KeepRunning -and $runSucceeded) {
        Write-Output "VM '$VMName' left running at the desktop. Run this when done:"
        Write-Output "  Stop-VM -Name '$VMName' -TurnOff -Force; Restore-VMSnapshot -VMName '$VMName' -Name '$CheckpointName' -Confirm:`$false"
    }
    else {
        if ([string](Get-VM -Name $VMName).State -cne 'Off') {
            Stop-VM -Name $VMName -TurnOff -Force
        }
        Restore-VMSnapshot -VMName $VMName -Name $CheckpointName -Confirm:$false
        Write-Output "VM '$VMName' is Off and restored to '$CheckpointName'."
    }
}

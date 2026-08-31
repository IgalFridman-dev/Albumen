Set-StrictMode -Version Latest

function ConvertTo-PhotoSiftProcessArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $quoted = New-Object System.Text.StringBuilder
    [void]$quoted.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$quoted.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$quoted.Append('"')
        }
        else {
            [void]$quoted.Append(('\' * $backslashCount))
            [void]$quoted.Append($character)
        }
        $backslashCount = 0
    }

    [void]$quoted.Append(('\' * ($backslashCount * 2)))
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Invoke-PhotoSiftProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @(),

        [Parameter()]
        [string]$WorkingDirectory = [System.Environment]::CurrentDirectory,

        [Parameter()]
        [ValidateRange(0, 3600)]
        [int]$TimeoutSeconds = 0
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-PhotoSiftProcessArgument -Value $_
    }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start native process: $FilePath"
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = $false
        if ($TimeoutSeconds -gt 0) {
            $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
            if ($timedOut) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
        else {
            $process.WaitForExit()
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
            TimedOut = $timedOut
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-PhotoSiftGoldenVmContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedVMName,

        [Parameter(Mandatory)]
        [string]$ExpectedCheckpointName,

        [Parameter(Mandatory)]
        [object]$VM,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Checkpoints,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$NetworkAdapters,

        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Credential,

        [Parameter(Mandatory)]
        [bool]$IsElevated
    )

    if (-not $IsElevated) {
        throw 'The Golden VM controller must run from an elevated PowerShell process.'
    }

    $vmNameProperty = $VM.PSObject.Properties['Name']
    $vmStateProperty = $VM.PSObject.Properties['State']
    if ($null -eq $vmNameProperty -or
        [string]$vmNameProperty.Value -cne $ExpectedVMName) {
        throw "Hyper-V returned a VM that did not match '$ExpectedVMName'."
    }
    if ($null -eq $vmStateProperty -or [string]$vmStateProperty.Value -cne 'Off') {
        throw "Golden VM '$ExpectedVMName' must be Off before verification."
    }

    $matchingCheckpoints = @($Checkpoints | Where-Object {
        $nameProperty = $_.PSObject.Properties['Name']
        $null -ne $nameProperty -and
            [string]$nameProperty.Value -ceq $ExpectedCheckpointName
    })
    if ($matchingCheckpoints.Count -ne 1) {
        throw "Golden VM '$ExpectedVMName' must contain exactly one '$ExpectedCheckpointName' checkpoint."
    }

    if (@($NetworkAdapters).Count -ne 0) {
        throw "Golden VM '$ExpectedVMName' must have zero virtual network adapters."
    }

    if ($null -eq $Credential -or $Credential -isnot [System.Management.Automation.PSCredential]) {
        throw 'The Golden VM controller requires a PowerShell Direct PSCredential.'
    }

    return [pscustomobject][ordered]@{
        vmName = $ExpectedVMName
        checkpointName = $ExpectedCheckpointName
        vmState = 'Off'
        networkAdapterCount = 0
    }
}

function Assert-PhotoSiftGoldenVmVerificationCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$CheckpointName
    )

    if ($CheckpointName -ceq 'Clean-Evaluation-Base') {
        throw "Normal verification cannot use immutable recovery checkpoint '$CheckpointName'."
    }

    return $CheckpointName
}

function Connect-PhotoSiftPowerShellDirect {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter()]
        [scriptblock]$OpenSession = {
            param($RequestedVMName, $RequestedCredential)
            New-PSSession `
                -VMName $RequestedVMName `
                -Credential $RequestedCredential `
                -ErrorAction Stop
        },

        [Parameter()]
        [scriptblock]$WaitBeforeRetry = { Start-Sleep -Seconds 2 }
    )

    do {
        try {
            return & $OpenSession $VMName $Credential
        }
        catch {
            if ($_.Exception.Message -match '(?i)credential is invalid') {
                throw
            }
            if ([System.DateTimeOffset]::Now -ge $Deadline) {
                throw
            }
            & $WaitBeforeRetry
        }
    } while ($true)
}

function Get-PhotoSiftGuestRunnerInvocation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptPath,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage,

        [Parameter(Mandatory)]
        [string]$RunRoot,

        [Parameter(Mandatory)]
        [string]$SdkRoot,

        [Parameter(Mandatory)]
        [string]$PackagesRoot
    )

    return [pscustomobject][ordered]@{
        scriptPath = $ScriptPath
        stage = $Stage
        runRoot = $RunRoot
        sdkRoot = $SdkRoot
        packagesRoot = $PackagesRoot
        arguments = @(
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy', 'Bypass',
            '-File', $ScriptPath,
            '-Stage', $Stage,
            '-RunRoot', $RunRoot,
            '-SdkRoot', $SdkRoot,
            '-PackagesRoot', $PackagesRoot)
    }
}

function Get-PhotoSiftGuestEvidencePaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Items
    )

    foreach ($item in $Items) {
        $fullNameProperty = $item.PSObject.Properties['FullName']
        if ($null -eq $fullNameProperty -or
            [string]::IsNullOrWhiteSpace([string]$fullNameProperty.Value)) {
            throw 'Golden VM evidence item did not contain a FullName path.'
        }
        [string]$fullNameProperty.Value
    }
}

function Invoke-PhotoSiftGoldenVmRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$CheckpointName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$HostInputPath,

        [Parameter(Mandatory)]
        [string]$HostOutputPath,

        [Parameter(Mandatory)]
        [string]$GuestRunRoot,

        [Parameter(Mandatory)]
        [string]$SdkRoot,

        [Parameter(Mandatory)]
        [string]$PackagesRoot,

        [Parameter(Mandatory)]
        [hashtable]$Operations
    )

    $requiredOperations = @(
        'RestoreCheckpoint',
        'StartVM',
        'WaitForVM',
        'OpenSession',
        'PrepareGuest',
        'CopySource',
        'InvokeGuest',
        'CopyEvidence',
        'CloseSession',
        'StopVM')
    foreach ($operationName in $requiredOperations) {
        if (-not $Operations.ContainsKey($operationName) -or
            $Operations[$operationName] -isnot [scriptblock]) {
            throw "Golden VM operation '$operationName' was not supplied."
        }
    }

    $resolvedHostInput = [System.IO.Path]::GetFullPath($HostInputPath)
    $resolvedHostOutput = [System.IO.Path]::GetFullPath($HostOutputPath)
    if (-not [System.IO.Directory]::Exists($resolvedHostInput)) {
        throw "Host input snapshot does not exist: $resolvedHostInput"
    }
    [System.IO.Directory]::CreateDirectory($resolvedHostOutput) | Out-Null

    $session = $null
    $failures = New-Object 'System.Collections.Generic.List[string]'
    try {
        & $Operations.RestoreCheckpoint $VMName $CheckpointName
        & $Operations.StartVM $VMName
        & $Operations.WaitForVM $VMName 120
        $session = & $Operations.OpenSession $VMName $Credential
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for '$VMName'."
        }

        & $Operations.PrepareGuest $session $GuestRunRoot
        & $Operations.CopySource $session $resolvedHostInput (Join-Path $GuestRunRoot 'input')
        try {
            & $Operations.InvokeGuest $session $Stage $GuestRunRoot $SdkRoot $PackagesRoot
        }
        catch {
            $failures.Add($_.Exception.ToString())
        }

        try {
            & $Operations.CopyEvidence `
                $session `
                (Join-Path $GuestRunRoot 'output') `
                $resolvedHostOutput
        }
        catch {
            $failures.Add("Could not retrieve Golden VM evidence: $($_.Exception)")
        }
    }
    catch {
        $failures.Add($_.Exception.ToString())
    }
    finally {
        if ($null -ne $session) {
            try {
                & $Operations.CloseSession $session
            }
            catch {
                $failures.Add("Could not close the PowerShell Direct session: $($_.Exception)")
            }
        }

        try {
            & $Operations.StopVM $VMName
        }
        catch {
            $failures.Add("Could not stop Golden VM '$VMName': $($_.Exception)")
        }

        try {
            & $Operations.RestoreCheckpoint $VMName $CheckpointName
        }
        catch {
            $failures.Add("Could not restore Golden checkpoint '$CheckpointName': $($_.Exception)")
        }
    }

    if ($failures.Count -gt 0) {
        throw ($failures -join [System.Environment]::NewLine)
    }

    return [pscustomobject][ordered]@{
        vmName = $VMName
        checkpointName = $CheckpointName
        stage = $Stage
        runId = $RunId
        outputPath = $resolvedHostOutput
    }
}

function Get-PhotoSiftVerificationStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage
    )

    $testProjects = switch ($Stage) {
        'Architecture' { @('PhotoSift.Infrastructure.Tests') }
        'Core' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Scenarios.Tests')
        }
        'Full' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Infrastructure.Tests',
                'PhotoSift.Scenarios.Tests')
        }
    }

    return [pscustomobject][ordered]@{
        stage = $Stage
        testProjects = @($testProjects)
        runRuntimeBoundary = $Stage -ceq 'Full'
        runWpfSmoke = $Stage -ceq 'Full'
    }
}

function Read-PhotoSiftTrxResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectName,

        [Parameter(Mandatory)]
        [string]$TrxPath,

        [Parameter(Mandatory)]
        [string]$OutputRoot
    )

    $resolvedTrxPath = [System.IO.Path]::GetFullPath($TrxPath)
    if (-not [System.IO.File]::Exists($resolvedTrxPath)) {
        throw "Test project '$ProjectName' did not produce TRX evidence at $resolvedTrxPath."
    }

    try {
        [xml]$trx = Get-Content -Raw -LiteralPath $resolvedTrxPath
    }
    catch {
        throw "Test project '$ProjectName' produced malformed TRX evidence at ${resolvedTrxPath}: $($_.Exception.Message)"
    }

    $resultSummaries = @($trx.GetElementsByTagName('ResultSummary'))
    if ($resultSummaries.Count -ne 1) {
        throw "Test project '$ProjectName' TRX evidence did not contain exactly one ResultSummary."
    }

    $outcome = $resultSummaries[0].Attributes['outcome']
    if ($null -eq $outcome -or $outcome.Value -cne 'Completed') {
        throw "Test project '$ProjectName' TRX evidence did not have a Completed outcome."
    }

    $counters = $resultSummaries[0].SelectSingleNode("*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw "Test project '$ProjectName' TRX evidence did not contain Counters."
    }

    $counts = [ordered]@{}
    foreach ($name in 'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted') {
        $attribute = $counters.Attributes[$name]
        $value = 0
        if ($null -eq $attribute -or
            -not [int]::TryParse(
                $attribute.Value,
                [System.Globalization.NumberStyles]::Integer,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$value) -or
            $value -lt 0) {
            throw "Test project '$ProjectName' TRX evidence had an invalid '$name' count."
        }
        $counts[$name] = $value
    }

    if ($counts.executed -eq 0) {
        throw "Test project '$ProjectName' executed zero tests."
    }
    if ($counts.total -ne $counts.executed -or
        $counts.passed -ne $counts.executed -or
        $counts.notExecuted -ne 0) {
        throw "Test project '$ProjectName' TRX evidence contained inconsistent test counts."
    }
    if (($counts.failed + $counts.error + $counts.timeout + $counts.aborted) -gt 0) {
        throw "Test project '$ProjectName' TRX evidence recorded failed or error results."
    }

    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $outputRootPrefix = $resolvedOutputRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTrxPath.StartsWith(
            $outputRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test project '$ProjectName' TRX evidence was outside output: $resolvedTrxPath."
    }

    return [pscustomobject]@{
        project = $ProjectName
        resultFile = $resolvedTrxPath.Substring($outputRootPrefix.Length)
        total = $counts.total
        executed = $counts.executed
        passed = $counts.passed
        failed = $counts.failed
        error = $counts.error
        timeout = $counts.timeout
        aborted = $counts.aborted
        notExecuted = $counts.notExecuted
    }
}

function Assert-PhotoSiftSmokeResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$StandardOutput,

        [Parameter(Mandatory)]
        [bool]$TimedOut
    )

    if ($TimedOut) {
        throw 'PhotoSift WPF smoke process timed out.'
    }
    if ($ExitCode -ne 0) {
        throw "PhotoSift WPF smoke process exited $ExitCode."
    }

    $expectedMarker = 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true'
    if (@($StandardOutput -split "`r?`n") -notcontains $expectedMarker) {
        throw "PhotoSift WPF smoke output did not contain: $expectedMarker"
    }
}

function Assert-PhotoSiftVerificationResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SummaryPath,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$ExpectedStage
    )

    $resolvedSummaryPath = [System.IO.Path]::GetFullPath($SummaryPath)
    if (-not [System.IO.File]::Exists($resolvedSummaryPath)) {
        throw "Golden VM did not produce summary.json at $resolvedSummaryPath."
    }

    try {
        $summary = Get-Content -Raw -LiteralPath $resolvedSummaryPath | ConvertFrom-Json
    }
    catch {
        throw "Golden VM produced malformed summary evidence: $($_.Exception.Message)"
    }

    if (-not [bool]$summary.success) {
        throw 'Golden VM verification reported success=false.'
    }
    if ([string]$summary.stage -cne $ExpectedStage) {
        throw "Golden VM stage '$($summary.stage)' did not match '$ExpectedStage'."
    }
    if (-not [bool]$summary.networkingDisabled) {
        throw 'Golden VM evidence did not record networking as disabled.'
    }
    if ([string]$summary.sdkVersion -cne '10.0.400') {
        throw "Golden VM evidence reported unexpected SDK '$($summary.sdkVersion)'."
    }
    if ([string]$summary.desktopRuntimeVersion -cne '10.0.11') {
        throw "Golden VM evidence reported unexpected Desktop Runtime '$($summary.desktopRuntimeVersion)'."
    }

    $failedCommands = @($summary.commands | Where-Object {
        $succeededProperty = $_.PSObject.Properties['succeeded']
        if ($null -ne $succeededProperty) {
            return -not [bool]$succeededProperty.Value
        }
        return [int]$_.exitCode -ne 0
    })
    if ($failedCommands.Count -gt 0) {
        throw "Golden VM commands failed: $($failedCommands.name -join ', ')"
    }

    $testsProperty = $summary.PSObject.Properties['tests']
    if ($null -eq $testsProperty) {
        throw 'Golden VM evidence did not contain structured test results.'
    }

    $expectedProjects = @(switch ($ExpectedStage) {
        'Architecture' { @('PhotoSift.Infrastructure.Tests') }
        'Core' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Scenarios.Tests')
        }
        'Full' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Infrastructure.Tests',
                'PhotoSift.Scenarios.Tests')
        }
    })

    $testResults = @($testsProperty.Value)
    foreach ($expectedProject in $expectedProjects) {
        $projectResults = @($testResults | Where-Object {
            [string]$_.project -ceq $expectedProject
        })
        if ($projectResults.Count -ne 1) {
            throw "Golden VM evidence did not contain exactly one test result for '$expectedProject'."
        }

        $counts = @{}
        foreach ($name in 'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted') {
            $property = $projectResults[0].PSObject.Properties[$name]
            $value = 0
            if ($null -eq $property -or
                -not [int]::TryParse(
                    [string]$property.Value,
                    [System.Globalization.NumberStyles]::Integer,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref]$value) -or
                $value -lt 0) {
                throw "Golden VM evidence for '$expectedProject' had an invalid '$name' count."
            }
            $counts[$name] = $value
        }

        if ($counts.executed -eq 0) {
            throw "Golden VM evidence recorded zero executed tests for '$expectedProject'."
        }
        if ($counts.total -ne $counts.executed -or
            $counts.passed -ne $counts.executed -or
            $counts.notExecuted -ne 0) {
            throw "Golden VM evidence for '$expectedProject' contained inconsistent test counts."
        }
        if ($counts.failed -gt 0 -or
            $counts.error -gt 0 -or
            $counts.timeout -gt 0 -or
            $counts.aborted -gt 0) {
            throw "Golden VM evidence recorded failed or error results for '$expectedProject'."
        }
    }

    if ($testResults.Count -ne $expectedProjects.Count) {
        throw "Golden VM evidence contained an unexpected number of test project results for '$ExpectedStage'."
    }

    return $summary
}

Export-ModuleMember -Function `
    Assert-PhotoSiftGoldenVmContract, `
    Assert-PhotoSiftGoldenVmVerificationCheckpoint, `
    Connect-PhotoSiftPowerShellDirect, `
    Get-PhotoSiftGuestRunnerInvocation, `
    Get-PhotoSiftGuestEvidencePaths, `
    Invoke-PhotoSiftGoldenVmRun, `
    Get-PhotoSiftVerificationStage, `
    Invoke-PhotoSiftProcess, `
    Read-PhotoSiftTrxResult, `
    Assert-PhotoSiftSmokeResult, `
    Assert-PhotoSiftVerificationResult

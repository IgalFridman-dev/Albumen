[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Architecture', 'Core', 'Full')]
    [string]$Stage,

    [Parameter(Mandatory)]
    [string]$RunRoot,

    [Parameter(Mandatory)]
    [string]$SdkRoot,

    [Parameter(Mandatory)]
    [string]$PackagesRoot,

    [Parameter(Mandatory)]
    [ValidateRange(1, 7200)]
    [int]$TimeoutSeconds
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$deadline = [System.DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)

$inputPath = Join-Path $RunRoot 'input'
$workingSource = Join-Path $RunRoot 'source'
$outputPath = Join-Path $RunRoot 'output'
$dotnetPath = Join-Path $SdkRoot 'dotnet.exe'
$summaryPath = Join-Path $outputPath 'summary.json'
$summaryTemporaryPath = Join-Path $outputPath 'summary.json.tmp'
$commands = New-Object 'System.Collections.Generic.List[object]'
$testResults = New-Object 'System.Collections.Generic.List[object]'
$failure = $null
$networkingDisabled = $false
$sdkVersion = $null
$desktopRuntimeVersion = $null
$dotnetCliTelemetryOptOut = $false
$testingPlatformTelemetryOptOut = $false

$modulePath = Join-Path $inputPath 'eng\verification\PhotoSiftVerification.psm1'
Import-Module $modulePath -Force

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @(),

        [Parameter()]
        [int[]]$AcceptedExitCodes = @(0),

        [Parameter()]
        [string]$WorkingDirectory = $workingSource
    )

    $logPath = Join-Path $outputPath ($Name + '.log')
    $startedAt = [System.DateTimeOffset]::UtcNow
    $remainingSeconds = Get-PhotoSiftRemainingSeconds `
        -Deadline $deadline `
        -OperationName "guest command '$Name'"
    $processResult = Invoke-PhotoSiftProcess `
        -FilePath $FilePath `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -TimeoutSeconds $remainingSeconds
    $commandOutput = @(
        $processResult.StandardOutput.TrimEnd()
        $processResult.StandardError.TrimEnd()
    ) | Where-Object { $_.Length -gt 0 }
    $commandOutput | Out-File -LiteralPath $logPath -Encoding utf8

    $result = [pscustomobject]@{
        name = $Name
        filePath = $FilePath
        arguments = $Arguments
        exitCode = $processResult.ExitCode
        succeeded = -not $processResult.TimedOut -and
            $AcceptedExitCodes -contains $processResult.ExitCode
        timedOut = $processResult.TimedOut
        startedAtUtc = $startedAt.ToString('O')
        durationMilliseconds = [long]([System.DateTimeOffset]::UtcNow - $startedAt).TotalMilliseconds
        logPath = [System.IO.Path]::GetFileName($logPath)
        output = ($commandOutput -join [System.Environment]::NewLine)
    }
    $commands.Add($result)

    if ($processResult.TimedOut) {
        throw "Command '$Name' timed out. See $logPath."
    }
    if ($AcceptedExitCodes -notcontains $processResult.ExitCode) {
        throw "Command '$Name' exited $($processResult.ExitCode). See $logPath."
    }
    return $result
}

function Add-TestProjectEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectName,

        [Parameter(Mandatory)]
        [string]$ResultFileName
    )

    $testEvidence = Read-PhotoSiftTrxResult `
        -ProjectName $ProjectName `
        -TrxPath (Join-Path (Join-Path $outputPath 'TestResults') $ResultFileName) `
        -OutputRoot $outputPath
    $testResults.Add($testEvidence)
}

try {
    foreach ($requiredPath in $inputPath, $SdkRoot, $PackagesRoot) {
        if (-not [System.IO.Directory]::Exists($requiredPath)) {
            throw "Required Golden VM directory does not exist: $requiredPath"
        }
    }
    if (-not [System.IO.File]::Exists($dotnetPath)) {
        throw "Pinned Golden VM SDK executable does not exist: $dotnetPath"
    }

    [System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($workingSource) | Out-Null

    $activeNetworkAdapters = @(
        Get-NetAdapter -ErrorAction Stop |
            Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' })
    $networkingDisabled = $activeNetworkAdapters.Count -eq 0
    if (-not $networkingDisabled) {
        throw "Golden VM has active network adapters: $($activeNetworkAdapters.Name -join ', ')"
    }

    Invoke-LoggedCommand `
        -Name 'copy-source' `
        -FilePath (Join-Path $env:SystemRoot 'System32\robocopy.exe') `
        -Arguments @(
            $inputPath,
            $workingSource,
            '/E',
            '/XJ',
            '/SL',
            '/XD', '.git', 'bin', 'obj',
            '/XF', '.git',
            '/R:1',
            '/W:1',
            '/NFL',
            '/NDL',
            '/NJH',
            '/NJS') `
        -AcceptedExitCodes (0..7) `
        -WorkingDirectory $RunRoot | Out-Null

    $env:DOTNET_ROOT = $SdkRoot
    $env:PATH = $SdkRoot + ';' + $env:PATH
    $env:DOTNET_CLI_HOME = Join-Path $RunRoot 'dotnet-home'
    $env:NUGET_PACKAGES = $PackagesRoot
    $env:APPDATA = Join-Path $RunRoot 'appdata'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:NUGET_XMLDOC_MODE = 'skip'
    $dotnetCliTelemetryOptOut = $env:DOTNET_CLI_TELEMETRY_OPTOUT -ceq '1'
    $testingPlatformTelemetryOptOut = $env:TESTINGPLATFORM_TELEMETRY_OPTOUT -ceq '1'
    [System.IO.Directory]::CreateDirectory($env:DOTNET_CLI_HOME) | Out-Null
    [System.IO.Directory]::CreateDirectory($env:APPDATA) | Out-Null

    $sdkResult = Invoke-LoggedCommand `
        -Name 'dotnet-version' `
        -FilePath $dotnetPath `
        -Arguments @('--version')
    $sdkVersion = $sdkResult.output.Trim()
    $runtimesResult = Invoke-LoggedCommand `
        -Name 'dotnet-runtimes' `
        -FilePath $dotnetPath `
        -Arguments @('--list-runtimes')
    $desktopRuntimeLine = @($runtimesResult.output -split "`r?`n" |
        Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App\s+' } |
        Select-Object -First 1)
    if ($desktopRuntimeLine.Count -ne 1 -or
        $desktopRuntimeLine[0] -notmatch '^Microsoft\.WindowsDesktop\.App\s+([^\s]+)') {
        throw 'Could not identify the Windows Desktop runtime version.'
    }
    $desktopRuntimeVersion = $Matches[1]
    if ($sdkVersion -cne '10.0.400') {
        throw "Expected .NET SDK 10.0.400 but found $sdkVersion."
    }
    if ($desktopRuntimeVersion -cne '10.0.11') {
        throw "Expected Windows Desktop Runtime 10.0.11 but found $desktopRuntimeVersion."
    }

    $stageDefinition = Get-PhotoSiftVerificationStage -Stage $Stage
    if ($Stage -ceq 'Architecture') {
        $projectPath = 'tests\PhotoSift.Infrastructure.Tests\PhotoSift.Infrastructure.Tests.csproj'
        Invoke-LoggedCommand `
            -Name $stageDefinition.restoreName `
            -FilePath $dotnetPath `
            -Arguments $stageDefinition.restoreArguments | Out-Null
        Invoke-LoggedCommand `
            -Name 'build-architecture' `
            -FilePath $dotnetPath `
            -Arguments @('build', $projectPath, '-c', 'Release', '-r', 'win-x64', '--no-restore') | Out-Null
        Invoke-LoggedCommand `
            -Name 'test-architecture' `
            -FilePath $dotnetPath `
            -Arguments @(
                'test', $projectPath,
                '-c', 'Release',
                '-r', 'win-x64',
                '--no-build',
                '--no-restore',
                '--logger', 'trx;LogFileName=architecture.trx',
                '--results-directory', (Join-Path $outputPath 'TestResults')) | Out-Null
        Add-TestProjectEvidence `
            -ProjectName 'PhotoSift.Infrastructure.Tests' `
            -ResultFileName 'architecture.trx'
    }
    else {
        Invoke-LoggedCommand `
            -Name $stageDefinition.restoreName `
            -FilePath $dotnetPath `
            -Arguments $stageDefinition.restoreArguments | Out-Null

        if ($Stage -ceq 'Full') {
            Invoke-LoggedCommand `
                -Name 'build-solution' `
                -FilePath $dotnetPath `
                -Arguments @('build', 'PhotoSift.slnx', '-c', 'Release', '--no-restore') | Out-Null
        }

        foreach ($testName in @($stageDefinition.testProjects)) {
            $testProject = "tests\$testName\$testName.csproj"
            if ($Stage -ceq 'Core') {
                Invoke-LoggedCommand `
                    -Name ('build-' + $testName) `
                    -FilePath $dotnetPath `
                    -Arguments @('build', $testProject, '-c', 'Release', '-r', 'win-x64', '--no-restore') | Out-Null
            }
            Invoke-LoggedCommand `
                -Name ('test-' + $testName) `
                -FilePath $dotnetPath `
                -Arguments @(
                    'test', $testProject,
                    '-c', 'Release',
                    '-r', 'win-x64',
                    '--no-build',
                    '--no-restore',
                    '--logger', "trx;LogFileName=$testName.trx",
                    '--results-directory', (Join-Path $outputPath 'TestResults')) | Out-Null
            Add-TestProjectEvidence `
                -ProjectName $testName `
                -ResultFileName ($testName + '.trx')
        }
    }

    if ($stageDefinition.runRuntimeBoundary) {
        Invoke-LoggedCommand `
            -Name 'runtime-boundary' `
            -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -Arguments @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', $stageDefinition.runtimeBoundaryScript,
                '-Configuration', 'Release',
                '-RuntimeIdentifier', 'win-x64') | Out-Null
    }
    if ($stageDefinition.runWpfSmoke) {
        Invoke-LoggedCommand `
            -Name 'wpf-smoke' `
            -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -Arguments @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', $stageDefinition.wpfSmokeScript,
                '-Configuration', 'Release',
                '-RuntimeIdentifier', 'win-x64') | Out-Null
    }
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    [System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
    $summary = [ordered]@{
        success = $null -eq $failure
        stage = $Stage
        networkingDisabled = $networkingDisabled
        sdkVersion = $sdkVersion
        desktopRuntimeVersion = $desktopRuntimeVersion
        dotnetCliTelemetryOptOut = $dotnetCliTelemetryOptOut
        testingPlatformTelemetryOptOut = $testingPlatformTelemetryOptOut
        error = $failure
        commands = $commands
        tests = $testResults
        completedAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    }
    $summary | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $summaryTemporaryPath -Encoding utf8
    Move-Item `
        -LiteralPath $summaryTemporaryPath `
        -Destination $summaryPath `
        -Force
}

if ($null -ne $failure) {
    throw $failure
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Architecture', 'Core', 'Full')]
    [string]$Stage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$mappedSource = 'C:\PhotoSift\input'
$workingSource = 'C:\PhotoSift\source'
$outputPath = 'C:\PhotoSift\output'
$dotnetPath = 'C:\PhotoSift\sdk\dotnet.exe'
$summaryPath = Join-Path $outputPath 'summary.json'
$summaryTemporaryPath = Join-Path $outputPath 'summary.json.tmp'
$commands = New-Object 'System.Collections.Generic.List[object]'
$failure = $null
$networkingDisabled = $false
$sdkVersion = $null
$desktopRuntimeVersion = $null

$sandboxModulePath = Join-Path $mappedSource 'eng\sandbox\PhotoSiftSandbox.psm1'
Import-Module $sandboxModulePath -Force

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
    Push-Location $WorkingDirectory
    try {
        $processResult = Invoke-PhotoSiftProcess `
            -FilePath $FilePath `
            -Arguments $Arguments `
            -WorkingDirectory $WorkingDirectory
        $exitCode = $processResult.ExitCode
        $commandOutput = @(
            $processResult.StandardOutput.TrimEnd()
            $processResult.StandardError.TrimEnd()
        ) | Where-Object { $_.Length -gt 0 }
    }
    finally {
        Pop-Location
    }

    $commandOutput | Out-File -LiteralPath $logPath -Encoding utf8
    $result = [pscustomobject]@{
        name = $Name
        filePath = $FilePath
        arguments = $Arguments
        exitCode = $exitCode
        succeeded = $AcceptedExitCodes -contains $exitCode
        startedAtUtc = $startedAt.ToString('O')
        durationMilliseconds = [long]([System.DateTimeOffset]::UtcNow - $startedAt).TotalMilliseconds
        logPath = [System.IO.Path]::GetFileName($logPath)
        output = ($commandOutput -join [System.Environment]::NewLine)
    }
    $commands.Add($result)

    if ($AcceptedExitCodes -notcontains $exitCode) {
        throw "Command '$Name' exited $exitCode. See $logPath."
    }

    return $result
}

try {
    [System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($workingSource) | Out-Null

    Get-ChildItem -Recurse -File -LiteralPath $mappedSource |
        ForEach-Object { $_.FullName.Substring($mappedSource.Length).TrimStart('\') } |
        Sort-Object |
        Set-Content -LiteralPath (Join-Path $outputPath 'mapped-source-files.txt') -Encoding utf8

    $activeNetworkAdapters = @(
        Get-NetAdapter -ErrorAction Stop |
            Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' })
    $networkingDisabled = $activeNetworkAdapters.Count -eq 0
    if (-not $networkingDisabled) {
        $adapterNames = $activeNetworkAdapters.Name -join ', '
        throw "Windows Sandbox has active network adapters: $adapterNames"
    }

    Invoke-LoggedCommand `
        -Name 'copy-source' `
        -FilePath 'C:\Windows\System32\robocopy.exe' `
        -Arguments @(
            $mappedSource,
            $workingSource,
            '/E',
            '/XD', '.git', 'bin', 'obj',
            '/XF', '.git',
            '/R:1',
            '/W:1',
            '/NFL',
            '/NDL',
            '/NJH',
            '/NJS') `
        -AcceptedExitCodes (0..7) `
        -WorkingDirectory 'C:\PhotoSift' | Out-Null

    Get-ChildItem -Recurse -File -LiteralPath $workingSource |
        ForEach-Object { $_.FullName.Substring($workingSource.Length).TrimStart('\') } |
        Sort-Object |
        Set-Content -LiteralPath (Join-Path $outputPath 'working-source-files.txt') -Encoding utf8

    $requiredArchitectureProject = Join-Path $workingSource 'tests\PhotoSift.Infrastructure.Tests\PhotoSift.Infrastructure.Tests.csproj'
    if (-not [System.IO.File]::Exists($requiredArchitectureProject)) {
        throw "Required architecture project was not copied: $requiredArchitectureProject"
    }

    $env:DOTNET_ROOT = 'C:\PhotoSift\sdk'
    $env:PATH = 'C:\PhotoSift\sdk;' + $env:PATH
    $env:DOTNET_CLI_HOME = 'C:\PhotoSift\dotnet-home'
    $env:NUGET_PACKAGES = 'C:\PhotoSift\packages'
    $env:APPDATA = 'C:\PhotoSift\appdata'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:NUGET_XMLDOC_MODE = 'skip'

    foreach ($directory in $env:DOTNET_CLI_HOME, $env:APPDATA) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $sdkResult = Invoke-LoggedCommand -Name 'dotnet-version' -FilePath $dotnetPath -Arguments @('--version')
    $sdkVersion = $sdkResult.output.Trim()
    $runtimesResult = Invoke-LoggedCommand -Name 'dotnet-runtimes' -FilePath $dotnetPath -Arguments @('--list-runtimes')
    $desktopRuntimeLine = @($runtimesResult.output -split "`r?`n" |
        Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App\s+' } |
        Select-Object -First 1)
    if ($desktopRuntimeLine.Count -ne 1 -or $desktopRuntimeLine[0] -notmatch '^Microsoft\.WindowsDesktop\.App\s+([^\s]+)') {
        throw 'Could not identify the Windows Desktop runtime version.'
    }
    $desktopRuntimeVersion = $Matches[1]

    if ($sdkVersion -ne '10.0.400') {
        throw "Expected .NET SDK 10.0.400 but found $sdkVersion."
    }
    if ($desktopRuntimeVersion -ne '10.0.11') {
        throw "Expected Windows Desktop Runtime 10.0.11 but found $desktopRuntimeVersion."
    }

    if ($Stage -eq 'Architecture') {
        $projectPath = 'tests\PhotoSift.Infrastructure.Tests\PhotoSift.Infrastructure.Tests.csproj'
        Invoke-LoggedCommand `
            -Name 'restore-architecture' `
            -FilePath $dotnetPath `
            -Arguments @('restore', $projectPath, '-r', 'win-x64', '--locked-mode', '--configfile', 'NuGet.Config', '-p:NuGetAudit=false') | Out-Null
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
                '--results-directory', 'C:\PhotoSift\output\TestResults') | Out-Null
    }
    elseif ($Stage -eq 'Core') {
        Invoke-LoggedCommand `
            -Name 'restore-core' `
            -FilePath $dotnetPath `
            -Arguments @('restore', 'PhotoSift.slnx', '-r', 'win-x64', '--locked-mode', '--configfile', 'NuGet.Config', '-p:NuGetAudit=false') | Out-Null

        $coreTestProjects = @(
            'tests\PhotoSift.Domain.Tests\PhotoSift.Domain.Tests.csproj',
            'tests\PhotoSift.Application.Tests\PhotoSift.Application.Tests.csproj',
            'tests\PhotoSift.Analysis.Tests\PhotoSift.Analysis.Tests.csproj',
            'tests\PhotoSift.Scenarios.Tests\PhotoSift.Scenarios.Tests.csproj')
        foreach ($testProject in $coreTestProjects) {
            $testName = [System.IO.Path]::GetFileNameWithoutExtension($testProject)
            Invoke-LoggedCommand `
                -Name ('build-' + $testName) `
                -FilePath $dotnetPath `
                -Arguments @('build', $testProject, '-c', 'Release', '-r', 'win-x64', '--no-restore') | Out-Null
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
                    '--results-directory', 'C:\PhotoSift\output\TestResults') | Out-Null
        }
    }
    else {
        Invoke-LoggedCommand `
            -Name 'restore-solution' `
            -FilePath $dotnetPath `
            -Arguments @('restore', 'PhotoSift.slnx', '-r', 'win-x64', '--locked-mode', '--configfile', 'NuGet.Config', '-p:NuGetAudit=false') | Out-Null
        Invoke-LoggedCommand `
            -Name 'build-solution' `
            -FilePath $dotnetPath `
            -Arguments @('build', 'PhotoSift.slnx', '-c', 'Release', '--no-restore') | Out-Null

        $testProjects = @(
            'tests\PhotoSift.Domain.Tests\PhotoSift.Domain.Tests.csproj',
            'tests\PhotoSift.Application.Tests\PhotoSift.Application.Tests.csproj',
            'tests\PhotoSift.Analysis.Tests\PhotoSift.Analysis.Tests.csproj',
            'tests\PhotoSift.Infrastructure.Tests\PhotoSift.Infrastructure.Tests.csproj',
            'tests\PhotoSift.Scenarios.Tests\PhotoSift.Scenarios.Tests.csproj')
        foreach ($testProject in $testProjects) {
            $testName = [System.IO.Path]::GetFileNameWithoutExtension($testProject)
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
                    '--results-directory', 'C:\PhotoSift\output\TestResults') | Out-Null
        }

        Invoke-LoggedCommand `
            -Name 'runtime-boundary' `
            -FilePath 'powershell.exe' `
            -Arguments @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', 'eng\Verify-RuntimeBoundary.ps1',
                '-Configuration', 'Release',
                '-RuntimeIdentifier', 'win-x64') | Out-Null
        Invoke-LoggedCommand `
            -Name 'wpf-smoke' `
            -FilePath 'powershell.exe' `
            -Arguments @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', 'eng\Invoke-OfflineLaunchSmoke.ps1',
                '-Configuration', 'Release',
                '-RuntimeIdentifier', 'win-x64') | Out-Null
    }
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    $summary = [ordered]@{
        success = $null -eq $failure
        stage = $Stage
        networkingDisabled = $networkingDisabled
        sdkVersion = $sdkVersion
        desktopRuntimeVersion = $desktopRuntimeVersion
        error = $failure
        commands = $commands
        completedAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryTemporaryPath -Encoding utf8
    Move-Item -LiteralPath $summaryTemporaryPath -Destination $summaryPath -Force
    Start-Process -FilePath 'C:\Windows\System32\shutdown.exe' -ArgumentList '/s /t 2 /f' -WindowStyle Hidden
}

if ($null -ne $failure) {
    throw $failure
}

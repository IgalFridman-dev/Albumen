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
        [string]$WorkingDirectory = [System.Environment]::CurrentDirectory
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
        $process.WaitForExit()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function New-PhotoSiftSandboxConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$SdkPath,

        [Parameter(Mandatory)]
        [string]$PackagesPath,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage
    )

    $paths = @{
        Source = [System.IO.Path]::GetFullPath($SourcePath)
        Sdk = [System.IO.Path]::GetFullPath($SdkPath)
        Packages = [System.IO.Path]::GetFullPath($PackagesPath)
        Output = [System.IO.Path]::GetFullPath($OutputPath)
    }

    foreach ($entry in $paths.GetEnumerator()) {
        if (-not [System.IO.Directory]::Exists($entry.Value)) {
            throw "$($entry.Key) directory does not exist: $($entry.Value)"
        }
    }

    $escapedSource = [System.Security.SecurityElement]::Escape($paths.Source)
    $escapedSdk = [System.Security.SecurityElement]::Escape($paths.Sdk)
    $escapedPackages = [System.Security.SecurityElement]::Escape($paths.Packages)
    $escapedOutput = [System.Security.SecurityElement]::Escape($paths.Output)

    return @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <ProtectedClient>Enable</ProtectedClient>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedSource</HostFolder>
      <SandboxFolder>C:\PhotoSift\input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$escapedSdk</HostFolder>
      <SandboxFolder>C:\PhotoSift\sdk</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$escapedPackages</HostFolder>
      <SandboxFolder>C:\PhotoSift\packages</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$escapedOutput</HostFolder>
      <SandboxFolder>C:\PhotoSift\output</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PhotoSift\input\eng\sandbox\Run-PhotoSiftVerification.ps1 -Stage $Stage</Command>
  </LogonCommand>
</Configuration>
"@
}

function Assert-PhotoSiftSandboxResult {
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
        throw "Windows Sandbox did not produce summary.json at $resolvedSummaryPath."
    }

    $summary = Get-Content -Raw -LiteralPath $resolvedSummaryPath | ConvertFrom-Json
    if (-not [bool]$summary.success) {
        throw 'Windows Sandbox verification reported success=false.'
    }

    if ([string]$summary.stage -ne $ExpectedStage) {
        throw "Windows Sandbox stage '$($summary.stage)' did not match '$ExpectedStage'."
    }

    if (-not [bool]$summary.networkingDisabled) {
        throw 'Windows Sandbox evidence did not record networking as disabled.'
    }

    $failedCommands = @($summary.commands | Where-Object {
        $succeededProperty = $_.PSObject.Properties['succeeded']
        if ($null -ne $succeededProperty) {
            return -not [bool]$succeededProperty.Value
        }

        return [int]$_.exitCode -ne 0
    })
    if ($failedCommands.Count -gt 0) {
        $names = $failedCommands.name -join ', '
        throw "Windows Sandbox commands failed: $names"
    }

    return $summary
}

Export-ModuleMember `
    -Function New-PhotoSiftSandboxConfiguration, Assert-PhotoSiftSandboxResult, Invoke-PhotoSiftProcess

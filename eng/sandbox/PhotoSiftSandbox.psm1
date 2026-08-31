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

function Find-PhotoSiftTestAdapterTargets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$SearchRoots
    )

    $adapterName = 'xunit.runner.visualstudio.testadapter.dll'
    $targets = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($searchRoot in $SearchRoots) {
        if ([string]::IsNullOrWhiteSpace($searchRoot)) {
            throw 'Adapter search roots must not contain an empty path.'
        }

        $resolvedRoot = [System.IO.Path]::GetFullPath($searchRoot)
        if (-not [System.IO.Directory]::Exists($resolvedRoot)) {
            continue
        }

        foreach ($candidate in @(Get-ChildItem `
                -LiteralPath $resolvedRoot `
                -Filter $adapterName `
                -File `
                -Recurse `
                -Force `
                -ErrorAction Stop)) {
            if (-not [string]::Equals(
                    $candidate.Name,
                    $adapterName,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $resolvedTarget = [System.IO.Path]::GetFullPath($candidate.FullName)
            $targets[$resolvedTarget] = $resolvedTarget
        }
    }

    return @($targets.Values | Sort-Object)
}

function Invoke-PhotoSiftTestAdapterSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$SearchRoots,

        [Parameter(Mandatory)]
        [object]$Certificate,

        [Parameter(Mandatory)]
        [string]$Checkpoint,

        [Parameter(Mandatory)]
        [System.Collections.IList]$EvidenceList,

        [Parameter()]
        [scriptblock]$GetSignature = {
            param($Path)
            Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
        },

        [Parameter()]
        [scriptblock]$SetSignature = {
            param($Path, $SigningCertificate)
            Set-AuthenticodeSignature `
                -LiteralPath $Path `
                -Certificate $SigningCertificate `
                -HashAlgorithm SHA256 `
                -ErrorAction Stop
        }
    )

    $targets = @(Find-PhotoSiftTestAdapterTargets -SearchRoots $SearchRoots)
    if ($targets.Count -eq 0) {
        throw "Adapter signing checkpoint '$Checkpoint' found zero xUnit VSTest adapter targets."
    }

    $results = New-Object 'System.Collections.Generic.List[object]'
    foreach ($target in $targets) {
        $beforeSignature = & $GetSignature $target
        if ($null -eq $beforeSignature) {
            throw "Adapter signing checkpoint '$Checkpoint' returned no pre-sign status for '$target'."
        }
        $beforeStatusProperty = $beforeSignature.PSObject.Properties['Status']
        if ($null -eq $beforeStatusProperty -or
            [string]::IsNullOrWhiteSpace([string]$beforeStatusProperty.Value)) {
            throw "Adapter signing checkpoint '$Checkpoint' could not read the pre-sign status for '$target'."
        }

        $beforeStatus = [string]$beforeStatusProperty.Value
        if ($beforeStatus -ceq 'Valid') {
            $action = 'Preserved'
        }
        elseif ($beforeStatus -ceq 'NotSigned') {
            try {
                & $SetSignature $target $Certificate | Out-Null
            }
            catch {
                $failedEvidence = [pscustomobject][ordered]@{
                    checkpoint = $Checkpoint
                    targetPath = $target
                    beforeStatus = $beforeStatus
                    afterStatus = 'Unavailable'
                    action = 'Failed'
                }
                $EvidenceList.Add($failedEvidence) | Out-Null
                throw "Adapter signing checkpoint '$Checkpoint' failed to sign '$target': $($_.Exception.Message)"
            }
            $action = 'Signed'
        }
        else {
            $rejectedEvidence = [pscustomobject][ordered]@{
                checkpoint = $Checkpoint
                targetPath = $target
                beforeStatus = $beforeStatus
                afterStatus = $beforeStatus
                action = 'Rejected'
            }
            $EvidenceList.Add($rejectedEvidence) | Out-Null
            $results.Add($rejectedEvidence)
            throw "Adapter signing checkpoint '$Checkpoint' rejected pre-sign status '$beforeStatus' for '$target'."
        }

        $afterSignature = & $GetSignature $target
        $afterStatusProperty = $afterSignature.PSObject.Properties['Status']
        $afterStatus = if ($null -eq $afterStatusProperty) {
            ''
        }
        else {
            [string]$afterStatusProperty.Value
        }

        $evidence = [pscustomobject][ordered]@{
            checkpoint = $Checkpoint
            targetPath = $target
            beforeStatus = $beforeStatus
            afterStatus = $afterStatus
            action = $action
        }
        $EvidenceList.Add($evidence) | Out-Null
        $results.Add($evidence)

        if ($afterStatus -cne 'Valid') {
            throw "Adapter signing checkpoint '$Checkpoint' left '$target' with invalid post-sign status '$afterStatus'."
        }
    }

    return $results.ToArray()
}

function New-PhotoSiftSandboxTestCertificate {
    [CmdletBinding()]
    param(
        [Parameter()]
        [scriptblock]$CreateCertificate = {
            param($Subject, $Type, $StoreLocation)
            New-SelfSignedCertificate `
                -Subject $Subject `
                -Type $Type `
                -CertStoreLocation $StoreLocation `
                -HashAlgorithm SHA256 `
                -KeyExportPolicy NonExportable `
                -NotAfter ([System.DateTime]::Now.AddDays(1)) `
                -ErrorAction Stop
        },

        [Parameter()]
        [scriptblock]$AddCertificateToStore = {
            param($Certificate, $StoreName)
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                $StoreName,
                [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
            try {
                $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                $store.Add($Certificate)
            }
            finally {
                $store.Close()
            }
        },

        [Parameter()]
        [scriptblock]$RemoveCertificateFromStore = {
            param($Thumbprint, $StoreName)
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                $StoreName,
                [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
            try {
                $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                $certificates = $store.Certificates.Find(
                    [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                    $Thumbprint,
                    $false)
                foreach ($certificate in $certificates) {
                    $store.Remove($certificate)
                }
            }
            finally {
                $store.Close()
            }
        }
    )

    try {
        $certificate = & $CreateCertificate `
            'CN=SandboxTestCert' `
            'CodeSigningCert' `
            'Cert:\LocalMachine\My'
    }
    catch {
        throw "Could not create the temporary Sandbox code-signing certificate: $($_.Exception.Message)"
    }

    if ($null -eq $certificate) {
        throw 'The temporary Sandbox code-signing certificate operation returned no certificate.'
    }

    $subjectProperty = $certificate.PSObject.Properties['Subject']
    $thumbprintProperty = $certificate.PSObject.Properties['Thumbprint']
    $privateKeyProperty = $certificate.PSObject.Properties['HasPrivateKey']
    if ($null -eq $subjectProperty -or
        [string]$subjectProperty.Value -cne 'CN=SandboxTestCert' -or
        $null -eq $thumbprintProperty -or
        [string]::IsNullOrWhiteSpace([string]$thumbprintProperty.Value) -or
        $null -eq $privateKeyProperty -or
        -not [bool]$privateKeyProperty.Value) {
        throw 'The temporary Sandbox code-signing certificate was missing its approved identity or private key.'
    }

    $trustedStores = New-Object 'System.Collections.Generic.List[string]'
    try {
        foreach ($storeName in 'Root', 'TrustedPublisher') {
            & $AddCertificateToStore $certificate $storeName | Out-Null
            $trustedStores.Add($storeName)
        }
    }
    catch {
        $setupError = $_.Exception.Message
        for ($storeIndex = $trustedStores.Count - 1; $storeIndex -ge 0; $storeIndex--) {
            $storeName = $trustedStores[$storeIndex]
            try {
                & $RemoveCertificateFromStore $certificate.Thumbprint $storeName | Out-Null
            }
            catch {
            }
        }
        try {
            & $RemoveCertificateFromStore $certificate.Thumbprint 'My' | Out-Null
        }
        catch {
        }
        throw "Could not trust the temporary Sandbox code-signing certificate: $setupError"
    }

    return $certificate
}

function Remove-PhotoSiftSandboxTestCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Certificate,

        [Parameter()]
        [scriptblock]$RemoveCertificateFromStore = {
            param($Thumbprint, $StoreName)
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                $StoreName,
                [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
            try {
                $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                $certificates = $store.Certificates.Find(
                    [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                    $Thumbprint,
                    $false)
                foreach ($storedCertificate in $certificates) {
                    $store.Remove($storedCertificate)
                }
            }
            finally {
                $store.Close()
            }
        }
    )

    $cleanupErrors = New-Object 'System.Collections.Generic.List[string]'
    foreach ($storeName in 'TrustedPublisher', 'Root', 'My') {
        try {
            & $RemoveCertificateFromStore $Certificate.Thumbprint $storeName | Out-Null
        }
        catch {
            $cleanupErrors.Add("${storeName}: $($_.Exception.Message)")
        }
    }

    if ($cleanupErrors.Count -gt 0) {
        throw "Could not fully clean up the temporary Sandbox code-signing certificate: $($cleanupErrors -join '; ')"
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

    $failedCount = $counts.failed + $counts.error + $counts.timeout + $counts.aborted
    if ($failedCount -gt 0) {
        throw "Test project '$ProjectName' TRX evidence recorded $failedCount failed or error results."
    }

    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $outputRootPrefix = $resolvedOutputRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTrxPath.StartsWith(
            $outputRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test project '$ProjectName' TRX evidence was outside mapped output: $resolvedTrxPath."
    }
    $relativeResultPath = $resolvedTrxPath.Substring($outputRootPrefix.Length)

    return [pscustomobject]@{
        project = $ProjectName
        resultFile = $relativeResultPath
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
    $outputLines = @($StandardOutput -split "`r?`n")
    if ($outputLines -notcontains $expectedMarker) {
        throw "PhotoSift WPF smoke output did not contain: $expectedMarker"
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

    $testsProperty = $summary.PSObject.Properties['tests']
    if ($null -eq $testsProperty) {
        throw 'Windows Sandbox evidence did not contain structured test results.'
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
            [string]$_.project -eq $expectedProject
        })
        if ($projectResults.Count -ne 1) {
            throw "Windows Sandbox evidence did not contain exactly one test result for '$expectedProject'."
        }

        $projectResult = $projectResults[0]
        $counts = @{}
        foreach ($name in 'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted') {
            $property = $projectResult.PSObject.Properties[$name]
            $value = 0
            if ($null -eq $property -or
                -not [int]::TryParse(
                    [string]$property.Value,
                    [System.Globalization.NumberStyles]::Integer,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref]$value) -or
                $value -lt 0) {
                throw "Windows Sandbox evidence for '$expectedProject' had an invalid '$name' count."
            }

            $counts[$name] = $value
        }

        if ($counts.executed -eq 0) {
            throw "Windows Sandbox evidence recorded zero executed tests for '$expectedProject'."
        }
        if ($counts.total -ne $counts.executed -or
            $counts.passed -ne $counts.executed -or
            $counts.notExecuted -ne 0) {
            throw "Windows Sandbox evidence for '$expectedProject' contained inconsistent test counts."
        }
        if ($counts.failed -gt 0 -or
            $counts.error -gt 0 -or
            $counts.timeout -gt 0 -or
            $counts.aborted -gt 0) {
            throw "Windows Sandbox evidence recorded failed or error results for '$expectedProject'."
        }
    }

    if ($testResults.Count -ne $expectedProjects.Count) {
        throw "Windows Sandbox evidence contained an unexpected number of test project results for stage '$ExpectedStage'."
    }

    return $summary
}

Export-ModuleMember `
    -Function New-PhotoSiftSandboxConfiguration, Assert-PhotoSiftSandboxResult, Invoke-PhotoSiftProcess, Assert-PhotoSiftSmokeResult, Read-PhotoSiftTrxResult, Find-PhotoSiftTestAdapterTargets, Invoke-PhotoSiftTestAdapterSigning, New-PhotoSiftSandboxTestCertificate, Remove-PhotoSiftSandboxTestCertificate

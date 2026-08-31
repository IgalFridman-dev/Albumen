$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repositoryRoot 'eng\sandbox\PhotoSiftSandbox.psm1'

Import-Module $modulePath -Force

$script:passed = 0

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Because
    )

    if ($Expected -ne $Actual) {
        throw "Expected '$Expected' but got '$Actual': $Because"
    }

    $script:passed++
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Operation,

        [Parameter(Mandatory)]
        [string]$Because
    )

    try {
        & $Operation
    }
    catch {
        $script:passed++
        return
    }

    throw "Expected operation to throw: $Because"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-Sandbox-Tooling-Tests'
$sourcePath = Join-Path $testRoot 'source & input'
$sdkPath = Join-Path $testRoot 'sdk'
$packagesPath = Join-Path $testRoot 'packages'
$outputPath = Join-Path $testRoot 'output'

foreach ($path in $sourcePath, $sdkPath, $packagesPath, $outputPath) {
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
}

$adapterTempRoot = Join-Path $testRoot 'adapter-temp'
$adapterSourceRoot = Join-Path $testRoot 'adapter-source'
$tempAdapterPath = Join-Path $adapterTempRoot 'nested\xunit.runner.visualstudio.testadapter.dll'
$sourceAdapterPath = Join-Path $adapterSourceRoot 'tests\bin\xunit.runner.visualstudio.testadapter.dll'
foreach ($path in (Split-Path -Parent $tempAdapterPath), (Split-Path -Parent $sourceAdapterPath)) {
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
}
'temp adapter' | Set-Content -LiteralPath $tempAdapterPath -Encoding utf8
'source adapter' | Set-Content -LiteralPath $sourceAdapterPath -Encoding utf8
'near match' | Set-Content `
    -LiteralPath (Join-Path $adapterSourceRoot 'tests\bin\xunit.runner.visualstudio.testadapter.dll.backup') `
    -Encoding utf8

$discoveredAdapters = @(Find-PhotoSiftTestAdapterTargets -SearchRoots @(
    $adapterTempRoot,
    $adapterTempRoot.ToUpperInvariant(),
    $adapterSourceRoot))
Assert-Equal 2 $discoveredAdapters.Count 'exact adapter targets beneath both allowed roots must be discovered once'
Assert-Equal 1 @($discoveredAdapters | Where-Object {
    $_ -ieq [System.IO.Path]::GetFullPath($tempAdapterPath)
}).Count 'the TEMP adapter target must be discovered by exact name'
Assert-Equal 1 @($discoveredAdapters | Where-Object {
    $_ -ieq [System.IO.Path]::GetFullPath($sourceAdapterPath)
}).Count 'the writable source adapter target must be discovered by exact name'

$testCertificate = [pscustomobject]@{
    Subject = 'CN=SandboxTestCert'
    Thumbprint = 'TEST-THUMBPRINT'
    HasPrivateKey = $true
}

$emptyAdapterRoot = Join-Path $testRoot 'empty-adapter-root'
[System.IO.Directory]::CreateDirectory($emptyAdapterRoot) | Out-Null
$emptyEvidence = New-Object 'System.Collections.Generic.List[object]'
Assert-Throws {
    Invoke-PhotoSiftTestAdapterSigning `
        -SearchRoots @($emptyAdapterRoot) `
        -Certificate $testCertificate `
        -Checkpoint 'Empty' `
        -EvidenceList $emptyEvidence `
        -GetSignature { throw 'signature lookup must not run without targets' } `
        -SetSignature { throw 'signing must not run without targets' }
} 'zero adapter targets must be rejected'

$validAdapterRoot = Join-Path $testRoot 'valid-adapter-root'
$validAdapterPath = Join-Path $validAdapterRoot 'xunit.runner.visualstudio.testadapter.dll'
[System.IO.Directory]::CreateDirectory($validAdapterRoot) | Out-Null
'valid adapter' | Set-Content -LiteralPath $validAdapterPath -Encoding utf8
$validState = [pscustomobject]@{ SignCalls = 0 }
$validEvidence = New-Object 'System.Collections.Generic.List[object]'
$validResults = @(Invoke-PhotoSiftTestAdapterSigning `
    -SearchRoots @($validAdapterRoot) `
    -Certificate $testCertificate `
    -Checkpoint 'Valid' `
    -EvidenceList $validEvidence `
    -GetSignature { param($Path) [pscustomobject]@{ Status = 'Valid' } } `
    -SetSignature {
        param($Path, $Certificate)
        $validState.SignCalls++
        [pscustomobject]@{ Status = 'Valid' }
    })
Assert-Equal 0 $validState.SignCalls 'an already-valid adapter signature must be preserved'
Assert-Equal 1 $validResults.Count 'valid adapter signing must return one evidence row'
Assert-Equal 'Valid' $validResults[0].beforeStatus 'valid evidence must record the pre-sign status'
Assert-Equal 'Valid' $validResults[0].afterStatus 'valid evidence must record the post-sign status'
Assert-Equal 'Preserved' $validResults[0].action 'valid evidence must record that the signature was preserved'

$unsignedAdapterRoot = Join-Path $testRoot 'unsigned-adapter-root'
$unsignedAdapterPath = Join-Path $unsignedAdapterRoot 'xunit.runner.visualstudio.testadapter.dll'
[System.IO.Directory]::CreateDirectory($unsignedAdapterRoot) | Out-Null
'unsigned adapter' | Set-Content -LiteralPath $unsignedAdapterPath -Encoding utf8
$unsignedState = [pscustomobject]@{ Signed = $false; SignCalls = 0 }
$unsignedEvidence = New-Object 'System.Collections.Generic.List[object]'
$unsignedResults = @(Invoke-PhotoSiftTestAdapterSigning `
    -SearchRoots @($unsignedAdapterRoot) `
    -Certificate $testCertificate `
    -Checkpoint 'Unsigned' `
    -EvidenceList $unsignedEvidence `
    -GetSignature {
        param($Path)
        [pscustomobject]@{ Status = $(if ($unsignedState.Signed) { 'Valid' } else { 'NotSigned' }) }
    } `
    -SetSignature {
        param($Path, $Certificate)
        $unsignedState.SignCalls++
        $unsignedState.Signed = $true
        [pscustomobject]@{ Status = 'Valid' }
    })
Assert-Equal 1 $unsignedState.SignCalls 'an unsigned adapter must be sent through signing exactly once'
Assert-Equal 'NotSigned' $unsignedResults[0].beforeStatus 'signed evidence must retain the unsigned pre-state'
Assert-Equal 'Valid' $unsignedResults[0].afterStatus 'signed evidence must record a valid post-state'
Assert-Equal 'Signed' $unsignedResults[0].action 'signed evidence must identify the signing action'
Assert-Equal `
    'checkpoint,targetPath,beforeStatus,afterStatus,action' `
    ($unsignedResults[0].PSObject.Properties.Name -join ',') `
    'loggable signing evidence must expose only the target, statuses, checkpoint, and action'

$failedSigningEvidence = New-Object 'System.Collections.Generic.List[object]'
Assert-Throws {
    Invoke-PhotoSiftTestAdapterSigning `
        -SearchRoots @($unsignedAdapterRoot) `
        -Certificate $testCertificate `
        -Checkpoint 'SigningFailure' `
        -EvidenceList $failedSigningEvidence `
        -GetSignature { param($Path) [pscustomobject]@{ Status = 'NotSigned' } } `
        -SetSignature { param($Path, $Certificate) throw 'synthetic signing failure' }
} 'a signing command failure must fail closed'
Assert-Equal 1 $failedSigningEvidence.Count 'a signing command failure must leave durable target evidence'
Assert-Equal $unsignedAdapterPath $failedSigningEvidence[0].targetPath 'failed signing evidence must identify the target'
Assert-Equal 'NotSigned' $failedSigningEvidence[0].beforeStatus 'failed signing evidence must retain the pre-sign status'
Assert-Equal 'Unavailable' $failedSigningEvidence[0].afterStatus 'failed signing evidence must record that post-sign status was unavailable'
Assert-Equal 'Failed' $failedSigningEvidence[0].action 'failed signing evidence must identify the failed action'

$invalidPostEvidence = New-Object 'System.Collections.Generic.List[object]'
Assert-Throws {
    Invoke-PhotoSiftTestAdapterSigning `
        -SearchRoots @($unsignedAdapterRoot) `
        -Certificate $testCertificate `
        -Checkpoint 'InvalidPost' `
        -EvidenceList $invalidPostEvidence `
        -GetSignature { param($Path) [pscustomobject]@{ Status = 'NotSigned' } } `
        -SetSignature { param($Path, $Certificate) [pscustomobject]@{ Status = 'NotSigned' } }
} 'a non-valid post-sign adapter status must be rejected'
Assert-Equal 1 $invalidPostEvidence.Count 'failed post-sign verification must leave durable target evidence'
Assert-Equal 'NotSigned' $invalidPostEvidence[0].afterStatus 'failed evidence must record the rejected post-sign status'

$unsafeState = [pscustomobject]@{ SignCalls = 0 }
$unsafeEvidence = New-Object 'System.Collections.Generic.List[object]'
Assert-Throws {
    Invoke-PhotoSiftTestAdapterSigning `
        -SearchRoots @($unsignedAdapterRoot) `
        -Certificate $testCertificate `
        -Checkpoint 'UnsafePre' `
        -EvidenceList $unsafeEvidence `
        -GetSignature { param($Path) [pscustomobject]@{ Status = 'HashMismatch' } } `
        -SetSignature {
            param($Path, $Certificate)
            $unsafeState.SignCalls++
        }
} 'a pre-existing non-valid signature must be rejected rather than overwritten'
Assert-Equal 0 $unsafeState.SignCalls 'unsafe pre-sign states must not be sent through signing'

$certificateState = [pscustomobject]@{
    CreateCalls = 0
    CreateSubject = $null
    CreateType = $null
    CreateStore = $null
    TrustedStores = New-Object 'System.Collections.Generic.List[string]'
    RemovedStores = New-Object 'System.Collections.Generic.List[string]'
}
$createdCertificate = New-PhotoSiftSandboxTestCertificate `
    -CreateCertificate {
        param($Subject, $Type, $StoreLocation)
        $certificateState.CreateCalls++
        $certificateState.CreateSubject = $Subject
        $certificateState.CreateType = $Type
        $certificateState.CreateStore = $StoreLocation
        $testCertificate
    } `
    -AddCertificateToStore {
        param($Certificate, $StoreName)
        $certificateState.TrustedStores.Add($StoreName)
    }
Assert-Equal 1 $certificateState.CreateCalls 'one certificate must be created for a guest run'
Assert-Equal 'CN=SandboxTestCert' $certificateState.CreateSubject 'the temporary signer must use the approved subject'
Assert-Equal 'CodeSigningCert' $certificateState.CreateType 'the temporary signer must be a code-signing certificate'
Assert-Equal 'Cert:\LocalMachine\My' $certificateState.CreateStore 'the temporary signer must be created in LocalMachine My'
Assert-Equal 'Root,TrustedPublisher' ($certificateState.TrustedStores -join ',') 'the same certificate must be trusted in both required stores'
Assert-Equal $testCertificate $createdCertificate 'certificate setup must return the created signer for reuse'

Remove-PhotoSiftSandboxTestCertificate `
    -Certificate $createdCertificate `
    -RemoveCertificateFromStore {
        param($Thumbprint, $StoreName)
        $certificateState.RemovedStores.Add($StoreName)
    }
Assert-Equal 'TrustedPublisher,Root,My' ($certificateState.RemovedStores -join ',') 'cleanup must remove the signer from all guest stores'

$configuration = New-PhotoSiftSandboxConfiguration `
    -SourcePath $sourcePath `
    -SdkPath $sdkPath `
    -PackagesPath $packagesPath `
    -OutputPath $outputPath `
    -Stage Architecture

[xml]$xml = $configuration
Assert-Equal 'Disable' $xml.Configuration.Networking 'networking must be disabled'
Assert-Equal 'Disable' $xml.Configuration.ClipboardRedirection 'clipboard must be disabled'
Assert-Equal 'Disable' $xml.Configuration.PrinterRedirection 'printer redirection must be disabled'
Assert-Equal 'Disable' $xml.Configuration.AudioInput 'audio input must be disabled'
Assert-Equal 'Disable' $xml.Configuration.VideoInput 'video input must be disabled'
Assert-Equal 'Disable' $xml.Configuration.VGpu 'vGPU must be disabled'

$mappings = @($xml.Configuration.MappedFolders.MappedFolder)
Assert-Equal 4 $mappings.Count 'exactly four host folders may be mapped'
Assert-Equal 'true' $mappings[0].ReadOnly.ToLowerInvariant() 'source mapping must be read-only'
Assert-Equal 'true' $mappings[1].ReadOnly.ToLowerInvariant() 'SDK mapping must be read-only'
Assert-Equal 'true' $mappings[2].ReadOnly.ToLowerInvariant() 'package mapping must be read-only'
Assert-Equal 'false' $mappings[3].ReadOnly.ToLowerInvariant() 'output mapping must be writable'
Assert-Equal $sourcePath ([string]$mappings[0].HostFolder) 'host path must round-trip through XML escaping'

if ($xml.Configuration.LogonCommand.Command -notmatch '-Stage Architecture') {
    throw 'Guest logon command did not contain the Architecture stage.'
}
$script:passed++

$coreConfiguration = New-PhotoSiftSandboxConfiguration `
    -SourcePath $sourcePath `
    -SdkPath $sdkPath `
    -PackagesPath $packagesPath `
    -OutputPath $outputPath `
    -Stage Core
if ($coreConfiguration -notmatch '-Stage Core') {
    throw 'Guest logon command did not contain the Core stage.'
}
$script:passed++

$missingSummary = Join-Path $outputPath 'missing-summary.json'
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $missingSummary -ExpectedStage Architecture
} 'missing evidence must be rejected'

$failedSummary = Join-Path $outputPath 'failed-summary.json'
@{
    success = $false
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $failedSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $failedSummary -ExpectedStage Architecture
} 'unsuccessful evidence must be rejected'

$wrongStageSummary = Join-Path $outputPath 'wrong-stage-summary.json'
@{
    success = $true
    stage = 'Full'
    networkingDisabled = $true
    commands = @()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $wrongStageSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $wrongStageSummary -ExpectedStage Architecture
} 'stage-mismatched evidence must be rejected'

$failedCommandSummary = Join-Path $outputPath 'failed-command-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test'; exitCode = 1 })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $failedCommandSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $failedCommandSummary -ExpectedStage Architecture
} 'nonzero guest commands must be rejected'

$missingTestEvidenceSummary = Join-Path $outputPath 'missing-test-evidence-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test-architecture'; exitCode = 0 })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $missingTestEvidenceSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $missingTestEvidenceSummary -ExpectedStage Architecture
} 'a successful guest summary without structured test evidence must be rejected'

$zeroTestEvidenceSummary = Join-Path $outputPath 'zero-test-evidence-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test-architecture'; exitCode = 0 })
    tests = @(@{
        project = 'PhotoSift.Infrastructure.Tests'
        resultFile = 'TestResults\architecture.trx'
        total = 0
        executed = 0
        passed = 0
        failed = 0
        error = 0
        timeout = 0
        aborted = 0
        notExecuted = 0
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $zeroTestEvidenceSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $zeroTestEvidenceSummary -ExpectedStage Architecture
} 'zero executed tests must be rejected even when the guest command exits zero'

$failedTestEvidenceSummary = Join-Path $outputPath 'failed-test-evidence-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test-architecture'; exitCode = 0 })
    tests = @(@{
        project = 'PhotoSift.Infrastructure.Tests'
        resultFile = 'TestResults\architecture.trx'
        total = 2
        executed = 2
        passed = 1
        failed = 1
        error = 0
        timeout = 0
        aborted = 0
        notExecuted = 0
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $failedTestEvidenceSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $failedTestEvidenceSummary -ExpectedStage Architecture
} 'failed test evidence must be rejected even when the guest command exits zero'

foreach ($rejectedCountName in 'timeout', 'aborted', 'notExecuted') {
    $rejectedCounts = [ordered]@{
        project = 'PhotoSift.Infrastructure.Tests'
        resultFile = 'TestResults\architecture.trx'
        total = 2
        executed = 2
        passed = 2
        failed = 0
        error = 0
        timeout = 0
        aborted = 0
        notExecuted = 0
    }
    $rejectedCounts[$rejectedCountName] = 1
    $rejectedCountSummary = Join-Path $outputPath ($rejectedCountName + '-test-evidence-summary.json')
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        commands = @(@{ name = 'test-architecture'; exitCode = 0 })
        tests = @($rejectedCounts)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $rejectedCountSummary -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftSandboxResult -SummaryPath $rejectedCountSummary -ExpectedStage Architecture
    } "nonzero $rejectedCountName test evidence must be rejected even when success and command exit codes are zero"
}

$incompletelyPassedTestEvidenceSummary = Join-Path $outputPath 'incompletely-passed-test-evidence-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test-architecture'; exitCode = 0 })
    tests = @(@{
        project = 'PhotoSift.Infrastructure.Tests'
        resultFile = 'TestResults\architecture.trx'
        total = 2
        executed = 2
        passed = 1
        failed = 0
        error = 0
        timeout = 0
        aborted = 0
        notExecuted = 0
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $incompletelyPassedTestEvidenceSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $incompletelyPassedTestEvidenceSummary -ExpectedStage Architecture
} 'controller evidence with fewer passed than executed tests must be rejected even when all failure counters are zero'

$passingSummary = Join-Path $outputPath 'passing-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test-architecture'; exitCode = 0 })
    tests = @(@{
        project = 'PhotoSift.Infrastructure.Tests'
        resultFile = 'TestResults\architecture.trx'
        total = 2
        executed = 2
        passed = 2
        failed = 0
        error = 0
        timeout = 0
        aborted = 0
        notExecuted = 0
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $passingSummary -Encoding utf8
Assert-PhotoSiftSandboxResult -SummaryPath $passingSummary -ExpectedStage Architecture | Out-Null
$script:passed++

$acceptedNonzeroSummary = Join-Path $outputPath 'accepted-nonzero-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'copy'; exitCode = 1; succeeded = $true })
    tests = @(@{
        project = 'PhotoSift.Infrastructure.Tests'
        resultFile = 'TestResults\architecture.trx'
        total = 2
        executed = 2
        passed = 2
        failed = 0
        error = 0
        timeout = 0
        aborted = 0
        notExecuted = 0
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $acceptedNonzeroSummary -Encoding utf8
Assert-PhotoSiftSandboxResult -SummaryPath $acceptedNonzeroSummary -ExpectedStage Architecture | Out-Null
$script:passed++

$missingTrxPath = Join-Path $outputPath 'missing.trx'
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $missingTrxPath `
        -OutputRoot $outputPath
} 'a missing TRX result must be rejected by the guest evidence parser'

$malformedTrxPath = Join-Path $outputPath 'malformed.trx'
'not XML' | Set-Content -LiteralPath $malformedTrxPath -Encoding utf8
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $malformedTrxPath `
        -OutputRoot $outputPath
} 'a malformed TRX result must be rejected by the guest evidence parser'

$zeroTrxPath = Join-Path $outputPath 'zero.trx'
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="0" executed="0" passed="0" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $zeroTrxPath -Encoding utf8
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $zeroTrxPath `
        -OutputRoot $outputPath
} 'a TRX result with zero executed tests must be rejected by the guest evidence parser'

$failedTrxPath = Join-Path $outputPath 'failed.trx'
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="2" executed="2" passed="1" failed="1" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $failedTrxPath -Encoding utf8
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $failedTrxPath `
        -OutputRoot $outputPath
} 'a TRX result with failed tests must be rejected by the guest evidence parser'

$nonCompletedTrxPath = Join-Path $outputPath 'non-completed.trx'
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Failed">
    <Counters total="2" executed="2" passed="2" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $nonCompletedTrxPath -Encoding utf8
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $nonCompletedTrxPath `
        -OutputRoot $outputPath
} 'a non-Completed TRX outcome must be rejected even when all tests passed'

$incompletelyPassedTrxPath = Join-Path $outputPath 'incompletely-passed.trx'
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="2" executed="2" passed="1" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $incompletelyPassedTrxPath -Encoding utf8
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $incompletelyPassedTrxPath `
        -OutputRoot $outputPath
} 'a TRX result with fewer passed than executed tests must be rejected when all failure counters are zero'

$notExecutedTrxPath = Join-Path $outputPath 'not-executed.trx'
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="2" executed="2" passed="2" failed="0" error="0" timeout="0" aborted="0" notExecuted="1" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $notExecutedTrxPath -Encoding utf8
Assert-Throws {
    Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $notExecutedTrxPath `
        -OutputRoot $outputPath
} 'a TRX result with notExecuted tests must be rejected'

$passingTrxPath = Join-Path $outputPath 'passing.trx'
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="3" executed="3" passed="3" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $passingTrxPath -Encoding utf8
$testEvidence = Read-PhotoSiftTrxResult `
    -ProjectName 'PhotoSift.Infrastructure.Tests' `
    -TrxPath $passingTrxPath `
    -OutputRoot $outputPath
Assert-Equal 'PhotoSift.Infrastructure.Tests' $testEvidence.project 'test evidence must identify its project'
Assert-Equal 'passing.trx' $testEvidence.resultFile 'test evidence must use a mapped-output-relative TRX path'
Assert-Equal 3 $testEvidence.total 'test evidence must record the total count'
Assert-Equal 3 $testEvidence.executed 'test evidence must record a nonzero executed count'
Assert-Equal 3 $testEvidence.passed 'test evidence must record the passed count'
Assert-Equal 0 $testEvidence.failed 'test evidence must record the failed count'
Assert-Equal 0 $testEvidence.error 'test evidence must record the error count'

$windowsPowerShellProbePath = Join-Path $testRoot 'windows-powershell-trx-probe.ps1'
@'
param(
    [Parameter(Mandatory)]
    [string]$ModulePath,

    [Parameter(Mandatory)]
    [string]$TrxPath,

    [Parameter(Mandatory)]
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
$result = Read-PhotoSiftTrxResult `
    -ProjectName 'PhotoSift.Infrastructure.Tests' `
    -TrxPath $TrxPath `
    -OutputRoot $OutputRoot
if ($result.resultFile -cne 'passing.trx') {
    throw "Unexpected relative result path: $($result.resultFile)"
}
'@ | Set-Content -LiteralPath $windowsPowerShellProbePath -Encoding utf8

$windowsPowerShellResult = Invoke-PhotoSiftProcess `
    -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
    -Arguments @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $windowsPowerShellProbePath,
        '-ModulePath', $modulePath,
        '-TrxPath', $passingTrxPath,
        '-OutputRoot', $outputPath)
if ($windowsPowerShellResult.ExitCode -ne 0) {
    throw "Guest TRX parsing failed under Windows PowerShell 5.1: $($windowsPowerShellResult.StandardError.Trim())"
}
$script:passed++

$commandProcessor = Join-Path $env:SystemRoot 'System32\cmd.exe'
$processResult = Invoke-PhotoSiftProcess `
    -FilePath $commandProcessor `
    -Arguments @('/d', '/c', '1>&2 echo non-fatal-warning & exit /b 0')
Assert-Equal 0 $processResult.ExitCode 'stderr must not turn a successful native process into a PowerShell error'
if ($processResult.StandardError -notmatch 'non-fatal-warning') {
    throw 'Expected native stderr to be captured as evidence.'
}
$script:passed++

$workingDirectoryProbe = Join-Path $testRoot 'working-directory-probe'
[System.IO.Directory]::CreateDirectory($workingDirectoryProbe) | Out-Null
Set-Content -LiteralPath (Join-Path $workingDirectoryProbe 'marker.txt') -Value 'present'
$workingDirectoryResult = Invoke-PhotoSiftProcess `
    -FilePath $commandProcessor `
    -Arguments @('/d', '/c', 'if exist marker.txt (exit /b 0) else (exit /b 9)') `
    -WorkingDirectory $workingDirectoryProbe
Assert-Equal 0 $workingDirectoryResult.ExitCode 'native processes must run from the requested working directory'

$guestVerificationScript = Get-Content -Raw -LiteralPath (
    Join-Path $repositoryRoot 'eng\sandbox\Run-PhotoSiftVerification.ps1')
if ($guestVerificationScript -notmatch [regex]::Escape('-SearchRoots @($env:TEMP, $workingSource)')) {
    throw 'Guest adapter signing must search only TEMP and the writable copied source tree.'
}
$script:passed++
$architectureBuildIndex = $guestVerificationScript.IndexOf("-Name 'build-architecture'")
$architectureSigningIndex = $guestVerificationScript.IndexOf("-Checkpoint 'Architecture'")
$architectureTestIndex = $guestVerificationScript.IndexOf("-Name 'test-architecture'")
Assert-Equal $true `
    ($architectureBuildIndex -ge 0 -and
        $architectureBuildIndex -lt $architectureSigningIndex -and
        $architectureSigningIndex -lt $architectureTestIndex) `
    'Architecture signing must occur after build and immediately before test'

$coreBuildIndex = $guestVerificationScript.IndexOf("-Name ('build-' + `$testName)")
$coreSigningIndex = $guestVerificationScript.IndexOf("-Checkpoint ('Core-' + `$testName)")
$coreTestIndex = $guestVerificationScript.IndexOf("-Name ('test-' + `$testName)")
Assert-Equal $true `
    ($coreBuildIndex -ge 0 -and
        $coreBuildIndex -lt $coreSigningIndex -and
        $coreSigningIndex -lt $coreTestIndex) `
    'Core signing must occur after each project build and immediately before its test'

$fullBuildIndex = $guestVerificationScript.IndexOf("-Name 'build-solution'")
$fullSigningIndex = $guestVerificationScript.IndexOf("-Checkpoint 'Full'")
$fullTestsIndex = $guestVerificationScript.IndexOf('$testProjects = @(')
Assert-Equal $true `
    ($fullBuildIndex -ge 0 -and
        $fullBuildIndex -lt $fullSigningIndex -and
        $fullSigningIndex -lt $fullTestsIndex) `
    'Full signing must occur after the solution build and immediately before the test sequence'
if ($guestVerificationScript -notmatch [regex]::Escape("@('restore', `$projectPath, '-r', 'win-x64'")) {
    throw 'Architecture restore must produce win-x64 assets.'
}
$script:passed++
if ($guestVerificationScript -notmatch [regex]::Escape("@('restore', 'PhotoSift.slnx', '-r', 'win-x64'")) {
    throw 'Full restore must produce win-x64 assets.'
}
$script:passed++

Assert-Throws {
    Assert-PhotoSiftSmokeResult `
        -ExitCode 1 `
        -StandardOutput 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true' `
        -TimedOut $false
} 'nonzero WPF smoke exits must be rejected'
Assert-Throws {
    Assert-PhotoSiftSmokeResult `
        -ExitCode 0 `
        -StandardOutput 'wrong marker' `
        -TimedOut $false
} 'missing WPF smoke marker must be rejected'
Assert-PhotoSiftSmokeResult `
    -ExitCode 0 `
    -StandardOutput 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true' `
    -TimedOut $false
$script:passed++

[xml]$sharedBuildConfiguration = Get-Content -Raw -LiteralPath (
    Join-Path $repositoryRoot 'Directory.Build.props')
Assert-Equal `
    'win-x64' `
    ([string]$sharedBuildConfiguration.Project.PropertyGroup.RuntimeIdentifier) `
    'the shared project boundary must select win-x64'
if ($guestVerificationScript -match [regex]::Escape("@('build', 'PhotoSift.slnx', '-c', 'Release', '-r'")) {
    throw 'Solution build must not use the unsupported solution-level RuntimeIdentifier switch.'
}
$script:passed++

Write-Output "Passed: $script:passed Failed: 0"

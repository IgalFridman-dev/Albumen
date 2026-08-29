$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$checkerPath = Join-Path $repositoryRoot 'eng\Verify-RuntimeBoundary.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'PhotoSift-RuntimeBoundary-Tests-' + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    function Invoke-BoundaryChecker {
        param(
            [Parameter(Mandatory)]
            [string[]]$Arguments,

            [Parameter(Mandatory)]
            [string]$OutputPrefix,

            [Parameter()]
            [string]$FilePath = 'pwsh.exe'
        )

        $standardOutputPath = Join-Path $testRoot ($OutputPrefix + '-stdout.txt')
        $standardErrorPath = Join-Path $testRoot ($OutputPrefix + '-stderr.txt')
        $engineArguments = @('-NoProfile')
        if ([System.IO.Path]::GetFileName($FilePath) -eq 'powershell.exe') {
            $engineArguments += @('-ExecutionPolicy', 'Bypass')
        }
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList ($engineArguments + @('-File', $checkerPath) + $Arguments) `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $standardOutputPath `
            -RedirectStandardError $standardErrorPath

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = @(
                Get-Content -Raw -LiteralPath $standardOutputPath
                Get-Content -Raw -LiteralPath $standardErrorPath
            ) -join [System.Environment]::NewLine
        }
    }

    $cleanManifestPath = Join-Path $testRoot 'clean.deps.json'
    @{
        libraries = @{
            'PhotoSift.App/1.0.0' = @{ type = 'project' }
        }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $cleanManifestPath -Encoding utf8

    $forbiddenManifestPath = Join-Path $testRoot 'forbidden.deps.json'
    @{
        libraries = @{
            'PhotoSift.App/1.0.0' = @{ type = 'project' }
            'System.Net.Http/10.0.0' = @{ type = 'package' }
        }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $forbiddenManifestPath -Encoding utf8

    $dependencyResult = Invoke-BoundaryChecker `
        -Arguments @('-DependencyManifestPath', $forbiddenManifestPath) `
        -OutputPrefix 'dependency'

    if ($dependencyResult.ExitCode -eq 0) {
        throw 'Forbidden dependency fixture was incorrectly accepted.'
    }
    if ($dependencyResult.Output -notmatch [regex]::Escape('System.Net.Http')) {
        throw 'Failure output did not identify System.Net.Http.'
    }

    $networkSourceRoot = Join-Path $testRoot 'network-source'
    [System.IO.Directory]::CreateDirectory($networkSourceRoot) | Out-Null
    @'
using System.Net.Http;

public static class ForbiddenNetworkFixture
{
    public static System.Net.Http.HttpClient CreateClient() => new();
}
'@ | Set-Content -LiteralPath (Join-Path $networkSourceRoot 'NetworkFixture.cs') -Encoding utf8

    $networkResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $networkSourceRoot) `
        -OutputPrefix 'network-api'
    if ($networkResult.ExitCode -eq 0) {
        throw 'Direct System.Net.Http.HttpClient fixture was incorrectly accepted.'
    }
    if ($networkResult.Output -notmatch [regex]::Escape('System.Net.*')) {
        throw 'Failure output did not identify the direct System.Net usage.'
    }

    $mutationSourceRoot = Join-Path $testRoot 'mutation-source'
    [System.IO.Directory]::CreateDirectory($mutationSourceRoot) | Out-Null
    @'
public static class ForbiddenMutationFixture
{
    public static void DeleteFile(string path) => System.IO.File.Delete(path);

    public static void MoveDirectory(string source, string destination) =>
        System.IO.Directory.Move(source, destination);
}
'@ | Set-Content -LiteralPath (Join-Path $mutationSourceRoot 'MutationFixture.cs') -Encoding utf8

    $mutationResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $mutationSourceRoot) `
        -OutputPrefix 'mutation-api'
    if ($mutationResult.ExitCode -eq 0) {
        throw 'Direct source-mutation API fixture was incorrectly accepted.'
    }
    if ($mutationResult.Output -notmatch [regex]::Escape('System.IO.File::Delete')) {
        throw 'Failure output did not identify the direct File.Delete call.'
    }
    if ($mutationResult.Output -notmatch [regex]::Escape('System.IO.Directory::Move')) {
        throw 'Failure output did not identify the direct Directory.Move call.'
    }

    $allowedSourceRoot = Join-Path $testRoot 'allowed-source'
    [System.IO.Directory]::CreateDirectory($allowedSourceRoot) | Out-Null
    @'
public static class AllowedFixture
{
    public static string GetMarker() => "local-only";
}
'@ | Set-Content -LiteralPath (Join-Path $allowedSourceRoot 'AllowedFixture.cs') -Encoding utf8

    $windowsPowerShellResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $allowedSourceRoot) `
        -OutputPrefix 'windows-powershell' `
        -FilePath 'powershell.exe'
    if ($windowsPowerShellResult.ExitCode -ne 0) {
        throw "Windows PowerShell compatibility fixture failed: $($windowsPowerShellResult.Output)"
    }
    if ($windowsPowerShellResult.Output -notmatch [regex]::Escape('ForbiddenApiCalls=0')) {
        throw 'Windows PowerShell compatibility output omitted ForbiddenApiCalls=0.'
    }

    $windowsPowerShellNegativeResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $networkSourceRoot) `
        -OutputPrefix 'windows-powershell-negative' `
        -FilePath 'powershell.exe'
    if ($windowsPowerShellNegativeResult.ExitCode -eq 0) {
        throw 'Windows PowerShell incorrectly accepted the direct System.Net fixture.'
    }
    if ($windowsPowerShellNegativeResult.Output -notmatch [regex]::Escape('System.Net.*')) {
        throw 'Windows PowerShell failure output did not identify direct System.Net usage.'
    }

    Write-Output 'Passed: 11 Failed: 0'
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $testRoot -ErrorAction SilentlyContinue
}

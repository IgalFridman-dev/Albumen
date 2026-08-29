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
            [string]$OutputPrefix
        )

        $standardOutputPath = Join-Path $testRoot ($OutputPrefix + '-stdout.txt')
        $standardErrorPath = Join-Path $testRoot ($OutputPrefix + '-stderr.txt')
        $process = Start-Process `
            -FilePath 'pwsh.exe' `
            -ArgumentList (@('-NoProfile', '-File', $checkerPath) + $Arguments) `
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

    $networkAssemblyPath = Join-Path $testRoot 'ForbiddenNetworkFixture.dll'
    Add-Type -TypeDefinition @'
public static class ForbiddenNetworkFixture
{
    public static System.Net.Http.HttpClient CreateClient() => new();
}
'@ -OutputAssembly $networkAssemblyPath

    $networkResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-AssemblyPath', $networkAssemblyPath) `
        -OutputPrefix 'network-api'
    if ($networkResult.ExitCode -eq 0) {
        throw 'Direct System.Net.Http.HttpClient fixture was incorrectly accepted.'
    }
    if ($networkResult.Output -notmatch [regex]::Escape('System.Net.Http.HttpClient::.ctor')) {
        throw 'Failure output did not identify the direct HttpClient constructor call.'
    }

    $mutationAssemblyPath = Join-Path $testRoot 'ForbiddenMutationFixture.dll'
    Add-Type -TypeDefinition @'
public static class ForbiddenMutationFixture
{
    public static void DeleteFile(string path) => System.IO.File.Delete(path);

    public static void MoveDirectory(string source, string destination) =>
        System.IO.Directory.Move(source, destination);
}
'@ -OutputAssembly $mutationAssemblyPath

    $mutationResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-AssemblyPath', $mutationAssemblyPath) `
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

    Write-Output 'Passed: 7 Failed: 0'
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $testRoot -ErrorAction SilentlyContinue
}

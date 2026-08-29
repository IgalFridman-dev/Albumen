$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$checkerPath = Join-Path $repositoryRoot 'eng\Verify-RuntimeBoundary.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'PhotoSift-RuntimeBoundary-Tests-' + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    $forbiddenManifestPath = Join-Path $testRoot 'forbidden.deps.json'
    @{
        libraries = @{
            'PhotoSift.App/1.0.0' = @{ type = 'project' }
            'System.Net.Http/10.0.0' = @{ type = 'package' }
        }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $forbiddenManifestPath -Encoding utf8

    $standardOutputPath = Join-Path $testRoot 'checker-stdout.txt'
    $standardErrorPath = Join-Path $testRoot 'checker-stderr.txt'
    $process = Start-Process `
        -FilePath 'pwsh.exe' `
        -ArgumentList @(
            '-NoProfile',
            '-File', $checkerPath,
            '-DependencyManifestPath', $forbiddenManifestPath) `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $standardOutputPath `
        -RedirectStandardError $standardErrorPath
    $output = @(
        Get-Content -Raw -LiteralPath $standardOutputPath
        Get-Content -Raw -LiteralPath $standardErrorPath
    ) -join [System.Environment]::NewLine

    if ($process.ExitCode -eq 0) {
        throw 'Forbidden dependency fixture was incorrectly accepted.'
    }
    if ($output -notmatch [regex]::Escape('System.Net.Http')) {
        throw 'Failure output did not identify System.Net.Http.'
    }

    Write-Output 'Passed: 2 Failed: 0'
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $testRoot -ErrorAction SilentlyContinue
}

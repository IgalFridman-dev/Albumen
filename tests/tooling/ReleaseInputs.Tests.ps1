[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SdkRoot,
    [Parameter(Mandatory)][string]$PackagesRoot,
    [string]$EvidenceRoot
)

# Host-safe input integration test: reads PE/ZIP bytes, never loads or executes them.
# Build results, first-party bytes, assets/deps and runtimeconfig are simulated.
. (Join-Path $PSScriptRoot 'PhotoSiftRelease.Tests.ps1') -LoadHelpersOnly
Add-Type -AssemblyName System.IO.Compression.FileSystem
$actualSdkRoot = [System.IO.Path]::GetFullPath($SdkRoot)
$actualPackagesRoot = [System.IO.Path]::GetFullPath($PackagesRoot)
$preflight = [ordered]@{
    mode = 'host-only-input-preflight'; externalProductExecution = $false
    simulatedBoundaries = @('SDK --version response (SDK is not executed/selected)', 'restore project.assets.json (schema derived from SDK/NuGet source, not a real restore)', 'publish compilation', 'first-party source/assembly', 'application .deps.json and runtimeconfig.json')
    packages = @(); frameworkPaths = @()
    unresolved = @('Actual restore/publish and application execution require the offline VM.', 'Apphost bytes are unmodified SDK data; this is not an executable application.', 'SQLite version is statically inspected, not queried by executing the DLL.')
}
try {
    $actualRepository = New-SyntheticReleaseRepository -Name 'actual-input-repository'
    $lock = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'src/PhotoSift.App/packages.lock.json') | ConvertFrom-Json
    foreach ($id in @('Microsoft.Data.Sqlite', 'Microsoft.Data.Sqlite.Core', 'SQLitePCLRaw.bundle_e_sqlite3', 'SQLitePCLRaw.core', 'SQLitePCLRaw.lib.e_sqlite3', 'SQLitePCLRaw.provider.e_sqlite3')) {
        $locked = $lock.dependencies.'net10.0-windows7.0'.$id
        $stem = $id.ToLowerInvariant() + '.' + $locked.resolved + '.nupkg'
        $packageRoot = Join-Path $actualPackagesRoot ($id.ToLowerInvariant() + '/' + $locked.resolved)
        $sidecar = [System.IO.File]::ReadAllText((Join-Path $packageRoot ($stem + '.sha512'))).Trim()
        $cacheMetadata = Get-Content -Raw -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') | ConvertFrom-Json
        Assert-Equal $locked.contentHash $cacheMetadata.contentHash 'NuGet cached content identity must match the checked-in lock (distinct from archive-byte SHA512)'
        $stream = [System.IO.File]::OpenRead((Join-Path $packageRoot $stem))
        $sha512 = [System.Security.Cryptography.SHA512]::Create()
        try { $packageHash = [Convert]::ToBase64String($sha512.ComputeHash($stream)) }
        finally { $stream.Dispose(); $sha512.Dispose() }
        Assert-Equal $sidecar $packageHash 'cached SQLite archive bytes must match its SHA512 sidecar'
        $preflight.packages += [pscustomobject]@{ id = $id; version = $locked.resolved; archiveSha512 = $packageHash; nugetContentHash = $cacheMetadata.contentHash }
    }

    $sqlitePayloads = @{
        'Microsoft.Data.Sqlite.dll' = 'microsoft.data.sqlite.core/10.0.11/lib/net8.0/Microsoft.Data.Sqlite.dll'
        'SQLitePCLRaw.batteries_v2.dll' = 'sqlitepclraw.bundle_e_sqlite3/2.1.12/lib/netstandard2.0/SQLitePCLRaw.batteries_v2.dll'
        'SQLitePCLRaw.core.dll' = 'sqlitepclraw.core/2.1.12/lib/netstandard2.0/SQLitePCLRaw.core.dll'
        'SQLitePCLRaw.provider.e_sqlite3.dll' = 'sqlitepclraw.provider.e_sqlite3/2.1.12/lib/net6.0/SQLitePCLRaw.provider.e_sqlite3.dll'
        'e_sqlite3.dll' = 'sqlitepclraw.lib.e_sqlite3/2.1.12/runtimes/win-x64/native/e_sqlite3.dll'
    }
    foreach ($source in $sqlitePayloads.Values) {
        $parts = $source.Split('/')
        $packageRoot = Join-Path $actualPackagesRoot ($parts[0] + '/' + $parts[1])
        $archivePath = Join-Path $packageRoot ($parts[0] + '.' + $parts[1] + '.nupkg')
        $entryPath = $parts[2..($parts.Length - 1)] -join '/'
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $stream = $archive.GetEntry($entryPath).Open()
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { $archiveHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            Assert-Equal $archiveHash (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $actualPackagesRoot $source)).Hash 'extracted SQLite payload must match the verified cached NuGet archive'
        }
        finally { $archive.Dispose() }
    }
    $afterPublish = {
        param($OutputPath, $PublishIndex)
        foreach ($destination in $sqlitePayloads.Keys) {
            [System.IO.File]::Copy((Join-Path $actualPackagesRoot $sqlitePayloads[$destination]), (Join-Path $OutputPath $destination), $true)
        }
        [System.IO.File]::Copy((Join-Path $actualSdkRoot 'packs/Microsoft.NETCore.App.Host.win-x64/10.0.11/runtimes/win-x64/native/apphost.exe'), (Join-Path $OutputPath 'PhotoSift.App.exe'), $true)
    }.GetNewClosure()
    $actualCalls = [System.Collections.Generic.List[object]]::new()
    $actualOperations = New-SyntheticReleaseOperations -Calls $actualCalls -DriftSecondPublish $false -AfterPublish $afterPublish
    $actualOperations.Remove('GetFileVersion')
    $actualOutput = Join-Path $testRoot 'actual-input-output'
    $actualResult = & $builderPath -RepositoryRoot $actualRepository -OutputRoot $actualOutput -SdkRoot $actualSdkRoot -PackagesRoot $actualPackagesRoot -SourceIdentity 'host-only-real-input-preflight' -CaptureOnly -Operations $actualOperations
    Assert-Equal 'needs-license-inventory' $actualResult.status 'actual apphost and package inputs must survive all production capture audits'
    Assert-Equal 'e_sqlite3.dll' ($actualResult.nativePaths -join ',') 'framework-dependent package has only SQLite native payload'
    Assert-True (-not (Test-Path (Join-Path $actualOutput 'evidence/framework-closure.json'))) 'no bundled runtime closure is captured'
    foreach ($destination in $sqlitePayloads.Keys) {
        Assert-Equal (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $actualPackagesRoot $sqlitePayloads[$destination])).Hash (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $actualResult.provisionalPublishPath $destination)).Hash 'published SQLite bytes equal the sealed cache'
    }
    $capture = Get-Content -Raw -LiteralPath $actualResult.dependencyCapturePath | ConvertFrom-Json
    Assert-Equal 7 @($capture.packages).Count 'apphost and six SQLite NuGet metadata sources must be captured'
    Assert-Equal 0 @($capture.components | Where-Object { $_.name -in @('.NET Runtime', 'Windows Desktop / WPF') }).Count 'installed frameworks must not acquire bundled-runtime license inventory'
    Assert-Equal 2 @(@($capture.packages | Where-Object { $_.id -ceq 'Microsoft.NETCore.App.Host.win-x64' })[0].licenseSources).Count 'SDK apphost retains LICENSE and ThirdPartyNotices provenance'
    $nativeSqlite = @($capture.components | Where-Object { $_.name -ceq 'Native SQLite' })[0]
    Assert-Equal '3.53.3' $nativeSqlite.version 'actual sealed SQLite static version'
    Assert-Equal 'embedded-utf8' $nativeSqlite.versionSource 'empty PE versions must not be invented'
    Assert-Equal 'sdk-pack' @($capture.packages | Where-Object { $_.id -ceq 'Microsoft.NETCore.App.Host.win-x64' })[0].sourceKind 'SDK apphost provenance must not claim a nonexistent NuGet package'
    $apphost = @($capture.packages | Where-Object { $_.id -ceq 'Microsoft.NETCore.App.Host.win-x64' })[0]
    Assert-Equal (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $actualSdkRoot 'packs/Microsoft.NETCore.App.Host.win-x64/10.0.11/runtimes/win-x64/native/apphost.exe')).Hash $apphost.sourceSha256 'apphost provenance hashes the actual SDK template'
    $preflight.nativePaths = $actualResult.nativePaths
    $preflight.dependencyCapture = $capture
    $preflight.status = 'passed'
    Write-Output 'Actual input preflight: PASS; 0 bundled framework paths, 1 native SQLite path, 7 package/SDK records; no product execution.'
}
finally {
    if ($EvidenceRoot) {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($EvidenceRoot)) | Out-Null
        Write-Utf8NoBom -Path (Join-Path $EvidenceRoot 'real-input-preflight.json') -Content ($preflight | ConvertTo-Json -Depth 12)
        if (Get-Variable actualOutput -ErrorAction SilentlyContinue) {
            $builderEvidence = Join-Path $actualOutput 'evidence'
            if (Test-Path -LiteralPath $builderEvidence) { Copy-Item -LiteralPath $builderEvidence -Destination (Join-Path $EvidenceRoot 'builder-evidence') -Recurse -Force }
        }
    }
    if ([System.IO.Directory]::Exists($testRoot)) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

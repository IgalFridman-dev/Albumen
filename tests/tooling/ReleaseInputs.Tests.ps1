[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SdkRoot,
    [Parameter(Mandatory)][string]$PackagesRoot,
    [Parameter(Mandatory)][string]$RuntimePackArchiveRoot,
    [string]$EvidenceRoot
)

# Host-safe input integration test: reads PE/ZIP bytes, never loads or executes them.
# Only first-party application bytes and external build command results are simulated.
. (Join-Path $PSScriptRoot 'AlbumenRelease.Tests.ps1') -LoadHelpersOnly
Add-Type -AssemblyName System.IO.Compression.FileSystem
$actualSdkRoot = [System.IO.Path]::GetFullPath($SdkRoot)
$actualPackagesRoot = [System.IO.Path]::GetFullPath($PackagesRoot)
$actualArchiveRoot = [System.IO.Path]::GetFullPath($RuntimePackArchiveRoot)
$preflight = [ordered]@{
    mode = 'host-only-input-preflight'; externalProductExecution = $false
    simulatedBoundaries = @('SDK --version response (SDK is not executed/selected)', 'restore project.assets.json (schema derived from SDK/NuGet source, not a real restore)', 'publish compilation', 'first-party source/assembly', 'application .deps.json and runtimeconfig.json')
    packages = @(); frameworkPaths = @()
    unresolved = @('Actual restore/publish and application execution require the offline VM.', 'Apphost bytes are unmodified SDK data; this is not an executable application.', 'SQLite version is statically inspected, not queried by executing the DLL.')
}
try {
    $actualRepository = New-SyntheticReleaseRepository -Name 'actual-input-repository'
    $destinationSources = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($spec in @(
            @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Count = 187; Selected = 187; Profile = '' },
            @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Count = 284; Selected = 209; Profile = 'WPF' }
        )) {
        $id = $spec.Id.ToLowerInvariant()
        $packRoot = Join-Path $actualPackagesRoot ($id + '/10.0.11')
        $archivePath = Join-Path $actualArchiveRoot ($id + '.10.0.11.nupkg')
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $reader = [System.IO.StreamReader]::new($archive.GetEntry('data/RuntimeList.xml').Open())
            try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
            Assert-Equal $spec.Count @($xml.FileList.File).Count 'the test must use the complete exact pinned manifest'
            $selected = @($xml.FileList.File | Where-Object {
                -not $spec.Profile -or $_.GetAttribute('Profile').Split(';') -ccontains 'WPF'
            })
            Assert-Equal $spec.Selected $selected.Count 'SDK WPF profile selection must match the exact pack'
            foreach ($entry in $selected) {
                $source = $entry.GetAttribute('Path').Replace('\', '/')
                $destination = $source.Split('/')[-1]
                if ($entry.GetAttribute('Type') -ceq 'Resources') {
                    $destination = $entry.GetAttribute('Culture') + '/' + $destination
                }
                $sourcePath = Join-Path $packRoot $source
                $stream = $archive.GetEntry($source).Open()
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try { $archiveHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
                finally { $sha.Dispose(); $stream.Dispose() }
                Assert-Equal $archiveHash (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash 'sealed-cache payload must equal the actual downloaded archive payload'
                $destinationSources[$destination] = [pscustomobject]@{ Source = $sourcePath; Hash = $archiveHash; Type = $entry.GetAttribute('Type') }
            }
            $sha512 = [System.Security.Cryptography.SHA512]::Create()
            $stream = [System.IO.File]::OpenRead($archivePath)
            try { $archiveSha512 = [Convert]::ToBase64String($sha512.ComputeHash($stream)) }
            finally { $stream.Dispose(); $sha512.Dispose() }
            $cachedSha512 = [System.IO.File]::ReadAllText((Join-Path $packRoot ($id + '.10.0.11.nupkg.sha512'))).Trim()
            Assert-Equal $archiveSha512 $cachedSha512 'cached NuGet SHA512 must identify the exact archive inspected'
            $preflight.packages += [pscustomobject]@{ id = $spec.Id; version = '10.0.11'; selected = $selected.Count; sha512 = $archiveSha512 }
        } finally { $archive.Dispose() }
    }
    Assert-Equal 395 $destinationSources.Count 'the exact selected framework union has one shared WindowsBase destination'
    $expectedNative = @('D3DCompiler_47_cor3.dll','Microsoft.DiaSymReader.Native.amd64.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','System.IO.Compression.Native.dll','clretwrc.dll','clrgc.dll','clrgcexp.dll','clrjit.dll','coreclr.dll','createdump.exe','hostfxr.dll','hostpolicy.dll','mscordaccore.dll','mscordaccore_amd64_amd64_10.0.1126.37416.dll','mscordbi.dll','mscorrc.dll','msquic.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll')
    foreach ($native in $expectedNative) { Assert-Equal 'Native' $destinationSources[$native].Type 'all actual native names must retain exact classification' }
    Assert-Equal 20 @($destinationSources.Values | Where-Object { $_.Type -ceq 'Native' }).Count 'exact native manifest closure'

    $lock = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'src/Albumen.App/packages.lock.json') | ConvertFrom-Json
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
    $afterPublish = {
        param($OutputPath, $PublishIndex)
        foreach ($destination in $destinationSources.Keys) {
            $path = Join-Path $OutputPath $destination
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
            [System.IO.File]::Copy($destinationSources[$destination].Source, $path, $true)
        }
        foreach ($destination in $sqlitePayloads.Keys) {
            [System.IO.File]::Copy((Join-Path $actualPackagesRoot $sqlitePayloads[$destination]), (Join-Path $OutputPath $destination), $true)
        }
        [System.IO.File]::Copy((Join-Path $actualSdkRoot 'packs/Microsoft.NETCore.App.Host.win-x64/10.0.11/runtimes/win-x64/native/apphost.exe'), (Join-Path $OutputPath 'Albumen.App.exe'), $true)
    }.GetNewClosure()
    $actualCalls = [System.Collections.Generic.List[object]]::new()
    $actualOperations = New-SyntheticReleaseOperations -Calls $actualCalls -DriftSecondPublish $false -AfterPublish $afterPublish
    $actualOperations.Remove('GetFileVersion')
    $actualOutput = Join-Path $testRoot 'actual-input-output'
    $actualResult = & $builderPath -RepositoryRoot $actualRepository -OutputRoot $actualOutput -SdkRoot $actualSdkRoot -PackagesRoot $actualPackagesRoot -SourceIdentity 'host-only-real-input-preflight' -CaptureOnly -Operations $actualOperations
    Assert-Equal 'needs-license-inventory' $actualResult.status 'actual runtime and package inputs must survive all production capture audits'
    $closure = Get-Content -Raw -LiteralPath $actualResult.frameworkClosurePath | ConvertFrom-Json
    Assert-Equal 395 @($closure.frameworkPaths).Count 'production closure must equal independent actual-input union'
    foreach ($destination in $closure.frameworkPaths) {
        Assert-True $destinationSources.ContainsKey($destination) 'production closure contains only SDK-mapped exact pack assets'
        Assert-Equal $destinationSources[$destination].Hash (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $actualResult.provisionalPublishPath $destination)).Hash 'every copied framework file preserves actual archive bytes'
    }
    Assert-Equal 21 @($actualResult.nativePaths).Count 'exact 20 framework natives plus SQLite'
    $capture = Get-Content -Raw -LiteralPath $actualResult.dependencyCapturePath | ConvertFrom-Json
    Assert-Equal 9 @($capture.packages).Count 'all actual NuGet and SDK metadata sources must be captured'
    Assert-Equal 2 @(@($capture.packages | Where-Object { $_.id -ceq 'Microsoft.NETCore.App.Runtime.win-x64' })[0].licenseSources).Count 'NETCore archive has LICENSE.TXT and THIRD-PARTY-NOTICES.TXT'
    Assert-Equal 1 @(@($capture.packages | Where-Object { $_.id -ceq 'Microsoft.WindowsDesktop.App.Runtime.win-x64' })[0].licenseSources).Count 'Desktop runtime archive has LICENSE only; no notice must be invented'
    $nativeSqlite = @($capture.components | Where-Object { $_.name -ceq 'Native SQLite' })[0]
    Assert-Equal '3.53.3' $nativeSqlite.version 'actual sealed SQLite static version'
    Assert-Equal 'embedded-utf8' $nativeSqlite.versionSource 'empty PE versions must not be invented'
    Assert-Equal 'sdk-pack' @($capture.packages | Where-Object { $_.id -ceq 'Microsoft.NETCore.App.Host.win-x64' })[0].sourceKind 'SDK apphost provenance must not claim a nonexistent NuGet package'
    $preflight.frameworkPaths = $closure.frameworkPaths
    $preflight.nativePaths = $actualResult.nativePaths
    $preflight.dependencyCapture = $capture
    $preflight.status = 'passed'
    Write-Output 'Actual input preflight: PASS; 395 framework paths, 20 framework native paths, 9 package/SDK records; no product execution.'
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

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repositoryRoot 'eng\release\PhotoSiftRelease.psm1'
$checkerPath = Join-Path $repositoryRoot 'eng\release\Test-PhotoSiftRelease.ps1'
$builderPath = Join-Path $repositoryRoot 'eng\release\New-PhotoSiftRelease.ps1'
$runtimeBoundaryPath = Join-Path $repositoryRoot 'eng\Verify-RuntimeBoundary.ps1'
$launcherAssetPath = Join-Path $repositoryRoot 'eng\release\assets\Start-PhotoSift.cmd'
$verifierAssetPath = Join-Path $repositoryRoot 'eng\release\assets\Verify-PhotoSiftRelease.ps1'

foreach ($requiredPath in @(
        $modulePath,
        $checkerPath,
        $builderPath,
        $runtimeBoundaryPath,
        $launcherAssetPath,
        $verifierAssetPath)) {
    if (-not [System.IO.File]::Exists($requiredPath)) {
        throw "Required release implementation does not exist: $requiredPath"
    }
}

Import-Module -Name $modulePath -Force

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'PhotoSift-Release-Tests-' + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:passed = 0

function Assert-Equal {
    param($Expected, $Actual, [string]$Because)

    if ($Expected -cne $Actual) {
        throw "Expected '$Expected' but got '$Actual': $Because"
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Because)

    if (-not $Condition) {
        throw "Expected true: $Because"
    }
}

function Assert-ThrowsLike {
    param(
        [scriptblock]$Action,
        [string]$Pattern,
        [string]$Because
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected failure matching '$Pattern' but got '$($_.Exception.Message)': $Because"
        }
        $script:passed++
        return
    }

    throw "Expected failure matching '$Pattern': $Because"
}

function Test-CorrectionRejection {
    param(
        [scriptblock]$Action,
        [string]$Pattern,
        [string]$Because,
        [System.Collections.Generic.List[string]]$Failures
    )

    try {
        & $Action
        $Failures.Add("ACCEPTED: $Because")
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            $Failures.Add(
                "WRONG FAILURE '$($_.Exception.Message)' (expected '$Pattern'): $Because")
        }
        else {
            $script:passed++
        }
    }
}

function Get-Sha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Write-ManifestAndDigest {
    param([string]$PackagePath, $Manifest)

    $manifestPath = Join-Path $PackagePath 'release-manifest.json'
    $digestPath = Join-Path $PackagePath 'release-manifest.sha256'
    Write-Utf8NoBom -Path $manifestPath -Content ($Manifest | ConvertTo-Json -Depth 8)
    Write-Utf8NoBom -Path $digestPath -Content ((Get-Sha256 -Path $manifestPath) + [System.Environment]::NewLine)
}

function Read-Manifest {
    param([string]$PackagePath)

    return Get-Content -Raw -LiteralPath (Join-Path $PackagePath 'release-manifest.json') |
        ConvertFrom-Json
}

function New-CleanPackage {
    param([string]$Name)

    $packagePath = Join-Path $testRoot $Name
    [System.IO.Directory]::CreateDirectory($packagePath) | Out-Null
    [System.IO.File]::Copy($launcherAssetPath, (Join-Path $packagePath 'Start-PhotoSift.cmd'))
    [System.IO.File]::Copy($verifierAssetPath, (Join-Path $packagePath 'Verify-PhotoSiftRelease.ps1'))
    Write-Utf8NoBom -Path (Join-Path $packagePath 'PhotoSift.App.exe') -Content 'synthetic application'
    Write-Utf8NoBom `
        -Path (Join-Path $packagePath 'PhotoSift.App.deps.json') `
        -Content '{"runtimeTarget":{"name":"win-x64"}}'
    Write-Utf8NoBom `
        -Path (Join-Path $packagePath 'PhotoSift.App.runtimeconfig.json') `
        -Content '{"runtimeOptions":{"tfm":"net10.0-windows"}}'
    [System.IO.Directory]::CreateDirectory((Join-Path $packagePath 'runtime')) | Out-Null
    Write-Utf8NoBom -Path (Join-Path $packagePath 'runtime\PhotoSift.Managed.dll') -Content 'managed fixture'
    Write-Utf8NoBom -Path (Join-Path $packagePath 'coreclr.dll') -Content 'named native fixture'
    Write-Utf8NoBom `
        -Path (Join-Path $packagePath 'release-native-paths.json') `
        -Content '{"schemaVersion":1,"nativePaths":["coreclr.dll"]}'
    Write-Utf8NoBom -Path (Join-Path $packagePath 'THIRD-PARTY-NOTICES.md') -Content 'fixture notice'
    New-PhotoSiftReleaseManifest `
        -PackagePath $packagePath `
        -OutputPath (Join-Path $packagePath 'release-manifest.json') | Out-Null
    return $packagePath
}

function Copy-Package {
    param([string]$SourcePath, [string]$Name)

    $destination = Join-Path $testRoot $Name
    Copy-Item -Recurse -LiteralPath $SourcePath -Destination $destination
    return $destination
}

function Invoke-ReleaseChecker {
    param([string]$PackagePath, [string]$Name)

    $stdoutPath = Join-Path $testRoot ($Name + '-stdout.txt')
    $stderrPath = Join-Path $testRoot ($Name + '-stderr.txt')
    $engineArguments = @('-NoProfile')
    if ([System.IO.Path]::GetFileName((Get-Process -Id $PID).Path) -ieq 'powershell.exe') {
        $engineArguments += @('-ExecutionPolicy', 'Bypass')
    }
    $process = Start-Process `
        -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList ($engineArguments + @('-File', $checkerPath, '-PackagePath', $PackagePath)) `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = @(
            Get-Content -Raw -LiteralPath $stdoutPath
            Get-Content -Raw -LiteralPath $stderrPath
        ) -join [System.Environment]::NewLine
    }
}

function Invoke-PackageVerifier {
    param(
        [string]$PackagePath,
        [string]$Name,
        [string[]]$ApplicationArguments = @()
    )

    $stdoutPath = Join-Path $testRoot ($Name + '-stdout.txt')
    $stderrPath = Join-Path $testRoot ($Name + '-stderr.txt')
    $engineArguments = @('-NoProfile')
    if ([System.IO.Path]::GetFileName((Get-Process -Id $PID).Path) -ieq 'powershell.exe') {
        $engineArguments += @('-ExecutionPolicy', 'Bypass')
    }
    $argumentList = $engineArguments + @(
        '-File',
        ('"' + (Join-Path $PackagePath 'Verify-PhotoSiftRelease.ps1') + '"')) + $ApplicationArguments
    $process = Start-Process `
        -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = @(
            Get-Content -Raw -LiteralPath $stdoutPath
            Get-Content -Raw -LiteralPath $stderrPath
        ) -join [System.Environment]::NewLine
    }
}

function Invoke-PackageLauncher {
    param(
        [string]$PackagePath,
        [string]$Name,
        [string[]]$ApplicationArguments = @()
    )

    $stdoutPath = Join-Path $testRoot ($Name + '-stdout.txt')
    $stderrPath = Join-Path $testRoot ($Name + '-stderr.txt')
    $process = Start-Process `
        -FilePath (Join-Path $PackagePath 'Start-PhotoSift.cmd') `
        -ArgumentList $ApplicationArguments `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = @(
            Get-Content -Raw -LiteralPath $stdoutPath
            Get-Content -Raw -LiteralPath $stderrPath
        ) -join [System.Environment]::NewLine
    }
}

function New-SyntheticReleaseRepository {
    param([string]$Name)

    $root = Join-Path $testRoot $Name
    foreach ($path in @(
            'eng\release\assets',
            'eng\release\licenses',
            'src\PhotoSift.App')) {
        [System.IO.Directory]::CreateDirectory((Join-Path $root $path)) | Out-Null
    }
    foreach ($source in @(
            $modulePath,
            $checkerPath,
            $builderPath,
            $runtimeBoundaryPath,
            $launcherAssetPath,
            $verifierAssetPath)) {
        $relativePath = $source.Substring($repositoryRoot.Length).TrimStart('\', '/')
        $destination = Join-Path $root $relativePath
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($destination)) | Out-Null
        [System.IO.File]::Copy($source, $destination)
    }
    Write-Utf8NoBom `
        -Path (Join-Path $root 'global.json') `
        -Content '{"sdk":{"version":"10.0.400","rollForward":"disable"}}'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'NuGet.Config') `
        -Content '<configuration><packageSources><clear /></packageSources></configuration>'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'src\PhotoSift.App\PhotoSift.App.csproj') `
        -Content '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'src\PhotoSift.App\Clean.cs') `
        -Content 'public static class Clean { }'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'src\PhotoSift.App\packages.lock.json') `
        -Content '{"version":2,"dependencies":{}}'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'THIRD-PARTY-NOTICES.md') `
        -Content '# Synthetic complete notices'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'eng\release\licenses\MIT.txt') `
        -Content 'Synthetic complete MIT license text'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'eng\release\licenses\Apache-2.0.txt') `
        -Content 'Synthetic complete Apache 2.0 license text'
    Write-Utf8NoBom `
        -Path (Join-Path $root 'eng\release\licenses\SQLite-Blessing.txt') `
        -Content 'Synthetic SQLite blessing text'
    $componentSpecifications = @(
        @{ Name = '.NET Host'; Version = '10.0.11'; Paths = @('PhotoSift.App.exe'); License = 'MIT'; LicensePaths = @('licenses/MIT.txt') },
        @{ Name = '.NET Runtime'; Version = '10.0.11'; Paths = @('coreclr.dll', 'System.Net.Http.dll'); License = 'MIT'; LicensePaths = @('licenses/MIT.txt') },
        @{ Name = 'Microsoft.Data.Sqlite'; Version = '10.0.11'; Paths = @(); License = 'MIT'; LicensePaths = @('licenses/MIT.txt') },
        @{ Name = 'Microsoft.Data.Sqlite.Core'; Version = '10.0.11'; Paths = @('Microsoft.Data.Sqlite.dll'); License = 'MIT'; LicensePaths = @('licenses/MIT.txt') },
        @{ Name = 'Native SQLite'; Version = '3.49.1'; Paths = @('e_sqlite3.dll'); License = 'blessing'; LicensePaths = @('licenses/SQLite-Blessing.txt') },
        @{ Name = 'SQLitePCLRaw.bundle_e_sqlite3'; Version = '2.1.12'; Paths = @('SQLitePCLRaw.batteries_v2.dll'); License = 'Apache-2.0'; LicensePaths = @('licenses/Apache-2.0.txt') },
        @{ Name = 'SQLitePCLRaw.core'; Version = '2.1.12'; Paths = @('SQLitePCLRaw.core.dll'); License = 'Apache-2.0'; LicensePaths = @('licenses/Apache-2.0.txt') },
        @{ Name = 'SQLitePCLRaw.lib.e_sqlite3'; Version = '2.1.12'; Paths = @('e_sqlite3.dll'); License = 'Apache-2.0'; LicensePaths = @('licenses/Apache-2.0.txt') },
        @{ Name = 'SQLitePCLRaw.provider.e_sqlite3'; Version = '2.1.12'; Paths = @('SQLitePCLRaw.provider.e_sqlite3.dll'); License = 'Apache-2.0'; LicensePaths = @('licenses/Apache-2.0.txt') },
        @{ Name = 'Windows Desktop / WPF'; Version = '10.0.11'; Paths = @('D3DCompiler_47_cor3.dll', 'PresentationCore.dll', 'wpfgfx_cor3.dll'); License = 'MIT'; LicensePaths = @('licenses/MIT.txt') })
    $inventory = [pscustomobject][ordered]@{
        schemaVersion = 1
        components = @($componentSpecifications | ForEach-Object {
                [pscustomobject][ordered]@{
                    name = $_.Name
                    version = $_.Version
                    shippedPaths = $_.Paths
                    licenseIdentifier = $_.License
                    packagedLicensePaths = $_.LicensePaths
                    officialSourceUrl = 'https://example.invalid/source/' + ($_.Name -replace ' ', '-')
                    redistributionTermUrl = 'https://example.invalid/terms/' + ($_.Name -replace ' ', '-')
                    redistributionApproval = 'pending-owner-approval'
                }
            })
    }
    Write-Utf8NoBom `
        -Path (Join-Path $root 'eng\release\release-dependencies.json') `
        -Content (($inventory | ConvertTo-Json -Depth 8) + [System.Environment]::NewLine)
    return $root
}

function New-SyntheticPackagesRoot {
    param([string]$Name)

    $root = Join-Path $testRoot $Name
    $netCorePath = Join-Path $root 'microsoft.netcore.app.runtime.win-x64\10.0.11\data'
    $windowsDesktopPath = Join-Path $root 'microsoft.windowsdesktop.app.runtime.win-x64\10.0.11\data'
    [System.IO.Directory]::CreateDirectory($netCorePath) | Out-Null
    [System.IO.Directory]::CreateDirectory($windowsDesktopPath) | Out-Null
    Write-Utf8NoBom `
        -Path (Join-Path $netCorePath 'RuntimeList.xml') `
        -Content @'
<FileList FrameworkName="Microsoft.NETCore.App" TargetFrameworkVersion="10.0">
  <File Type="Native" Path="coreclr.dll" />
  <File Type="Managed" Path="System.Net.Http.dll" />
</FileList>
'@
    Write-Utf8NoBom `
        -Path (Join-Path $windowsDesktopPath 'RuntimeList.xml') `
        -Content @'
<FileList FrameworkName="Microsoft.WindowsDesktop.App" TargetFrameworkVersion="10.0">
  <File Type="Native" Path="D3DCompiler_47_cor3.dll" />
  <File Type="Managed" Path="PresentationCore.dll" />
  <File Type="Native" Path="wpfgfx_cor3.dll" />
</FileList>
'@
    foreach ($package in @(
            @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = '10.0.11'; License = 'MIT' },
            @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Version = '10.0.11'; License = 'MIT' },
            @{ Id = 'Microsoft.NETCore.App.Host.win-x64'; Version = '10.0.11'; License = 'MIT' },
            @{ Id = 'Microsoft.Data.Sqlite'; Version = '10.0.11'; License = 'MIT' },
            @{ Id = 'Microsoft.Data.Sqlite.Core'; Version = '10.0.11'; License = 'MIT' },
            @{ Id = 'SQLitePCLRaw.bundle_e_sqlite3'; Version = '2.1.12'; License = 'Apache-2.0' },
            @{ Id = 'SQLitePCLRaw.core'; Version = '2.1.12'; License = 'Apache-2.0' },
            @{ Id = 'SQLitePCLRaw.lib.e_sqlite3'; Version = '2.1.12'; License = 'Apache-2.0' },
            @{ Id = 'SQLitePCLRaw.provider.e_sqlite3'; Version = '2.1.12'; License = 'Apache-2.0' }
        )) {
        $packagePath = Join-Path $root ($package.Id.ToLowerInvariant() + '\' + $package.Version)
        [System.IO.Directory]::CreateDirectory($packagePath) | Out-Null
        Write-Utf8NoBom `
            -Path (Join-Path $packagePath ($package.Id.ToLowerInvariant() + '.nuspec')) `
            -Content @"
<?xml version="1.0"?>
<package><metadata>
  <id>$($package.Id)</id><version>$($package.Version)</version>
  <license type="expression">$($package.License)</license>
  <projectUrl>https://example.invalid/$($package.Id)</projectUrl>
  <repository type="git" url="https://example.invalid/source/$($package.Id)" />
</metadata></package>
"@
        Write-Utf8NoBom `
            -Path (Join-Path $packagePath 'LICENSE.txt') `
            -Content "synthetic $($package.License) license for $($package.Id)"
        if ($package.Id -like 'Microsoft.*Runtime*' -or
            $package.Id -like 'Microsoft.WindowsDesktop*') {
            Write-Utf8NoBom `
                -Path (Join-Path $packagePath 'THIRD-PARTY-NOTICES.TXT') `
                -Content "synthetic third-party notices for $($package.Id)"
        }
    }
    return $root
}

function Write-SyntheticPublishOutput {
    param(
        [string]$OutputPath,
        [bool]$Drift
    )

    [System.IO.Directory]::CreateDirectory($OutputPath) | Out-Null
    $libraries = [ordered]@{
        'PhotoSift.App/1.0.0' = @{ type = 'project' }
        'Microsoft.NETCore.App.Runtime.win-x64/10.0.11' = @{ type = 'package' }
        'Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.11' = @{ type = 'package' }
        'Microsoft.NETCore.App.Host.win-x64/10.0.11' = @{ type = 'package' }
        'Microsoft.Data.Sqlite/10.0.11' = @{ type = 'package' }
        'Microsoft.Data.Sqlite.Core/10.0.11' = @{ type = 'package' }
        'SQLitePCLRaw.bundle_e_sqlite3/2.1.12' = @{ type = 'package' }
        'SQLitePCLRaw.core/2.1.12' = @{ type = 'package' }
        'SQLitePCLRaw.lib.e_sqlite3/2.1.12' = @{ type = 'package' }
        'SQLitePCLRaw.provider.e_sqlite3/2.1.12' = @{ type = 'package' }
    }
    [pscustomobject]@{ libraries = $libraries } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $OutputPath 'PhotoSift.App.deps.json') -Encoding utf8
    Write-Utf8NoBom `
        -Path (Join-Path $OutputPath 'PhotoSift.App.runtimeconfig.json') `
        -Content '{"runtimeOptions":{"tfm":"net10.0-windows","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.11"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.11"}]}}'
    foreach ($file in @(
            'PhotoSift.App.exe',
            'PhotoSift.App.dll',
            'Microsoft.Data.Sqlite.dll',
            'SQLitePCLRaw.batteries_v2.dll',
            'SQLitePCLRaw.core.dll',
            'SQLitePCLRaw.provider.e_sqlite3.dll',
            'e_sqlite3.dll',
            'coreclr.dll',
            'D3DCompiler_47_cor3.dll',
            'System.Net.Http.dll',
            'PresentationCore.dll',
            'wpfgfx_cor3.dll')) {
        $content = "synthetic $file"
        if ($Drift -and $file -ceq 'PhotoSift.App.dll') {
            $content += ' drift'
        }
        Write-Utf8NoBom -Path (Join-Path $OutputPath $file) -Content $content
    }
}

function New-SyntheticReleaseOperations {
    param(
        [System.Collections.Generic.List[object]]$Calls,
        [bool]$DriftSecondPublish,
        [string]$ExtraRestoredPackage,
        [scriptblock]$AfterPublish,
        [bool]$InjectCaseCollision,
        [string]$SelectedSdkVersion = '10.0.400'
    )

    $state = [pscustomobject]@{ PublishCount = 0 }
    $operations = @{
        GetFileVersion = {
            param($LiteralPath)
            return [pscustomobject]@{
                FileVersion = '3.49.1'
                ProductVersion = '3.49.1'
            }
        }
        InvokeExternal = {
            param($Name, $FilePath, $Arguments, $WorkingDirectory)

            $Calls.Add([pscustomobject]@{
                    Name = $Name
                    FilePath = $FilePath
                    Arguments = [string[]]@($Arguments)
                    WorkingDirectory = $WorkingDirectory
                })
            if ($Name -ceq 'sdk-version') {
                return [pscustomobject]@{
                    ExitCode = 0
                    Output = $SelectedSdkVersion
                }
            }
            if ($Name -like 'restore-*') {
                $assetsPath = Join-Path $WorkingDirectory 'src\PhotoSift.App\obj\project.assets.json'
                [System.IO.Directory]::CreateDirectory(
                    [System.IO.Path]::GetDirectoryName($assetsPath)) | Out-Null
                $restoredLibraries = [ordered]@{
                    'Microsoft.NETCore.App.Runtime.win-x64/10.0.11' = @{ type = 'package' }
                    'Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.11' = @{ type = 'package' }
                    'Microsoft.NETCore.App.Host.win-x64/10.0.11' = @{ type = 'package' }
                    'Microsoft.Data.Sqlite/10.0.11' = @{ type = 'package' }
                    'Microsoft.Data.Sqlite.Core/10.0.11' = @{ type = 'package' }
                    'SQLitePCLRaw.bundle_e_sqlite3/2.1.12' = @{ type = 'package' }
                    'SQLitePCLRaw.core/2.1.12' = @{ type = 'package' }
                    'SQLitePCLRaw.lib.e_sqlite3/2.1.12' = @{ type = 'package' }
                    'SQLitePCLRaw.provider.e_sqlite3/2.1.12' = @{ type = 'package' }
                }
                if (-not [string]::IsNullOrWhiteSpace($ExtraRestoredPackage)) {
                    $restoredLibraries[$ExtraRestoredPackage] = @{ type = 'package' }
                }
                [pscustomobject]@{
                    libraries = $restoredLibraries
                } | ConvertTo-Json -Depth 6 |
                    Set-Content -LiteralPath $assetsPath -Encoding utf8
            }
            if ($Name -like 'publish-*') {
                $state.PublishCount++
                $outputIndex = [Array]::IndexOf([object[]]$Arguments, '-o')
                if ($outputIndex -lt 0) {
                    throw "Synthetic publish invocation omitted -o: $($Arguments -join ' ')"
                }
                Write-SyntheticPublishOutput `
                    -OutputPath $Arguments[$outputIndex + 1] `
                    -Drift ($DriftSecondPublish -and $state.PublishCount -eq 2)
                if ($null -ne $AfterPublish) {
                    & $AfterPublish $Arguments[$outputIndex + 1] $state.PublishCount
                }
            }
            return [pscustomobject]@{ ExitCode = 0; Output = "synthetic $Name" }
        }.GetNewClosure()
    }
    if ($InjectCaseCollision) {
        $operations['GetRawPublishEntries'] = {
            param($RootPath)

            $entries = @(
                Get-ChildItem -Force -Recurse -LiteralPath $RootPath)
            return @($entries + @(
                    [pscustomobject]@{
                        FullName = Join-Path $RootPath 'CORECLR.dll'
                        Attributes = [System.IO.FileAttributes]::Normal
                        PSIsContainer = $false
                    }))
        }
    }
    return $operations
}

try {
    $cleanPackage = New-CleanPackage -Name 'clean'
    Test-PhotoSiftRelease -PackagePath $cleanPackage | Out-Null
    $cleanCheckerResult = Invoke-ReleaseChecker -PackagePath $cleanPackage -Name 'clean-checker'
    Assert-Equal 0 $cleanCheckerResult.ExitCode 'the public checker script must accept the canonical package'
    $script:passed++

    $manifest = Read-Manifest -PackagePath $cleanPackage
    Assert-Equal 1 $manifest.schemaVersion 'the schema version must be exact'
    Assert-Equal `
        'PhotoSift.App.deps.json,PhotoSift.App.exe,PhotoSift.App.runtimeconfig.json,Start-PhotoSift.cmd,THIRD-PARTY-NOTICES.md,Verify-PhotoSiftRelease.ps1,coreclr.dll,release-native-paths.json,runtime/PhotoSift.Managed.dll' `
        (($manifest.payload | ForEach-Object { $_.path }) -join ',') `
        'payload paths must be canonical slash-separated ordinal-sorted paths'
    Assert-Equal `
        'managed-runtime,application,managed-runtime,launcher,notice,launcher,native-runtime,managed-runtime,managed-runtime' `
        (($manifest.payload | ForEach-Object { $_.kind }) -join ',') `
        'each clean payload path must have its exact bounded kind'
    Assert-Equal 'coreclr.dll' ($manifest.nativePaths -join ',') 'the named native path must be explicit'
    foreach ($entry in $manifest.payload) {
        Assert-True ($entry.length -ge 0) "payload length must be nonnegative for $($entry.path)"
        Assert-True ($entry.sha256 -cmatch '^[0-9A-F]{64}$') "payload SHA-256 must be uppercase hex for $($entry.path)"
    }
    $script:passed++

    $expandedNativePackage = Copy-Package `
        -SourcePath $cleanPackage `
        -Name 'expanded-native-policy'
    Write-Utf8NoBom `
        -Path (Join-Path $expandedNativePackage 'hostfxr.dll') `
        -Content 'second named native fixture'
    Write-Utf8NoBom `
        -Path (Join-Path $expandedNativePackage 'release-native-paths.json') `
        -Content '{"schemaVersion":1,"nativePaths":["coreclr.dll","hostfxr.dll"]}'
    New-PhotoSiftReleaseManifest `
        -PackagePath $expandedNativePackage `
        -OutputPath (Join-Path $expandedNativePackage 'release-manifest.json') | Out-Null
    $expandedNativeManifest = Read-Manifest -PackagePath $expandedNativePackage
    Assert-Equal `
        'coreclr.dll,hostfxr.dll' `
        ($expandedNativeManifest.nativePaths -join ',') `
        'the package-local explicit native policy must support the exact pinned publish closure'
    Test-PhotoSiftRelease -PackagePath $expandedNativePackage | Out-Null
    $script:passed++

    $correctionFailures = [System.Collections.Generic.List[string]]::new()
    foreach ($nativeBypass in @(
            @{ Name = 'unexpected-executable'; Path = 'unexpected.exe'; NativePe = $false },
            @{ Name = 'unexpected-native-pe'; Path = 'unexpected.dll'; NativePe = $true }
        )) {
        $fixture = Copy-Package -SourcePath $cleanPackage -Name $nativeBypass.Name
        $artifactPath = Join-Path $fixture $nativeBypass.Path
        if ($nativeBypass.NativePe) {
            [System.IO.File]::Copy((Join-Path $env:SystemRoot 'System32\cmd.exe'), $artifactPath)
        }
        else {
            Write-Utf8NoBom -Path $artifactPath -Content 'unexpected executable fixture'
        }
        Test-CorrectionRejection `
            -Action {
                New-PhotoSiftReleaseManifest `
                    -PackagePath $fixture `
                    -OutputPath (Join-Path $fixture 'release-manifest.json') | Out-Null
            } `
            -Pattern 'approved native|unexpected native' `
            -Because "an unapproved native-like payload must be rejected: $($nativeBypass.Path)" `
            -Failures $correctionFailures
    }
    $missingApprovedNative = Copy-Package `
        -SourcePath $cleanPackage `
        -Name 'missing-approved-native'
    Remove-Item -LiteralPath (Join-Path $missingApprovedNative 'coreclr.dll')
    Test-CorrectionRejection `
        -Action {
            New-PhotoSiftReleaseManifest `
                -PackagePath $missingApprovedNative `
                -OutputPath (Join-Path $missingApprovedNative 'release-manifest.json') | Out-Null
        } `
        -Pattern 'approved native|required payload' `
        -Because 'the independently approved native path set must be present exactly' `
        -Failures $correctionFailures
    foreach ($toolingBypass in @(
            @{ Path = 'python.exe' },
            @{ Path = 'python311.dll' },
            @{ Path = 'export.exe' },
            @{ Path = 'python311.zip' },
            @{ Path = 'python311._pth' },
            @{ Path = 'pyvenv.cfg' },
            @{ Path = 'export-data.exe' },
            @{ Path = 'build.ps1' },
            @{ Path = 'runtime\build.ps1' },
            @{ Path = 'misc\project.csproj' },
            @{ Path = 'runtime\Start-PhotoSift.cmd' },
            @{ Path = 'runtime\Verify-PhotoSiftRelease.ps1' }
        )) {
        $fixture = Copy-Package `
            -SourcePath $cleanPackage `
            -Name ('tooling-' + $toolingBypass.Path.Replace('\', '-').Replace('/', '-').Replace('.', '-'))
        $toolingPath = Join-Path $fixture $toolingBypass.Path
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($toolingPath)) | Out-Null
        Write-Utf8NoBom `
            -Path $toolingPath `
            -Content 'development tooling fixture'
        Test-CorrectionRejection `
            -Action {
                New-PhotoSiftReleaseManifest `
                    -PackagePath $fixture `
                    -OutputPath (Join-Path $fixture 'release-manifest.json') | Out-Null
            } `
            -Pattern 'forbidden Python or development tooling artifact' `
            -Because "Python/export/development tooling must be rejected recursively: $($toolingBypass.Path)" `
            -Failures $correctionFailures
    }
    if ($correctionFailures.Count -gt 0) {
        throw "Correction rejection failures:`n$($correctionFailures -join [System.Environment]::NewLine)"
    }

    foreach ($case in @(
            @{ Name = 'duplicate-path'; Pattern = 'duplicate|collision'; Mutate = {
                    param($value)
                    $value.payload = @($value.payload) + @($value.payload[0])
                } },
            @{ Name = 'case-collision'; Pattern = 'duplicate|collision'; Mutate = {
                    param($value)
                    $copy = $value.payload[0].PSObject.Copy()
                    $copy.path = $copy.path.ToUpperInvariant()
                    $value.payload = @($value.payload) + @($copy)
                } },
            @{ Name = 'parent-segment'; Pattern = 'canonical|segment|path'; Mutate = {
                    param($value)
                    $value.payload[0].path = '../PhotoSift.App.exe'
                } },
            @{ Name = 'rooted-path'; Pattern = 'rooted|canonical|path'; Mutate = {
                    param($value)
                    $value.payload[0].path = 'C:/PhotoSift.App.exe'
                } },
            @{ Name = 'alternate-separator'; Pattern = 'separator|canonical|path'; Mutate = {
                    param($value)
                    $value.payload[-1].path = 'runtime\PhotoSift.Managed.dll'
                } },
            @{ Name = 'native-paths-not-array'; Pattern = 'array'; Mutate = {
                    param($value)
                    $value.nativePaths = $value.nativePaths[0]
                } }
        )) {
        $fixture = Copy-Package -SourcePath $cleanPackage -Name $case.Name
        $caseManifest = Read-Manifest -PackagePath $fixture
        & $case.Mutate $caseManifest
        Write-ManifestAndDigest -PackagePath $fixture -Manifest $caseManifest
        Assert-ThrowsLike `
            -Action { Test-PhotoSiftRelease -PackagePath $fixture | Out-Null } `
            -Pattern $case.Pattern `
            -Because "the $($case.Name) manifest must be rejected before any payload file is opened"
    }

    $malformedDigest = Copy-Package -SourcePath $cleanPackage -Name 'malformed-digest'
    Write-Utf8NoBom -Path (Join-Path $malformedDigest 'release-manifest.sha256') -Content 'not-a-digest'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $malformedDigest | Out-Null } `
        -Pattern 'digest|SHA-256' `
        -Because 'a malformed detached digest must fail closed'

    $missing = Copy-Package -SourcePath $cleanPackage -Name 'missing'
    Remove-Item -LiteralPath (Join-Path $missing 'runtime\PhotoSift.Managed.dll')
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $missing | Out-Null } `
        -Pattern 'missing|set|payload' `
        -Because 'a missing payload file must be rejected'

    $extra = Copy-Package -SourcePath $cleanPackage -Name 'extra'
    Write-Utf8NoBom -Path (Join-Path $extra 'extra.txt') -Content 'extra'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $extra | Out-Null } `
        -Pattern 'extra|set|payload' `
        -Because 'an extra on-disk file must be rejected'

    $lengthMutation = Copy-Package -SourcePath $cleanPackage -Name 'length-mutation'
    Add-Content -LiteralPath (Join-Path $lengthMutation 'PhotoSift.App.exe') -Value 'different length'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $lengthMutation | Out-Null } `
        -Pattern 'length' `
        -Because 'a changed payload length must be rejected'

    $hashMutation = Copy-Package -SourcePath $cleanPackage -Name 'hash-mutation'
    $hashPath = Join-Path $hashMutation 'PhotoSift.App.exe'
    $hashBytes = [System.IO.File]::ReadAllBytes($hashPath)
    $hashBytes[0] = $hashBytes[0] -bxor 1
    [System.IO.File]::WriteAllBytes($hashPath, $hashBytes)
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $hashMutation | Out-Null } `
        -Pattern 'hash|SHA-256' `
        -Because 'same-length payload corruption must be rejected'

    foreach ($forbidden in @(
            @{ Name = 'model'; Path = 'models\weights.onnx' },
            @{ Name = 'test'; Path = 'PhotoSift.App.Tests.dll' },
            @{ Name = 'corpus'; Path = 'corpus\sample.jpg' },
            @{ Name = 'python'; Path = 'tools\export.py' },
            @{ Name = 'cache'; Path = 'cache\index.db' },
            @{ Name = 'secret'; Path = '.env' }
        )) {
        $fixture = Copy-Package -SourcePath $cleanPackage -Name ('forbidden-' + $forbidden.Name)
        $artifactPath = Join-Path $fixture $forbidden.Path
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($artifactPath)) | Out-Null
        Write-Utf8NoBom -Path $artifactPath -Content 'forbidden fixture'
        Assert-ThrowsLike `
            -Action {
                New-PhotoSiftReleaseManifest `
                    -PackagePath $fixture `
                    -OutputPath (Join-Path $fixture 'release-manifest.json') | Out-Null
            } `
            -Pattern 'forbidden|model|test|corpus|Python|cache|secret|development' `
            -Because "the $($forbidden.Name) artifact must never enter a release manifest"
    }

    $emptyModelDirectory = Copy-Package -SourcePath $cleanPackage -Name 'forbidden-empty-model-directory'
    [System.IO.Directory]::CreateDirectory((Join-Path $emptyModelDirectory 'models')) | Out-Null
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $emptyModelDirectory | Out-Null } `
        -Pattern 'forbidden|model' `
        -Because 'an empty model directory must not evade the recursive release-content policy'

    $reparseBeforePolicyFixture = Copy-Package `
        -SourcePath $cleanPackage `
        -Name 'reparse-before-native-policy'
    $reparseBeforePolicyTarget = Join-Path $testRoot 'reparse-before-native-policy-target'
    [System.IO.Directory]::CreateDirectory($reparseBeforePolicyTarget) | Out-Null
    $reparseBeforePolicyPath = Join-Path $reparseBeforePolicyFixture 'linked'
    New-Item `
        -ItemType Junction `
        -Path $reparseBeforePolicyPath `
        -Target $reparseBeforePolicyTarget | Out-Null
    Write-Utf8NoBom `
        -Path (Join-Path $reparseBeforePolicyFixture 'release-native-paths.json') `
        -Content 'not valid JSON'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $reparseBeforePolicyFixture | Out-Null } `
        -Pattern 'reparse' `
        -Because 'reparse rejection must occur before any package payload, including native policy, is parsed'
    Remove-Item -Force -LiteralPath $reparseBeforePolicyPath

    $reparseFixture = Copy-Package -SourcePath $cleanPackage -Name 'reparse'
    $reparseTarget = Join-Path $testRoot 'reparse-target'
    [System.IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    $reparsePath = Join-Path $reparseFixture 'linked'
    New-Item -ItemType Junction -Path $reparsePath -Target $reparseTarget | Out-Null
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $reparseFixture | Out-Null } `
        -Pattern 'reparse' `
        -Because 'every package reparse point must be rejected before traversal'
    Remove-Item -Force -LiteralPath $reparsePath

    $payloadCorruption = Copy-Package -SourcePath $cleanPackage -Name 'payload-only-corruption'
    $payloadManifestPath = Join-Path $payloadCorruption 'release-manifest.json'
    $payloadDigestPath = Join-Path $payloadCorruption 'release-manifest.sha256'
    $manifestHashBefore = Get-Sha256 -Path $payloadManifestPath
    $digestHashBefore = Get-Sha256 -Path $payloadDigestPath
    Add-Content -LiteralPath (Join-Path $payloadCorruption 'PhotoSift.App.exe') -Value 'corrupt'
    $payloadResult = Invoke-ReleaseChecker -PackagePath $payloadCorruption -Name 'payload-only-corruption'
    Assert-True ($payloadResult.ExitCode -ne 0) 'ordinary payload-only corruption must fail supported verification'
    Assert-Equal $manifestHashBefore (Get-Sha256 -Path $payloadManifestPath) 'verification must never regenerate the manifest from corrupt payload'
    Assert-Equal $digestHashBefore (Get-Sha256 -Path $payloadDigestPath) 'verification must never regenerate the digest from corrupt payload'
    $script:passed++

    $manifestCorruption = Copy-Package -SourcePath $cleanPackage -Name 'manifest-only-corruption'
    $manifestOnlyPath = Join-Path $manifestCorruption 'release-manifest.json'
    $manifestOnlyDigestPath = Join-Path $manifestCorruption 'release-manifest.sha256'
    $manifestOnlyBytes = [System.IO.File]::ReadAllBytes($manifestOnlyPath)
    $manifestOnlyBytes[0] = $manifestOnlyBytes[0] -bxor 1
    [System.IO.File]::WriteAllBytes($manifestOnlyPath, $manifestOnlyBytes)
    $manifestCorruptHashBefore = Get-Sha256 -Path $manifestOnlyPath
    $manifestDigestHashBefore = Get-Sha256 -Path $manifestOnlyDigestPath
    $manifestResult = Invoke-ReleaseChecker -PackagePath $manifestCorruption -Name 'manifest-only-corruption'
    Assert-True ($manifestResult.ExitCode -ne 0) 'manifest-only corruption must fail detached-digest verification'
    Assert-Equal $manifestCorruptHashBefore (Get-Sha256 -Path $manifestOnlyPath) 'verification must not rewrite a corrupt manifest'
    Assert-Equal $manifestDigestHashBefore (Get-Sha256 -Path $manifestOnlyDigestPath) 'verification must not rewrite its detached digest'
    $script:passed++

    $launchFixture = Copy-Package -SourcePath $cleanPackage -Name 'launch'
    [System.IO.File]::Copy(
        (Join-Path $env:SystemRoot 'System32\cmd.exe'),
        (Join-Path $launchFixture 'PhotoSift.App.exe'),
        $true)
    New-PhotoSiftReleaseManifest `
        -PackagePath $launchFixture `
        -OutputPath (Join-Path $launchFixture 'release-manifest.json') | Out-Null
    $markerPath = Join-Path $testRoot 'application-launched.txt'
    Add-Content -LiteralPath (Join-Path $launchFixture 'runtime\PhotoSift.Managed.dll') -Value 'corrupt'
    $blockedLaunch = Invoke-PackageVerifier `
        -PackagePath $launchFixture `
        -Name 'blocked-launch' `
        -ApplicationArguments @('/d', '/c', ('"echo launched>' + $markerPath + ' & exit /b 7"'))
    Assert-True ($blockedLaunch.ExitCode -ne 0) 'the package verifier must reject corruption'
    Assert-True (-not [System.IO.File]::Exists($markerPath)) 'the application must not start before complete verification'
    $script:passed++

    Set-Content -NoNewline -LiteralPath (Join-Path $launchFixture 'runtime\PhotoSift.Managed.dll') -Value 'managed fixture'
    New-PhotoSiftReleaseManifest `
        -PackagePath $launchFixture `
        -OutputPath (Join-Path $launchFixture 'release-manifest.json') | Out-Null
    $successfulLaunch = Invoke-PackageVerifier `
        -PackagePath $launchFixture `
        -Name 'successful-launch' `
        -ApplicationArguments @('/d', '/c', ('"echo launched>' + $markerPath + ' & echo benign 1>&2 & exit /b 7"'))
    Assert-Equal 7 $successfulLaunch.ExitCode 'the verifier must propagate the verified application exit code despite native stderr'
    Assert-True ([System.IO.File]::Exists($markerPath)) 'the application must start after complete verification'
    $script:passed++

    $launcherResult = Invoke-PackageLauncher `
        -PackagePath $launchFixture `
        -Name 'supported-launcher' `
        -ApplicationArguments @('/d', '/c', '"exit /b 9"')
    Assert-Equal 9 $launcherResult.ExitCode 'the supported batch launcher must forward arguments and propagate the verified application exit code'
    $script:passed++

    $syntheticRepository = New-SyntheticReleaseRepository -Name 'builder-repository'
    $syntheticPackages = New-SyntheticPackagesRoot -Name 'builder-packages'
    $syntheticSdk = Join-Path $testRoot 'builder-sdk'
    [System.IO.Directory]::CreateDirectory($syntheticSdk) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $syntheticSdk 'sdk\10.0.400')) | Out-Null
    Write-Utf8NoBom -Path (Join-Path $syntheticSdk 'dotnet.exe') -Content 'never executed synthetic SDK'

    $wrongSdkOutput = Join-Path $testRoot 'wrong-sdk-output'
    $wrongSdkCalls = [System.Collections.Generic.List[object]]::new()
    $wrongSdkOperations = New-SyntheticReleaseOperations `
        -Calls $wrongSdkCalls `
        -DriftSecondPublish $false `
        -SelectedSdkVersion '10.0.401'
    Assert-ThrowsLike `
        -Action {
            & $builderPath `
                -RepositoryRoot $syntheticRepository `
                -OutputRoot $wrongSdkOutput `
                -SdkRoot $syntheticSdk `
                -PackagesRoot $syntheticPackages `
                -SourceIdentity 'synthetic-source-identity' `
                -CaptureOnly `
                -Operations $wrongSdkOperations | Out-Null
        } `
        -Pattern 'SDK|10\.0\.400|selected|version' `
        -Because 'the supplied dotnet host must report exactly SDK 10.0.400 before any restore or publish'
    Assert-Equal 'sdk-version' `
        ($wrongSdkCalls.Name -join ',') `
        'a wrong selected SDK must fail immediately after the version probe and before restore or publish'
    $rootGlobalJson = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'global.json') |
        ConvertFrom-Json
    Assert-Equal 'disable' `
        ([string]$rootGlobalJson.sdk.rollForward) `
        'the release workflow must disable SDK roll-forward in the repository pin'
    $script:passed++

    $captureRepository = New-SyntheticReleaseRepository -Name 'capture-repository'
    Remove-Item -Force -LiteralPath (Join-Path $captureRepository 'THIRD-PARTY-NOTICES.md')
    Remove-Item -Recurse -Force -LiteralPath (Join-Path $captureRepository 'eng\release\licenses')
    $captureOutput = Join-Path $testRoot 'capture-output'
    $captureCalls = [System.Collections.Generic.List[object]]::new()
    $captureOperations = New-SyntheticReleaseOperations `
        -Calls $captureCalls `
        -DriftSecondPublish $false
    $captureResult = & $builderPath `
        -RepositoryRoot $captureRepository `
        -OutputRoot $captureOutput `
        -SdkRoot $syntheticSdk `
        -PackagesRoot $syntheticPackages `
        -SourceIdentity 'synthetic-source-identity' `
        -CaptureOnly `
        -Operations $captureOperations
    Assert-Equal 'needs-license-inventory' $captureResult.status 'capture-only mode must stop before candidate packaging'
    $captureEvidence = Get-Content -Raw -LiteralPath (
        Join-Path $captureOutput 'evidence\capture-evidence.json') | ConvertFrom-Json
    Assert-Equal '10.0.400' `
        ([string]$captureEvidence.sdkVersion) `
        'capture evidence must record the SDK version observed from the supplied dotnet host'
    Assert-True `
        ([System.IO.Directory]::Exists((Join-Path $captureOutput 'provisional-publish'))) `
        'capture-only mode must retain one raw deterministic publish for exact inventory analysis'
    Assert-True `
        (-not [System.IO.Directory]::Exists((Join-Path $captureOutput 'candidate'))) `
        'capture-only mode must not label an unlicensed folder as a release candidate'
    $dependencyCapture = Get-Content -Raw -LiteralPath (
        Join-Path $captureOutput 'evidence\dependency-capture.json') | ConvertFrom-Json
    Assert-Equal 1 $dependencyCapture.schemaVersion 'dependency capture schema must be versioned'
    Assert-Equal `
        '.NET Host,.NET Runtime,Microsoft.Data.Sqlite,Microsoft.Data.Sqlite.Core,Native SQLite,SQLitePCLRaw.bundle_e_sqlite3,SQLitePCLRaw.core,SQLitePCLRaw.lib.e_sqlite3,SQLitePCLRaw.provider.e_sqlite3,Windows Desktop / WPF' `
        (($dependencyCapture.components | ForEach-Object { $_.name }) -join ',') `
        'capture-only mode must enumerate every required shipped/runtime component'
    $nativeSqliteCapture = @($dependencyCapture.components | Where-Object {
            $_.name -ceq 'Native SQLite'
        })[0]
    Assert-Equal '3.49.1' $nativeSqliteCapture.version 'native SQLite version must come from the shipped binary evidence'
    Assert-Equal 'e_sqlite3.dll' ($nativeSqliteCapture.shippedPaths -join ',') 'native SQLite must map to its exact shipped path'
    Assert-True `
        (@($dependencyCapture.packages).Count -eq 9) `
        'capture-only mode must retain exact local metadata for both runtime packs, host pack, and all SQLite NuGet packages'
    Assert-True `
        ([System.IO.File]::Exists((Join-Path $captureOutput 'evidence\license-sources\microsoft.netcore.app.runtime.win-x64\10.0.11\LICENSE.txt'))) `
        'capture-only mode must preserve local package license sources for review without downloading anything'
    $script:passed++

    foreach ($captureAuditCase in @(
            @{
                Name = 'capture-forbidden-artifact'
                Pattern = 'forbidden|test|artifact'
                Mutate = {
                    param($OutputPath, $PublishIndex)
                    Write-Utf8NoBom `
                        -Path (Join-Path $OutputPath 'PhotoSift.App.Tests.dll') `
                        -Content 'forbidden test artifact'
                }
            },
            @{
                Name = 'capture-development-build-script'
                Pattern = 'development|tooling|build\.ps1'
                Mutate = {
                    param($OutputPath, $PublishIndex)
                    $developmentPath = Join-Path $OutputPath 'nested\release\build.ps1'
                    [System.IO.Directory]::CreateDirectory(
                        [System.IO.Path]::GetDirectoryName($developmentPath)) | Out-Null
                    Write-Utf8NoBom `
                        -Path $developmentPath `
                        -Content 'forbidden generic development script'
                }
            },
            @{
                Name = 'capture-development-project'
                Pattern = 'development|tooling|csproj'
                Mutate = {
                    param($OutputPath, $PublishIndex)
                    $projectPath = Join-Path $OutputPath 'nested\source\Unexpected.csproj'
                    [System.IO.Directory]::CreateDirectory(
                        [System.IO.Path]::GetDirectoryName($projectPath)) | Out-Null
                    Write-Utf8NoBom `
                        -Path $projectPath `
                        -Content '<Project />'
                }
            },
            @{
                Name = 'capture-empty-model-directory'
                Pattern = 'model|forbidden|directory'
                Mutate = {
                    param($OutputPath, $PublishIndex)
                    [System.IO.Directory]::CreateDirectory(
                        (Join-Path $OutputPath 'nested\models')) | Out-Null
                }
            },
            @{
                Name = 'capture-final-metadata'
                Pattern = 'manifest|metadata|capture'
                Mutate = {
                    param($OutputPath, $PublishIndex)
                    Write-Utf8NoBom `
                        -Path (Join-Path $OutputPath 'release-manifest.json') `
                        -Content '{"forbidden":"preexisting final metadata"}'
                    Write-Utf8NoBom `
                        -Path (Join-Path $OutputPath 'release-manifest.sha256') `
                        -Content ('0' * 64)
                }
            },
            @{
                Name = 'capture-preexisting-candidate'
                Pattern = 'candidate|capture'
                Mutate = {
                    param($OutputPath, $PublishIndex)
                    $candidatePath = Join-Path (Split-Path -Parent $OutputPath) 'candidate'
                    [System.IO.Directory]::CreateDirectory($candidatePath) | Out-Null
                    Write-Utf8NoBom `
                        -Path (Join-Path $candidatePath 'unexpected.txt') `
                        -Content 'capture-only must not retain a candidate'
                }
            }
        )) {
        $auditOutput = Join-Path $testRoot ($captureAuditCase.Name + '-output')
        $auditCalls = [System.Collections.Generic.List[object]]::new()
        $mutator = $captureAuditCase.Mutate.GetNewClosure()
        $auditOperations = New-SyntheticReleaseOperations `
            -Calls $auditCalls `
            -DriftSecondPublish $false `
            -AfterPublish $mutator
        Assert-ThrowsLike `
            -Action {
                & $builderPath `
                    -RepositoryRoot $captureRepository `
                    -OutputRoot $auditOutput `
                    -SdkRoot $syntheticSdk `
                    -PackagesRoot $syntheticPackages `
                    -SourceIdentity 'synthetic-source-identity' `
                    -CaptureOnly `
                    -Operations $auditOperations | Out-Null
            } `
            -Pattern $captureAuditCase.Pattern `
            -Because "capture-only must audit and reject $($captureAuditCase.Name) before preserving a provisional publish"
        Assert-True `
            (-not [System.IO.Directory]::Exists((Join-Path $auditOutput 'provisional-publish'))) `
            "a failed $($captureAuditCase.Name) audit must not preserve a provisional publish"
    }

    $collisionOutput = Join-Path $testRoot 'capture-case-collision-output'
    $collisionCalls = [System.Collections.Generic.List[object]]::new()
    $collisionOperations = New-SyntheticReleaseOperations `
        -Calls $collisionCalls `
        -DriftSecondPublish $false `
        -InjectCaseCollision $true
    Assert-ThrowsLike `
        -Action {
            & $builderPath `
                -RepositoryRoot $captureRepository `
                -OutputRoot $collisionOutput `
                -SdkRoot $syntheticSdk `
                -PackagesRoot $syntheticPackages `
                -SourceIdentity 'synthetic-source-identity' `
                -CaptureOnly `
                -Operations $collisionOperations | Out-Null
        } `
        -Pattern 'duplicate|collision|canonical' `
        -Because 'capture-only must reject ordinal-ignore-case raw publish path collisions'

    $reparseTarget = Join-Path $testRoot 'capture-reparse-target'
    [System.IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    $reparseOutput = Join-Path $testRoot 'capture-reparse-output'
    $reparseCalls = [System.Collections.Generic.List[object]]::new()
    $reparseMutator = {
        param($OutputPath, $PublishIndex)
        New-Item `
            -ItemType Junction `
            -Path (Join-Path $OutputPath 'linked') `
            -Target $reparseTarget | Out-Null
    }.GetNewClosure()
    $reparseOperations = New-SyntheticReleaseOperations `
        -Calls $reparseCalls `
        -DriftSecondPublish $false `
        -AfterPublish $reparseMutator
    Assert-ThrowsLike `
        -Action {
            & $builderPath `
                -RepositoryRoot $captureRepository `
                -OutputRoot $reparseOutput `
                -SdkRoot $syntheticSdk `
                -PackagesRoot $syntheticPackages `
                -SourceIdentity 'synthetic-source-identity' `
                -CaptureOnly `
                -Operations $reparseOperations | Out-Null
        } `
        -Pattern 'reparse' `
        -Because 'capture-only must reject raw publish reparse points before traversal or preservation'
    foreach ($rawIndex in 1..2) {
        $linkedPath = Join-Path $reparseOutput ("publish-$rawIndex-raw\linked")
        if ([System.IO.Directory]::Exists($linkedPath)) {
            Remove-Item -Force -LiteralPath $linkedPath
        }
    }
    $script:passed++

    foreach ($runtimeListMismatch in @(
            @{
                Name = 'wrong-runtime-framework'
                Attribute = 'FrameworkName="Microsoft.NETCore.App"'
                Replacement = 'FrameworkName="Contoso.NotNetCore"'
            },
            @{
                Name = 'wrong-runtime-identifier'
                Attribute = 'TargetFrameworkVersion="10.0"'
                Replacement = 'TargetFrameworkVersion="10.0" RuntimeIdentifier="linux-x64"'
            }
        )) {
        $mismatchedPackages = Join-Path $testRoot $runtimeListMismatch.Name
        Copy-Item -Recurse -LiteralPath $syntheticPackages -Destination $mismatchedPackages
        $mismatchedRuntimeListPath = Join-Path $mismatchedPackages `
            'microsoft.netcore.app.runtime.win-x64\10.0.11\data\RuntimeList.xml'
        $mismatchedRuntimeList = Get-Content -Raw -LiteralPath $mismatchedRuntimeListPath
        Write-Utf8NoBom `
            -Path $mismatchedRuntimeListPath `
            -Content $mismatchedRuntimeList.Replace(
                $runtimeListMismatch.Attribute,
                $runtimeListMismatch.Replacement)
        $mismatchCalls = [System.Collections.Generic.List[object]]::new()
        $mismatchOperations = New-SyntheticReleaseOperations `
            -Calls $mismatchCalls `
            -DriftSecondPublish $false
        Assert-ThrowsLike `
            -Action {
                & $builderPath `
                    -RepositoryRoot $captureRepository `
                    -OutputRoot (Join-Path $testRoot ($runtimeListMismatch.Name + '-output')) `
                    -SdkRoot $syntheticSdk `
                    -PackagesRoot $mismatchedPackages `
                    -SourceIdentity 'synthetic-source-identity' `
                    -CaptureOnly `
                    -Operations $mismatchOperations | Out-Null
            } `
            -Pattern 'runtime pack list does not match' `
            -Because "the builder must reject a RuntimeList.xml with $($runtimeListMismatch.Name)"
    }
    $script:passed++

    $unapprovedDependencyOutput = Join-Path $testRoot 'unapproved-dependency-output'
    $unapprovedDependencyCalls = [System.Collections.Generic.List[object]]::new()
    $unapprovedDependencyOperations = New-SyntheticReleaseOperations `
        -Calls $unapprovedDependencyCalls `
        -DriftSecondPublish $false `
        -ExtraRestoredPackage 'Contoso.LocalHelper/1.0.0'
    Assert-ThrowsLike `
        -Action {
            & $builderPath `
                -RepositoryRoot $captureRepository `
                -OutputRoot $unapprovedDependencyOutput `
                -SdkRoot $syntheticSdk `
                -PackagesRoot $syntheticPackages `
                -SourceIdentity 'synthetic-source-identity' `
                -CaptureOnly `
                -Operations $unapprovedDependencyOperations | Out-Null
        } `
        -Pattern 'approved|dependency|package|Contoso.LocalHelper' `
        -Because 'self-contained restore must retain the E5-S3 exact approved production package closure'
    $script:passed++

    $builderOutput = Join-Path $testRoot 'builder-output'
    $builderCalls = [System.Collections.Generic.List[object]]::new()
    $builderOperations = New-SyntheticReleaseOperations `
        -Calls $builderCalls `
        -DriftSecondPublish $false
    $buildResult = & $builderPath `
        -RepositoryRoot $syntheticRepository `
        -OutputRoot $builderOutput `
        -SdkRoot $syntheticSdk `
        -PackagesRoot $syntheticPackages `
        -SourceIdentity 'synthetic-source-identity' `
        -Operations $builderOperations
    Assert-Equal 5 $builderCalls.Count 'the builder must probe the SDK and perform one locked restore for each of two clean publishes'
    Assert-Equal 'publish-1,publish-2,restore-1,restore-2,sdk-version' `
        (($builderCalls | ForEach-Object { $_.Name } | Sort-Object) -join ',') `
        'the independent build workflow must contain one SDK probe, two restores, and two publishes'
    Assert-Equal 'sdk-version' $builderCalls[0].Name 'the exact SDK selection must be observed before any restore or publish'
    Assert-Equal '--version' ($builderCalls[0].Arguments -join ',') 'the SDK probe must request only dotnet --version'
    $buildCalls = @($builderCalls | Where-Object { $_.Name -like 'restore-*' -or $_.Name -like 'publish-*' })
    $buildWorkingRoots = @($buildCalls | ForEach-Object { $_.WorkingDirectory } | Sort-Object -Unique)
    Assert-Equal 2 $buildWorkingRoots.Count 'the two clean builds must use distinct source and intermediate roots'
    foreach ($buildWorkingRoot in $buildWorkingRoots) {
        Assert-Equal 1 `
            @($buildCalls | Where-Object {
                    $_.Name -like 'restore-*' -and $_.WorkingDirectory -ceq $buildWorkingRoot
                }).Count `
            'each clean working root must receive exactly one locked restore'
        Assert-Equal 1 `
            @($buildCalls | Where-Object {
                    $_.Name -like 'publish-*' -and $_.WorkingDirectory -ceq $buildWorkingRoot
                }).Count `
            'each clean working root must receive exactly one matching no-restore publish'
    }
    Assert-True `
        (-not [System.IO.Directory]::Exists((Join-Path $builderOutput '.work'))) `
        'transient independent source/intermediate roots must not remain in final build evidence'
    foreach ($publishCall in @($builderCalls | Where-Object { $_.Name -like 'publish-*' })) {
        $arguments = $publishCall.Arguments -join ' '
        foreach ($requiredArgument in @(
                'publish',
                '-c Release',
                '-r win-x64',
                '--self-contained true',
                '--no-restore',
                '-p:PublishSingleFile=false',
                '-p:RuntimeFrameworkVersion=10.0.11',
                '-p:TargetLatestRuntimePatch=false',
                '-p:DebugType=None',
                '-p:DebugSymbols=false')) {
            Assert-True `
                ($arguments.Contains($requiredArgument)) `
                "publish invocation must include '$requiredArgument'"
        }
    }
    foreach ($restoreCall in @($builderCalls | Where-Object { $_.Name -like 'restore-*' })) {
        $restoreArguments = $restoreCall.Arguments -join ' '
        foreach ($requiredArgument in @(
                'restore',
                '-r win-x64',
                '--locked-mode',
                '--configfile',
                '-p:RuntimeFrameworkVersion=10.0.11',
                '-p:TargetLatestRuntimePatch=false')) {
            Assert-True `
                ($restoreArguments.Contains($requiredArgument)) `
                "each restore invocation must include '$requiredArgument'"
        }
    }
    $candidatePath = Join-Path $builderOutput 'candidate'
    Assert-True ([System.IO.Directory]::Exists($candidatePath)) 'candidate must be copied only after two equal package inventories'
    Test-PhotoSiftRelease -PackagePath $candidatePath | Out-Null
    $candidateNativePolicy = Get-Content -Raw -LiteralPath (
        Join-Path $candidatePath 'release-native-paths.json') | ConvertFrom-Json
    Assert-Equal `
        'D3DCompiler_47_cor3.dll,coreclr.dll,e_sqlite3.dll,wpfgfx_cor3.dll' `
        ($candidateNativePolicy.nativePaths -join ',') `
        'the builder must derive the exact native closure from the pinned runtime packs plus SQLite'
    Assert-Equal $true $buildResult.inventoriesEqual 'the successful builder result must report exact tuple equality'
    Assert-Equal 'pending-owner-approval' $buildResult.status 'a provisional candidate must stop at the redistribution owner gate'
    Assert-Equal 'path,length,sha256' ($buildResult.comparedFields -join ',') 'determinism must be bounded to exact path, length, and SHA-256 tuples'
    $script:passed++

    $mismatchedRepository = Join-Path $testRoot 'mismatched-inventory-repository'
    Copy-Item -Recurse -LiteralPath $syntheticRepository -Destination $mismatchedRepository
    $mismatchedInventoryPath = Join-Path $mismatchedRepository 'eng\release\release-dependencies.json'
    $mismatchedInventory = Get-Content -Raw -LiteralPath $mismatchedInventoryPath | ConvertFrom-Json
    @($mismatchedInventory.components | Where-Object { $_.name -ceq '.NET Runtime' })[0].shippedPaths = @('coreclr.dll')
    Write-Utf8NoBom `
        -Path $mismatchedInventoryPath `
        -Content (($mismatchedInventory | ConvertTo-Json -Depth 8) + [System.Environment]::NewLine)
    $mismatchedOutput = Join-Path $testRoot 'mismatched-inventory-output'
    $mismatchedCalls = [System.Collections.Generic.List[object]]::new()
    $mismatchedOperations = New-SyntheticReleaseOperations `
        -Calls $mismatchedCalls `
        -DriftSecondPublish $false
    Assert-ThrowsLike `
        -Action {
            & $builderPath `
                -RepositoryRoot $mismatchedRepository `
                -OutputRoot $mismatchedOutput `
                -SdkRoot $syntheticSdk `
                -PackagesRoot $syntheticPackages `
                -SourceIdentity 'synthetic-source-identity' `
                -Operations $mismatchedOperations | Out-Null
        } `
        -Pattern 'inventory|shipped path|component' `
        -Because 'a dependency inventory that omits one actually shipped framework file must block candidate creation'
    Assert-True `
        (-not [System.IO.Directory]::Exists((Join-Path $mismatchedOutput 'candidate'))) `
        'an inventory mismatch must leave no provisional candidate'
    $script:passed++

    $driftOutput = Join-Path $testRoot 'builder-drift-output'
    $driftCalls = [System.Collections.Generic.List[object]]::new()
    $driftOperations = New-SyntheticReleaseOperations `
        -Calls $driftCalls `
        -DriftSecondPublish $true
    Assert-ThrowsLike `
        -Action {
            & $builderPath `
                -RepositoryRoot $syntheticRepository `
                -OutputRoot $driftOutput `
                -SdkRoot $syntheticSdk `
                -PackagesRoot $syntheticPackages `
                -SourceIdentity 'synthetic-source-identity' `
                -Operations $driftOperations | Out-Null
        } `
        -Pattern 'determin|differ|inventory' `
        -Because 'the candidate must not be created when the two publish tuple inventories differ'
    Assert-True `
        (-not [System.IO.Directory]::Exists((Join-Path $driftOutput 'candidate'))) `
        'a drifted second publish must leave no candidate directory'
    $script:passed++

    Write-Output "Passed: $script:passed Failed: 0"
}
finally {
    if ([System.IO.Directory]::Exists($testRoot)) {
        Remove-Item -Recurse -Force -LiteralPath $testRoot -ErrorAction SilentlyContinue
    }
}

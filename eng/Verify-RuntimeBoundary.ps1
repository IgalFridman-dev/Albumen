[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$RuntimeIdentifier = 'win-x64',

    [Parameter()]
    [string]$DependencyManifestPath,

    [Parameter()]
    [string]$SourceRootPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$forbiddenDependencyPatterns = @(
    'System\.Net\.Http',
    'WebView',
    'CefSharp',
    'Chromium',
    'Telemetry',
    'ApplicationInsights',
    'OpenTelemetry',
    'Sentry',
    'Updater?',
    'Authentication',
    'Identity',
    'Remote.?Config',
    'Downloader?',
    'ImageSharp',
    'SkiaSharp',
    'Magick',
    'Onnx',
    'TensorFlow')

$approvedPackages = [ordered]@{
    'Microsoft.Data.Sqlite' = [pscustomobject]@{
        Version = '10.0.11'
        Sha512 = '7je7UELzm131GiLYc4PpZvfKXIgIyzPM+v+tjcd/nbnuWRfgcONYKzDTqJlURxwVCFsVnlpmq6y6yn4qvR8QXQ=='
    }
    'Microsoft.Data.Sqlite.Core' = [pscustomobject]@{
        Version = '10.0.11'
        Sha512 = 'hubA20AGenQ4Sx0ElWaPpB8DISjXpdx463+1zOGRslsT0e/t/06ITv+pHsop8CcJ0d8PZLfgnT7juCDVD79Dkw=='
    }
    'SQLitePCLRaw.bundle_e_sqlite3' = [pscustomobject]@{
        Version = '2.1.12'
        Sha512 = 'mAgscpQMLw5/nfA1Q5oJVAT29yROUo1ifZGbbTpx/lwZpSxMUGoYbKfmvdm8oXER+RzxqBmmQzeBEVKfeHv2nw=='
    }
    'SQLitePCLRaw.core' = [pscustomobject]@{
        Version = '2.1.12'
        Sha512 = 'ETpNw9DY3ckWLgRRAeCHj+GKOuPi61aeczkXhgHexUvqoZBAYg8RYESE2J7O1M7+o6QbdSEZwrw9bfqztUVWXg=='
    }
    'SQLitePCLRaw.lib.e_sqlite3' = [pscustomobject]@{
        Version = '2.1.12'
        Sha512 = 'fWi8Dbknuhgg72fWinIdjXVaqO1hHL4YBBwVLnr7e1c9TAZwJ0QE38j9syW1hwx6HaqEVTwI+O07WPdZn8Rp0w=='
    }
    'SQLitePCLRaw.provider.e_sqlite3' = [pscustomobject]@{
        Version = '2.1.12'
        Sha512 = 'W3oH4XIfCzFrgUSDKHhN6N+dgzA5YHOR2VxX8GB6Qy7CyrJJgxPEG8NirgYWlPQC5P2jz2knSsexWu4tDUL33g=='
    }
}
$approvedPackageLibraries = @($approvedPackages.GetEnumerator() | ForEach-Object {
    "$($_.Key)/$($_.Value.Version)"
})
$sqliteDependencyPattern = '^(?:Microsoft\.Data\.Sqlite|SQLitePCL)'

function Get-JsonPropertyNames {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return @()
    }

    return @($Value.PSObject.Properties | ForEach-Object { $_.Name })
}

function Assert-NoForbiddenDependencies {
    param(
        [Parameter(Mandatory)]
        [string[]]$Names
    )

    $violations = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in $Names) {
        foreach ($pattern in $forbiddenDependencyPatterns) {
            if ($name -match $pattern) {
                $violations.Add($name)
                break
            }
        }
    }

    if ($violations.Count -gt 0) {
        $distinctViolations = @($violations | Sort-Object -Unique)
        throw "Forbidden runtime dependencies: $($distinctViolations -join ', ')"
    }
}

function Assert-OnlyApprovedSqliteLibraries {
    param(
        [Parameter(Mandatory)]
        [string[]]$Names
    )

    $sqliteLibraries = @($Names | Where-Object { $_ -match $sqliteDependencyPattern })
    $unexpected = @($sqliteLibraries | Where-Object { $approvedPackageLibraries -notcontains $_ })
    if ($unexpected.Count -gt 0) {
        throw "Unapproved SQLite runtime dependencies: $(@($unexpected | Sort-Object -Unique) -join ', ')"
    }
}

function Assert-NoForbiddenRuntimeArtifacts {
    param(
        [Parameter(Mandatory)]
        [string]$DirectoryPath
    )

    $resolvedDirectoryPath = [System.IO.Path]::GetFullPath($DirectoryPath)
    $directoryPrefix = $resolvedDirectoryPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -File -Force -Recurse -LiteralPath $resolvedDirectoryPath |
        Sort-Object FullName)
    $violations = New-Object 'System.Collections.Generic.List[string]'
    $testArtifactPattern = '(?i)(?:^testhost(?:\.|$)|\.Tests\.|^xunit\.|^Microsoft\.(?:TestPlatform|Testing|ApplicationInsights)\.|^(?:OpenTelemetry|Sentry|Datadog\.Trace|NewRelic|Elastic\.Apm)(?:\.|$)|(?:^|[._-])Tool(?:ing|s)?(?:[._-]|$))'
    $updaterArtifactPattern = '(?i)(?:^|[._-])(?:Updater?|UpdateService)(?:[._-]|$)'
    $modelArtifactPattern = '(?i)(?:\.(?:onnx|tflite|safetensors|gguf|pt|pth|pb)$|(?:^|[._-])(?:tokenizer|vocab|model|weights)(?:[._-]|$))'
    $configPattern = '(?i)(?:https?|wss?)://|(?:telemetry|updater?|remote.?config|download)(?:url|uri|endpoint)'
    $binaryPattern = '(?i)System\.Net\.(?:Http|Sockets)|HttpClient|WebView2?|ApplicationInsights|OpenTelemetry|Sentry|RemoteConfig|ModelDownloader'
    $binaryCount = 0
    $configCount = 0

    foreach ($file in $files) {
        if (-not $file.FullName.StartsWith($directoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime artifact escaped the inspected directory: $($file.Name)"
        }
        $relativePath = $file.FullName.Substring($directoryPrefix.Length)
        if ($file.Name -match $testArtifactPattern) {
            $violations.Add("$relativePath -> test/tooling/telemetry artifact")
        }
        if ($file.Name -match $updaterArtifactPattern) {
            $violations.Add("$relativePath -> updater artifact")
        }
        if ($file.Name -match $modelArtifactPattern) {
            $violations.Add("$relativePath -> model artifact")
        }

        if ($file.Extension -in @('.json', '.config', '.xml')) {
            $configCount++
            $content = Get-Content -Raw -LiteralPath $file.FullName
            if ($content -match $configPattern) {
                $violations.Add("$relativePath -> remote endpoint/config marker")
            }
        }

        if ($file.Extension -in @('.dll', '.exe')) {
            $binaryCount++
            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
            $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
            if ($ascii -match $binaryPattern) {
                $violations.Add("$relativePath -> forbidden binary capability marker")
            }
        }
    }

    if ($violations.Count -gt 0) {
        $distinctViolations = @($violations | Sort-Object -Unique)
        throw "Forbidden runtime artifacts: $($distinctViolations -join ', ')"
    }

    return [pscustomobject]@{
        Files = @($files | ForEach-Object {
            $_.FullName.Substring($directoryPrefix.Length)
        })
        BinaryCount = $binaryCount
        ConfigCount = $configCount
    }
}

function Assert-NoForbiddenProductionApis {
    param(
        [Parameter(Mandatory)]
        [string]$RootPath
    )

    $resolvedRootPath = [System.IO.Path]::GetFullPath($RootPath)
    if (-not [System.IO.Directory]::Exists($resolvedRootPath)) {
        throw "Production source root does not exist: $resolvedRootPath"
    }

    $sourceFiles = @(
        Get-ChildItem -Recurse -Force -File -Filter '*.cs' -LiteralPath $resolvedRootPath |
            Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
            Sort-Object FullName)
    if ($sourceFiles.Count -eq 0) {
        throw "Production source root contains no C# files: $resolvedRootPath"
    }

    $forbiddenApiPatterns = [ordered]@{
        'System.Net.*' = '(?:global::)?System\s*\.\s*Net\b|\b(?:HttpClient|HttpMessageInvoker|HttpWebRequest|WebRequest|WebClient|Socket|TcpClient|TcpListener|UdpClient)\b'
        'System.IO.File::Delete' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?File\s*\.\s*Delete\s*\('
        'System.IO.File::Move' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?File\s*\.\s*Move\s*\('
        'System.IO.File::Replace' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?File\s*\.\s*Replace\s*\('
        'System.IO.Directory::Delete' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?Directory\s*\.\s*Delete\s*\('
        'System.IO.Directory::Move' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?Directory\s*\.\s*Move\s*\('
        'System.IO.FileInfo mutation capability' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?FileInfo\b'
        'System.IO.DirectoryInfo mutation capability' = '(?:global::)?(?:System\s*\.\s*IO\s*\.\s*)?DirectoryInfo\b'
        'Microsoft.VisualBasic.FileIO.FileSystem mutation' = '(?:global::)?Microsoft\s*\.\s*VisualBasic\s*\.\s*FileIO\s*\.\s*FileSystem\s*\.\s*(?:DeleteDirectory|DeleteFile|MoveDirectory|MoveFile)\s*\('
    }

    $violations = New-Object 'System.Collections.Generic.List[string]'
    foreach ($sourceFile in $sourceFiles) {
        $source = Get-Content -Raw -LiteralPath $sourceFile.FullName
        $relativePath = $sourceFile.FullName.Substring($resolvedRootPath.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        foreach ($entry in $forbiddenApiPatterns.GetEnumerator()) {
            if ($source -match $entry.Value) {
                $violations.Add("$relativePath -> $($entry.Key)")
            }
        }

        $isWindowsInfrastructure = $relativePath -like 'PhotoSift.Infrastructure.Windows\*'
        $isApp = $relativePath -like 'PhotoSift.App\*'
        if ($source -match '\[\s*(?:(?:global\s*::\s*)?System\s*\.\s*Runtime\s*\.\s*InteropServices\s*\.\s*)?(?:LibraryImport|DllImport)(?:Attribute)?\b' -and
            -not $isWindowsInfrastructure) {
            $violations.Add("$relativePath -> native interop outside Windows infrastructure")
        }
        if ($source -match '\b(?:Directory\.EnumerateFileSystemEntries|File\.OpenHandle|SafeFileHandle)\b' -and
            -not $isWindowsInfrastructure) {
            $violations.Add("$relativePath -> direct filesystem inspection outside Windows infrastructure")
        }
        if ($source -match '\bOpenFolderDialog\b' -and -not $isApp) {
            $violations.Add("$relativePath -> folder picker outside App")
        }
    }

    if ($violations.Count -gt 0) {
        $distinctViolations = @($violations | Sort-Object -Unique)
        foreach ($violation in $distinctViolations) {
            [Console]::Error.WriteLine($violation)
        }
        throw "Forbidden production source APIs: $($distinctViolations -join ', ')"
    }

    return $sourceFiles.Count
}

if ([string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    $manifestPath = Join-Path $repositoryRoot (
        'src\PhotoSift.App\bin\{0}\net10.0-windows\{1}\PhotoSift.App.deps.json' -f `
            $Configuration,
            $RuntimeIdentifier)
}
else {
    $manifestPath = [System.IO.Path]::GetFullPath($DependencyManifestPath)
}

if (-not [System.IO.File]::Exists($manifestPath)) {
    throw "Dependency manifest does not exist: $manifestPath"
}

$dependencyManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$runtimeLibraries = Get-JsonPropertyNames -Value $dependencyManifest.libraries
Assert-NoForbiddenDependencies -Names $runtimeLibraries
Assert-OnlyApprovedSqliteLibraries -Names $runtimeLibraries
$outputDirectory = Split-Path -Parent $manifestPath
$artifactInspection = Assert-NoForbiddenRuntimeArtifacts -DirectoryPath $outputDirectory

if ([string]::IsNullOrWhiteSpace($SourceRootPath)) {
    $resolvedSourceRootPath = Join-Path $repositoryRoot 'src'
}
else {
    $resolvedSourceRootPath = [System.IO.Path]::GetFullPath($SourceRootPath)
}
$sourceFileCount = Assert-NoForbiddenProductionApis -RootPath $resolvedSourceRootPath

if (-not [string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    Write-Output "PHOTOSIFT_RUNTIME_BOUNDARY_OK manifest=$manifestPath"
    Write-Output "SourceFilesScanned=$sourceFileCount"
    Write-Output "RuntimeFilesScanned=$($artifactInspection.Files.Count)"
    Write-Output "RuntimeBinariesScanned=$($artifactInspection.BinaryCount)"
    Write-Output "RuntimeConfigsScanned=$($artifactInspection.ConfigCount)"
    Write-Output 'ForbiddenRuntimeArtifacts=0'
    Write-Output 'ForbiddenApiCalls=0'
    return
}

$expectedProjects = @(
    'PhotoSift.Analysis',
    'PhotoSift.App',
    'PhotoSift.Application',
    'PhotoSift.Domain',
    'PhotoSift.Infrastructure.Sqlite',
    'PhotoSift.Infrastructure.Windows')
$projectPaths = @(Get-ChildItem -Recurse -File -Filter '*.csproj' -LiteralPath (
    Join-Path $repositoryRoot 'src'))
$projectNames = @($projectPaths.BaseName | Sort-Object)
if ([string]::Join('|', $projectNames) -ne [string]::Join('|', $expectedProjects)) {
    throw "Production projects differ from the E1-S1 boundary: $($projectNames -join ', ')"
}

$packageLibraries = New-Object 'System.Collections.Generic.List[string]'
$directPackageReferences = New-Object 'System.Collections.Generic.List[string]'
$projectEdges = New-Object 'System.Collections.Generic.List[string]'
foreach ($projectPath in $projectPaths) {
    [xml]$project = Get-Content -Raw -LiteralPath $projectPath.FullName
    foreach ($packageReference in @($project.SelectNodes('//PackageReference'))) {
        $packageName = [string]$packageReference.Include
        $directPackageReferences.Add("$($projectPath.BaseName)=$packageName")
        if (-not [string]::IsNullOrWhiteSpace($packageReference.GetAttribute('Version'))) {
            throw "Production PackageReference must use the central version: $($projectPath.BaseName)=$packageName"
        }
    }

    foreach ($projectReference in @($project.SelectNodes('//ProjectReference'))) {
        $targetName = [System.IO.Path]::GetFileNameWithoutExtension(
            [string]$projectReference.Include)
        $projectEdges.Add("$($projectPath.BaseName)->$targetName")
    }

    $assetsPath = Join-Path $projectPath.DirectoryName 'obj\project.assets.json'
    if (-not [System.IO.File]::Exists($assetsPath)) {
        throw "Production assets file does not exist: $assetsPath"
    }

    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    foreach ($library in @($assets.libraries.PSObject.Properties)) {
        if ([string]$library.Value.type -eq 'package') {
            $packageLibraries.Add($library.Name)
            $separator = $library.Name.LastIndexOf('/')
            if ($separator -le 0) {
                throw "Malformed production package library: $($library.Name)"
            }
            $packageName = $library.Name.Substring(0, $separator)
            $packageVersion = $library.Name.Substring($separator + 1)
            if (-not $approvedPackages.Contains($packageName)) {
                throw "Production NuGet dependency is not approved: $($library.Name)"
            }
            $approved = $approvedPackages[$packageName]
            if ($packageVersion -cne $approved.Version -or
                [string]$library.Value.sha512 -cne $approved.Sha512) {
                throw "Production NuGet dependency version/hash differs from the lock: $($library.Name)"
            }
        }
    }
}

$expectedDirectPackageReferences = @('PhotoSift.Infrastructure.Sqlite=Microsoft.Data.Sqlite')
$actualDirectPackageReferences = @($directPackageReferences | Sort-Object -Unique)
if ([string]::Join('|', $actualDirectPackageReferences) -cne
    [string]::Join('|', $expectedDirectPackageReferences)) {
    throw "Production PackageReferences differ from the approved SQLite boundary: $($actualDirectPackageReferences -join ', ')"
}

[xml]$centralPackages = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props')
$sqliteCentralVersions = @($centralPackages.SelectNodes('//PackageVersion') | Where-Object {
    [string]$_.Include -match $sqliteDependencyPattern
})
if ($sqliteCentralVersions.Count -ne 1 -or
    [string]$sqliteCentralVersions[0].Include -cne 'Microsoft.Data.Sqlite' -or
    [string]$sqliteCentralVersions[0].Version -cne $approvedPackages['Microsoft.Data.Sqlite'].Version) {
    throw 'The central SQLite package surface must be exactly Microsoft.Data.Sqlite 10.0.11.'
}

$distinctPackageLibraries = @($packageLibraries | Sort-Object -Unique)
if ([string]::Join('|', $distinctPackageLibraries) -cne
    [string]::Join('|', @($approvedPackageLibraries | Sort-Object))) {
    throw "Production NuGet closure differs from the approved SQLite boundary: $($distinctPackageLibraries -join ', ')"
}

$expectedRuntimeLibraries = @(
    @($expectedProjects | ForEach-Object { "$_/1.0.0" })
    $approvedPackageLibraries
)
$unexpectedRuntimeLibraries = @(
    $runtimeLibraries | Where-Object { $expectedRuntimeLibraries -notcontains $_ })
if ($unexpectedRuntimeLibraries.Count -gt 0) {
    throw "Unexpected runtime libraries: $($unexpectedRuntimeLibraries -join ', ')"
}
$missingRuntimeLibraries = @(
    $expectedRuntimeLibraries | Where-Object { $runtimeLibraries -notcontains $_ })
if ($missingRuntimeLibraries.Count -gt 0) {
    throw "Missing runtime libraries: $($missingRuntimeLibraries -join ', ')"
}

$outputFiles = @($artifactInspection.Files)
Assert-NoForbiddenDependencies -Names $outputFiles
$requiredSqliteOutputFiles = @(
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.batteries_v2.dll',
    'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll',
    'e_sqlite3.dll')
$sqliteOutputFiles = @($outputFiles | Where-Object {
    $_ -match '^(?:Microsoft\.Data\.Sqlite|SQLitePCLRaw|e_sqlite3)'
})
if ([string]::Join('|', $sqliteOutputFiles) -cne
    [string]::Join('|', @($requiredSqliteOutputFiles | Sort-Object))) {
    throw "SQLite runtime output differs from the approved win-x64 closure: $($sqliteOutputFiles -join ', ')"
}
$nativeSqlitePath = Join-Path $outputDirectory 'e_sqlite3.dll'
$nativeSqliteSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $nativeSqlitePath).Hash
if ($nativeSqliteSha256 -cne 'B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E') {
    throw "SQLite native runtime hash differs from the approved win-x64 closure: $nativeSqliteSha256"
}

Write-Output 'PHOTOSIFT_RUNTIME_BOUNDARY_OK'
Write-Output "TargetFramework=net10.0-windows"
Write-Output "RuntimeIdentifier=$RuntimeIdentifier"
Write-Output "ProductionProjects=$($projectNames -join ',')"
Write-Output "ProjectReferences=$(@($projectEdges | Sort-Object) -join ',')"
Write-Output "RuntimeLibraries=$($runtimeLibraries -join ',')"
Write-Output "SourceFilesScanned=$sourceFileCount"
Write-Output "ProductionPackages=$($distinctPackageLibraries -join ',')"
Write-Output "SQLiteNativeSha256=$nativeSqliteSha256"
Write-Output "OutputFiles=$($outputFiles -join ',')"
Write-Output "RuntimeBinariesScanned=$($artifactInspection.BinaryCount)"
Write-Output "RuntimeConfigsScanned=$($artifactInspection.ConfigCount)"
Write-Output 'ForbiddenDependencies=0'
Write-Output 'ForbiddenRuntimeArtifacts=0'
Write-Output 'ForbiddenApiCalls=0'

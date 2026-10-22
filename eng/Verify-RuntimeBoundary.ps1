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
    [string]$SourceRootPath,

    [Parameter()]
    [string]$FrameworkClosureManifestPath,

    [Parameter()]
    [switch]$FrameworkDependentRelease
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
    'TensorFlow')

$approvedPackages = [ordered]@{
    'Google.Protobuf' = [pscustomobject]@{
        Version = '3.30.2'
        Sha512 = 'Y2aOVLIt75yeeEWigg9V9YnjsEm53sADtLGq0gLhwaXpk3iu8tYSoauolyhenagA2sWno2TQ2WujI0HQd6s1Vw=='
    }
    'Microsoft.Data.Sqlite' = [pscustomobject]@{
        Version = '10.0.11'
        Sha512 = '7je7UELzm131GiLYc4PpZvfKXIgIyzPM+v+tjcd/nbnuWRfgcONYKzDTqJlURxwVCFsVnlpmq6y6yn4qvR8QXQ=='
    }
    'Microsoft.Data.Sqlite.Core' = [pscustomobject]@{
        Version = '10.0.11'
        Sha512 = 'hubA20AGenQ4Sx0ElWaPpB8DISjXpdx463+1zOGRslsT0e/t/06ITv+pHsop8CcJ0d8PZLfgnT7juCDVD79Dkw=='
    }
    'Microsoft.ML.OnnxRuntime' = [pscustomobject]@{
        Version = '1.31.0'
        Sha512 = '7v58S0xqY1ZmLui5BnK3FQEWagRhmhkR/9yLrxF5rQGppGfcMaw7W0a2wgT1SWnT6ug0dmr1B2I4VWhlY/yvng=='
    }
    'Microsoft.ML.OnnxRuntime.Managed' = [pscustomobject]@{
        Version = '1.31.0'
        Sha512 = 'o9nvEiddLLlAMOHN0XZ/06Oal0W6S7iWiSb8lzQAwfQKNOZtX4C8he5/I7Mc/DlSprNfovAW6kCkwX2BAE0JHw=='
    }
    'Microsoft.ML.Tokenizers' = [pscustomobject]@{
        Version = '2.0.1'
        Sha512 = 'RZN3zslVWinBCMDAnbZWEY8lQbMkP54Yxm1s5NigPcrvYFzXMrDjNcVM9afq4SZHf1BV1rEmO0p7ZdfBLDbu4g=='
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
    'System.Numerics.Tensors' = [pscustomobject]@{
        Version = '9.0.0'
        Sha512 = 'hyJB4UlpAi19Xr9AXzu2NuagKC4lPfHObNMEAA0HmqFz2rX7wKgzeYzO/jM/eBHDhnUGFFEjk5cOoJaxqg5J4A=='
    }
}
$approvedPackageLibraries = @($approvedPackages.GetEnumerator() | ForEach-Object {
    "$($_.Key)/$($_.Value.Version)"
})
$sqliteDependencyPattern = '^(?:Microsoft\.Data\.Sqlite|SQLitePCL)'
# The SigLIP 2 files listed in models/manifest.json; any other model-shaped file is still forbidden.
$approvedModelPaths = @('models/text.onnx', 'models/tokenizer.model', 'models/vision.onnx')
# Approved files whose names look like framework content but are not in the runtime packs:
# a package assembly, and the app-local Visual C++ runtime that onnxruntime.dll imports.
$approvedPackageRuntimePaths = @('System.Numerics.Tensors.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
# Microsoft.ML.Tokenizers 2.0.1 contains an HttpClient helper reachable only through URL-loading
# APIs Albumen never calls; Albumen loads its SentencePiece model from a local file. Pinned by hash.
$approvedNetworkMarkerBinaries = @{
    'Microsoft.ML.Tokenizers.dll' = '3130D6AB12517CE8B0BAE3C59CC6EDC8145B1BCC64221C06AD7D603A802EE5B5'
}

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

function Read-SelfContainedFrameworkClosure {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )

    $resolvedManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
    if (-not [System.IO.File]::Exists($resolvedManifestPath)) {
        throw "Framework closure manifest does not exist: $resolvedManifestPath"
    }

    $manifest = Get-Content -Raw -LiteralPath $resolvedManifestPath | ConvertFrom-Json
    $propertyNames = @(Get-JsonPropertyNames -Value $manifest | Sort-Object)
    $expectedPropertyNames = @(
        'frameworkPaths',
        'netCoreRuntimeVersion',
        'runtimeIdentifier',
        'schemaVersion',
        'windowsDesktopRuntimeVersion')
    if ([string]::Join('|', $propertyNames) -cne
        [string]::Join('|', $expectedPropertyNames)) {
        throw 'Framework closure manifest has unexpected or missing properties.'
    }
    if ($manifest.schemaVersion -ne 1 -or
        [string]$manifest.runtimeIdentifier -cne 'win-x64' -or
        [string]$manifest.netCoreRuntimeVersion -cne '10.0.11' -or
        [string]$manifest.windowsDesktopRuntimeVersion -cne '10.0.11') {
        throw 'Framework closure manifest does not identify the pinned win-x64 .NET/Windows Desktop 10.0.11 closure.'
    }
    if ($manifest.frameworkPaths -isnot [System.Array]) {
        throw 'Framework closure manifest frameworkPaths must be a JSON array.'
    }

    $paths = [string[]]@($manifest.frameworkPaths)
    $seen = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $paths) {
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path -cne $path.Trim() -or
            $path.Contains('\') -or
            [System.IO.Path]::IsPathRooted($path) -or
            @($path.Split('/') | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..')
                }).Count -gt 0) {
            throw "Framework closure contains a noncanonical path: $path"
        }
        if ($seen.ContainsKey($path)) {
            throw "Framework closure contains a duplicate or case-colliding path: $path"
        }
        $seen.Add($path, $true)
    }

    $sortedPaths = [string[]]@($paths)
    [System.Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
    if ([string]::Join('|', $paths) -cne [string]::Join('|', $sortedPaths)) {
        throw 'Framework closure paths are not in canonical ordinal order.'
    }

    $runtimeConfigPath = Join-Path $OutputDirectory 'Albumen.App.runtimeconfig.json'
    $appAssemblyPath = Join-Path $OutputDirectory 'Albumen.App.dll'
    $appHostPath = Join-Path $OutputDirectory 'Albumen.App.exe'
    if (-not [System.IO.File]::Exists($runtimeConfigPath)) {
        throw 'Self-contained folder publish is missing Albumen.App.runtimeconfig.json.'
    }
    if (-not [System.IO.File]::Exists($appAssemblyPath)) {
        throw 'Single-file-shaped output is not allowed; folder publish must contain Albumen.App.dll.'
    }
    if (-not [System.IO.File]::Exists($appHostPath)) {
        throw 'Self-contained folder publish is missing Albumen.App.exe.'
    }
    $runtimeConfig = Get-Content -Raw -LiteralPath $runtimeConfigPath | ConvertFrom-Json
    if ($null -eq $runtimeConfig.runtimeOptions -or
        [string]$runtimeConfig.runtimeOptions.tfm -cne 'net10.0') {
        throw 'Albumen.App.runtimeconfig.json runtime TFM must be exactly net10.0.'
    }
    $runtimeOptionProperties = @(Get-JsonPropertyNames -Value $runtimeConfig.runtimeOptions)
    if ($runtimeOptionProperties -contains 'framework' -or
        $runtimeOptionProperties -contains 'frameworks') {
        throw 'Framework-dependent runtimeconfig is not an accepted self-contained folder publish.'
    }
    if ($runtimeOptionProperties -notcontains 'includedFrameworks' -or
        $runtimeConfig.runtimeOptions.includedFrameworks -isnot [System.Array]) {
        throw 'Self-contained runtimeconfig must contain the exact included framework array.'
    }
    $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks |
        ForEach-Object {
            $properties = @(Get-JsonPropertyNames -Value $_ | Sort-Object)
            if ([string]::Join('|', $properties) -cne 'name|version' -or
                $_.name -isnot [string] -or $_.version -isnot [string]) {
                throw 'Self-contained runtimeconfig included framework entry is malformed.'
            }
            "$($_.name)/$($_.version)"
        } | Sort-Object)
    $expectedIncludedFrameworks = @(
        'Microsoft.NETCore.App/10.0.11',
        'Microsoft.WindowsDesktop.App/10.0.11')
    if ([string]::Join('|', $includedFrameworks) -cne
        [string]::Join('|', $expectedIncludedFrameworks)) {
        throw 'Self-contained runtimeconfig included frameworks must be exactly Microsoft.NETCore.App and Microsoft.WindowsDesktop.App 10.0.11.'
    }

    $resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    foreach ($path in $paths) {
        $literalPath = Join-Path $resolvedOutputDirectory $path.Replace(
            '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not [System.IO.File]::Exists($literalPath)) {
            throw "Pinned framework closure path is missing from the self-contained publish: $path"
        }
    }

    $frameworkShapedPattern = '(?i)^(?:System\.|Microsoft\.(?:CSharp|VisualBasic|Win32|WindowsDesktop)|Presentation|ReachFramework|UIAutomation|WindowsBase|coreclr|clrjit|hostfxr|hostpolicy|mscord|msquic|createdump|wpfgfx|PresentationNative|PenImc|vcruntime|D3DCompiler|DirectWriteForwarder)'
    $outputPrefix = $resolvedOutputDirectory.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in @(Get-ChildItem -File -Force -Recurse -LiteralPath $resolvedOutputDirectory)) {
        $relativePath = $file.FullName.Substring($outputPrefix.Length).Replace('\', '/')
        if ($file.Name -match $frameworkShapedPattern -and -not $seen.ContainsKey($relativePath) -and
            $approvedPackageRuntimePaths -cnotcontains $relativePath) {
            throw "Runtime artifact is outside the exact pinned framework closure: $relativePath"
        }
    }

    return $paths
}

function Assert-FrameworkDependentRelease {
    param([string]$OutputDirectory, [object]$DependencyManifest)

    if (-not [string]::IsNullOrWhiteSpace($FrameworkClosureManifestPath)) {
        throw 'Framework-dependent release cannot include a bundled framework closure.'
    }
    foreach ($name in @('Albumen.App.exe', 'Albumen.App.dll', 'Albumen.App.runtimeconfig.json')) {
        if (-not [System.IO.File]::Exists((Join-Path $OutputDirectory $name))) {
            throw "Framework-dependent folder release is missing $name."
        }
    }
    $config = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'Albumen.App.runtimeconfig.json') | ConvertFrom-Json
    $options = $config.runtimeOptions
    $properties = @(Get-JsonPropertyNames -Value $options)
    if ($null -eq $options -or [string]$options.tfm -cne 'net10.0' -or
        $properties -contains 'includedFrameworks' -or $properties -contains 'framework' -or
        $properties -notcontains 'frameworks' -or $options.frameworks -isnot [System.Array]) {
        throw 'Framework-dependent runtimeconfig must request the two net10.0 shared frameworks.'
    }
    foreach ($property in @('applyPatches', 'rollForwardOnNoCandidateFx')) {
        if ($properties -contains $property) { throw "Runtimeconfig must not override ordinary patch roll-forward: $property" }
    }
    if ($properties -contains 'rollForward' -and [string]$options.rollForward -cne 'LatestPatch') {
        throw 'Runtimeconfig rollForward must allow compatible 10.0 patches using LatestPatch.'
    }
    $frameworks = @($options.frameworks | ForEach-Object {
        if ([string]::Join('|', @(Get-JsonPropertyNames -Value $_ | Sort-Object)) -cne 'name|version') {
            throw 'Framework-dependent runtimeconfig framework entry is malformed.'
        }
        "$($_.name)/$($_.version)"
    } | Sort-Object)
    if (($frameworks -join '|') -cne 'Microsoft.NETCore.App/10.0.11|Microsoft.WindowsDesktop.App/10.0.11') {
        throw 'Framework-dependent runtimeconfig must request exactly NETCore and WindowsDesktop 10.0.11.'
    }
    $firstPartyNames = @('Albumen.App', 'Albumen.Analysis', 'Albumen.Application',
        'Albumen.Domain', 'Albumen.Infrastructure.Sqlite', 'Albumen.Infrastructure.Windows')
    foreach ($library in $DependencyManifest.libraries.PSObject.Properties) {
        $projectName = ($library.Name -split '/')[0]
        if ($approvedPackageLibraries -ccontains $library.Name) { continue }
        if ($library.Value.type -ceq 'project' -and $firstPartyNames -ccontains $projectName) { continue }
        throw "Unexpected framework-dependent runtime library: $($library.Name)"
    }
    $allowedBinaries = @($firstPartyNames | ForEach-Object { $_ + '.dll' }) + @(
        'Albumen.App.exe', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll',
        'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll')
    foreach ($file in @(Get-ChildItem -Force -File -Recurse -LiteralPath $OutputDirectory)) {
        if ($file.Extension -in @('.dll', '.exe') -and
            ($allowedBinaries -cnotcontains $file.Name -or $file.DirectoryName -ine $OutputDirectory)) {
            throw "Bundled framework or unapproved binary is forbidden in the framework-dependent release: $($file.Name)"
        }
    }
}

function Assert-NoForbiddenRuntimeArtifacts {
    param(
        [Parameter(Mandatory)]
        [string]$DirectoryPath,

        [Parameter()]
        [string[]]$FrameworkPaths = @()
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
    $frameworkPathSet = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($frameworkPath in $FrameworkPaths) {
        $frameworkPathSet[$frameworkPath] = $true
    }

    foreach ($file in $files) {
        if (-not $file.FullName.StartsWith($directoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime artifact escaped the inspected directory: $($file.Name)"
        }
        $relativePath = $file.FullName.Substring($directoryPrefix.Length)
        $canonicalRelativePath = $relativePath.Replace('\', '/')
        $isFrameworkContent = $frameworkPathSet.ContainsKey($canonicalRelativePath)
        if (-not $isFrameworkContent -and $file.Name -match $testArtifactPattern) {
            $violations.Add("$relativePath -> test/tooling/telemetry artifact")
        }
        if (-not $isFrameworkContent -and $file.Name -match $updaterArtifactPattern) {
            $violations.Add("$relativePath -> updater artifact")
        }
        if (-not $isFrameworkContent -and $file.Name -match $modelArtifactPattern -and
            $approvedModelPaths -cnotcontains $canonicalRelativePath) {
            $violations.Add("$relativePath -> model artifact")
        }

        if (-not $isFrameworkContent -and $file.Extension -in @('.json', '.config', '.xml')) {
            $configCount++
            $content = Get-Content -Raw -LiteralPath $file.FullName
            if ($content -match $configPattern) {
                $violations.Add("$relativePath -> remote endpoint/config marker")
            }
        }

        if (-not $isFrameworkContent -and $file.Extension -in @('.dll', '.exe')) {
            $binaryCount++
            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
            $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
            $isApprovedMarker = $approvedNetworkMarkerBinaries.ContainsKey($canonicalRelativePath) -and
                (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash -ceq
                    $approvedNetworkMarkerBinaries[$canonicalRelativePath]
            if ($ascii -match $binaryPattern -and -not $isApprovedMarker) {
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

        $isWindowsInfrastructure = $relativePath -like 'Albumen.Infrastructure.Windows\*'
        $isApp = $relativePath -like 'Albumen.App\*'
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
        'src\Albumen.App\bin\{0}\net10.0-windows\{1}\Albumen.App.deps.json' -f `
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
if ($FrameworkDependentRelease) {
    Assert-FrameworkDependentRelease -OutputDirectory $outputDirectory -DependencyManifest $dependencyManifest
}
$frameworkPaths = @()
if (-not [string]::IsNullOrWhiteSpace($FrameworkClosureManifestPath)) {
    $frameworkPaths = @(Read-SelfContainedFrameworkClosure `
        -ManifestPath $FrameworkClosureManifestPath `
        -OutputDirectory $outputDirectory)
}
$artifactInspection = Assert-NoForbiddenRuntimeArtifacts `
    -DirectoryPath $outputDirectory `
    -FrameworkPaths $frameworkPaths

if ([string]::IsNullOrWhiteSpace($SourceRootPath)) {
    $resolvedSourceRootPath = Join-Path $repositoryRoot 'src'
}
else {
    $resolvedSourceRootPath = [System.IO.Path]::GetFullPath($SourceRootPath)
}
$sourceFileCount = Assert-NoForbiddenProductionApis -RootPath $resolvedSourceRootPath

if (-not [string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    Write-Output "ALBUMEN_RUNTIME_BOUNDARY_OK manifest=$manifestPath"
    Write-Output "SourceFilesScanned=$sourceFileCount"
    Write-Output "RuntimeFilesScanned=$($artifactInspection.Files.Count)"
    Write-Output "RuntimeBinariesScanned=$($artifactInspection.BinaryCount)"
    Write-Output "RuntimeConfigsScanned=$($artifactInspection.ConfigCount)"
    Write-Output 'ForbiddenRuntimeArtifacts=0'
    Write-Output 'ForbiddenApiCalls=0'
    return
}

$expectedProjects = @(
    'Albumen.Analysis',
    'Albumen.App',
    'Albumen.Application',
    'Albumen.Domain',
    'Albumen.Infrastructure.Onnx',
    'Albumen.Infrastructure.Sqlite',
    'Albumen.Infrastructure.Windows')
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

$expectedDirectPackageReferences = @(
    'Albumen.Infrastructure.Onnx=Microsoft.ML.OnnxRuntime',
    'Albumen.Infrastructure.Onnx=Microsoft.ML.Tokenizers',
    'Albumen.Infrastructure.Sqlite=Microsoft.Data.Sqlite')
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
# The app-local Visual C++ runtime from the official VC_redist.x64.exe 14.44.35211.0 (owner-approved).
$approvedVcRuntime = [ordered]@{
    'msvcp140.dll' = '0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9'
    'msvcp140_1.dll' = 'BFAD5AEF4C63A669E3C140655CDFDF395B6C979B400A447BD5DCB65ED8826C3D'
    'vcruntime140.dll' = 'D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066'
    'vcruntime140_1.dll' = '1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F'
}
foreach ($entry in $approvedVcRuntime.GetEnumerator()) {
    $vcRuntimePath = Join-Path $outputDirectory $entry.Key
    if (-not [System.IO.File]::Exists($vcRuntimePath) -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $vcRuntimePath).Hash -cne $entry.Value) {
        throw "Visual C++ runtime differs from the approved app-local files: $($entry.Key)"
    }
}

Write-Output 'ALBUMEN_RUNTIME_BOUNDARY_OK'
Write-Output "TargetFramework=net10.0-windows"
Write-Output "RuntimeIdentifier=$RuntimeIdentifier"
Write-Output "ProductionProjects=$($projectNames -join ',')"
Write-Output "ProjectReferences=$(@($projectEdges | Sort-Object) -join ',')"
Write-Output "RuntimeLibraries=$($runtimeLibraries -join ',')"
Write-Output "SourceFilesScanned=$sourceFileCount"
Write-Output "ProductionPackages=$($distinctPackageLibraries -join ',')"
Write-Output "SQLiteNativeSha256=$nativeSqliteSha256"
Write-Output "VcRuntimeFiles=$($approvedVcRuntime.Keys -join ',')"
Write-Output "OutputFiles=$($outputFiles -join ',')"
Write-Output "RuntimeBinariesScanned=$($artifactInspection.BinaryCount)"
Write-Output "RuntimeConfigsScanned=$($artifactInspection.ConfigCount)"
Write-Output 'ForbiddenDependencies=0'
Write-Output 'ForbiddenRuntimeArtifacts=0'
Write-Output 'ForbiddenApiCalls=0'

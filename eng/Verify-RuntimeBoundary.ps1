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
    'Microsoft\.Data\.Sqlite',
    'SQLitePCL',
    'ImageSharp',
    'SkiaSharp',
    'Magick',
    'Onnx',
    'TensorFlow')

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
        Get-ChildItem -Recurse -File -Filter '*.cs' -LiteralPath $resolvedRootPath |
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
        foreach ($entry in $forbiddenApiPatterns.GetEnumerator()) {
            if ($source -match $entry.Value) {
                $relativePath = $sourceFile.FullName.Substring($resolvedRootPath.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar)
                $violations.Add("$relativePath -> $($entry.Key)")
            }
        }
    }

    if ($violations.Count -gt 0) {
        $distinctViolations = @($violations | Sort-Object -Unique)
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

$packageNames = New-Object 'System.Collections.Generic.List[string]'
$projectEdges = New-Object 'System.Collections.Generic.List[string]'
foreach ($projectPath in $projectPaths) {
    [xml]$project = Get-Content -Raw -LiteralPath $projectPath.FullName
    foreach ($packageReference in @($project.SelectNodes('//PackageReference'))) {
        $packageNames.Add([string]$packageReference.Include)
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
            $packageNames.Add($library.Name)
        }
    }
}

$distinctPackages = @($packageNames | Sort-Object -Unique)
if ($distinctPackages.Count -gt 0) {
    Assert-NoForbiddenDependencies -Names $distinctPackages
    throw "Production NuGet dependencies are outside the E1-S1 seed: $($distinctPackages -join ', ')"
}

$expectedRuntimeLibraries = @($expectedProjects | ForEach-Object { "$_/1.0.0" })
$unexpectedRuntimeLibraries = @(
    $runtimeLibraries | Where-Object { $expectedRuntimeLibraries -notcontains $_ })
if ($unexpectedRuntimeLibraries.Count -gt 0) {
    throw "Unexpected runtime libraries: $($unexpectedRuntimeLibraries -join ', ')"
}

$outputDirectory = Split-Path -Parent $manifestPath
$outputFiles = @(Get-ChildItem -File -LiteralPath $outputDirectory | Select-Object -ExpandProperty Name | Sort-Object)
Assert-NoForbiddenDependencies -Names $outputFiles

Write-Output 'PHOTOSIFT_RUNTIME_BOUNDARY_OK'
Write-Output "TargetFramework=net10.0-windows"
Write-Output "RuntimeIdentifier=$RuntimeIdentifier"
Write-Output "ProductionProjects=$($projectNames -join ',')"
Write-Output "ProjectReferences=$(@($projectEdges | Sort-Object) -join ',')"
Write-Output "RuntimeLibraries=$($runtimeLibraries -join ',')"
Write-Output "SourceFilesScanned=$sourceFileCount"
Write-Output 'ProductionPackages=(none)'
Write-Output "OutputFiles=$($outputFiles -join ',')"
Write-Output 'ForbiddenDependencies=0'
Write-Output 'ForbiddenApiCalls=0'

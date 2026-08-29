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
    [string[]]$AssemblyPath = @()
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

function Get-MemberReferenceTypeName {
    param(
        [Parameter(Mandatory)]
        [System.Reflection.Metadata.MetadataReader]$MetadataReader,

        [Parameter(Mandatory)]
        [System.Reflection.Metadata.EntityHandle]$Parent
    )

    if ($Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) {
        return $null
    }

    $rowNumber = [System.Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber($Parent)
    $typeHandle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::TypeReferenceHandle(
        $rowNumber)
    $typeReference = $MetadataReader.GetTypeReference($typeHandle)
    $namespace = $MetadataReader.GetString($typeReference.Namespace)
    $name = $MetadataReader.GetString($typeReference.Name)
    if ([string]::IsNullOrWhiteSpace($namespace)) {
        return $name
    }

    return "$namespace.$name"
}

function Test-ForbiddenProductionApi {
    param(
        [Parameter(Mandatory)]
        [string]$TypeName,

        [Parameter(Mandatory)]
        [string]$MemberName
    )

    if ($TypeName -eq 'System.Net' -or $TypeName.StartsWith(
        'System.Net.',
        [System.StringComparison]::Ordinal)) {
        return $true
    }

    $forbiddenMutationMembers = @{
        'System.IO.File' = @('Delete', 'Move', 'Replace')
        'System.IO.Directory' = @('Delete', 'Move')
        'System.IO.FileInfo' = @('Delete', 'MoveTo', 'Replace')
        'System.IO.DirectoryInfo' = @('Delete', 'MoveTo')
        'Microsoft.VisualBasic.FileIO.FileSystem' = @(
            'DeleteDirectory',
            'DeleteFile',
            'MoveDirectory',
            'MoveFile')
    }

    return $forbiddenMutationMembers.ContainsKey($TypeName) -and
        $forbiddenMutationMembers[$TypeName] -contains $MemberName
}

function Assert-NoForbiddenProductionApis {
    param(
        [Parameter(Mandatory)]
        [string[]]$Paths
    )

    $violations = New-Object 'System.Collections.Generic.List[string]'
    foreach ($path in $Paths) {
        $resolvedPath = [System.IO.Path]::GetFullPath($path)
        if (-not [System.IO.File]::Exists($resolvedPath)) {
            throw "Production assembly does not exist: $resolvedPath"
        }

        $stream = [System.IO.File]::OpenRead($resolvedPath)
        $peReader = $null
        try {
            $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            if (-not $peReader.HasMetadata) {
                throw "Production assembly does not contain managed metadata: $resolvedPath"
            }

            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader(
                $peReader)
            foreach ($memberHandle in $metadata.MemberReferences) {
                $member = $metadata.GetMemberReference($memberHandle)
                $typeName = Get-MemberReferenceTypeName `
                    -MetadataReader $metadata `
                    -Parent $member.Parent
                if ([string]::IsNullOrWhiteSpace($typeName)) {
                    continue
                }

                $memberName = $metadata.GetString($member.Name)
                if (Test-ForbiddenProductionApi -TypeName $typeName -MemberName $memberName) {
                    $violations.Add("$typeName::$memberName")
                }
            }
        }
        finally {
            if ($null -ne $peReader) {
                $peReader.Dispose()
            }
            $stream.Dispose()
        }
    }

    if ($violations.Count -gt 0) {
        $distinctViolations = @($violations | Sort-Object -Unique)
        throw "Forbidden production API calls: $($distinctViolations -join ', ')"
    }
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

$resolvedAssemblyPaths = @($AssemblyPath | ForEach-Object {
    [System.IO.Path]::GetFullPath($_)
})
if ($resolvedAssemblyPaths.Count -eq 0 -and
    [string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    $outputDirectory = Split-Path -Parent $manifestPath
    $resolvedAssemblyPaths = @(
        Get-ChildItem -File -Filter 'PhotoSift.*.dll' -LiteralPath $outputDirectory |
            Select-Object -ExpandProperty FullName |
            Sort-Object)
}
Assert-NoForbiddenProductionApis -Paths $resolvedAssemblyPaths

if (-not [string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    Write-Output "PHOTOSIFT_RUNTIME_BOUNDARY_OK manifest=$manifestPath"
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
$scannedAssemblyNames = @(
    $resolvedAssemblyPaths |
        ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) } |
        Sort-Object)
if ([string]::Join('|', $scannedAssemblyNames) -ne
    [string]::Join('|', $expectedProjects)) {
    throw "Scanned production assemblies differ from the E1-S1 boundary: $($scannedAssemblyNames -join ', ')"
}
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

$outputFiles = @(Get-ChildItem -File -LiteralPath $outputDirectory | Select-Object -ExpandProperty Name | Sort-Object)
Assert-NoForbiddenDependencies -Names $outputFiles

Write-Output 'PHOTOSIFT_RUNTIME_BOUNDARY_OK'
Write-Output "TargetFramework=net10.0-windows"
Write-Output "RuntimeIdentifier=$RuntimeIdentifier"
Write-Output "ProductionProjects=$($projectNames -join ',')"
Write-Output "ProjectReferences=$(@($projectEdges | Sort-Object) -join ',')"
Write-Output "RuntimeLibraries=$($runtimeLibraries -join ',')"
Write-Output "ScannedAssemblies=$($scannedAssemblyNames -join ',')"
Write-Output 'ProductionPackages=(none)'
Write-Output "OutputFiles=$($outputFiles -join ',')"
Write-Output 'ForbiddenDependencies=0'
Write-Output 'ForbiddenApiCalls=0'

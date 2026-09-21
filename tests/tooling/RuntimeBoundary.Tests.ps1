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

    function New-RuntimeArtifactFixture {
        param(
            [Parameter(Mandatory)]
            [string]$Name
        )

        $root = Join-Path $testRoot $Name
        $source = Join-Path $root 'source'
        [System.IO.Directory]::CreateDirectory($source) | Out-Null
        'public static class CleanProductionFixture { }' |
            Set-Content -LiteralPath (Join-Path $source 'CleanProductionFixture.cs') -Encoding utf8
        $manifest = Join-Path $root 'PhotoSift.App.deps.json'
        @{
            libraries = @{
                'PhotoSift.App/1.0.0' = @{ type = 'project' }
            }
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
        return [pscustomobject]@{ Root = $root; Source = $source; Manifest = $manifest }
    }

    function Assert-RuntimeArtifactRejected {
        param(
            [Parameter(Mandatory)]
            [string]$CaseName,

            [Parameter(Mandatory)]
            [string]$ArtifactName,

            [Parameter(Mandatory)]
            [byte[]]$Content
        )

        $fixture = New-RuntimeArtifactFixture -Name $CaseName
        [System.IO.File]::WriteAllBytes((Join-Path $fixture.Root $ArtifactName), $Content)
        $result = Invoke-BoundaryChecker `
            -Arguments @(
                '-DependencyManifestPath', $fixture.Manifest,
                '-SourceRootPath', $fixture.Source) `
            -OutputPrefix $CaseName
        if ($result.ExitCode -eq 0) {
            throw "Forbidden runtime artifact '$ArtifactName' was incorrectly accepted."
        }
        if ($result.Output -notmatch [regex]::Escape($ArtifactName)) {
            throw "Failure output did not identify forbidden runtime artifact '$ArtifactName'."
        }
    }

    Assert-RuntimeArtifactRejected `
        -CaseName 'test-assembly' `
        -ArtifactName 'PhotoSift.Scenarios.Tests.dll' `
        -Content ([System.Text.Encoding]::UTF8.GetBytes('clean fixture'))
    Assert-RuntimeArtifactRejected `
        -CaseName 'test-host' `
        -ArtifactName 'testhost.exe' `
        -Content ([System.Text.Encoding]::UTF8.GetBytes('clean fixture'))
    Assert-RuntimeArtifactRejected `
        -CaseName 'telemetry-library' `
        -ArtifactName 'OpenTelemetry.dll' `
        -Content ([System.Text.Encoding]::UTF8.GetBytes('clean fixture'))
    Assert-RuntimeArtifactRejected `
        -CaseName 'remote-config' `
        -ArtifactName 'PhotoSift.App.runtimeconfig.json' `
        -Content ([System.Text.Encoding]::UTF8.GetBytes('{"service":"https://telemetry.invalid"}'))
    Assert-RuntimeArtifactRejected `
        -CaseName 'binary-capability' `
        -ArtifactName 'PhotoSift.App.dll' `
        -Content ([System.Text.Encoding]::ASCII.GetBytes('binary-prefix System.Net.Http binary-suffix'))

    $cleanRuntime = New-RuntimeArtifactFixture -Name 'clean-runtime'
    [System.IO.File]::WriteAllBytes(
        (Join-Path $cleanRuntime.Root 'PhotoSift.App.dll'),
        [System.Text.Encoding]::ASCII.GetBytes('clean binary fixture'))
    '{"runtimeOptions":{"tfm":"net10.0-windows"}}' |
        Set-Content -LiteralPath (Join-Path $cleanRuntime.Root 'PhotoSift.App.runtimeconfig.json') -Encoding utf8
    $cleanRuntimeResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanRuntime.Manifest,
            '-SourceRootPath', $cleanRuntime.Source) `
        -OutputPrefix 'clean-runtime'
    if ($cleanRuntimeResult.ExitCode -ne 0) {
        throw "Clean runtime artifact fixture failed: $($cleanRuntimeResult.Output)"
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
    $generatedSourceRoot = Join-Path $allowedSourceRoot 'obj\Release'
    [System.IO.Directory]::CreateDirectory($generatedSourceRoot) | Out-Null
    'global using System.Net.Http;' |
        Set-Content -LiteralPath (Join-Path $generatedSourceRoot 'GlobalUsings.g.cs') -Encoding utf8
    $binarySourceRoot = Join-Path $allowedSourceRoot 'bin\Release'
    [System.IO.Directory]::CreateDirectory($binarySourceRoot) | Out-Null
    'global using System.Net.Sockets;' |
        Set-Content -LiteralPath (Join-Path $binarySourceRoot 'BinaryGenerated.cs') -Encoding utf8

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

    $nativeInteropSourceRoot = Join-Path $testRoot 'native-interop-source'
    $nativeInteropProjectPath = Join-Path $nativeInteropSourceRoot 'PhotoSift.Application'
    [System.IO.Directory]::CreateDirectory($nativeInteropProjectPath) | Out-Null
    @'
using System.Runtime.InteropServices;

public static partial class ForbiddenNativeInteropFixture
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentProcessId();
}
'@ | Set-Content -LiteralPath (Join-Path $nativeInteropProjectPath 'NativeInteropFixture.cs') -Encoding utf8
    @'
public static class ForbiddenQualifiedNativeInteropFixture
{
    [global::System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern uint GetCurrentProcessId();
}
'@ | Set-Content -LiteralPath (
        Join-Path $nativeInteropProjectPath 'QualifiedNativeInteropFixture.cs') -Encoding utf8
    $hiddenNativeInteropPath = Join-Path `
        $nativeInteropProjectPath `
        'HiddenNativeInteropFixture.cs'
    @'
using System.Runtime.InteropServices;

public static partial class HiddenNativeInteropFixture
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();
}
'@ | Set-Content -LiteralPath $hiddenNativeInteropPath -Encoding utf8
    [System.IO.File]::SetAttributes(
        $hiddenNativeInteropPath,
        [System.IO.File]::GetAttributes($hiddenNativeInteropPath) -bor
            [System.IO.FileAttributes]::Hidden)

    $nativeInteropResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $nativeInteropSourceRoot) `
        -OutputPrefix 'native-interop'
    if ($nativeInteropResult.ExitCode -eq 0) {
        throw 'Native interop outside Windows infrastructure fixture was incorrectly accepted.'
    }
    if ($nativeInteropResult.Output -notmatch [regex]::Escape(
            'PhotoSift.Application\NativeInteropFixture.cs -> native interop outside Windows infrastructure')) {
        throw 'Failure output did not identify the native interop path-policy violation.'
    }
    if ($nativeInteropResult.Output -notmatch [regex]::Escape(
            'PhotoSift.Application\QualifiedNativeInteropFixture.cs -> native interop outside Windows infrastructure')) {
        throw 'Failure output did not identify the qualified native interop path-policy violation.'
    }
    if ($nativeInteropResult.Output -notmatch [regex]::Escape(
            'PhotoSift.Application\HiddenNativeInteropFixture.cs -> native interop outside Windows infrastructure')) {
        throw 'Failure output did not identify the hidden native interop path-policy violation.'
    }

    $filesystemInspectionSourceRoot = Join-Path $testRoot 'filesystem-inspection-source'
    $filesystemInspectionProjectPath = Join-Path $filesystemInspectionSourceRoot 'PhotoSift.Application'
    [System.IO.Directory]::CreateDirectory($filesystemInspectionProjectPath) | Out-Null
    @'
using Microsoft.Win32.SafeHandles;

public static class ForbiddenFilesystemInspectionFixture
{
    public static void Inspect(SafeFileHandle handle) => _ = File.OpenHandle("fixture");
}
'@ | Set-Content -LiteralPath (Join-Path $filesystemInspectionProjectPath 'FilesystemInspectionFixture.cs') -Encoding utf8

    $filesystemInspectionResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $filesystemInspectionSourceRoot) `
        -OutputPrefix 'filesystem-inspection'
    if ($filesystemInspectionResult.ExitCode -eq 0) {
        throw 'Direct filesystem inspection outside Windows infrastructure fixture was incorrectly accepted.'
    }
    if ($filesystemInspectionResult.Output -notmatch [regex]::Escape(
            'PhotoSift.Application\FilesystemInspectionFixture.cs -> direct filesystem inspection outside Windows infrastructure')) {
        throw 'Failure output did not identify the direct filesystem inspection path-policy violation.'
    }

    $folderPickerSourceRoot = Join-Path $testRoot 'folder-picker-source'
    $folderPickerProjectPath = Join-Path $folderPickerSourceRoot 'PhotoSift.Application'
    [System.IO.Directory]::CreateDirectory($folderPickerProjectPath) | Out-Null
    @'
public static class ForbiddenFolderPickerFixture
{
    public static object Create() => new OpenFolderDialog();
}
'@ | Set-Content -LiteralPath (Join-Path $folderPickerProjectPath 'FolderPickerFixture.cs') -Encoding utf8

    $folderPickerResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $folderPickerSourceRoot) `
        -OutputPrefix 'folder-picker'
    if ($folderPickerResult.ExitCode -eq 0) {
        throw 'Folder picker outside App fixture was incorrectly accepted.'
    }
    if ($folderPickerResult.Output -notmatch [regex]::Escape(
            'PhotoSift.Application\FolderPickerFixture.cs -> folder picker outside App')) {
        throw 'Failure output did not identify the folder picker path-policy violation.'
    }

    $allowedPathPolicySourceRoot = Join-Path $testRoot 'allowed-path-policy-source'
    $allowedWindowsInfrastructurePath = Join-Path `
        $allowedPathPolicySourceRoot `
        'PhotoSift.Infrastructure.Windows'
    [System.IO.Directory]::CreateDirectory($allowedWindowsInfrastructurePath) | Out-Null
    @'
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

public static partial class AllowedWindowsInfrastructureFixture
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentProcessId();

    public static IEnumerable<string> Enumerate(string path) =>
        Directory.EnumerateFileSystemEntries(path);

    public static void Inspect(SafeFileHandle handle) => _ = File.OpenHandle("fixture");
}
'@ | Set-Content -LiteralPath (
        Join-Path $allowedWindowsInfrastructurePath 'AllowedWindowsInfrastructureFixture.cs') -Encoding utf8
    $allowedAppPath = Join-Path $allowedPathPolicySourceRoot 'PhotoSift.App'
    [System.IO.Directory]::CreateDirectory($allowedAppPath) | Out-Null
    @'
public static class AllowedAppFixture
{
    public static object Create() => new OpenFolderDialog();
}
'@ | Set-Content -LiteralPath (Join-Path $allowedAppPath 'AllowedAppFixture.cs') -Encoding utf8

    $allowedPathPolicyResult = Invoke-BoundaryChecker `
        -Arguments @(
            '-DependencyManifestPath', $cleanManifestPath,
            '-SourceRootPath', $allowedPathPolicySourceRoot) `
        -OutputPrefix 'allowed-path-policy'
    if ($allowedPathPolicyResult.ExitCode -ne 0) {
        throw "Allowed path-policy fixture failed: $($allowedPathPolicyResult.Output)"
    }

    Write-Output 'Passed: 21 Failed: 0'
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $testRoot -ErrorAction SilentlyContinue
}

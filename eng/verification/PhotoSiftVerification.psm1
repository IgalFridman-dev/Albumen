Set-StrictMode -Version Latest

if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot) -and
    $null -eq ('PhotoSift.Verification.NativeProcessTree' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PhotoSift.Verification
{
    public static class NativeProcessTree
    {
        private const uint SnapshotProcesses = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint ThreadCount;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static int[] GetDescendants(int rootProcessId)
        {
            var parentByProcess = new Dictionary<uint, uint>();
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == new IntPtr(-1))
            {
                return new int[0];
            }

            try
            {
                var entry = new ProcessEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        parentByProcess[entry.ProcessId] = entry.ParentProcessId;
                        entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var descendants = new List<int>();
            var parents = new HashSet<uint>();
            parents.Add((uint)rootProcessId);
            bool added;
            do
            {
                added = false;
                foreach (var pair in parentByProcess)
                {
                    if (parents.Contains(pair.Value) && !parents.Contains(pair.Key))
                    {
                        parents.Add(pair.Key);
                        descendants.Add((int)pair.Key);
                        added = true;
                    }
                }
            }
            while (added);

            return descendants.ToArray();
        }
    }
}
'@
}

function ConvertTo-PhotoSiftProcessArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $quoted = New-Object System.Text.StringBuilder
    [void]$quoted.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$quoted.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$quoted.Append('"')
        }
        else {
            [void]$quoted.Append(('\' * $backslashCount))
            [void]$quoted.Append($character)
        }
        $backslashCount = 0
    }

    [void]$quoted.Append(('\' * ($backslashCount * 2)))
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Stop-PhotoSiftProcessTree {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    if ($Process.HasExited) {
        return
    }

    $descendantProcessIds = @()
    if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
        try {
            $descendantProcessIds = @(
                [PhotoSift.Verification.NativeProcessTree]::GetDescendants($Process.Id))
        }
        catch {
            # taskkill still provides the normal Windows process-tree path.
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
        try {
        $taskKillPath = Join-Path $env:SystemRoot 'System32\taskkill.exe'
        $taskKillStartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $taskKillStartInfo.FileName = $taskKillPath
        $taskKillStartInfo.Arguments = "/PID $($Process.Id) /T /F"
        $taskKillStartInfo.UseShellExecute = $false
        $taskKillStartInfo.CreateNoWindow = $true
        $taskKillStartInfo.RedirectStandardOutput = $true
        $taskKillStartInfo.RedirectStandardError = $true
        $taskKillProcess = New-Object System.Diagnostics.Process
        $taskKillProcess.StartInfo = $taskKillStartInfo
        try {
            if ($taskKillProcess.Start()) {
                [void]$taskKillProcess.StandardOutput.ReadToEndAsync()
                [void]$taskKillProcess.StandardError.ReadToEndAsync()
                if (-not $taskKillProcess.WaitForExit(5000)) {
                    $taskKillProcess.Kill()
                    $taskKillProcess.WaitForExit()
                }
            }
        }
        finally {
            $taskKillProcess.Dispose()
        }
        }
        catch {
            # Runtime process-tree termination below is the cross-platform fallback.
        }
    }

    if (-not $Process.HasExited) {
        try {
            $Process.Kill($true)
        }
        catch [System.Management.Automation.MethodException] {
            # Windows PowerShell's .NET Framework Process type has no Kill(Boolean) overload.
        }
        catch [System.Management.Automation.RuntimeException] {
            # The root-process fallback below still prevents an indefinite wait.
        }
    }

    if (-not $Process.HasExited) {
        $Process.Kill()
    }

    for ($index = $descendantProcessIds.Count - 1; $index -ge 0; $index--) {
        try {
            $descendant = [System.Diagnostics.Process]::GetProcessById(
                $descendantProcessIds[$index])
            try {
                if (-not $descendant.HasExited) {
                    $descendant.Kill()
                    $descendant.WaitForExit()
                }
            }
            finally {
                $descendant.Dispose()
            }
        }
        catch {
            # The descendant was already terminated by taskkill or exited naturally.
        }
    }
}

function Invoke-PhotoSiftProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @(),

        [Parameter()]
        [string]$WorkingDirectory = [System.Environment]::CurrentDirectory,

        [Parameter()]
        [ValidateRange(0, 7200)]
        [int]$TimeoutSeconds = 0
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-PhotoSiftProcessArgument -Value $_
    }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start native process: $FilePath"
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = $false
        if ($TimeoutSeconds -gt 0) {
            $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
            if ($timedOut) {
                Stop-PhotoSiftProcessTree -Process $process
                $process.WaitForExit()
            }
        }
        else {
            $process.WaitForExit()
        }

        return [pscustomobject]@{
            ProcessId = $process.Id
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
            TimedOut = $timedOut
        }
    }
    finally {
        $process.Dispose()
    }
}

function ConvertFrom-PhotoSiftNetworkAuditXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Xml
    )

    [xml]$document = $Xml
    $namespace = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $namespace.AddNamespace('e', 'http://schemas.microsoft.com/win/2004/08/events/event')
    $eventIdNode = $document.SelectSingleNode('/e:Event/e:System/e:EventID', $namespace)
    if ($null -eq $eventIdNode) {
        throw 'Network audit event has no EventID.'
    }
    $values = @{}
    foreach ($node in @($document.SelectNodes('/e:Event/e:EventData/e:Data', $namespace))) {
        $values[[string]$node.Name] = [string]$node.InnerText
    }
    if (-not $values.ContainsKey('ProcessID')) {
        throw 'Network audit event has no ProcessID.'
    }
    $processText = [string]$values.ProcessID
    $processId = if ($processText.StartsWith('0x', [System.StringComparison]::OrdinalIgnoreCase)) {
        [System.Convert]::ToInt32($processText.Substring(2), 16)
    }
    else {
        [int]$processText
    }

    return [pscustomobject]@{
        EventId = [int]$eventIdNode.InnerText
        ProcessId = $processId
        ExecutableName = [System.IO.Path]::GetFileName([string]$values.Application)
    }
}

function Get-PhotoSiftRemainingSeconds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter(Mandatory)]
        [string]$OperationName
    )

    $remaining = [int][System.Math]::Ceiling(
        ($Deadline - [System.DateTimeOffset]::Now).TotalSeconds)
    if ($remaining -le 0) {
        throw "Golden VM verification timed out before '$OperationName'."
    }

    return $remaining
}

function Invoke-PhotoSiftPowerShellWithDeadline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$ScriptBlock,

        [Parameter()]
        [object[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter(Mandatory)]
        [string]$OperationName,

        [Parameter()]
        [switch]$AllowErrorStream
    )

    $remainingSeconds = Get-PhotoSiftRemainingSeconds `
        -Deadline $Deadline `
        -OperationName $OperationName
    $powerShell = [System.Management.Automation.PowerShell]::Create()
    try {
        [void]$powerShell.AddScript($ScriptBlock.ToString())
        foreach ($argument in $ArgumentList) {
            [void]$powerShell.AddArgument($argument)
        }

        $asyncResult = $powerShell.BeginInvoke()
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($remainingSeconds * 1000)) {
            $powerShell.Stop()
            throw "Golden VM verification timed out during '$OperationName'."
        }

        try {
            $output = $powerShell.EndInvoke($asyncResult)
        }
        catch {
            if ($powerShell.Streams.Error.Count -gt 0) {
                $PSCmdlet.ThrowTerminatingError($powerShell.Streams.Error[0])
            }
            throw
        }
        if ($powerShell.HadErrors -and -not $AllowErrorStream) {
            if ($powerShell.Streams.Error.Count -eq 1) {
                $PSCmdlet.ThrowTerminatingError($powerShell.Streams.Error[0])
            }
            $messages = @($powerShell.Streams.Error | ForEach-Object { $_.ToString() })
            throw "Golden VM operation '$OperationName' failed: $($messages -join [System.Environment]::NewLine)"
        }
        return $output
    }
    finally {
        $powerShell.Dispose()
    }
}

function Assert-PhotoSiftSnapshotHasNoReparsePoints {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SnapshotRoot
    )

    $resolvedSnapshotRoot = [System.IO.Path]::GetFullPath($SnapshotRoot)
    $reparsePoints = @(Get-ChildItem -LiteralPath $resolvedSnapshotRoot -Force -Recurse |
        Where-Object {
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        })
    if ($reparsePoints.Count -gt 0) {
        throw "Golden VM source snapshot contained a reparse point: $($reparsePoints[0].FullName)"
    }

    return $resolvedSnapshotRoot
}

function New-PhotoSiftSourceSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$HostInputPath,

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline
    )

    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $resolvedHostInputPath = [System.IO.Path]::GetFullPath($HostInputPath)
    [System.IO.Directory]::CreateDirectory($resolvedHostInputPath) | Out-Null
    $remainingSeconds = Get-PhotoSiftRemainingSeconds `
        -Deadline $Deadline `
        -OperationName 'source snapshot copy'
    $copyResult = Invoke-PhotoSiftProcess `
        -FilePath (Join-Path $env:SystemRoot 'System32\robocopy.exe') `
        -Arguments @(
            $resolvedRepositoryRoot,
            $resolvedHostInputPath,
            '/E',
            '/XJ',
            '/SL',
            '/XD', '.git', 'bin', 'obj',
            '/XF', '.git',
            '/R:1',
            '/W:1',
            '/NFL',
            '/NDL',
            '/NJH',
            '/NJS') `
        -WorkingDirectory $resolvedRepositoryRoot `
        -TimeoutSeconds $remainingSeconds
    if ($copyResult.TimedOut) {
        throw 'Golden VM source snapshot copy timed out.'
    }
    if ($copyResult.ExitCode -notin 0..7) {
        throw "Could not prepare the Golden VM source snapshot; robocopy exited $($copyResult.ExitCode)."
    }

    Assert-PhotoSiftSnapshotHasNoReparsePoints `
        -SnapshotRoot $resolvedHostInputPath | Out-Null
    return $resolvedHostInputPath
}

function Remove-PhotoSiftHostInputSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$HostInputPath,

        [Parameter(Mandatory)]
        [string]$RunsRoot
    )

    $resolvedHostInputPath = [System.IO.Path]::GetFullPath($HostInputPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedRunsRoot = [System.IO.Path]::GetFullPath($RunsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $runPath = [System.IO.Path]::GetDirectoryName($resolvedHostInputPath)
    $parentOfRun = [System.IO.Path]::GetDirectoryName($runPath)
    if ([System.IO.Path]::GetFileName($resolvedHostInputPath) -cne 'input' -or
        -not $parentOfRun.Equals(
            $resolvedRunsRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a source snapshot outside the expected runs layout: $resolvedHostInputPath"
    }

    if ([System.IO.Directory]::Exists($resolvedHostInputPath)) {
        Remove-Item -LiteralPath $resolvedHostInputPath -Recurse -Force
    }
}

function Resolve-PhotoSiftGoldenVmCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [string]$ExpectedComputerName,

        [Parameter(Mandatory)]
        [string]$ExpectedUserName
    )

    $acceptedUserNames = @(
        $ExpectedUserName,
        ".\$ExpectedUserName",
        "$ExpectedComputerName\$ExpectedUserName")
    $matchesExpectedUser = @($acceptedUserNames | Where-Object {
        $_.Equals(
            $Credential.UserName,
            [System.StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
    if (-not $matchesExpectedUser) {
        throw "Golden VM credentials must identify the local account '$ExpectedComputerName\$ExpectedUserName'; got '$($Credential.UserName)'."
    }

    return New-Object System.Management.Automation.PSCredential(
        "$ExpectedComputerName\$ExpectedUserName",
        $Credential.Password)
}

function Get-PhotoSiftGoldenVmCredential {
    [CmdletBinding()]
    param(
        [Parameter()]
        [AllowNull()]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter()]
        [string]$ExpectedComputerName = 'PHOTOSIFTVM',

        [Parameter()]
        [string]$ExpectedUserName = 'PhotoSiftRunner',

        [Parameter()]
        [scriptblock]$PromptCredential = {
            param($UserName, $Message)
            Get-Credential -UserName $UserName -Message $Message
        }
    )

    $resolvedCredential = $Credential
    if ($null -eq $resolvedCredential) {
        $canonicalUserName = "$ExpectedComputerName\$ExpectedUserName"
        $resolvedCredential = & $PromptCredential `
            $canonicalUserName `
            "Enter the password for the Golden VM account $canonicalUserName. Capturing this credential does not authenticate it."
        if ($null -eq $resolvedCredential) {
            throw 'Golden VM credential prompt was cancelled.'
        }
    }

    return Resolve-PhotoSiftGoldenVmCredential `
        -Credential $resolvedCredential `
        -ExpectedComputerName $ExpectedComputerName `
        -ExpectedUserName $ExpectedUserName
}

function Assert-PhotoSiftGoldenVmContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedVMName,

        [Parameter(Mandatory)]
        [string]$ExpectedCheckpointName,

        [Parameter(Mandatory)]
        [object]$VM,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Checkpoints,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$NetworkAdapters,

        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Credential,

        [Parameter(Mandatory)]
        [bool]$IsElevated
    )

    if (-not $IsElevated) {
        throw 'The Golden VM controller must run from an elevated PowerShell process.'
    }

    $vmNameProperty = $VM.PSObject.Properties['Name']
    $vmStateProperty = $VM.PSObject.Properties['State']
    if ($null -eq $vmNameProperty -or
        [string]$vmNameProperty.Value -cne $ExpectedVMName) {
        throw "Hyper-V returned a VM that did not match '$ExpectedVMName'."
    }
    if ($null -eq $vmStateProperty -or [string]$vmStateProperty.Value -cne 'Off') {
        throw "Golden VM '$ExpectedVMName' must be Off before verification."
    }

    $matchingCheckpoints = @($Checkpoints | Where-Object {
        $nameProperty = $_.PSObject.Properties['Name']
        $null -ne $nameProperty -and
            [string]$nameProperty.Value -ceq $ExpectedCheckpointName
    })
    if ($matchingCheckpoints.Count -ne 1) {
        throw "Golden VM '$ExpectedVMName' must contain exactly one '$ExpectedCheckpointName' checkpoint."
    }

    if (@($NetworkAdapters).Count -ne 0) {
        throw "Golden VM '$ExpectedVMName' must have zero virtual network adapters."
    }

    if ($null -eq $Credential -or $Credential -isnot [System.Management.Automation.PSCredential]) {
        throw 'The Golden VM controller requires a PowerShell Direct PSCredential.'
    }

    return [pscustomobject][ordered]@{
        vmName = $ExpectedVMName
        checkpointName = $ExpectedCheckpointName
        vmState = 'Off'
        networkAdapterCount = 0
    }
}

function Assert-PhotoSiftGoldenVmVerificationCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$CheckpointName
    )

    if ($CheckpointName -ceq 'Clean-Evaluation-Base') {
        throw "Normal verification cannot use immutable recovery checkpoint '$CheckpointName'."
    }

    return $CheckpointName
}

function Test-PhotoSiftPowerShellDirectTransientFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    return (
        $ErrorRecord.CategoryInfo.Category -ceq
            [System.Management.Automation.ErrorCategory]::ResourceUnavailable -and
        $ErrorRecord.FullyQualifiedErrorId -match
            '^(PhotoSift\.PowerShellDirectNotReady|PhotoSift\.HyperVSocketNotReady)(,|$)')
}

function Connect-PhotoSiftPowerShellDirect {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$CheckpointName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter()]
        [string]$ExpectedComputerName = 'PHOTOSIFTVM',

        [Parameter()]
        [string]$ExpectedUserName = 'PhotoSiftRunner',

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter()]
        [scriptblock]$OpenSession = {
            param($RequestedVMName, $RequestedCredential, $OpenTimeoutMilliseconds)
            $hadSessionOption = Test-Path -LiteralPath 'variable:PSSessionOption'
            $previousSessionOption = $null
            if ($hadSessionOption) {
                $previousSessionOption = $PSSessionOption
            }
            try {
                $PSSessionOption = New-PSSessionOption `
                    -OpenTimeout $OpenTimeoutMilliseconds `
                    -OperationTimeout $OpenTimeoutMilliseconds
                New-PSSession `
                    -VMName $RequestedVMName `
                    -Credential $RequestedCredential `
                    -ErrorAction Stop
            }
            finally {
                if ($hadSessionOption) {
                    $PSSessionOption = $previousSessionOption
                }
                else {
                    Remove-Variable -Name PSSessionOption -ErrorAction SilentlyContinue
                }
            }
        },

        [Parameter()]
        [scriptblock]$InspectIdentity = {
            param($Session)
            Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
                [pscustomobject][ordered]@{
                    identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
                    computerName = $env:COMPUTERNAME
                    userProfile = $env:USERPROFILE
                }
            }
        },

        [Parameter()]
        [scriptblock]$CloseSession = {
            param($Session)
            Remove-PSSession -Session $Session -ErrorAction Stop
        },

        [Parameter()]
        [scriptblock]$InvokeWithDeadline = {
            param($Operation, $Arguments, $RequestedDeadline, $OperationName)
            $wrapperArguments = New-Object 'object[]' 2
            $wrapperArguments[0] = $Operation
            $wrapperArguments[1] = [object[]]$Arguments
            $envelopes = @(Invoke-PhotoSiftPowerShellWithDeadline `
                -ScriptBlock {
                    param($RequestedOperation, $RequestedArguments)
                    try {
                        [pscustomobject]@{
                            succeeded = $true
                            output = @(& $RequestedOperation @RequestedArguments)
                            errorRecord = $null
                        }
                    }
                    catch {
                        [pscustomobject]@{
                            succeeded = $false
                            output = @()
                            errorRecord = $_
                        }
                    }
                } `
                -ArgumentList $wrapperArguments `
                -Deadline $RequestedDeadline `
                -OperationName $OperationName `
                -AllowErrorStream)
            if ($envelopes.Count -ne 1) {
                throw "Bounded Golden VM operation '$OperationName' returned $($envelopes.Count) envelopes."
            }
            $envelope = $envelopes[0]
            if (-not [bool]$envelope.succeeded) {
                throw ([System.Management.Automation.ErrorRecord]$envelope.errorRecord)
            }
            return @($envelope.output)
        },

        [Parameter()]
        [scriptblock]$WaitBeforeRetry = {
            param($Seconds)
            Start-Sleep -Seconds $Seconds
        }
    )

    $session = $null
    do {
        $remainingOpenSeconds = Get-PhotoSiftRemainingSeconds `
            -Deadline $Deadline `
            -OperationName 'PowerShell Direct session creation'
        $openTimeoutMilliseconds = [int][System.Math]::Max(
            1,
            [System.Math]::Min(
                [int]::MaxValue,
                [long]$remainingOpenSeconds * 1000L))
        try {
            $sessionResults = @(& $OpenSession `
                    $VMName `
                    $Credential `
                    $openTimeoutMilliseconds)
            if ($sessionResults.Count -ne 1) {
                throw "PowerShell Direct returned $($sessionResults.Count) sessions."
            }
            $session = $sessionResults[0]
            break
        }
        catch {
            if (-not (Test-PhotoSiftPowerShellDirectTransientFailure -ErrorRecord $_)) {
                throw "PowerShell Direct session authentication/connection failed for VM '$VMName' after restoring checkpoint '$CheckpointName' with account '$($Credential.UserName)'. The secure credential was captured but not authenticated. The session was attempted once to protect the account from lockout. No PhotoSift script saved or changed the guest password. $($_.Exception.Message)"
            }
            $remainingSeconds = Get-PhotoSiftRemainingSeconds `
                -Deadline $Deadline `
                -OperationName 'PowerShell Direct readiness retry'
            $retryDelaySeconds = [System.Math]::Min(2, $remainingSeconds)
            & $WaitBeforeRetry $retryDelaySeconds
        }
    } while ($true)

    if ($null -eq $session) {
        throw "PowerShell Direct returned no session for VM '$VMName' after restoring checkpoint '$CheckpointName'."
    }

    try {
        [void](Get-PhotoSiftRemainingSeconds `
                -Deadline $Deadline `
                -OperationName 'PowerShell Direct identity attestation')
        $identityArguments = [object[]]@($session)
        $identityResults = @(& $InvokeWithDeadline `
                $InspectIdentity `
                $identityArguments `
                $Deadline `
                'PowerShell Direct identity attestation')
        if ($identityResults.Count -ne 1) {
            throw "identity probe returned $($identityResults.Count) results"
        }
        $identity = $identityResults[0]
        $expectedIdentity = "$ExpectedComputerName\$ExpectedUserName"
        $expectedProfile = "C:\Users\$ExpectedUserName"
        if (-not [string]::Equals(
                [string]$identity.identity,
                $expectedIdentity,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [string]$identity.computerName,
                $ExpectedComputerName,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [string]$identity.userProfile,
                $expectedProfile,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "expected identity '$expectedIdentity' and profile '$expectedProfile', got '$($identity.identity)' and '$($identity.userProfile)'"
        }
    }
    catch {
        try {
            & $CloseSession $session
        }
        catch {
        }
        throw "PowerShell Direct identity attestation failed for VM '$VMName' checkpoint '$CheckpointName'. $($_.Exception.Message)"
    }

    return $session
}

function Get-PhotoSiftGuestRunnerInvocation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptPath,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage,

        [Parameter(Mandatory)]
        [string]$RunRoot,

        [Parameter(Mandatory)]
        [string]$SdkRoot,

        [Parameter(Mandatory)]
        [string]$PackagesRoot,

        [Parameter(Mandatory)]
        [ValidateRange(1, 7200)]
        [int]$TimeoutSeconds
    )

    return [pscustomobject][ordered]@{
        scriptPath = $ScriptPath
        stage = $Stage
        runRoot = $RunRoot
        sdkRoot = $SdkRoot
        packagesRoot = $PackagesRoot
        timeoutSeconds = $TimeoutSeconds
        arguments = @(
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy', 'Bypass',
            '-File', $ScriptPath,
            '-Stage', $Stage,
            '-RunRoot', $RunRoot,
            '-SdkRoot', $SdkRoot,
            '-PackagesRoot', $PackagesRoot,
            '-TimeoutSeconds', $TimeoutSeconds.ToString(
                [System.Globalization.CultureInfo]::InvariantCulture))
    }
}

function Get-PhotoSiftGuestEvidencePaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Items
    )

    foreach ($item in $Items) {
        $fullNameProperty = $item.PSObject.Properties['FullName']
        if ($null -eq $fullNameProperty -or
            [string]::IsNullOrWhiteSpace([string]$fullNameProperty.Value)) {
            throw 'Golden VM evidence item did not contain a FullName path.'
        }
        [string]$fullNameProperty.Value
    }
}

function Invoke-PhotoSiftGoldenVmRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$CheckpointName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$HostInputPath,

        [Parameter(Mandatory)]
        [string]$HostOutputPath,

        [Parameter(Mandatory)]
        [string]$GuestRunRoot,

        [Parameter(Mandatory)]
        [string]$SdkRoot,

        [Parameter(Mandatory)]
        [string]$PackagesRoot,

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter(Mandatory)]
        [hashtable]$Operations
    )

    $requiredOperations = @(
        'RestoreCheckpoint',
        'InspectVmContract',
        'StartVM',
        'WaitForVM',
        'OpenSession',
        'PrepareGuest',
        'CopySource',
        'InvokeGuest',
        'CopyEvidence',
        'CloseSession',
        'StopVM')
    foreach ($operationName in $requiredOperations) {
        if (-not $Operations.ContainsKey($operationName) -or
            $Operations[$operationName] -isnot [scriptblock]) {
            throw "Golden VM operation '$operationName' was not supplied."
        }
    }

    $resolvedHostInput = [System.IO.Path]::GetFullPath($HostInputPath)
    $resolvedHostOutput = [System.IO.Path]::GetFullPath($HostOutputPath)
    if (-not [System.IO.Directory]::Exists($resolvedHostInput)) {
        throw "Host input snapshot does not exist: $resolvedHostInput"
    }
    [System.IO.Directory]::CreateDirectory($resolvedHostOutput) | Out-Null

    $session = $null
    $postRestoreContract = $null
    $postCleanupContract = $null
    $failures = New-Object 'System.Collections.Generic.List[string]'
    try {
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'checkpoint restore')
        & $Operations.RestoreCheckpoint $VMName $CheckpointName $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'post-restore VM contract inspection')
        $postRestoreContract = & $Operations.InspectVmContract $VMName $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'VM startup')
        & $Operations.StartVM $VMName $Deadline
        $waitSeconds = [System.Math]::Min(
            120,
            (Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'VM heartbeat'))
        & $Operations.WaitForVM $VMName $waitSeconds $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'PowerShell Direct connection')
        $session = & $Operations.OpenSession $VMName $Credential $Deadline
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for '$VMName'."
        }

        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'guest preparation')
        & $Operations.PrepareGuest $session $GuestRunRoot $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'guest source copy')
        & $Operations.CopySource $session $resolvedHostInput (Join-Path $GuestRunRoot 'input') $Deadline
        try {
            [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'guest verification')
            & $Operations.InvokeGuest $session $Stage $GuestRunRoot $SdkRoot $PackagesRoot $Deadline
        }
        catch {
            $failures.Add($_.Exception.ToString())
        }

        try {
            [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'guest evidence copy')
            & $Operations.CopyEvidence `
                $session `
                (Join-Path $GuestRunRoot 'output') `
                $resolvedHostOutput `
                $Deadline
        }
        catch {
            $failures.Add("Could not retrieve Golden VM evidence: $($_.Exception)")
        }
    }
    catch {
        $failures.Add($_.Exception.ToString())
    }
    finally {
        if ($null -ne $session) {
            try {
                & $Operations.CloseSession $session
            }
            catch {
                $failures.Add("Could not close the PowerShell Direct session: $($_.Exception)")
            }
        }

        try {
            & $Operations.StopVM $VMName
        }
        catch {
            $failures.Add("Could not stop Golden VM '$VMName': $($_.Exception)")
        }

        try {
            & $Operations.RestoreCheckpoint $VMName $CheckpointName
        }
        catch {
            $failures.Add("Could not restore Golden checkpoint '$CheckpointName': $($_.Exception)")
        }

        try {
            $postCleanupContract = & $Operations.InspectVmContract $VMName
        }
        catch {
            $failures.Add("Golden VM post-cleanup contract failed: $($_.Exception)")
        }
    }

    if ($failures.Count -gt 0) {
        throw ($failures -join [System.Environment]::NewLine)
    }

    return [pscustomobject][ordered]@{
        vmName = $VMName
        checkpointName = $CheckpointName
        stage = $Stage
        runId = $RunId
        outputPath = $resolvedHostOutput
        postRestoreVmState = [string]$postRestoreContract.vmState
        postRestoreNetworkAdapterCount = [int]$postRestoreContract.networkAdapterCount
        postCleanupVmState = [string]$postCleanupContract.vmState
        postCleanupNetworkAdapterCount = [int]$postCleanupContract.networkAdapterCount
    }
}

function Get-PhotoSiftVerificationStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage
    )

    $testProjects = switch ($Stage) {
        'Architecture' { @('PhotoSift.Infrastructure.Tests') }
        'Core' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Scenarios.Tests')
        }
        'Full' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Infrastructure.Tests',
                'PhotoSift.Scenarios.Tests')
        }
    }

    $restoreTarget = if ($Stage -ceq 'Architecture') {
        'tests\PhotoSift.Infrastructure.Tests\PhotoSift.Infrastructure.Tests.csproj'
    }
    else {
        'PhotoSift.slnx'
    }
    $restoreName = switch ($Stage) {
        'Architecture' { 'restore-architecture' }
        'Core' { 'restore-core' }
        'Full' { 'restore-solution' }
    }

    return [pscustomobject][ordered]@{
        stage = $Stage
        testProjects = @($testProjects)
        restoreName = $restoreName
        restoreArguments = @(
            'restore',
            $restoreTarget,
            '-r', 'win-x64',
            '--locked-mode',
            '--configfile', 'NuGet.Config',
            '-p:NuGetAudit=false')
        runRuntimeBoundary = $Stage -ceq 'Full'
        runWpfSmoke = $Stage -ceq 'Full'
        runtimeBoundaryScript = if ($Stage -ceq 'Full') {
            'eng\Verify-RuntimeBoundary.ps1'
        } else { $null }
        wpfSmokeScript = if ($Stage -ceq 'Full') {
            'eng\Invoke-OfflineLaunchSmoke.ps1'
        } else { $null }
        signingEnabled = $false
    }
}

function Read-PhotoSiftTrxResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectName,

        [Parameter(Mandatory)]
        [string]$TrxPath,

        [Parameter(Mandatory)]
        [string]$OutputRoot
    )

    $resolvedTrxPath = [System.IO.Path]::GetFullPath($TrxPath)
    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $outputRootPrefix = $resolvedOutputRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTrxPath.StartsWith(
            $outputRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test project '$ProjectName' TRX evidence was outside output: $resolvedTrxPath."
    }
    if (-not [System.IO.File]::Exists($resolvedTrxPath)) {
        throw "Test project '$ProjectName' did not produce TRX evidence at $resolvedTrxPath."
    }

    try {
        [xml]$trx = Get-Content -Raw -LiteralPath $resolvedTrxPath
    }
    catch {
        throw "Test project '$ProjectName' produced malformed TRX evidence at ${resolvedTrxPath}: $($_.Exception.Message)"
    }

    $resultSummaries = @($trx.GetElementsByTagName('ResultSummary'))
    if ($resultSummaries.Count -ne 1) {
        throw "Test project '$ProjectName' TRX evidence did not contain exactly one ResultSummary."
    }

    $outcome = $resultSummaries[0].Attributes['outcome']
    if ($null -eq $outcome -or $outcome.Value -cne 'Completed') {
        throw "Test project '$ProjectName' TRX evidence did not have a Completed outcome."
    }

    $counters = $resultSummaries[0].SelectSingleNode("*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw "Test project '$ProjectName' TRX evidence did not contain Counters."
    }

    $counts = [ordered]@{}
    foreach ($name in 'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted') {
        $attribute = $counters.Attributes[$name]
        $value = 0
        if ($null -eq $attribute -or
            -not [int]::TryParse(
                $attribute.Value,
                [System.Globalization.NumberStyles]::Integer,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$value) -or
            $value -lt 0) {
            throw "Test project '$ProjectName' TRX evidence had an invalid '$name' count."
        }
        $counts[$name] = $value
    }

    if ($counts.executed -eq 0) {
        throw "Test project '$ProjectName' executed zero tests."
    }
    if ($counts.total -ne $counts.executed -or
        $counts.passed -ne $counts.executed -or
        $counts.notExecuted -ne 0) {
        throw "Test project '$ProjectName' TRX evidence contained inconsistent test counts."
    }
    if (($counts.failed + $counts.error + $counts.timeout + $counts.aborted) -gt 0) {
        throw "Test project '$ProjectName' TRX evidence recorded failed or error results."
    }

    return [pscustomobject]@{
        project = $ProjectName
        resultFile = $resolvedTrxPath.Substring($outputRootPrefix.Length)
        total = $counts.total
        executed = $counts.executed
        passed = $counts.passed
        failed = $counts.failed
        error = $counts.error
        timeout = $counts.timeout
        aborted = $counts.aborted
        notExecuted = $counts.notExecuted
    }
}

function Test-PhotoSiftJsonInteger {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Value
    )

    return $Value -is [sbyte] -or
        $Value -is [byte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64]
}

function Get-PhotoSiftExpectedCommandNames {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$Stage
    )

    switch ($Stage) {
        'Architecture' {
            return @(
                'copy-source',
                'dotnet-version',
                'dotnet-runtimes',
                'restore-architecture',
                'build-architecture',
                'test-architecture')
        }
        'Core' {
            return @(
                'copy-source',
                'dotnet-version',
                'dotnet-runtimes',
                'restore-core',
                'build-PhotoSift.Domain.Tests',
                'test-PhotoSift.Domain.Tests',
                'build-PhotoSift.Application.Tests',
                'test-PhotoSift.Application.Tests',
                'build-PhotoSift.Analysis.Tests',
                'test-PhotoSift.Analysis.Tests',
                'build-PhotoSift.Scenarios.Tests',
                'test-PhotoSift.Scenarios.Tests')
        }
        'Full' {
            return @(
                'copy-source',
                'dotnet-version',
                'dotnet-runtimes',
                'restore-solution',
                'build-solution',
                'test-PhotoSift.Domain.Tests',
                'test-PhotoSift.Application.Tests',
                'test-PhotoSift.Analysis.Tests',
                'test-PhotoSift.Infrastructure.Tests',
                'test-PhotoSift.Scenarios.Tests',
                'runtime-boundary',
                'wpf-smoke')
        }
    }
}

function Assert-PhotoSiftSmokeResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$StandardOutput,

        [Parameter(Mandatory)]
        [bool]$TimedOut
    )

    if ($TimedOut) {
        throw 'PhotoSift WPF smoke process timed out.'
    }
    if ($ExitCode -ne 0) {
        throw "PhotoSift WPF smoke process exited $ExitCode."
    }

    $expectedMarker = 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true'
    if (@($StandardOutput -split "`r?`n") -notcontains $expectedMarker) {
        throw "PhotoSift WPF smoke output did not contain: $expectedMarker"
    }
}

function Assert-PhotoSiftVerificationResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SummaryPath,

        [Parameter(Mandatory)]
        [ValidateSet('Architecture', 'Core', 'Full')]
        [string]$ExpectedStage
    )

    $resolvedSummaryPath = [System.IO.Path]::GetFullPath($SummaryPath)
    if (-not [System.IO.File]::Exists($resolvedSummaryPath)) {
        throw "Golden VM did not produce summary.json at $resolvedSummaryPath."
    }

    try {
        $summary = Get-Content -Raw -LiteralPath $resolvedSummaryPath | ConvertFrom-Json
    }
    catch {
        throw "Golden VM produced malformed summary evidence: $($_.Exception.Message)"
    }

    $successProperty = $summary.PSObject.Properties['success']
    if ($null -eq $successProperty -or
        $successProperty.Value -isnot [bool] -or
        -not $successProperty.Value) {
        throw 'Golden VM verification reported success=false.'
    }
    $stageProperty = $summary.PSObject.Properties['stage']
    if ($null -eq $stageProperty -or
        $stageProperty.Value -isnot [string] -or
        $stageProperty.Value -cne $ExpectedStage) {
        throw "Golden VM stage '$($summary.stage)' did not match '$ExpectedStage'."
    }
    $networkingProperty = $summary.PSObject.Properties['networkingDisabled']
    if ($null -eq $networkingProperty -or
        $networkingProperty.Value -isnot [bool] -or
        -not $networkingProperty.Value) {
        throw 'Golden VM evidence did not record networking as disabled.'
    }
    foreach ($telemetryPropertyName in 'dotnetCliTelemetryOptOut', 'testingPlatformTelemetryOptOut') {
        $telemetryProperty = $summary.PSObject.Properties[$telemetryPropertyName]
        if ($null -eq $telemetryProperty -or
            $telemetryProperty.Value -isnot [bool] -or
            -not $telemetryProperty.Value) {
            throw "Golden VM evidence did not record $telemetryPropertyName=true."
        }
    }
    $sdkVersionProperty = $summary.PSObject.Properties['sdkVersion']
    if ($null -eq $sdkVersionProperty -or
        $sdkVersionProperty.Value -isnot [string] -or
        $sdkVersionProperty.Value -cne '10.0.400') {
        throw "Golden VM evidence reported unexpected SDK '$($summary.sdkVersion)'."
    }
    $desktopRuntimeProperty = $summary.PSObject.Properties['desktopRuntimeVersion']
    if ($null -eq $desktopRuntimeProperty -or
        $desktopRuntimeProperty.Value -isnot [string] -or
        $desktopRuntimeProperty.Value -cne '10.0.11') {
        throw "Golden VM evidence reported unexpected Desktop Runtime '$($summary.desktopRuntimeVersion)'."
    }

    $outputRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetDirectoryName($resolvedSummaryPath))
    $outputRootPrefix = $outputRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $commandsProperty = $summary.PSObject.Properties['commands']
    if ($null -eq $commandsProperty) {
        throw 'Golden VM evidence did not contain command results.'
    }
    $commandResults = @($commandsProperty.Value)
    $expectedCommandNames = @(Get-PhotoSiftExpectedCommandNames -Stage $ExpectedStage)
    if ($commandResults.Count -ne $expectedCommandNames.Count) {
        throw "Golden VM evidence contained an unexpected command count for '$ExpectedStage'."
    }
    foreach ($expectedCommandName in $expectedCommandNames) {
        $matchingCommands = @($commandResults | Where-Object {
            $nameProperty = $_.PSObject.Properties['name']
            $null -ne $nameProperty -and
                $nameProperty.Value -is [string] -and
                $nameProperty.Value -ceq $expectedCommandName
        })
        if ($matchingCommands.Count -ne 1) {
            throw "Golden VM evidence did not contain exactly one '$expectedCommandName' command."
        }

        $command = $matchingCommands[0]
        $succeededProperty = $command.PSObject.Properties['succeeded']
        if ($null -eq $succeededProperty -or
            $succeededProperty.Value -isnot [bool] -or
            -not $succeededProperty.Value) {
            throw "Golden VM command '$expectedCommandName' did not report succeeded=true."
        }
        $exitCodeProperty = $command.PSObject.Properties['exitCode']
        if ($null -eq $exitCodeProperty -or
            -not (Test-PhotoSiftJsonInteger -Value $exitCodeProperty.Value)) {
            throw "Golden VM command '$expectedCommandName' had an invalid exitCode."
        }
        $exitCode = [long]$exitCodeProperty.Value
        if (($expectedCommandName -ceq 'copy-source' -and $exitCode -notin 0..7) -or
            ($expectedCommandName -cne 'copy-source' -and $exitCode -ne 0)) {
            throw "Golden VM command '$expectedCommandName' reported an unaccepted exitCode '$exitCode'."
        }
        $timedOutProperty = $command.PSObject.Properties['timedOut']
        if ($null -eq $timedOutProperty -or
            $timedOutProperty.Value -isnot [bool] -or
            $timedOutProperty.Value) {
            throw "Golden VM command '$expectedCommandName' did not report timedOut=false."
        }
        $logPathProperty = $command.PSObject.Properties['logPath']
        if ($null -eq $logPathProperty -or
            $logPathProperty.Value -isnot [string] -or
            [string]::IsNullOrWhiteSpace($logPathProperty.Value)) {
            throw "Golden VM command '$expectedCommandName' did not name a command log."
        }
        $resolvedLogPath = [System.IO.Path]::GetFullPath(
            (Join-Path $outputRoot $logPathProperty.Value))
        if (-not $resolvedLogPath.StartsWith(
                $outputRootPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.File]::Exists($resolvedLogPath)) {
            throw "Golden VM command '$expectedCommandName' log was missing or outside output."
        }
    }

    $testsProperty = $summary.PSObject.Properties['tests']
    if ($null -eq $testsProperty) {
        throw 'Golden VM evidence did not contain structured test results.'
    }

    $expectedProjects = @(switch ($ExpectedStage) {
        'Architecture' { @('PhotoSift.Infrastructure.Tests') }
        'Core' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Scenarios.Tests')
        }
        'Full' {
            @(
                'PhotoSift.Domain.Tests',
                'PhotoSift.Application.Tests',
                'PhotoSift.Analysis.Tests',
                'PhotoSift.Infrastructure.Tests',
                'PhotoSift.Scenarios.Tests')
        }
    })

    $testResults = @($testsProperty.Value)
    foreach ($expectedProject in $expectedProjects) {
        $projectResults = @($testResults | Where-Object {
            [string]$_.project -ceq $expectedProject
        })
        if ($projectResults.Count -ne 1) {
            throw "Golden VM evidence did not contain exactly one test result for '$expectedProject'."
        }

        $resultFileProperty = $projectResults[0].PSObject.Properties['resultFile']
        if ($null -eq $resultFileProperty -or
            $resultFileProperty.Value -isnot [string] -or
            [string]::IsNullOrWhiteSpace($resultFileProperty.Value)) {
            throw "Golden VM evidence for '$expectedProject' did not name a TRX file."
        }

        $counts = @{}
        foreach ($name in 'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted') {
            $property = $projectResults[0].PSObject.Properties[$name]
            if ($null -eq $property -or
                -not (Test-PhotoSiftJsonInteger -Value $property.Value) -or
                [long]$property.Value -lt 0) {
                throw "Golden VM evidence for '$expectedProject' had an invalid '$name' count."
            }
            $counts[$name] = [int]$property.Value
        }

        if ($counts.executed -eq 0) {
            throw "Golden VM evidence recorded zero executed tests for '$expectedProject'."
        }
        if ($counts.total -ne $counts.executed -or
            $counts.passed -ne $counts.executed -or
            $counts.notExecuted -ne 0) {
            throw "Golden VM evidence for '$expectedProject' contained inconsistent test counts."
        }
        if ($counts.failed -gt 0 -or
            $counts.error -gt 0 -or
            $counts.timeout -gt 0 -or
            $counts.aborted -gt 0) {
            throw "Golden VM evidence recorded failed or error results for '$expectedProject'."
        }

        $reparsedResult = Read-PhotoSiftTrxResult `
            -ProjectName $expectedProject `
            -TrxPath (Join-Path $outputRoot $resultFileProperty.Value) `
            -OutputRoot $outputRoot
        foreach ($name in 'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted') {
            if ([int]$reparsedResult.$name -ne $counts[$name]) {
                throw "Golden VM summary/TRX '$name' count mismatch for '$expectedProject'."
            }
        }
    }

    if ($testResults.Count -ne $expectedProjects.Count) {
        throw "Golden VM evidence contained an unexpected number of test project results for '$ExpectedStage'."
    }

    return $summary
}

Export-ModuleMember -Function `
    Assert-PhotoSiftGoldenVmContract, `
    Assert-PhotoSiftGoldenVmVerificationCheckpoint, `
    Assert-PhotoSiftSnapshotHasNoReparsePoints, `
    Resolve-PhotoSiftGoldenVmCredential, `
    Get-PhotoSiftGoldenVmCredential, `
    Connect-PhotoSiftPowerShellDirect, `
    Get-PhotoSiftRemainingSeconds, `
    Get-PhotoSiftGuestRunnerInvocation, `
    Get-PhotoSiftGuestEvidencePaths, `
    Invoke-PhotoSiftGoldenVmRun, `
    Invoke-PhotoSiftPowerShellWithDeadline, `
    Get-PhotoSiftVerificationStage, `
    Invoke-PhotoSiftProcess, `
    ConvertFrom-PhotoSiftNetworkAuditXml, `
    New-PhotoSiftSourceSnapshot, `
    Remove-PhotoSiftHostInputSnapshot, `
    Read-PhotoSiftTrxResult, `
    Assert-PhotoSiftSmokeResult, `
    Assert-PhotoSiftVerificationResult

Set-StrictMode -Version Latest

$verificationModulePath = Join-Path $PSScriptRoot 'PhotoSiftVerification.psm1'
Import-Module $verificationModulePath

function Get-PhotoSiftDirectoryInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.Directory]::Exists($resolvedPath)) {
        throw "Toolchain directory does not exist: $resolvedPath"
    }

    $files = @(Get-ChildItem `
        -LiteralPath $resolvedPath `
        -File `
        -Recurse `
        -Force `
        -ErrorAction Stop)
    [long]$totalBytes = 0
    foreach ($file in $files) {
        $totalBytes += [long]$file.Length
    }

    return [pscustomobject][ordered]@{
        path = $resolvedPath
        fileCount = $files.Count
        totalBytes = $totalBytes
    }
}

function Assert-PhotoSiftToolchainPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SdkPath,

        [Parameter(Mandatory)]
        [string]$PackagesPath
    )

    $resolvedSdkPath = [System.IO.Path]::GetFullPath($SdkPath)
    $resolvedPackagesPath = [System.IO.Path]::GetFullPath($PackagesPath)
    $dotnetPath = Join-Path $resolvedSdkPath 'dotnet.exe'
    $sdkVersionPath = Join-Path $resolvedSdkPath 'sdk\10.0.400'
    $desktopRuntimePath = Join-Path `
        $resolvedSdkPath `
        'shared\Microsoft.WindowsDesktop.App\10.0.11'

    if (-not [System.IO.File]::Exists($dotnetPath)) {
        throw "Pinned SDK executable does not exist: $dotnetPath"
    }
    if (-not [System.IO.Directory]::Exists($sdkVersionPath)) {
        throw "Pinned SDK 10.0.400 does not exist beneath: $resolvedSdkPath"
    }
    if (-not [System.IO.Directory]::Exists($desktopRuntimePath)) {
        throw "Pinned Windows Desktop Runtime 10.0.11 does not exist beneath: $resolvedSdkPath"
    }

    $sdkInventory = Get-PhotoSiftDirectoryInventory -Path $resolvedSdkPath
    $packagesInventory = Get-PhotoSiftDirectoryInventory -Path $resolvedPackagesPath
    if ($sdkInventory.fileCount -eq 0) {
        throw "Pinned SDK payload is empty: $resolvedSdkPath"
    }
    if ($packagesInventory.fileCount -eq 0) {
        throw "Locked NuGet package cache is empty: $resolvedPackagesPath"
    }

    return [pscustomobject][ordered]@{
        sdkPath = $resolvedSdkPath
        packagesPath = $resolvedPackagesPath
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        sdkInventory = $sdkInventory
        packagesInventory = $packagesInventory
    }
}

function Assert-PhotoSiftGoldenVmProvisioningContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedVMName,

        [Parameter(Mandatory)]
        [string]$SourceCheckpointName,

        [Parameter(Mandatory)]
        [string]$TargetCheckpointName,

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

    if ($SourceCheckpointName -ceq $TargetCheckpointName) {
        throw 'Source and target Golden VM checkpoints must have different names.'
    }

    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName $ExpectedVMName `
        -ExpectedCheckpointName $SourceCheckpointName `
        -VM $VM `
        -Checkpoints $Checkpoints `
        -NetworkAdapters $NetworkAdapters `
        -Credential $Credential `
        -IsElevated $IsElevated | Out-Null

    $matchingTargets = @($Checkpoints | Where-Object {
        $nameProperty = $_.PSObject.Properties['Name']
        $null -ne $nameProperty -and
            [string]$nameProperty.Value -ceq $TargetCheckpointName
    })
    if ($matchingTargets.Count -ne 0) {
        throw "Golden VM '$ExpectedVMName' already contains target checkpoint '$TargetCheckpointName'."
    }

    return [pscustomobject][ordered]@{
        vmName = $ExpectedVMName
        sourceCheckpointName = $SourceCheckpointName
        targetCheckpointName = $TargetCheckpointName
        vmState = 'Off'
        networkAdapterCount = 0
    }
}

function Assert-PhotoSiftInventoryMatch {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual
    )

    if ([int]$Expected.fileCount -le 0 -or
        [int]$Actual.fileCount -ne [int]$Expected.fileCount -or
        [long]$Actual.totalBytes -ne [long]$Expected.totalBytes) {
        throw "$Name payload inventory did not match after Golden VM copy."
    }
}

function Invoke-PhotoSiftGoldenVmProvisioning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$SourceCheckpointName,

        [Parameter(Mandatory)]
        [string]$TargetCheckpointName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [string]$SdkPath,

        [Parameter(Mandatory)]
        [string]$PackagesPath,

        [Parameter(Mandatory)]
        [object]$HostSdkInventory,

        [Parameter(Mandatory)]
        [object]$HostPackagesInventory,

        [Parameter(Mandatory)]
        [hashtable]$Operations
    )

    $requiredOperations = @(
        'RestoreSourceCheckpoint',
        'StartVM',
        'WaitForVM',
        'OpenSession',
        'PrepareGuest',
        'CopySdk',
        'CopyPackages',
        'InspectGuestPayload',
        'ShutdownGuest',
        'CloseSession',
        'WaitForPowerOff',
        'CreateTargetCheckpoint',
        'ReadCheckpoints',
        'RemoveTargetCheckpoint',
        'StopVM')
    foreach ($operationName in $requiredOperations) {
        if (-not $Operations.ContainsKey($operationName) -or
            $Operations[$operationName] -isnot [scriptblock]) {
            throw "Golden VM provisioning operation '$operationName' was not supplied."
        }
    }

    $resolvedSdkPath = [System.IO.Path]::GetFullPath($SdkPath)
    $resolvedPackagesPath = [System.IO.Path]::GetFullPath($PackagesPath)
    $guestToolchainRoot = 'C:\PhotoSift\golden'
    $guestSdkPath = Join-Path $guestToolchainRoot 'sdk'
    $guestPackagesPath = Join-Path $guestToolchainRoot 'packages'
    $session = $null
    $sessionClosed = $false
    $targetCreated = $false
    $completed = $false
    $failures = New-Object 'System.Collections.Generic.List[string]'

    try {
        & $Operations.RestoreSourceCheckpoint $VMName $SourceCheckpointName
        & $Operations.StartVM $VMName
        & $Operations.WaitForVM $VMName 120
        $session = & $Operations.OpenSession $VMName $Credential
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for '$VMName'."
        }

        & $Operations.PrepareGuest $session $guestToolchainRoot
        & $Operations.CopySdk $session $resolvedSdkPath $guestSdkPath
        & $Operations.CopyPackages $session $resolvedPackagesPath $guestPackagesPath
        $guestPayload = & $Operations.InspectGuestPayload `
            $session `
            $guestSdkPath `
            $guestPackagesPath
        if ($null -eq $guestPayload) {
            throw 'Golden VM returned no copied toolchain inventory.'
        }
        if ([string]$guestPayload.sdkVersion -cne '10.0.400') {
            throw "Golden VM copied SDK version was '$($guestPayload.sdkVersion)', expected '10.0.400'."
        }
        if ([string]$guestPayload.desktopRuntimeVersion -cne '10.0.11') {
            throw "Golden VM copied Desktop Runtime version was '$($guestPayload.desktopRuntimeVersion)', expected '10.0.11'."
        }
        Assert-PhotoSiftInventoryMatch `
            -Name 'SDK' `
            -Expected $HostSdkInventory `
            -Actual $guestPayload.sdkInventory
        Assert-PhotoSiftInventoryMatch `
            -Name 'NuGet package cache' `
            -Expected $HostPackagesInventory `
            -Actual $guestPayload.packagesInventory

        & $Operations.ShutdownGuest $session
        & $Operations.CloseSession $session
        $sessionClosed = $true
        $session = $null
        & $Operations.WaitForPowerOff $VMName 120
        & $Operations.CreateTargetCheckpoint $VMName $TargetCheckpointName
        $targetCreated = $true

        $checkpoints = @(& $Operations.ReadCheckpoints $VMName)
        foreach ($checkpointName in $SourceCheckpointName, $TargetCheckpointName) {
            $matching = @($checkpoints | Where-Object {
                $nameProperty = $_.PSObject.Properties['Name']
                $null -ne $nameProperty -and
                    [string]$nameProperty.Value -ceq $checkpointName
            })
            if ($matching.Count -ne 1) {
                throw "Golden VM must contain exactly one '$checkpointName' checkpoint after provisioning."
            }
        }
        $completed = $true
    }
    catch {
        $failures.Add($_.Exception.ToString())
    }
    finally {
        if ($null -ne $session -and -not $sessionClosed) {
            try {
                & $Operations.CloseSession $session
            }
            catch {
                $failures.Add("Could not close the PowerShell Direct session: $($_.Exception)")
            }
        }

        if (-not $completed) {
            if ($targetCreated) {
                try {
                    & $Operations.RemoveTargetCheckpoint $VMName $TargetCheckpointName
                    $targetCreated = $false
                }
                catch {
                    $failures.Add("Could not remove incomplete target checkpoint '$TargetCheckpointName': $($_.Exception)")
                }
            }
            try {
                & $Operations.StopVM $VMName
            }
            catch {
                $failures.Add("Could not stop Golden VM '$VMName': $($_.Exception)")
            }
            try {
                & $Operations.RestoreSourceCheckpoint $VMName $SourceCheckpointName
            }
            catch {
                $failures.Add("Could not restore source checkpoint '$SourceCheckpointName': $($_.Exception)")
            }
        }
    }

    if ($failures.Count -gt 0) {
        throw ($failures -join [System.Environment]::NewLine)
    }

    return [pscustomobject][ordered]@{
        vmName = $VMName
        sourceCheckpointName = $SourceCheckpointName
        targetCheckpointName = $TargetCheckpointName
        sdkFileCount = [int]$HostSdkInventory.fileCount
        packagesFileCount = [int]$HostPackagesInventory.fileCount
    }
}

Export-ModuleMember -Function `
    Get-PhotoSiftDirectoryInventory, `
    Assert-PhotoSiftToolchainPayload, `
    Assert-PhotoSiftGoldenVmProvisioningContract, `
    Invoke-PhotoSiftGoldenVmProvisioning

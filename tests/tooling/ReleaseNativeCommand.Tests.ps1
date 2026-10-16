[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$Operations = $null
$builderPath = Join-Path $PSScriptRoot '../../eng/release/New-PhotoSiftRelease.ps1'
# Load the exact production functions without running the builder's SDK setup.
# No command operation is mocked: each test launches Windows cmd.exe itself.
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $builderPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Release builder failed to parse.' }
foreach ($name in @('Write-ReleaseUtf8NoBom', 'Invoke-ReleaseExternal')) {
    $functions = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false))
    if ($functions.Count -ne 1) { throw "Expected one production function: $name" }
    . ([scriptblock]::Create($functions[0].Extent.Text))
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('PhotoSift-NativeCommand-' + [guid]::NewGuid().ToString('N'))
$evidencePath = Join-Path $testRoot 'evidence'
$workingPath = Join-Path $testRoot 'working directory with spaces'
[System.IO.Directory]::CreateDirectory($evidencePath) | Out-Null
[System.IO.Directory]::CreateDirectory($workingPath) | Out-Null
$originalLocation = (Get-Location).Path
$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
try {
    # These catch stderr promotion, dropped output, exit-code masking, and
    # failure to write evidence before throwing. Expectations are literals.
    foreach ($case in @(
        @{ Name = 'stderr-success'; Command = 'echo diagnostic 1>&2 & exit /b 0'; Exit = 0; Text = @('diagnostic') },
        @{ Name = 'mixed-success'; Command = 'echo stdout-marker & echo stderr-marker 1>&2 & cd & exit /b 0'; Exit = 0; Text = @('stdout-marker', 'stderr-marker', $workingPath) },
        @{ Name = 'stderr-failure'; Command = 'echo before-failure & echo failure-detail 1>&2 & exit /b 7'; Exit = 7; Text = @('before-failure', 'failure-detail') },
        @{ Name = 'silent-failure'; Command = 'exit /b 9'; Exit = 9; Text = @() },
        @{ Name = 'silent-success'; Command = 'exit /b 0'; Exit = 0; Text = @() },
        @{ Name = 'long-mixed-output'; Command = '(for /L %i in (1,1,2000) do @echo stdout-%i & @echo stderr-%i 1>&2) & exit /b 0'; Exit = 0; Text = @('stdout-1', 'stderr-1', 'stdout-2000', 'stderr-2000') }
    )) {
        try {
            $caught = $null
            $result = $null
            try {
                $result = Invoke-ReleaseExternal -Name $case.Name -FilePath $env:ComSpec -Arguments @('/d', '/c', $case.Command) -WorkingDirectory $workingPath
            } catch { $caught = $_ }
            $recordPath = Join-Path $evidencePath ($case.Name + '-command.json')
            if (-not (Test-Path -LiteralPath $recordPath)) { throw "Missing command evidence; runner error: $caught" }
            $record = Get-Content -Raw -LiteralPath $recordPath | ConvertFrom-Json
            if ($record.exitCode -ne $case.Exit -or $record.injected) { throw 'Incorrect native exit code or provenance.' }
            foreach ($expected in $case.Text) {
                if (-not $record.output.Contains($expected)) { throw "Missing captured output: $expected" }
            }
            if ($case.Exit -eq 0) {
                if ($null -ne $caught -or $null -eq $result -or $result.ExitCode -ne 0) { throw "Expected successful native command: $caught" }
                if ($result.Output -cne $record.output) { throw 'Returned and persisted output differ.' }
            } elseif ($null -eq $caught -or $caught.Exception.Message -notmatch "exited $($case.Exit):") {
                throw "Nonzero native exit did not produce the expected failure: $caught"
            }
            if ($ErrorActionPreference -cne 'Stop' -or -not $PSNativeCommandUseErrorActionPreference -or (Get-Location).Path -cne $originalLocation) {
                throw 'Runner changed caller preferences or location.'
            }
            $passed++
        } catch { $failures.Add("$($case.Name): $($_.Exception.Message)") }
    }
    # A failed launch must not inherit the previous successful process's exit 0.
    try {
        $caught = $null
        try {
            Invoke-ReleaseExternal -Name 'missing-executable' -FilePath (Join-Path $testRoot 'missing.exe') -Arguments @() -WorkingDirectory $workingPath | Out-Null
        } catch { $caught = $_ }
        if ($null -eq $caught) { throw 'Missing executable was accepted.' }
        if (Test-Path -LiteralPath (Join-Path $evidencePath 'missing-executable-command.json')) { throw 'Failed launch must not claim a completed process exit.' }
        if ($ErrorActionPreference -cne 'Stop' -or -not $PSNativeCommandUseErrorActionPreference -or (Get-Location).Path -cne $originalLocation) { throw 'Failed launch changed caller state.' }
        $passed++
    } catch { $failures.Add("missing-executable: $($_.Exception.Message)") }
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
    Write-Output "Native command tests: Passed: $passed Failed: 0"
}
finally {
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\PhotoSift-NativeCommand-'
    if (-not $resolvedTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe native test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$generator = Join-Path $repositoryRoot 'eng\icon\New-AlbumenIcon.ps1'
$committed = Join-Path $repositoryRoot 'src\Albumen.App\Assets\Albumen.ico'
$expectedSizes = '16,20,24,32,48,64,128,256'

$workspace = Join-Path ([IO.Path]::GetTempPath()) ('albumen-icon-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
try {
    $generated = Join-Path $workspace 'Albumen.ico'
    & $generator -OutputPath $generated | Out-Null

    # The committed icon is exactly what the script draws.
    $expected = [IO.File]::ReadAllBytes($committed)
    $actual = [IO.File]::ReadAllBytes($generated)
    if ([Convert]::ToBase64String($expected) -cne [Convert]::ToBase64String($actual)) {
        throw 'Regenerating the icon changed its bytes; run eng\icon\New-AlbumenIcon.ps1 and commit src\Albumen.App\Assets\Albumen.ico.'
    }

    # One ICO directory with the eight sizes, each entry a PNG inside the file.
    if ([BitConverter]::ToUInt16($expected, 0) -ne 0 -or [BitConverter]::ToUInt16($expected, 2) -ne 1) {
        throw 'The icon does not start with an ICO directory header.'
    }
    $count = [BitConverter]::ToUInt16($expected, 4)
    $sizes = @()
    for ($index = 0; $index -lt $count; $index++) {
        $entry = 6 + 16 * $index
        $width = if ($expected[$entry] -eq 0) { 256 } else { [int]$expected[$entry] }
        $height = if ($expected[$entry + 1] -eq 0) { 256 } else { [int]$expected[$entry + 1] }
        if ($width -ne $height) { throw "Icon entry $index is not square ($width x $height)." }
        $length = [BitConverter]::ToUInt32($expected, $entry + 8)
        $offset = [BitConverter]::ToUInt32($expected, $entry + 12)
        if ($offset + $length -gt $expected.Length) { throw "Icon entry $index points past the end of the file." }
        $signature = ($expected[$offset..($offset + 3)] | ForEach-Object { '{0:X2}' -f $_ }) -join ''
        if ($signature -cne '89504E47') { throw "Icon entry $index ($width px) is not PNG-compressed." }
        $sizes += $width
    }
    if (($sizes -join ',') -cne $expectedSizes) {
        throw "Expected icon sizes $expectedSizes; found $($sizes -join ',')."
    }
}
finally {
    Remove-Item -LiteralPath $workspace -Recurse -Force
}
Write-Output "PASS: the committed icon matches the generator byte for byte and holds PNG entries at $expectedSizes px."

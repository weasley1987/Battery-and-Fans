$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 compiler is required.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'Battery and Fans.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'src\Hardware.cs') (Join-Path $PSScriptRoot 'src\Tray.cs')
if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $output
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | ForEach-Object {
    "$($_.Hash)  Battery and Fans.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
}

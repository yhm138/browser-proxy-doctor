param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$destination = Join-Path $OutputDirectory 'BrowserProxyDoctor.exe'
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$destination" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BrowserProxyDoctor.exe.config') -Destination $OutputDirectory -Force
Write-Output "Built: $destination"

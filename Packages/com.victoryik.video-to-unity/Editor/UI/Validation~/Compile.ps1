param(
    [Parameter(Mandatory = $true)][string]$UnityDataPath,
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$ConverterFile,
    [Parameter(Mandatory = $true)][string]$UguiAssembly
)
$ErrorActionPreference = 'Stop'
$uiDirectory = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compiler = Join-Path $UnityDataPath 'DotNetSdkRoslyn/csc.dll'
$arguments = @('/nologo', '/target:library', '/langversion:9', '/nostdlib+', '/define:UNITY_EDITOR,UNITY_2022_3_OR_NEWER', ('/out:"' + (Join-Path $outputDirectory 'VideoToUnity.Editor.Validation.dll') + '"'))
$references = @((Join-Path $UnityDataPath 'NetStandard/ref/2.1.0/netstandard.dll'), $UguiAssembly)
$references += Get-ChildItem -LiteralPath (Join-Path $UnityDataPath 'Managed/UnityEngine') -Filter '*.dll' | Select-Object -ExpandProperty FullName
$arguments += $references | ForEach-Object { '/reference:"' + $_ + '"' }
$sources = @(Get-ChildItem -LiteralPath $uiDirectory -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$sources += Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.cs' | Select-Object -ExpandProperty FullName
$sources += (Resolve-Path -LiteralPath $ConverterFile).Path
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $outputDirectory 'compile.rsp'
Set-Content -LiteralPath $responseFile -Value $arguments -Encoding utf8
$hashes = $sources | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 | Select-Object Path,Hash }
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'source-hashes.json') -Encoding utf8
& dotnet $compiler ('@' + $responseFile) 2>&1 | Tee-Object -FilePath (Join-Path $outputDirectory 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed. See out/compile.log.' }
$validationMessage = 'PASS: C# compile against installed Unity assemblies and actual runtime/converter sources. Unity was not launched.'
Add-Content -LiteralPath (Join-Path $outputDirectory 'compile.log') -Value $validationMessage -Encoding utf8
Write-Output 'PASS: C# compile against installed Unity assemblies and actual runtime/converter sources. Unity was not launched.'

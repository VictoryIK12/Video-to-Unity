param(
    [Parameter(Mandatory=$true)][string]$NUnitAssembly,
    [string]$DotnetRoot = (Split-Path -Parent (Get-Command dotnet).Source)
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = Join-Path $PSScriptRoot '.out/pure'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sdk = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$framework = Get-ChildItem -LiteralPath (Join-Path $pack.FullName 'ref') -Directory | Select-Object -First 1
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$dotnet = Join-Path $DotnetRoot 'dotnet.exe'
$references = @(Get-ChildItem -LiteralPath $framework.FullName -Filter '*.dll' | ForEach-Object { '/reference:"' + $_.FullName + '"' })
$model = Join-Path $output 'VideoToUnity.Editor.dll'
$modelSource = Join-Path $root 'Packages/com.victoryik.video-to-unity/Editor/BackgroundUI/BackgroundRemovalUiModel.cs'
$response = Join-Path $output 'model.rsp'
Set-Content -LiteralPath $response -Value (@('/nologo','/target:library','/langversion:9','/nostdlib+','/warnaserror+',('/out:"' + $model + '"')) + $references + ('"' + $modelSource + '"')) -Encoding UTF8
& $dotnet $compiler ('@' + $response)
if ($LASTEXITCODE -ne 0) { throw 'Production geometry compilation failed.' }
$runner = Join-Path $output 'UsabilityChecks.dll'
$sources = @((Join-Path $root 'Packages/com.victoryik.video-to-unity/Tests/Editor/BackgroundUsabilityAuditTests.cs'), (Join-Path $PSScriptRoot 'PureUsabilityRunner.cs'))
$response = Join-Path $output 'tests.rsp'
$options = @('/nologo','/target:exe','/langversion:9','/nostdlib+','/warnaserror+',('/out:"' + $runner + '"'),('/reference:"' + $NUnitAssembly + '"'),('/reference:"' + $model + '"'))
Set-Content -LiteralPath $response -Value ($options + $references + @($sources | ForEach-Object { '"' + $_ + '"' })) -Encoding UTF8
& $dotnet $compiler ('@' + $response)
if ($LASTEXITCODE -ne 0) { throw 'Usability test compilation failed.' }
Copy-Item -LiteralPath $NUnitAssembly -Destination (Join-Path $output 'nunit.framework.dll') -Force
@{ runtimeOptions = @{ tfm = $framework.Name; framework = @{ name = 'Microsoft.NETCore.App'; version = $pack.Name }; rollForward = 'LatestPatch' } } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'UsabilityChecks.runtimeconfig.json') -Encoding UTF8
& $dotnet $runner 2>&1 | Tee-Object -FilePath (Join-Path $output 'results.log')
if ($LASTEXITCODE -ne 0) { throw 'Usability checks failed.' }

param([Parameter(Mandatory = $true)][string]$DotnetRoot)
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sdk = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$framework = Get-ChildItem -LiteralPath (Join-Path $pack.FullName 'ref') -Directory | Select-Object -First 1
$testAssembly = Join-Path $outputDirectory 'FileTransferTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:9', '/nostdlib+', ('/out:"' + $testAssembly + '"'))
$arguments += Get-ChildItem -LiteralPath $framework.FullName -Filter '*.dll' | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += '"' + (Join-Path (Split-Path -Parent $PSScriptRoot) 'VideoEffectFileTransfer.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'FileTransferTests.cs') + '"'
$responseFile = Join-Path $outputDirectory 'file-tests.rsp'
Set-Content -LiteralPath $responseFile -Value $arguments -Encoding utf8
& (Join-Path $DotnetRoot 'dotnet.exe') (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@' + $responseFile)
if ($LASTEXITCODE -ne 0) { throw 'File transfer test compilation failed.' }
$runtime = @{ runtimeOptions = @{ tfm = $framework.Name; framework = @{ name = 'Microsoft.NETCore.App'; version = $pack.Name }; rollForward = 'LatestPatch' } }
$runtime | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory 'FileTransferTests.runtimeconfig.json') -Encoding utf8
& (Join-Path $DotnetRoot 'dotnet.exe') $testAssembly $outputDirectory 2>&1 | Tee-Object -FilePath (Join-Path $outputDirectory 'file-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'File transfer tests failed.' }

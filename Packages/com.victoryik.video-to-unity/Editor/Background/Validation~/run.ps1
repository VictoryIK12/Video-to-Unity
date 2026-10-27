# Builds and runs the standalone background backend validation with real FFmpeg. No Unity launch.
# Requires .NET 9 SDK and FFmpeg/FFprobe (libx264, libx265, libvpx) on PATH.
param([string]$Only = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$build = Join-Path $here 'Build'
$lib = Join-Path $build 'lib'
$hostDir = Join-Path $build 'host'
New-Item -ItemType Directory -Force -Path $lib, $hostDir | Out-Null

# 1) Unity API surface gate: production sources only, netstandard2.1, C# 9, warnings are errors.
$production = '<Compile Include="../../../*.cs"/><Compile Include="../../../../Conversion/VideoConverter.cs"/>'
Set-Content -LiteralPath (Join-Path $lib 'Lib.csproj') -Encoding utf8 -Value ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><LangVersion>9.0</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>' + $production + '</ItemGroup></Project>')
& dotnet build (Join-Path $lib 'Lib.csproj') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'netstandard2.1 compile of production sources failed.' }
Write-Output 'PASS netstandard2.1 / C# 9 compile of production sources (0 warnings)'

# 2) Harness host compiled with the SAME production source files.
Set-Content -LiteralPath (Join-Path $hostDir 'BackgroundHarness.csproj') -Encoding utf8 -Value ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><LangVersion>latest</LangVersion><Nullable>disable</Nullable></PropertyGroup><ItemGroup>' + $production + '<Compile Include="../../BackgroundHarness.cs"/></ItemGroup></Project>')
& dotnet build (Join-Path $hostDir 'BackgroundHarness.csproj') -c Release --nologo -v q -o (Join-Path $build 'bin')
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }

$run = Join-Path $build ('run-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$arguments = @((Join-Path $build 'bin/BackgroundHarness.dll'), $run)
if ($Only) { $arguments += $Only }
& dotnet @arguments
$code = $LASTEXITCODE
# Relative to this folder: console transcripts copied into reports carry no machine paths.
Write-Output ('Evidence: Build/' + (Split-Path -Leaf $run) + '/evidence.json')
exit $code


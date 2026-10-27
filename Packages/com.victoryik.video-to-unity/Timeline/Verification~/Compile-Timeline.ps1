param(
    [Parameter(Mandatory = $true)][string] $UnityEditorData,
    [Parameter(Mandatory = $true)][string] $TimelinePackage,
    [Parameter(Mandatory = $true)][string] $RuntimeSources,
    [Parameter(Mandatory = $true)][string] $CscDll
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $PSScriptRoot 'Temp/compile'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$netstandard = Join-Path $UnityEditorData 'NetStandard/ref/2.1.0/netstandard.dll'
$engineRefs = @(Get-ChildItem -LiteralPath (Join-Path $UnityEditorData 'Managed/UnityEngine') -Filter '*.dll' | ForEach-Object FullName)
$editorRefs = @(Get-ChildItem -LiteralPath (Join-Path $UnityEditorData 'Managed') -Filter 'UnityEditor*.dll' | Where-Object Name -NE 'UnityEditor.dll' | ForEach-Object FullName)
$defines = 'UNITY_EDITOR,UNITY_EDITOR_WIN,UNITY_2020_1_OR_NEWER,UNITY_2020_2_OR_NEWER,UNITY_2021_1_OR_NEWER,UNITY_2021_2_OR_NEWER,UNITY_2022_1_OR_NEWER,UNITY_2022_2_OR_NEWER,UNITY_2022_3_OR_NEWER,TIMELINE_FRAMEACCURATE'

function Compile-Assembly([string] $AssemblyName, [string[]] $Sources, [string[]] $References) {
    $dll = Join-Path $taskOutput ($AssemblyName + '.dll')
    $rsp = Join-Path $taskOutput ($AssemblyName + '.rsp')
    $log = Join-Path $taskOutput ($AssemblyName + '.log')
    $arguments = @('/nologo', '/target:library', '/nostdlib+', '/langversion:9.0', "/define:$defines", ('/out:"' + $dll + '"'))
    $arguments += @($References | Select-Object -Unique | ForEach-Object { '/reference:"' + $_ + '"' })
    $arguments += @($Sources | ForEach-Object { '"' + $_ + '"' })
    Set-Content -LiteralPath $rsp -Value $arguments -Encoding utf8
    $compilerOutput = & dotnet $CscDll ('@' + $rsp) 2>&1
    $compilerExit = $LASTEXITCODE
    Set-Content -LiteralPath $log -Value ($compilerOutput -join [Environment]::NewLine) -Encoding utf8
    if ($compilerExit -ne 0) {
        $compilerOutput | Write-Host
        throw "Compilation failed: $AssemblyName (exit $compilerExit)"
    }
    Write-Host "PASS: $AssemblyName ($($Sources.Count) source files)"
    return $dll
}

$common = @($netstandard) + $engineRefs + $editorRefs
$timelineFiles = @(Get-ChildItem -LiteralPath (Join-Path $TimelinePackage 'Runtime') -Filter '*.cs' -Recurse | ForEach-Object FullName)
$timelineDll = Compile-Assembly 'Unity.Timeline' $timelineFiles $common
$timelineEditorFiles = @(Get-ChildItem -LiteralPath (Join-Path $TimelinePackage 'Editor') -Filter '*.cs' -Recurse | ForEach-Object FullName)
$timelineEditorDll = Compile-Assembly 'Unity.Timeline.Editor' $timelineEditorFiles ($common + @($timelineDll))
# Compile the main owner's actual sources; no substitute Runtime classes are generated.
$runtimeFiles = @((Join-Path $RuntimeSources 'VideoEffectAsset.cs'), (Join-Path $RuntimeSources 'VideoEffectPlayer.cs'))
$runtimeDll = Compile-Assembly 'VideoToUnity.Runtime' $runtimeFiles $common
$ownFiles = @(Get-ChildItem -LiteralPath $taskRoot -Filter '*.cs' | ForEach-Object FullName)
$ownDll = Compile-Assembly 'VideoToUnity.Timeline' $ownFiles ($common + @($timelineDll, $runtimeDll))
$ownEditorFiles = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Editor') -Filter '*.cs' | ForEach-Object FullName)
$ownEditorDll = Compile-Assembly 'VideoToUnity.Timeline.Editor' $ownEditorFiles ($common + @($timelineDll, $timelineEditorDll, $runtimeDll, $ownDll))
# Also compile the player-facing assemblies with no UNITY_EDITOR define or editor DLL references.
$taskOutput = Join-Path $PSScriptRoot 'Temp/compile-player'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$defines = $defines.Replace('UNITY_EDITOR,UNITY_EDITOR_WIN,', '')
$common = @($netstandard) + $engineRefs
$timelineDll = Compile-Assembly 'Unity.Timeline' $timelineFiles $common
$runtimeDll = Compile-Assembly 'VideoToUnity.Runtime' $runtimeFiles $common
$ownDll = Compile-Assembly 'VideoToUnity.Timeline' $ownFiles ($common + @($timelineDll, $runtimeDll))
Write-Host 'PASS: static API compilation only. Unity and the native decoder were not launched.'

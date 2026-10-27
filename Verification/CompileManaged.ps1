param(
    [Parameter(Mandatory=$true)][string]$UnityData,
    [Parameter(Mandatory=$true)][string]$UguiAssembly,
    [Parameter(Mandatory=$true)][string]$TimelineAssembly,
    [Parameter(Mandatory=$true)][string]$TimelineEditorAssembly,
    [string]$TestRunnerAssembly,
    [string]$NUnitAssembly,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repository 'Packages/com.victoryik.video-to-unity'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository '.validation/managed' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $UnityData 'DotNetSdkRoslyn/csc.dll'
$hostPath = Join-Path $UnityData 'NetCoreRuntime/dotnet.exe'
$references = @(Get-ChildItem -LiteralPath (Join-Path $UnityData 'NetStandard/ref/2.1.0') -Filter '*.dll' | ForEach-Object FullName)
$references += @(Get-ChildItem -LiteralPath (Join-Path $UnityData 'NetStandard/compat/2.1.0/shims/netfx') -Filter '*.dll' | ForEach-Object FullName)
$references += @(Get-ChildItem -LiteralPath (Join-Path $UnityData 'Managed/UnityEngine') -Filter '*.dll' | ForEach-Object FullName)
$references += $UguiAssembly
$editorReferences = @(Get-ChildItem -LiteralPath (Join-Path $UnityData 'Managed') -Filter 'UnityEditor*.dll' | Where-Object Name -NE 'UnityEditor.dll' | ForEach-Object FullName)
function Compile([string]$Name, [string[]]$Sources, [string[]]$Extra, [bool]$EditorBuild) {
    $subdir = if ($EditorBuild) { 'Editor' } else { 'Player' }
    $destination = Join-Path $OutputDirectory $subdir
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $target = Join-Path $destination ($Name + '.dll')
    $defines = 'UNITY_2022_3_OR_NEWER,UNITY_STANDALONE_WIN'
    if ($EditorBuild) { $defines += ',UNITY_EDITOR,UNITY_EDITOR_WIN,UNITY_INCLUDE_TESTS' }
    $args = @('/nologo','/target:library','/langversion:9','/nostdlib+','/warnaserror+', "/define:$defines", ('/out:"' + $target + '"'))
    $refs = $references + $Extra
    if ($EditorBuild) { $refs += $editorReferences }
    $args += $refs | Select-Object -Unique | ForEach-Object { '/reference:"' + $_ + '"' }
    $args += $Sources | ForEach-Object { '"' + $_ + '"' }
    $response = Join-Path $destination ($Name + '.rsp')
    Set-Content -LiteralPath $response -Value $args -Encoding utf8
    $diagnostics = & $hostPath $compiler ('@' + $response) 2>&1
    $diagnostics | Set-Content -LiteralPath (Join-Path $destination ($Name + '.log')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw ($Name + ': ' + ($diagnostics -join "`n")) }
    Write-Host "PASS $subdir/$Name ($($Sources.Count) sources)"
    return $target
}
$runtimeSources = @(Get-ChildItem -LiteralPath (Join-Path $package 'Runtime') -Filter '*.cs' | ForEach-Object FullName)
$timelineSources = @(Get-ChildItem -LiteralPath (Join-Path $package 'Timeline') -Filter '*.cs' | ForEach-Object FullName)
foreach ($editorBuild in @($true, $false)) {
    $runtime = Compile 'VideoToUnity.Runtime' $runtimeSources @() $editorBuild
    $timeline = Compile 'VideoToUnity.Timeline' $timelineSources @($runtime, $TimelineAssembly) $editorBuild
    if ($editorBuild) {
        $timelineEditorSources = @(Get-ChildItem -LiteralPath (Join-Path $package 'Timeline/Editor') -Filter '*.cs' | ForEach-Object FullName)
        $timelineEditor = Compile 'VideoToUnity.Timeline.Editor' $timelineEditorSources @($runtime, $timeline, $TimelineAssembly, $TimelineEditorAssembly) $true
        $editorSources = @(Get-ChildItem -LiteralPath (Join-Path $package 'Editor') -Filter '*.cs' -Recurse | Where-Object FullName -NotMatch '[\\/][^\\/]*~[\\/]' | ForEach-Object FullName)
        $editor = Compile 'VideoToUnity.Editor' $editorSources @($runtime, $TimelineAssembly) $true
        if ($TestRunnerAssembly -and $NUnitAssembly) {
            $testSources = @(Get-ChildItem -LiteralPath (Join-Path $package 'Tests/Editor') -Filter '*.cs' | ForEach-Object FullName)
            $null = Compile 'VideoToUnity.Tests' $testSources @($runtime, $timeline, $editor, $TimelineAssembly, $TestRunnerAssembly, $NUnitAssembly) $true
        }
    }
}
Write-Host 'Managed compilation only. This does not launch Unity or validate native shader/video behavior.'

param(
    [Parameter(Mandatory = $true)][ValidateSet('urp','hdrp')][string]$Pipeline,
    [Parameter(Mandatory = $true)][ValidateSet('Create','Install','Test')][string]$Phase,
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$FixturePath
)
$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$validation = Join-Path $repository '.validation'
$project = Join-Path $validation $Pipeline
$subject = Join-Path $validation 'subject'
$output = Join-Path $validation ($Pipeline + '-results-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $validation -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $validation '.gitignore'))) { Set-Content -LiteralPath (Join-Path $validation '.gitignore') -Value '*' -Encoding utf8 }
$active = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($project) }
if ($active) { throw "This dedicated project already has a Unity process: $($active.ProcessId)" }
$log = Join-Path $validation ($Pipeline + '-' + $Phase.ToLowerInvariant() + '.log')
$arguments = '-batchmode -force-d3d11 -logFile "' + $log + '" '
if ($Phase -eq 'Create') {
    if (Test-Path -LiteralPath $project) { throw 'Refusing to replace an existing test project.' }
    $template = if ($Pipeline -eq 'urp') { 'com.unity.template.3d-cross-platform-17.0.14.tgz' } else { 'com.unity.template.3d-high-end-17.0.7.tgz' }
    $templatePath = Join-Path (Split-Path -Parent $UnityEditor) ('Data/Resources/PackageManager/ProjectTemplates/' + $template)
    if (-not (Test-Path -LiteralPath $templatePath)) { throw "Required official template missing: $templatePath" }
    $arguments += '-quit -createProject "' + $project + '" -cloneFromTemplate "' + $templatePath + '"'
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt'))) { throw 'Create the dedicated project first.' }
    $editorFolder = Join-Path $project 'Assets/PipelineValidation/Editor'
    New-Item -ItemType Directory -Path $editorFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageBootstrap.cs') -Destination (Join-Path $editorFolder 'PackageBootstrap.cs') -Force
    $arguments += '-projectPath "' + $project + '" '
    if ($Phase -eq 'Install') {
        if (-not (Test-Path -LiteralPath $subject)) { Copy-Item -LiteralPath $PackagePath -Destination $subject -Recurse }
        $arguments += '-executeMethod VideoToUnity.PipelineValidation.PackageBootstrap.Install -vtuPackage "' + $subject + '"'
    } else {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PipelineTestSetup.cs') -Destination (Join-Path $editorFolder 'PipelineTestSetup.cs') -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PipelineCanvasProbe.cs') -Destination (Join-Path $project 'Assets/PipelineValidation/PipelineCanvasProbe.cs') -Force
        $streaming = Join-Path $project 'Assets/StreamingAssets'
        New-Item -ItemType Directory -Path $streaming,$output -Force | Out-Null
        $fixtureName = [System.IO.Path]::GetFileName($FixturePath)
        Copy-Item -LiteralPath $FixturePath -Destination (Join-Path $streaming $fixtureName) -Force
        $arguments += '-executeMethod VideoToUnity.PipelineValidation.PipelineTestSetup.Run -vtuPipeline ' + $Pipeline + ' -vtuOutput "' + $output + '" -vtuFixtureName "' + $fixtureName + '"'
    }
}
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
$state = [pscustomobject]@{ pipeline=$Pipeline; phase=$Phase; pid=$process.Id; project=$project; log=$log; output=$output; started=(Get-Date).ToUniversalTime().ToString('o'); arguments=$arguments }
$state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $validation ($Pipeline + '-process.json')) -Encoding utf8
$state | ConvertTo-Json

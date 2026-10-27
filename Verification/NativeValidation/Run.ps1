param(
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [Parameter(Mandatory=$true)][ValidateSet('2022','6')][string]$Generation,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ProjectName,
    [switch]$Installed
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repository ('.validation/' + $ProjectName)
$marker = Join-Path $projectPath '.video-to-unity-validation'
if (!(Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw 'Unity editor executable not found.' }
if (Test-Path -LiteralPath $projectPath) {
    if (!(Test-Path -LiteralPath $marker)) { throw 'Existing folder is not a marked disposable validation project.' }
} else {
    New-Item -ItemType Directory -Path (Split-Path -Parent $projectPath) -Force | Out-Null
}
function Invoke-Editor([string]$Label, [string[]]$Options) {
    $log = Join-Path $repository ('.validation/' + $ProjectName + '-' + $Label + '.log')
    $arguments = @('-batchmode','-force-d3d11','-logFile',('"' + $log + '"')) + $Options
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WorkingDirectory $repository -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(1200000)) { $process.Kill(); throw "$Label timed out; stopped only the editor launched by this runner." }
    if ($process.ExitCode -ne 0) { throw "$Label failed with exit code $($process.ExitCode). See $log" }
    Write-Host "PASS $ProjectName / $Label"
}
if (!(Test-Path -LiteralPath $projectPath)) {
    Invoke-Editor 'create' @('-createProject',('"' + $projectPath + '"'),'-quit')
    New-Item -ItemType File -Path $marker | Out-Null
}
$prepareArgs = @((Join-Path $PSScriptRoot 'Prepare.py'),'--name',$ProjectName,'--generation',$Generation)
if ($Installed) { $prepareArgs += '--installed' }
& python @prepareArgs
if ($LASTEXITCODE -ne 0) { throw 'Native test preparation failed.' }
$projectArgs = @('-projectPath',('"' + $projectPath + '"'))
if (!$Installed) { Invoke-Editor 'install' ($projectArgs + @('-executeMethod','V3PackageBootstrap.Install')) }
foreach ($space in @('Gamma','Linear')) {
    Invoke-Editor ('set-' + $space) ($projectArgs + @('-executeMethod',('V3PackageBootstrap.' + $space),'-quit'))
    foreach ($platform in @('EditMode','PlayMode')) {
        $assembly = if ($platform -eq 'EditMode') { 'VideoToUnity.Validation.Native' } else { 'VideoToUnity.Validation.PlayMode' }
        $label = ($space + '-' + $platform).ToLowerInvariant()
        $results = Join-Path $projectPath ('final-' + $label + '.xml')
        if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results }
        Invoke-Editor $label ($projectArgs + @('-runTests','-testPlatform',$platform,'-assemblyNames',$assembly,'-testResults',('"' + $results + '"')))
        [xml]$report = Get-Content -LiteralPath $results -Raw
        if ([int]$report.'test-run'.failed -ne 0 -or [int]$report.'test-run'.skipped -ne 0 -or [int]$report.'test-run'.passed -eq 0) { throw "Incomplete tests: $results" }
        Write-Host "$($report.'test-run'.passed) passed, zero failed or skipped."
    }
}

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fixture = Get-Content -LiteralPath (Join-Path $root 'Fixtures/fixture.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $root $fixture.file) -Algorithm SHA256).Hash -ne $fixture.sha256) { throw 'Fixture hash mismatch' }
$total = 0
foreach ($pipeline in @('urp','hdrp')) {
    $directory = Join-Path $root ('Results/' + $pipeline)
    $result = Get-Content -LiteralPath (Join-Path $directory 'results.json') -Raw | ConvertFrom-Json
    if (-not $result.passed -or $result.error -or $result.cases.Count -ne 7 -or $result.pipeline -ne $pipeline) { throw "Failed/incomplete result: $pipeline" }
    if ($result.fixture -ne 'packed-alpha.mp4' -or $result.fixtureSha256 -ne $fixture.sha256) { throw 'Final native run did not use the canonical fixture' }
    if ($result.width -ne 256 -or $result.height -ne 128 -or $result.colorSpace -ne 'Linear') { throw 'Unexpected render settings' }
    foreach ($case in $result.cases) {
        if ($case.samples.Count -ne 4 -or -not $case.passed -or $case.decoderFrame -lt 3 -or $case.cameraFrames -lt 4) { throw "Incomplete native case: $($case.name)" }
        if (-not (Test-Path -LiteralPath (Join-Path $directory $case.capture))) { throw 'Missing native image capture' }
        foreach ($sample in $case.samples) {
            $channelError = 0.0
            foreach ($channel in @('r','g','b','a')) { $channelError = [Math]::Max($channelError, [Math]::Abs($sample.actual.$channel - $sample.expected.$channel)) }
            if (-not $sample.passed -or $channelError -gt $result.tolerance -or [Math]::Abs($channelError - $sample.maxError) -gt 0.000001) { throw "Pixel evidence mismatch: $pipeline/$($case.name)/$($sample.name)" }
            $total++
        }
    }
}
$core = Get-Content -LiteralPath (Join-Path $root 'core-source-fingerprints.json') -Raw | ConvertFrom-Json
if ($core.Count -ne 5 -or @($core | Where-Object { -not $_.matchesSnapshot }).Count -ne 0) { throw 'Current core source did not match the tested snapshot' }
$package = Join-Path $root '../../Packages/com.victoryik.video-to-unity'
foreach ($entry in $core) {
    if ((Get-FileHash -LiteralPath (Join-Path $package $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Core source changed since native verification: $($entry.path)" }
}
Write-Output "PASS: fixture checksum, 14 cases, $total recorded RGBA checks, native render/decode counters and image presence. This checks the saved evidence; it does not launch Unity."

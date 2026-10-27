# Unity 6 URP / HDRP native Canvas-video verification

**Final canonical sample PASS: URP 17.3.0 and HDRP 17.3.0, 7 cases / 28 RGBA pixel checks each.** Both pipelines were rerun with the regenerated canonical `Samples~/Basic/Media/packed-alpha.mp4` after the converter color-tag fix. This is native Canvas rendering through the actual SRP camera with a real VideoPlayer decoder. No Graphics.Blit-only substitute, Computer Use, screen UI automation, or production-runtime changes were used.

Temporary projects, the tested package snapshot and full machine logs remain under ignored `.validation/`. The committed files contain reproduction helpers and portable evidence.

## Recorded environment

| Setting | URP | HDRP |
| --- | --- | --- |
| Editor | Unity 6000.3.23f1 (09d2ecc7fb28) | Same |
| Pipeline | Universal RP 17.3.0 | High Definition RP 17.3.0 |
| uGUI | 2.0.0 | 2.0.0 |
| GPU / API | NVIDIA GeForce RTX 5080 / Direct3D11, level 11.1 | Same |
| Color space | Linear | Linear |
| Actual pipeline instance | UniversalRenderPipeline | HDRenderPipeline |
| Active template asset | `Assets/Settings/PC_RPAsset.asset` | `Assets/Settings/HDRP High Fidelity.asset` |
| Camera target | 256×128, ARGBHalf, Linear, depth 24, AA 1 | Same |
| Camera | Orthographic, size 0.5, aspect 2, position (0,0,-10), near/far 0.1/100 | Same |
| Background | Linear RGBA (0,0,1,0) | Same |
| Postprocessing | Disabled on UniversalAdditionalCameraData | FrameSettings Postprocess=false and ExposureControl=false; overrides explicitly enabled |
| Additional settings | AA=None, HDR output disabled; camera HDR allowed | Color clear, AA=None, volume layer mask=0; color buffer changed through SerializedObject to **R16G16B16A16** for alpha output |

HDRP's RGBA color buffer configuration follows its [alpha-channel documentation](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/Alpha-Output.html). These are intentional diagnostic settings in new isolated test projects, not edits to the plugin or another project.

## Cases and measurements

Each pipeline passed the following seven cases:

1. Empty camera background control.
2. World Space Canvas, no mask.
3. World Space Canvas, standard uGUI `Mask`.
4. World Space Canvas, `RectMask2D`.
5. Screen Space Camera Canvas, no mask.
6. Screen Space Camera Canvas, standard uGUI `Mask`.
7. Screen Space Camera Canvas, `RectMask2D`.

The video naturally advanced to decoder frame **3** before pausing for deterministic pixel capture. Each case waited for additional `RenderPipelineManager.endCameraRendering` callbacks on the actual test camera; samples came from `camera.targetTexture` via ReadPixels. The test did not call `Camera.Render`, `Graphics.Blit`, or a shader-only synthetic rendering path.

World Space Canvas dimensions were 256×128 UI units at 1/128 scale, producing a 2×1 world-space rectangle. Screen Space Camera Canvas used the same camera at plane distance 1. The child RawImage/View occupied 256×128. Mask parents were centered 128×128 rectangles; `Mask.showMaskGraphic=false`. No user input or raycasting was needed.

Samples were taken at (32,96), (96,96), (160,96), (224,96), safely away from the bottom moving marker and quadrant edges. All four channels were compared against expected linear values, with a declared per-channel tolerance of 0.07. **The observed maximum RGBA error was only 0.00390625 in both pipelines.**

| Region | Actual RGBA, both pipelines | Expected behavior |
| --- | --- | --- |
| Transparent | (0, 0, 1, 0) | Background RGB and zero alpha retained |
| Opaque black | (0, 0, 0, 1) | Black artwork preserved, fully opaque |
| Half-red over blue | (0.5024414, 0, 0.4960938, 0.5039063) | Approximately half red / half background, alpha about 0.5 |
| Opaque green, unmasked | (0, 1, 0, 1) | Fully opaque original green |
| Green region, outside either mask | (0, 0, 1, 0) | Video clipped; original background restored |

The JSON files contain every case, exact floating-point samples, expected values, decoder frame/time, camera render counts, and camera settings. PNG files are viewable diagnostics containing linear values and alpha, not display-calibrated screenshots. Numeric RGBA results are authoritative.

- [Summary](Results/summary.json)
- [URP full results](Results/urp/results.json), [configuration](Results/urp/configuration.txt), [native log excerpt](Results/urp/native-log-excerpt.txt)
- [HDRP full results](Results/hdrp/results.json), [configuration](Results/hdrp/configuration.txt), [native log excerpt](Results/hdrp/native-log-excerpt.txt)
- [URP native World Space output](Results/urp/WorldSpace-none.png), [HDRP masked output](Results/hdrp/WorldSpace-Mask.png)
- [Prior tagged diagnostic fixture results](Results/diagnostic/summary.json) are preserved separately; they are not substituted for the final canonical run.

## Subject and fixture provenance

The package under test was copied once, read-only, into `.validation/subject`; UPM installed that local snapshot. The **32 recorded C#/shader/asmdef/package-JSON hashes remained identical after testing**. [subject-hashes.json](subject-hashes.json) identifies that package snapshot, including the runtime View implementation using IMaterialModifier and private `_VideoTex`. Before the canonical rerun, all **five core runtime/shader/asmdef files were independently rehashed against the current core source and matched the tested snapshot**; see [core-source-fingerprints.json](core-source-fingerprints.json). The later converter implementation is not invoked in this rendering suite. No hand edit was made to runtime, shader, converter, package manifests, the old Editor UI, or README.

The final canonical [packed-alpha.mp4](Fixtures/packed-alpha.mp4) fixture is bundled unchanged (5,068 bytes):

`SHA256 548C364E4B1DB1A2244A26C973886097E0FF7E0330CA872F04BD8D7B60695CA3`

It contains 24 frames at 12 fps, coded size 256×64, logical RGB content 128×64. Logical x=0–31 is transparent, 32–63 opaque black, 64–95 half-red, 96–127 opaque green; the bottom blue marker is excluded from sampling. See [fixture metadata](Fixtures/fixture.json).

[ffprobe evidence](Fixtures/canonical-ffprobe.json) confirms H.264/yuv420p, limited range, bt709 matrix, bt709 primaries and **iec61966-2-1 (sRGB) transfer**. Each final Unity results JSON independently computes and records the SHA-256 of the actual StreamingAssets file it decoded. This verifies rendering of the regenerated canonical artifact; it does not independently execute or validate the conversion process, and NativeAlpha WebM is outside this suite.

The earlier [packed-tagged.mp4](Fixtures/packed-tagged.mp4) diagnostic fixture is retained with [separate metadata](Fixtures/diagnostic-fixture.json), SHA-256 `063DAE750FF7A0B252D5E647DF010655FC373A16E49E378C42B626E5A4B5D92A`. Its earlier native run also passed, but the top-level Results/urp and Results/hdrp directories contain the final canonical rerun.

## Reproduction

Use Windows with the installed Unity 6000.3.23f1 editor and its official templates:

- URP: `com.unity.template.3d-cross-platform-17.0.14.tgz`
- HDRP: `com.unity.template.3d-high-end-17.0.7.tgz`

The launcher uses the documented [`-createProject` / `-cloneFromTemplate`](https://docs.unity.com/en-us/engine/6000.3/manual/unity-editor/command-line-arguments/editor) workflow. Package installation uses `UnityEditor.PackageManager.Client.AddAndRemove`; manifests are not edited by hand. Every process starts through `Start-Process -WindowStyle Hidden -batchmode -force-d3d11`. `-nographics` is deliberately absent because real GPU rendering is required.

```powershell
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.3.23f1/Editor/Unity.exe'
$package = '<absolute path to the package matching subject-hashes.json>'
$fixture = (Resolve-Path './Verification/PipelineValidation/Fixtures/packed-alpha.mp4').Path

# Run each phase separately; wait for its dedicated PID to terminate and inspect its log.
# Repeat for hdrp. The launcher rejects an already-running test project.
./Verification/PipelineValidation/Invoke-PipelineValidation.ps1 -Pipeline urp -Phase Create -UnityEditor $editor -PackagePath $package -FixturePath $fixture
./Verification/PipelineValidation/Invoke-PipelineValidation.ps1 -Pipeline urp -Phase Install -UnityEditor $editor -PackagePath $package -FixturePath $fixture
./Verification/PipelineValidation/Invoke-PipelineValidation.ps1 -Pipeline urp -Phase Test -UnityEditor $editor -PackagePath $package -FixturePath $fixture
```

Use a fresh worktree/validation directory for a fresh subject; Create refuses to replace existing projects and the subject snapshot is reused deliberately. The launcher writes `.validation/<pipeline>-process.json` with the exact PID, arguments, log and output directory. New Test launches receive a timestamped output directory to prevent stale results being confused with a new run. Copy that run's configuration/results/PNGs after completion; do not infer success merely from starting the process.

`PipelineTestSetup.cs` creates an empty test scene and enters Play Mode programmatically. `PipelineCanvasProbe.cs` drives the decoder, creates actual Canvas hierarchies, checks RGBA, writes results and requests EditorApplication.Exit(0) on success or a nonzero exit on failure. Its timeout is 180 seconds after Play Mode starts; the asynchronous package bootstrap has a 600-second timeout. `PackageBootstrap.cs` is the only helper copied before package installation, avoiding a reference to a not-yet-installed runtime assembly.

Run `./Verification/PipelineValidation/Verify-Evidence.ps1` to check the portable canonical fixture checksum, matching per-run fingerprints, 14 case results, 56 saved RGBA comparisons, native decode/render counters and captures. This evidence check does not launch Unity or stand in for a new native run.

## Limits

- Final native runs passed. The preliminary helper compile failed on an ambiguous `PackageInfo` name; fully qualifying the helper reference fixed it. This was not a plugin compilation/rendering failure.
- All creation/install/test processes for these projects terminated, including the preliminary compile attempts; the two final test PIDs were confirmed absent before collection. No monitoring process remains. The report records completion and the harness-requested exit, not a separately retained Windows exit-code handle.
- Covered Windows Direct3D11, Unity 6.3, Linear, the above active pipeline assets/settings, one packed fixture, and simple rectangular masks. Not covered: D3D12/Vulkan/Metal, Gamma URP, HDR display, postprocessing/exposure effects, custom renderer features, soft/nested masks, complex scene depth/lighting, mobile/WebGL, NativeAlpha codecs, or full game/player builds.
- Screen Space Overlay is not included here: this suite intentionally captures the actual SRP camera target using World Space and Screen Space Camera. The separate Built-in suite covers Gamma/Linear, UI/Undo/scene save, PlayMode and Timeline. Overlay object creation is checked there; final Overlay backbuffer pixels are not captured.
- No pipeline incompatibility was observed under the recorded settings. No runtime fix was made or needed by this suite. Subject/fixture hashes and complete pixel evidence should accompany any integration verdict.
- No Computer Use or desktop/browser UI automation was used.

State: **Native verification complete; integrated into preview.2.**

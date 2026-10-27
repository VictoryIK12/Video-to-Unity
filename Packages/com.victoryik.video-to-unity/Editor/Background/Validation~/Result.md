# Background backend validation

`run.ps1` compiles the production sources (`Background/*.cs` and `Conversion/VideoConverter.cs`) twice: for netstandard2.1 / C# 9 with warnings as errors and no UnityEngine reference, and into a .NET 9 console host with `BackgroundHarness.cs`. The host generates fixtures with the real FFmpeg on PATH and drives the public `BackgroundVideoProcessor.Start` API. Unity is not launched. Console lines and `evidence.json` pass through the harness's path scrubber, so recorded evidence carries no machine-specific absolute paths.

Recorded run: FFmpeg N-126374-g089a48eb36-20260831 (Windows), .NET SDK 9.0.314, **28/28 PASS**. Raw output: `evidence.json`.

| Area | Result |
| --- | --- |
| Auto analysis | Black border → 0,0,0 (0.975); green → 0,175,63 (1.000); size/fps/duration metadata correct |
| Packed H.264 / VP8 alpha WebM | 48 frames each, decoded alpha 0..255, background ≤6, foreground colors within codec tolerance on every frame; packed worst timing error 0 ms |
| Exact clock | 60 fps source on a 1/60000 clock → packed output time base 1/60000, every frame exactly 1000 ticks; 30000/1001 source → 1/30000, every frame 1001 ticks. The intermediate is FFV1 BGRA in QuickTime on the source's integer clock (Matroska rounded to milliseconds) |
| Straight RGB | Soft ramp, 33 111 partial-alpha pixels: decoded red within 6 codes of the straight session output (premultiplied would differ by up to 93); alpha error ≤2 (H.264) / ≤5 (VP8) |
| Preview | Requested times map to the displayed frame (0.499 s → 11, 0.5 → 12, past end → last); top-down rows; full-size frames byte-identical to a sequential decode; no upscaling. A seek starts at the keyframe opening the target's GOP and selects the exact frame index |
| Open GOP | The fixture reproduces the strict-seek decoder error; the strict seek then falls back to an exact sequential decode. Five-frame analysis completes and previews of frames 23/35/47/62/95 (including a leading B-frame) are byte-identical to a sequential decode |
| Transparent sources | 50% alpha ProRes 4444 and VP8 alpha WebM are rejected in Analyze, Preview and Convert with a redirect to the alpha conversion; existing alpha is never replaced. ProRes 4444 whose alpha channel is opaque converts normally |
| Parallel matte | 120 real frames over 5 option sets (border on/off, smoothing 0/.15/.5/.9, protected regions, 1280×720) byte-identical to the pre-audit sequential implementation |
| Frame hash | MurmurHash3_x64_128 reproduces SMHasher's verification value 0x6384BA69; with FFmpeg's seed it equals `ffmpeg -f hash -hash murmur3` for 12 lengths up to 8 294 405 bytes at an unaligned offset. Every intermediate frame is compared with FFmpeg's framehash of the decoded file |
| Protected region | Protected corner alpha 255 with auto and manual colors; outside still 0 |
| Low confidence | Mixed border 0.50: Analyze returns a warning; auto Preview/Convert fail with a manual-selection message; manual options succeed |
| Timing (WebM) | 29.97 fps, 1.25 s start offset, AAC audio: 60 frames, output starts at 0, worst 0.5 ms (WebM millisecond clock), audio dropped |
| Rejections | VFR (names frame 20, 62.5 ms vs 41.667 ms), odd 191×108, HDR PQ, display rotation, two video streams, truncated MP4 |
| Result checks | Nothing removed → fails before encoding; whole video transparent → success with `Warning` |
| Lifecycle | Cancel/Dispose return in 0–1 ms; Completion becomes Canceled during frame processing and during the final converter stage; source write-locked during work and unchanged afterwards; temp directory clean; pre-existing sentinel untouched; concurrent jobs get unique outputs; result survives until Dispose |
| Memory | 1280×720×240 (843 MiB raw RGBA) streamed with +3 MiB peak harness working set (host process only) |
| Paths and errors | Korean, spaces, `& ^ % ; ' ! ( ) [ ] $ #` in source and temp paths. A tool that cannot start is reported with the OS reason only, for both the background job and VideoConverter; the framework message would also name the working directory |

## Measured performance

Real 1920×1080 60 fps H.264 clip, 396 frames (6.6 s, not included), manual black key, PackedH264. Ryzen 7 9800X3D (16 logical processors), NVMe temp drive, same FFmpeg build. "Mono" is Unity 2022.3.62f3's MonoBleedingEdge runtime running the same code outside the Editor (Release code optimization, Unity's default).

| | Before (ec9e56d) | After |
| --- | --- | --- |
| Total, .NET 9 | 100.8 s | 33.7 s |
| Total, Mono | 150.6 s | 49.7 s |
| Matte + intermediate stage, .NET 9 / Mono | 9.8 / 53.5 s | 5.1 / 15.0 s |
| Converter timestamp scan / alpha reference / color reference / encode, .NET 9 | 17.5 / 17.2 / 17.8 / 20.2 s | 2.9 / 3.0 / 3.5 / 7.9 s |
| Peak private memory, host / FFmpeg children (.NET 9) | 71 / 829 MiB | 69 / 1 304 MiB |
| Peak temporary disk | 3 524 MiB | 3 524 MiB |

Per 1080p frame, the matte takes 13.5 → 3.0 ms on .NET 9, 52.9 → 7.5 ms on Mono and 57.6 → 8.8 ms on Mono Debug. The integrity hash (SHA-256 → MurmurHash3) takes 3.3 → 0.8 ms on .NET 9 and 67 → 19 ms on Mono.

Where the time went: the converter decoded the lossless intermediate four times single-threaded (`-threads 1`, and ffprobe's default), about 17 s each. Decoding and filtering are now threaded; all seven verification chains (FFV1 alpha and both color references, packed matte and color, VP8 alpha and color) produced identical SHA-256 output with 1, 8, 16 and automatic threads. Every FFmpeg stage is capped at min(8, logical processors) threads and runs below normal priority; the matte uses at most min(8, logical processors − 1) workers. On the packed encode, x264 at 2/4/8/12/automatic threads took 14.3/10.4/8.1/7.1/6.7 s with peak private memory of 1 038/1 162/1 411/1 662/2 045 MiB, hence the cap of 8.

Memory figures come from .NET 9 runs: the host's private bytes, and the summed private bytes of ffmpeg/ffprobe processes started by the run, sampled every 0.2 s. Mono does not report process memory on Windows; its FFmpeg children are the same.

## Limits

Constant frame rate only. The final converter still writes disk-backed alpha and RGB references (about 4 bytes per source pixel per frame) beside the intermediate, 3.5 GB peak for this 6.6 s clip. Open-GOP sources preview and analyze exactly but decode from the first frame, which is slower for long videos. AlphaWebM output timing follows WebM's millisecond clock. No audio output. Only this Windows FFmpeg build was exercised, and Mono was measured outside the Editor.

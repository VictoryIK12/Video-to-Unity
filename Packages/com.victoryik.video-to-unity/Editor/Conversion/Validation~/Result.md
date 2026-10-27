# Color metadata and decoded RGB validation — 2026-10-27

## Threading follow-up (final audit)

All 43 standalone cases pass again after decoding, filtering and encoding were made multithreaded (capped at min(8, logical processors) per FFmpeg stage, below normal priority) and tool start failures stopped quoting the working directory. `evidence.json` was regenerated from that run with machine paths replaced by placeholders. Its output SHA-256s differ from the earlier record because x264 and libvpx thread counts change the bitstream; every verification bound below still applies to the actual output. The seven decode/filter chains used for verification produced identical bytes with 1, 8, 16 and automatic threads (see `../../Background/Validation~/Result.md`).

Scope: production VideoConverter.cs, using a standalone managed host and real FFmpeg.

**43 standalone cases passed: the previous 32 plus 11 color/header regressions and diagnostic conversions.** Production C# also compiled against Unity 2022.3 references with warnings treated as errors. Native Unity checks are recorded separately in the repository verification report.

## Findings and fix

1. Command-line primaries/transfer options alone did not survive the filter/encoder path when source frames lacked these tags. The new filter graph assigns `setparams` after color conversion and again after packed `hstack`; encoder/container options agree with those frame values. H.264 also receives explicit x264 primaries/transfer/matrix parameters and `+write_colr`. Known source transfer tags (including sRGB) are preserved; missing transfer defaults to BT.709.
2. VP8's matrix is a separate problem. Main's old WebM declared BT.709 in the container, but FFmpeg's native VP8 decoder reported BT.601 (`bt470bg`) frames. `libvpx` instead dropped the matrix tag and decoded RGB using the 601 fallback. Encoding 709-valued YUV produced red `[232,0,1]` and green `[19,255,8]` in the experiment. **VP8 now contains actual 601-valued YUV and declares `bt470bg`**, while retaining BT.709 RGB primaries and the source transfer. The same experiment then decoded red `[253,0,0]` and green `[0,255,0]` with both decoders. This follows [RFC 6386 §9.2](https://www.rfc-editor.org/rfc/rfc6386#section-9.2) and the observed [libvpx decoder behavior](https://ffmpeg.org/doxygen/trunk/libvpxdec_8c_source.html).
3. The old YUV-plane comparison could pass while a decoder applied the wrong matrix. Verification now compares every **decoded RGB** channel, using the output decoder's own interpretation without overriding its matrix. The source reference accounts for inevitable 4:2:0 subsampling and YUV-versus-YUVA upsampling differences. Separate saturated/midtone tests compare patches directly to independently decoded source RGB. The RGB bound is stricter: maximum 16 codes and mean ≤2 per frame, replacing maximum 48/mean 4 on YUV.
4. Output headers and every decoded frame must have the expected range, matrix, primaries and transfer. H.264's first access unit is copied to raw Annex B and its SPS is independently probed so MP4 container tags cannot mask missing VUI. The Python suite also checks actual MP4 `nclx` fields.

## Diagnostic source snapshot

Read-only input snapshot SHA-256: `67ce500b60a712905937b601ac8ce108d4138d9eb330bfb1d9066e5bbae76564`. The source had BT.709 matrix metadata but missing primaries/transfer. Both conversions preserve 128×64 content, 24 frames, alpha 0–255 and timing; packed encoded width is 256.

| Output | Matrix / primaries / transfer / range | Source red → decoded red | Source green → decoded green | Full RGB max / mean error¹ |
|---|---|---|---|---|
| PackedH264 | bt709 / bt709 / bt709 / tv | `[255,0,0]` → `[253,0,0]` | `[0,255,0]` → `[0,254,0]` | 5 / 0.122182 |
| AlphaWebM | bt470bg / bt709 / bt709 / tv | `[255,0,0]` → `[253,0,0]` | `[0,255,0]` → `[0,255,0]` | 8 / 0.024875 |

¹ Full RGB comparison is against the decoded source's canonical target 4:2:0 representation; direct decoded-source patch error is at most 2 codes in every frame. No transfer/gamma operation is applied to numeric alpha coverage.

Output SHA-256:

- Packed: `f3415804540429626cf6d181bb69ed445d2209f80bc173cadad92882ad3f7b1c`.
- WebM: `d8b6b346f886a6fe5eb386ad5dee2fd4f353a55bbe9787dda5422b7bfcebe318`.

## Regression coverage

The original 32 cases still exercise independently generated ProRes/VP8-alpha sources into both formats, all alpha pixels, asymmetric corners and moving marks, real color contents, frame count/timing, VFR and single frames, nonzero start, audio-first sources, Korean/metacharacter paths, arbitrary extensions, concurrent jobs, cancellation/disposal at every phase, no overwrite and cleanup. Opaque/fake-alpha/odd/rotated/anamorphic/HDR inputs and spatial corruption are still rejected. Forced `libvpx` remains mandatory for all VP8 alpha reads.

Added cases:

- BT.709-matrix sources missing primaries/transfer → both outputs (2).
- Properly tagged sRGB-transfer sources → both outputs, preserving transfer code 13 (2).
- Corrected 601 VP8-alpha output used as input → both formats (2).
- A deliberate WebM containing 709-valued YUV but plausible 601 header tags is rejected by the actual RGB comparison at byte 0, error 21 (1).
- Missing H.264 primaries/transfer rejected; then a separate file with manually correct MP4 `colr` but missing SPS tags also rejected by the raw-bitstream check (2).
- Main's actual MOV → both formats, direct decoded-source patch checks across all 24 frames (2).

Every successful conversion is additionally checked for output stream and **all frame** color tags; every packed output gets independent `nclx` and raw-SPS inspection. Tagged/un-tagged swatches include red, green, blue, magenta, cyan, gray and midtone RGB. Direct source-patch error is ≤3 for ProRes conversions and ≤4 across the extra VP8→H.264 generation. Cancellation returned immediately to the caller; complete shutdown/cleanup was 1–119 ms in this run. No job files remained.

## Reproduction and evidence

From the repository root:

```text
python Packages/com.victoryik.video-to-unity/Editor/Conversion/Validation~/validate.py --main-fixtures Packages/com.victoryik.video-to-unity/Samples~/Basic
```

Omit `--main-fixtures` for the portable, self-generated **41-case** suite; passing the optional read-only Basic directory adds the two main-sample cases. The script never edits that directory. `--quick` skips the eight longer phase-cancellation cases.

Recorded run: `Validation~/Build/run-e30bfa898774/`, including generated sources, real codec outputs, wrong-matrix/SPS fixtures and complete `results.json`. Corrected main outputs are `main-diagnostic-source-packed-666988.mp4` and `main-diagnostic-source-webm-3b9044.webm` there. `evidence.json` is the portable committed record with SHA-256s, tags, RGB errors and actual decoded patch values. Generated media/binaries remain ignored and are not committed.

Environment: Windows, FFmpeg `N-126374-g089a48eb36-20260831`; .NET 9 target, SDK 10.0.400. Managed compile: Unity 2022.3.62f3's Roslyn plus `NetStandard/ref/2.1.0/netstandard.dll`, C# 9, `/warnaserror+`, zero errors/warnings. Only the compiler was invoked.

Native integration must regenerate its media from the authoritative MOV with this converter and run its separate native importer/playback/Linear checks. Existing incorrectly encoded WebM is not automatically repaired. Other FFmpeg versions, VP9, macOS/Linux execution and native device/browser playback remain outside this standalone suite's verified results. Input/fidelity limits and packed-matte sampling semantics remain documented in `../Conversion.md`.

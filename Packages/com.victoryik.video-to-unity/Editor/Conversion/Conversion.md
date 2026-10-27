# Native asynchronous converter

`VideoToUnity.Editor.VideoConverter` implements the v3 conversion contract with no Unity dependency. It refactors the process lifecycle, bounded diagnostics and temporary-file ownership pattern from the existing editor converter. There is no filename-extension or original-project restriction.

```csharp
using (var conversion = VideoConverter.Start(sourcePath, existingTempDirectory,
    ffmpegExecutable, ffprobeExecutable, VideoConversionFormat.PackedH264))
{
    var result = await conversion.Completion;
    File.Copy(result.FilePath, newDestinationPath, overwrite: false);
    // Import/create the effect asset on the Unity main thread, outside this converter.
}
```

`PackedH264` is the optional/default format. `Completion` faults on validation/tool failure and is **Canceled** on cancellation. `Status` can be read on the UI thread. `Cancel()` and `Dispose()` do not wait on processes. Disposing cancels work and deletes the temporary result after all writers and pipe readers have stopped. A successful result remains available until disposal. The caller owns copying to a new permanent destination, AssetDatabase work and importer settings.

`Result.Width`/`Height` are the original content dimensions. Packed encoded width is twice `Width`. `Frames` is the actual decoded count; alpha extrema are decoded 8-bit output coverage values (0–255), not ProRes's original high-bit-depth range.

## Conversion and verification

- All input formats must have a real decodable alpha plane with at least one non-opaque pixel representable at 8-bit precision. ProRes 4444 MOV and VP8-alpha WebM are independently tested. VP8 source reads, source conversion and output verification explicitly select `libvpx`; metadata such as `alpha_mode=1` is never sufficient. VP9 selects `libvpx-vp9` but is not covered by the recorded fixture suite.
- Packed output is RGB on the left and neutral gray alpha on the right, H.264 High profile, `yuv420p`, limited range, `+faststart`, no audio. Both halves have the original width/height; no resizing, padding, rotation, or spatial cropping is applied to source content. High quality with capped quantization avoids severe loss of isolated matte features.
- AlphaWebM output is actual VP8 `yuva420p` with alternate references disabled, not an opaque WebM bearing an alpha tag. It encodes **BT.601 matrix coefficients** (`bt470bg`) rather than BT.709 YUV: VP8 decoders use 601-like coefficients even if a container says 709. This is a real matrix conversion, not relabeling the same YUV bytes. RGB primaries remain BT.709; the matrix, primaries and transfer function are separate properties.
- Color metadata is set on filtered frames, encoder options and container options. H.264 also receives explicit x264 VUI parameters and MP4 `write_colr`. Both output stream headers and every decoded frame must report the expected range, matrix, primaries and transfer. H.264 additionally copies one access unit to raw Annex B and probes its SPS **without container metadata**; a correct `colr` atom cannot conceal incomplete bitstream tags. Explicit source transfer characteristics, including sRGB, are preserved; an unspecified source transfer defaults to BT.709.
- Source references are disk-backed 8-bit alpha and **decoded RGB** from canonical 4:2:0 source frames. The canonical matrix is BT.709 for PackedH264 and BT.601 for AlphaWebM. It accounts for chroma subsampling and uses YUV/YUVA respectively, matching their different RGB upsampling kernels. The output is decoded to RGB using its own decoder metadata, with **no forced input matrix**. Equal YUV-plane bytes can no longer hide a wrong RGB interpretation. Independent regression checks also compare decoded-source saturated and midtone RGB patches directly, bypassing the canonical reference.
- Output geometry, codec, decoded count, every frame's relative presentation time and the final frame duration are checked. The start is normalized to zero; variable frame intervals are retained. Timing tolerance is the sum of input and output time-base ticks plus 2 microseconds, with a 2-microsecond floor. WebM's millisecond clock is therefore allowed to round without accumulating drift.
- The packed verifier decodes the **whole packed frame to RGB first**, then crops its entire right half and extracts red/gray values. It never runs `alphaextract` on packed H.264. This includes any chroma interpolation across the seam. The entire left half's decoded RGB is also compared to the canonical decoded source RGB.
- Every matte pixel and every RGB channel sample must differ by at most 16 codes, and each frame's mean absolute error must be ≤2 codes for each comparison. A single large error or a damaged frame cannot hide inside a clip-wide average. These are rejection bounds, not promised exact/lossless fidelity. Recorded fixtures are much closer (see `Validation~/Result.md`). The previous 48-code YUV limit is no longer used.
- No shell, `-y`, permanent destination or Unity worker-thread calls. Each job owns a unique temporary directory and uses `CreateNew` for its references plus FFmpeg `-n` for output. Diagnostics are drained continuously and retained only up to 16 KiB. Raw comparison memory is bounded; references consume disk proportional to decoded duration/resolution. Cancellation terminates only the specific direct child it started and waits for exit internally before completing/cleaning up. Cleanup only names owned files and never recursively deletes a directory.
- Decoding, filtering and encoding are multithreaded, capped at min(8, logical processors) threads per FFmpeg stage, and every tool runs below normal priority so the Editor stays responsive. Every verification decode chain was measured byte-identical with 1, 8, 16 and automatic threads; the encoder thread count changes only the bitstream, which is verified as before. A tool that cannot start is reported with the OS reason alone, because the framework message would also name the current working directory.

## Matte range and gamma: integration requirement

Alpha is a **coverage value**, not luminance. For packed output the converter copies its normalized 0–255 code into neutral RGB without a gamma curve, then explicitly maps full-range RGB to limited-range YUV. A neutral RGB intermediate avoids the measured gray-to-YUV range bias in the tested FFmpeg build. Decoding restores the original numerical gray code, subject to codec error.

The runtime must recover that code value when reading the matte. If Unity's texture/sampling path has applied an sRGB-to-linear conversion to RGB, the matte sample must be converted back to its original code value before using it as alpha; otherwise encoded 128/255 may become roughly 0.216. Do not apply a second correction when the sampling path already returns code values. This converter does not pre-compensate for an assumed GPU color-space path. Windows Built-in Gamma/Linear playback and alpha-128 coverage are verified on Unity 2022.3/6; Unity 6 URP/HDRP Linear camera rendering is also verified. Other decoder/platform paths still require validation with known matte patches.

Packed MP4 importer transcoding must be disabled. For AlphaWebM, the contract calls for Keep Alpha and Transcode. The converter does not set either importer. Selection of Windows/mobile/web/Linux playback paths belongs to the editor/runtime integration; successful FFmpeg decoding does not establish Unity or browser support.

## Deliberate limits

- Existing local files only; exactly one video stream. Audio is allowed in the source but discarded. Remote URLs and playlists are not a conversion input API.
- Even width and height, square pixels, progressive SDR Rec.709/sRGB. Odd dimensions, anamorphic pixels, display matrices/rotation, interlacing, HDR transfer tags and known non-Rec.709 primaries are rejected with a reason rather than silently changing geometry/color. Unspecified color metadata is treated as ordinary SDR, not a color-management guarantee.
- High-bit-depth input is reduced to 8-bit output. Almost-opaque alpha that rounds entirely to 255 is rejected. Premultiplied versus straight RGB is not inferred or changed; the effect asset must describe its source correctly.
- Frames need strictly increasing known timestamps and a known positive last-frame duration. If the selected container cannot preserve them within its tick tolerance, conversion fails. This includes source files with corrupt/missing timing.
- Lossy encoding can fail the fidelity limits on unusual content; no unverified result is returned. Very large/long sources require substantial temporary disk space (about 4 bytes per source pixel per frame for alpha/RGB references, plus output and a copied H.264 access unit); pixel dimensions above 16384 are rejected. Codec/platform limits may be lower, especially for packed double-width output.
- A previously misencoded VP8 source with 709-valued YUV and contradictory container tags is not automatically repaired. Conversion preserves the RGB actually decoded by libvpx (whose unspecified matrix falls back to 601). Regenerate such material from its authoritative RGB/ProRes source; changing its tags cannot reconstruct the intended colors reliably.
- Requires FFmpeg/FFprobe with `-fps_mode`, libx264, libvpx and the filters used here (modern 5.1+ builds). Only the exact Windows build in the test report was exercised. No binaries are bundled/downloaded. Linux/macOS process behavior and native Unity/mobile/browser playback remain untested.

References: [FFmpeg command/timestamp options](https://ffmpeg.org/ffmpeg.html), [scale, setparams and extraction filters](https://ffmpeg.org/ffmpeg-filters.html), [VP8 color-space definition, RFC 6386 §9.2](https://www.rfc-editor.org/rfc/rfc6386#section-9.2), [FFmpeg libvpx decoder color-space handling](https://ffmpeg.org/doxygen/trunk/libvpxdec_8c_source.html). The actual build's CLI and decoded fixtures supplied the validation evidence.

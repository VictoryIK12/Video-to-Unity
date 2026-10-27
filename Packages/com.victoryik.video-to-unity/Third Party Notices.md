# External tools and dependencies

The Video to Unity source code, documentation and synthetic diagnostic samples
are provided under the MIT license in `LICENSE`. That license does not replace
the terms of the external products listed below or grant rights to videos you import.

## Unity

Unity Editor, Unity runtime libraries, uGUI and Timeline are external dependencies
distributed by Unity under their own terms. Their binaries and source packages are
not bundled here. Unity Package Manager resolves the dependencies in `package.json`.
Unity Test Framework/NUnit are used for development validation through Unity's tools;
their binaries are not bundled either.

## FFmpeg and ffprobe

FFmpeg and ffprobe are optional, external command-line tools used for analysis and
conversion in the Editor. They are not linked into the plugin or included in this
repository, UPM package, ZIP or tarball. Runtime playback uses Unity VideoPlayer.

On Windows x64, the opt-in tool preparation button downloads the pinned FFmpeg
9.0.2 essentials build from Gyan's release, verifies its size and SHA-256, and
retains the build's LICENSE and README alongside the executables in the user cache.
[Gyan's builds](https://www.gyan.dev/ffmpeg/builds/#about-these-builds) are distributed
under GPLv3. The download and its dependencies retain their own terms; the plugin's
MIT license does not relicense them. If you redistribute external tools, review the
licenses and corresponding-source requirements of the actual build you distribute.

- [Pinned upstream release](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2)
- [FFmpeg licensing information](https://ffmpeg.org/legal.html)

Other platforms may use an existing installation or manually configured tools.
No Python or standalone .NET SDK is required to use the Unity plugin. The repository's
developer validation scripts may require them separately.

## MurmurHash3

`Editor/Background/BackgroundFrameHash.cs` is an independent C# implementation of
Austin Appleby's MurmurHash3 x64 128-bit algorithm. The SMHasher reference implementation
is public domain. This implementation is used to compare decoded frames with FFmpeg's
frame hashes. No FFmpeg implementation source is included.

## Diagnostic media

Sample clips contain generated color patches and simple shapes. The source generators
are `Verification/GenerateFixtures.py` and `Verification/GenerateBackgroundFixtures.py`
in the repository. The included verification screenshots show diagnostic test scenes.
These samples are available under the same MIT license as this project.

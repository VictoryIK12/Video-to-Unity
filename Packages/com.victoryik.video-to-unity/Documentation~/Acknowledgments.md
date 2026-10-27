# Acknowledgments

The intermediate-frame integrity checker implements Austin Appleby's
MurmurHash3_x64_128 algorithm. The author placed the algorithm's reference source
in the public domain: [SMHasher reference](https://github.com/aappleby/smhasher/blob/master/src/MurmurHash3.cpp).
The C# implementation uses FFmpeg's seed for interoperability and is checked
against SMHasher's verification value and FFmpeg's frame hashes.

Unity packages and the externally installed FFmpeg/ffprobe tools are separate
dependencies. No FFmpeg executable or FFmpeg source file is included in this package.

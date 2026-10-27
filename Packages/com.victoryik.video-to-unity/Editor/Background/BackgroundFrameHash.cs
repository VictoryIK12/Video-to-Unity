using System;
using System.Runtime.InteropServices;

namespace VideoToUnity.Editor
{
    /// <summary>
    /// MurmurHash3_x64_128, the 128-bit variant of MurmurHash3 by Austin Appleby. The author placed the
    /// algorithm and its reference implementation (SMHasher, MurmurHash3.cpp) in the public domain; this
    /// is an independent C# implementation of that published algorithm and contains no third-party code.
    /// FFmpeg's framehash muxer ("-hash murmur3") applies the same algorithm with the 64-bit seed
    /// <see cref="FfmpegSeed"/>, so a frame hashed here can be compared with FFmpeg's hash of the decoded
    /// file. Validation~ checks SMHasher's published verification value and equality with FFmpeg.
    /// Every byte is covered; an accidental difference goes unnoticed with probability about 2^-128.
    /// It verifies integrity and is not a defense against deliberate forgery.
    /// </summary>
    internal static class BackgroundFrameHash
    {
        /// <summary>Seed FFmpeg's murmur3 hash uses for both 64-bit lanes; an interoperability constant.</summary>
        public const ulong FfmpegSeed = 0x725acc55daddca55UL;
        private const ulong C1 = 0x87c37b91114253d5UL, C2 = 0x4cf5ad432745937fUL;

        public static byte[] Compute(byte[] data, int offset, int count) { return Compute(data, offset, count, FfmpegSeed); }

        public static byte[] Compute(byte[] data, int offset, int count, ulong seed)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            // Multiplication, rotation and narrowing intentionally wrap; a /checked+ build must not trap them.
            unchecked
            {
                ulong h1 = seed, h2 = seed, k1, k2;
                int blocks = count / 16;
                if (BitConverter.IsLittleEndian)
                {
                    ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(data, offset, blocks * 16));
                    for (int i = 0; i < words.Length; i += 2)
                    {
                        k1 = words[i] * C1; k1 = Rotl(k1, 31) * C2; h1 ^= k1;
                        h1 = Rotl(h1, 27) + h2; h1 = h1 * 5 + 0x52dce729;
                        k2 = words[i + 1] * C2; k2 = Rotl(k2, 33) * C1; h2 ^= k2;
                        h2 = Rotl(h2, 31) + h1; h2 = h2 * 5 + 0x38495ab5;
                    }
                }
                else
                {
                    for (int block = 0; block < blocks; block++)
                    {
                        int at = offset + block * 16;
                        k1 = Little(data, at, 8) * C1; k1 = Rotl(k1, 31) * C2; h1 ^= k1;
                        h1 = Rotl(h1, 27) + h2; h1 = h1 * 5 + 0x52dce729;
                        k2 = Little(data, at + 8, 8) * C2; k2 = Rotl(k2, 33) * C1; h2 ^= k2;
                        h2 = Rotl(h2, 31) + h1; h2 = h2 * 5 + 0x38495ab5;
                    }
                }
                // Tail: bytes 8..14 form k2 and bytes 0..7 form k1, little-endian, mixed only when present.
                int tail = offset + blocks * 16, rest = count & 15;
                if (rest > 8) { k2 = Little(data, tail + 8, rest - 8) * C2; k2 = Rotl(k2, 33) * C1; h2 ^= k2; }
                if (rest > 0) { k1 = Little(data, tail, Math.Min(rest, 8)) * C1; k1 = Rotl(k1, 31) * C2; h1 ^= k1; }
                h1 ^= (ulong)count; h2 ^= (ulong)count;
                h1 += h2; h2 += h1;
                h1 = Mix(h1); h2 = Mix(h2);
                h1 += h2; h2 += h1;
                var digest = new byte[16];
                for (int i = 0; i < 8; i++) { digest[i] = (byte)(h1 >> (8 * i)); digest[8 + i] = (byte)(h2 >> (8 * i)); }
                return digest;
            }
        }

        private static ulong Rotl(ulong value, int bits) { return (value << bits) | (value >> (64 - bits)); }

        private static ulong Mix(ulong k)
        { unchecked { k ^= k >> 33; k *= 0xff51afd7ed558ccdUL; k ^= k >> 33; k *= 0xc4ceb9fe1a85ec53UL; return k ^ (k >> 33); } }

        private static ulong Little(byte[] bytes, int index, int count)
        { ulong value = 0; for (int i = count - 1; i >= 0; i--) value = (value << 8) | bytes[index + i]; return value; }
    }
}

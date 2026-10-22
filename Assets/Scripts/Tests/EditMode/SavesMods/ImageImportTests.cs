// ImageImportTests.cs
// Feature: CLAUDE.md "Mods (remake-only)" > Image formats (ImageImport.cs): TGA (uncompressed / RLE, grey / 24 / 32-bit,
// either row order), BMP (8-bit palette, 24 / 32-bit, either row order), DDS (DXT1 / DXT3 / DXT5, ATC, uncompressed) and
// the game's .aei (raw RGBA, ETC1, PVRTC 2 / 4bpp, ATC, DXT by type) decoded to pixels bottom row first, like Unity's
// textures. The files are built byte by byte here from the formats' specifications (TGA: Truevision 2.0; BMP:
// BITMAPINFOHEADER, rows padded to 4 bytes, positive height = bottom row first; DDS: the 128-byte header, BC1 / BC2 / BC3
// blocks with RGB565 endpoints; ETC1: the Khronos block layout and modifier table; PVRTC 1: Imagination's word layout,
// Morton order and modulation modes; ATC: AMD's palette; AEI: Reference/tools/asset_conversion/aei.py's header and the
// engine's types), and the expected pixels worked out by hand.

using System;
using System.Text;
using GoF2Remake.Modding;
using NUnit.Framework;
using UnityEngine;

namespace GoF2Remake.Tests
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public class ImageImportTests
    {
        static readonly Color32 Red = new Color32(255, 0, 0, 255), Green = new Color32(0, 255, 0, 255), Blue = new Color32(0, 0, 255, 255),
                                White = new Color32(255, 255, 255, 255);

        static Color32[] Decode(byte[] b, string name, out int w, out int h)
        {
            Assert.IsTrue(ImageImport.DecodePixels(b, name, out w, out h, out var px, out string error), error);
            Assert.IsNull(error);
            Assert.AreEqual(w * h, px.Length);
            return px;
        }

        /// <summary>The pixel at (x, y) counted from the image's top-left corner, in a bottom-row-first array.</summary>
        static Color32 At(Color32[] bottomUp, int w, int h, int x, int y) => bottomUp[(h - 1 - y) * w + x];

        static void AreEqual(Color32 expected, Color32 actual, string what = null) =>
            Assert.AreEqual((expected.r, expected.g, expected.b, expected.a), (actual.r, actual.g, actual.b, actual.a), what);

        static void W16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void W32(byte[] b, int o, int v) { W16(b, o, v); W16(b, o + 2, v >> 16); }

        static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
            return r;
        }

        // ---- the format check ---------------------------------------------------------------------------------------------

        [Test]
        public void PngAndJpgAreLeftToUnity()
        {
            Assert.IsTrue(ImageImport.IsNative(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }));
            Assert.IsTrue(ImageImport.IsNative(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
            Assert.IsFalse(ImageImport.IsNative(Encoding.ASCII.GetBytes("BM-not-native")));
            Assert.IsFalse(ImageImport.IsNative(null));
            Assert.IsFalse(ImageImport.DecodePixels(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "a.png", out _, out _, out _, out _),
                "DecodePixels reads only the formats Unity doesn't");
        }

        [Test]
        public void TheExtensionsOffered()
        {
            CollectionAssert.AreEquivalent(new[] { "png", "jpg", "jpeg", "tga", "bmp", "dds", "aei" }, ImageImport.Extensions);
            CollectionAssert.AreEquivalent(new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".aei" }, ImageImport.FileExtensions);
        }

        [Test]
        public void AShortOrEmptyFileIsRefused()
        {
            Assert.IsFalse(ImageImport.DecodePixels(null, "a.tga", out _, out _, out _, out string e1));
            StringAssert.Contains("empty or cut short", e1);
            Assert.IsFalse(ImageImport.DecodePixels(new byte[10], "a.tga", out _, out _, out _, out string e2));
            StringAssert.Contains("empty or cut short", e2);
        }

        [Test]
        public void AnUnknownFormatIsRefused()
        {
            Assert.IsFalse(ImageImport.DecodePixels(new byte[40], "a.gif", out _, out _, out var px, out string e));
            Assert.IsNull(px);
            StringAssert.Contains("Not an image the game reads", e);
        }

        [Test]
        public void ATruncatedFileIsRefusedNotThrown()
        {
            var tga = Tga(2, 24, 2, 2, 0, new byte[] { 255, 0, 0 });   // 4 pixels announced, 1 given
            Assert.IsFalse(ImageImport.DecodePixels(tga, "a.tga", out _, out _, out _, out string e));
            StringAssert.Contains("couldn't be read", e);
        }

        // ---- TGA --------------------------------------------------------------------------------------------------------

        static byte[] Tga(int type, int bpp, int w, int h, int descriptor, byte[] data, int idLength = 0)
        {
            var head = new byte[18 + idLength];
            head[0] = (byte)idLength;
            head[2] = (byte)type;
            W16(head, 12, w);
            W16(head, 14, h);
            head[16] = (byte)bpp;
            head[17] = (byte)descriptor;
            return Concat(head, data);
        }

        // BGR(A) bytes
        static byte[] Bgr(Color32 c) => new[] { c.b, c.g, c.r };
        static byte[] Bgra(Color32 c) => new[] { c.b, c.g, c.r, c.a };

        [Test]
        public void Tga24BottomRowFirst()
        {
            // descriptor bit 5 clear: the first pixel in the file is the bottom-left one.
            var tga = Tga(2, 24, 2, 2, 0, Concat(Bgr(Blue), Bgr(White), Bgr(Red), Bgr(Green)));
            var px = Decode(tga, "img.tga", out int w, out int h);
            Assert.AreEqual(2, w);
            Assert.AreEqual(2, h);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Green, At(px, w, h, 1, 0));
            AreEqual(Blue, At(px, w, h, 0, 1));
            AreEqual(White, At(px, w, h, 1, 1));
            AreEqual(Blue, px[0], "bottom-left first");
        }

        [Test]
        public void Tga32TopRowFirstWithAlphaAndAnImageId()
        {
            var a = new Color32(255, 0, 0, 10); var b = new Color32(0, 255, 0, 20);
            var c = new Color32(0, 0, 255, 30); var d = new Color32(255, 255, 255, 40);
            var tga = Tga(2, 32, 2, 2, 0x28, Concat(Bgra(a), Bgra(b), Bgra(c), Bgra(d)), idLength: 5);
            var px = Decode(tga, "IMG.TGA", out int w, out int h);
            AreEqual(a, At(px, w, h, 0, 0));
            AreEqual(b, At(px, w, h, 1, 0));
            AreEqual(c, At(px, w, h, 0, 1));
            AreEqual(d, At(px, w, h, 1, 1));
        }

        [Test]
        public void TgaRunLengthEncoded()
        {
            // type 10: 0x81 = a run of 2 (red), 0x01 = 2 raw pixels (green, blue); bottom row first.
            var tga = Tga(10, 24, 2, 2, 0, Concat(new byte[] { 0x81 }, Bgr(Red), new byte[] { 0x01 }, Bgr(Green), Bgr(Blue)));
            var px = Decode(tga, "rle.tga", out int w, out int h);
            AreEqual(Red, At(px, w, h, 0, 1));
            AreEqual(Red, At(px, w, h, 1, 1));
            AreEqual(Green, At(px, w, h, 0, 0));
            AreEqual(Blue, At(px, w, h, 1, 0));
        }

        [Test]
        public void TgaGrey()
        {
            var tga = Tga(3, 8, 2, 1, 0, new byte[] { 0x40, 0xC0 });
            var px = Decode(tga, "g.tga", out int w, out int h);
            AreEqual(new Color32(0x40, 0x40, 0x40, 255), px[0]);
            AreEqual(new Color32(0xC0, 0xC0, 0xC0, 255), px[1]);
        }

        [Test]
        public void TgaWithAColourMapIsRefused()
        {
            var tga = Tga(1, 8, 1, 1, 0, new byte[] { 0, 0, 0 });
            tga[1] = 1;
            Assert.IsFalse(ImageImport.DecodePixels(tga, "map.tga", out _, out _, out _, out string e));
            StringAssert.Contains("save it as PNG", e);
        }

        [Test]
        public void ATgaIsKnownByItsExtensionOnly()
        {
            var tga = Tga(2, 24, 1, 1, 0, Bgr(Red));
            Assert.IsFalse(ImageImport.DecodePixels(tga, "img.png", out _, out _, out _, out _));
            Assert.IsTrue(ImageImport.DecodePixels(tga, "dir/img.Tga", out _, out _, out _, out _));
        }

        // ---- BMP --------------------------------------------------------------------------------------------------------

        static byte[] Bmp(int w, int h, int bpp, byte[] rows, byte[] palette = null, int colours = 0, int compression = 0)
        {
            int paletteBytes = palette?.Length ?? 0;
            int dataAt = 14 + 40 + paletteBytes;
            var head = new byte[dataAt];
            head[0] = (byte)'B'; head[1] = (byte)'M';
            W32(head, 2, dataAt + rows.Length);
            W32(head, 10, dataAt);
            W32(head, 14, 40);
            W32(head, 18, w);
            W32(head, 22, h);
            W16(head, 26, 1);
            W16(head, 28, bpp);
            W32(head, 30, compression);
            W32(head, 34, rows.Length);
            W32(head, 46, colours);
            palette?.CopyTo(head, 54);
            return Concat(head, rows);
        }

        [Test]
        public void Bmp24BottomUpWithPaddedRows()
        {
            // 2 pixels x 3 bytes = 6, padded to 8; positive height: the bottom row comes first.
            var rows = Concat(Bgr(Blue), Bgr(White), new byte[2], Bgr(Red), Bgr(Green), new byte[2]);
            var px = Decode(Bmp(2, 2, 24, rows), "a.bmp", out int w, out int h);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Green, At(px, w, h, 1, 0));
            AreEqual(Blue, At(px, w, h, 0, 1));
            AreEqual(White, At(px, w, h, 1, 1));
        }

        [Test]
        public void Bmp24TopDown()
        {
            var rows = Concat(Bgr(Red), Bgr(Green), new byte[2], Bgr(Blue), Bgr(White), new byte[2]);
            var px = Decode(Bmp(2, -2, 24, rows), "a.bmp", out int w, out int h);
            Assert.AreEqual(2, h);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Green, At(px, w, h, 1, 0));
            AreEqual(Blue, At(px, w, h, 0, 1));
            AreEqual(White, At(px, w, h, 1, 1));
        }

        [Test]
        public void Bmp8WithAPalette()
        {
            var yellow = new Color32(255, 255, 0, 255); var magenta = new Color32(255, 0, 255, 255);
            var palette = Concat(Bgr(yellow), new byte[1], Bgr(magenta), new byte[1]);
            var rows = new byte[] { 0, 1, 0, 0, 1, 0, 0, 0 };   // bottom row: yellow, magenta; top row: magenta, yellow
            var px = Decode(Bmp(2, 2, 8, rows, palette, 2), "p.bmp", out int w, out int h);
            AreEqual(yellow, At(px, w, h, 0, 1));
            AreEqual(magenta, At(px, w, h, 1, 1));
            AreEqual(magenta, At(px, w, h, 0, 0));
            AreEqual(yellow, At(px, w, h, 1, 0));
        }

        [Test]
        public void Bmp32UncompressedIsOpaque()
        {
            // BI_RGB at 32 bits: the fourth byte is unused, the image opaque.
            var px = Decode(Bmp(1, 1, 32, Bgra(new Color32(10, 20, 30, 0))), "o.bmp", out _, out _);
            AreEqual(new Color32(10, 20, 30, 255), px[0]);
        }

        [Test]
        public void Bmp16IsRefused()
        {
            Assert.IsFalse(ImageImport.DecodePixels(Bmp(1, 1, 16, new byte[4]), "x.bmp", out _, out _, out _, out string e));
            StringAssert.Contains("save it as PNG", e);
        }

        // ---- DDS --------------------------------------------------------------------------------------------------------

        static byte[] DdsHeader(int w, int h, string fourCC, int pfFlags, int bits = 0, uint r = 0, uint g = 0, uint b = 0, uint a = 0)
        {
            var d = new byte[128];
            Encoding.ASCII.GetBytes("DDS ").CopyTo(d, 0);
            W32(d, 4, 124);
            W32(d, 8, 0x1007);
            W32(d, 12, h);
            W32(d, 16, w);
            W32(d, 76, 32);
            W32(d, 80, pfFlags);
            if (fourCC != null) Encoding.ASCII.GetBytes(fourCC).CopyTo(d, 84);
            W32(d, 88, bits);
            W32(d, 92, (int)r); W32(d, 96, (int)g); W32(d, 100, (int)b); W32(d, 104, (int)a);
            return d;
        }

        /// <summary>A BC1 colour block: endpoints (RGB565) and the 2-bit indices of the 16 pixels (row by row).</summary>
        static byte[] ColourBlock(int c0, int c1, params int[] indices)
        {
            var blk = new byte[8];
            W16(blk, 0, c0);
            W16(blk, 2, c1);
            uint bits = 0;
            for (int p = 0; p < indices.Length; p++) bits |= (uint)indices[p] << (2 * p);
            W32(blk, 4, (int)bits);
            return blk;
        }

        const int R565 = 0xF800, B565 = 0x001F, White565 = 0xFFFF;

        [Test]
        public void DdsDxt1FourColours()
        {
            // c0 > c1: index 2 = 2/3 c0 + 1/3 c1, index 3 = 1/3 c0 + 2/3 c1 (255 x 2 / 3 = 170, 255 / 3 = 85).
            var dds = Concat(DdsHeader(4, 4, "DXT1", 0x4), ColourBlock(R565, B565, 0, 1, 2, 3));
            var px = Decode(dds, "a.dds", out int w, out int h);
            Assert.AreEqual(4, w);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Blue, At(px, w, h, 1, 0));
            AreEqual(new Color32(170, 0, 85, 255), At(px, w, h, 2, 0));
            AreEqual(new Color32(85, 0, 170, 255), At(px, w, h, 3, 0));
            AreEqual(Red, At(px, w, h, 0, 3), "index 0 elsewhere");
        }

        [Test]
        public void DdsDxt1ThreeColoursAndTransparent()
        {
            // c0 <= c1: index 3 is transparent black.
            var dds = Concat(DdsHeader(4, 4, "DXT1", 0x4), ColourBlock(B565, R565, 0, 1, 3, 3));
            var px = Decode(dds, "a.dds", out int w, out int h);
            AreEqual(Blue, At(px, w, h, 0, 0));
            AreEqual(Red, At(px, w, h, 1, 0));
            AreEqual(new Color32(0, 0, 0, 0), At(px, w, h, 2, 0));
        }

        [Test]
        public void DdsDxt1CutsTheBlocksToTheImage()
        {
            // a 2 x 2 image is one block whose first two rows / columns are used
            var dds = Concat(DdsHeader(2, 2, "DXT1", 0x4), ColourBlock(R565, B565, 0, 1, 1, 1, 1, 0, 1, 1));
            var px = Decode(dds, "a.dds", out int w, out int h);
            Assert.AreEqual(4, px.Length);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Blue, At(px, w, h, 1, 0));
            AreEqual(Blue, At(px, w, h, 0, 1));
            AreEqual(Red, At(px, w, h, 1, 1));
        }

        [Test]
        public void DdsDxt5EightAlphas()
        {
            // a0 > a1: index 0 = a0, 1 = a1; pixel 0 -> 0 (255), pixel 1 -> 1 (0): 3-bit indices from byte 2.
            var alpha = new byte[] { 255, 0, 1 << 3, 0, 0, 0, 0, 0 };
            var dds = Concat(DdsHeader(4, 4, "DXT5", 0x4), alpha, ColourBlock(White565, White565));
            var px = Decode(dds, "a.dds", out int w, out int h);
            AreEqual(new Color32(255, 255, 255, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(255, 255, 255, 0), At(px, w, h, 1, 0));
            AreEqual(new Color32(255, 255, 255, 255), At(px, w, h, 2, 0));
        }

        [Test]
        public void DdsDxt5SixAlphasWithZeroAndFull()
        {
            // a0 <= a1: index 6 = 0, index 7 = 255.
            var alpha = new byte[] { 10, 200, 6 | 7 << 3, 0, 0, 0, 0, 0 };
            var dds = Concat(DdsHeader(4, 4, "DXT5", 0x4), alpha, ColourBlock(White565, White565));
            var px = Decode(dds, "a.dds", out int w, out int h);
            Assert.AreEqual(0, At(px, w, h, 0, 0).a);
            Assert.AreEqual(255, At(px, w, h, 1, 0).a);
            Assert.AreEqual(10, At(px, w, h, 2, 0).a, "index 0 = a0");
        }

        [Test]
        public void DdsDxt3ExplicitAlpha()
        {
            // 4 bits per pixel, low nibble first, x 17.
            var alpha = new byte[] { 0x8F, 0x00, 0, 0, 0, 0, 0, 0 };
            var dds = Concat(DdsHeader(4, 4, "DXT3", 0x4), alpha, ColourBlock(R565, R565));
            var px = Decode(dds, "a.dds", out int w, out int h);
            AreEqual(new Color32(255, 0, 0, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(255, 0, 0, 136), At(px, w, h, 1, 0));
            AreEqual(new Color32(255, 0, 0, 0), At(px, w, h, 2, 0));
        }

        [Test]
        public void DdsUncompressed32BitWithAlpha()
        {
            var head = DdsHeader(2, 1, null, 0x41, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
            var dds = Concat(head, new byte[] { 30, 20, 10, 40, 3, 2, 1, 0 });   // BGRA little-endian, top row first
            var px = Decode(dds, "a.dds", out int w, out int h);
            AreEqual(new Color32(10, 20, 30, 40), At(px, w, h, 0, 0));
            AreEqual(new Color32(1, 2, 3, 0), At(px, w, h, 1, 0));
        }

        [Test]
        public void DdsUncompressed24BitIsOpaque()
        {
            var head = DdsHeader(1, 2, null, 0x40, 24, 0x00FF0000, 0x0000FF00, 0x000000FF);
            var px = Decode(Concat(head, new byte[] { 3, 2, 1, 6, 5, 4 }), "a.dds", out int w, out int h);
            AreEqual(new Color32(1, 2, 3, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(4, 5, 6, 255), At(px, w, h, 0, 1));
        }

        [Test]
        public void DdsDx10IsRefused()
        {
            var dds = Concat(DdsHeader(4, 4, "DX10", 0x4), new byte[64]);
            Assert.IsFalse(ImageImport.DecodePixels(dds, "a.dds", out _, out _, out _, out string e));
            StringAssert.Contains("DX10", e);
        }

        // ---- AEI ---------------------------------------------------------------------------------------------------------

        /// <summary>"AEimage", a byte, the type (offset 8), width / height / region count (u16 at 9 / 11 / 13), 8 bytes per
        /// region, then the data (aei.py).</summary>
        static byte[] AeiHeader(int type, int w, int h, int regions = 0)
        {
            var b = new byte[15 + 8 * regions];
            Encoding.ASCII.GetBytes("AEimage").CopyTo(b, 0);
            b[8] = (byte)type;
            W16(b, 9, w);
            W16(b, 11, h);
            W16(b, 13, regions);
            return b;
        }

        static byte[] Sized(byte[] data)
        {
            var s = new byte[4];
            W32(s, 0, data.Length);
            return Concat(s, data);
        }

        [Test]
        public void AeiRawRgbaTopRowFirst()
        {
            var a = new Color32(1, 2, 3, 4); var b = new Color32(5, 6, 7, 8); var c = new Color32(9, 10, 11, 12); var d = new Color32(13, 14, 15, 128);
            var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 128 };
            var px = Decode(Concat(AeiHeader(1, 2, 2, 1), data), "x.aei", out int w, out int h);
            AreEqual(a, At(px, w, h, 0, 0));
            AreEqual(b, At(px, w, h, 1, 0));
            AreEqual(c, At(px, w, h, 0, 1));
            AreEqual(d, At(px, w, h, 1, 1));
        }

        [Test]
        public void AeiEtc1Individual()
        {
            // individual mode, flip 0: R1 15 / R2 0, G1 0 / G2 15, B 0, tables 0 -> the left half (x 0-1) (255, 0, 0), the
            // right half (0, 255, 0); every index 00 = +2 (table 0's small value): (255, 2, 2) and (2, 255, 2).
            var block = new byte[] { 0xF0, 0x0F, 0x00, 0x00, 0, 0, 0, 0 };
            var px = Decode(Concat(AeiHeader(0x14, 4, 4), Sized(block)), "e.aei", out int w, out int h);
            AreEqual(new Color32(255, 2, 2, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(255, 2, 2, 255), At(px, w, h, 1, 3));
            AreEqual(new Color32(2, 255, 2, 255), At(px, w, h, 2, 0));
            AreEqual(new Color32(2, 255, 2, 255), At(px, w, h, 3, 3));
        }

        [Test]
        public void AeiEtc1FlippedWithModifiers()
        {
            // flip 1: the top half (y 0-1) is colour 1, the bottom half colour 2. Pixel (0, 0) is index 0: its LSB (bit 0 of
            // the low word) and MSB (bit 16) both set = -8 (table 0's large value, negated): (247, 0, 0). Pixel (0, 1) is
            // index 1 (indices run down the columns): MSB only = -2: (253, 0, 0).
            var block = new byte[] { 0xF0, 0x0F, 0x00, 0x01, 0x00, 0x03, 0x00, 0x01 };
            var px = Decode(Concat(AeiHeader(0x16, 4, 4), Sized(block)), "e.aei", out int w, out int h);
            AreEqual(new Color32(247, 0, 0, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(253, 0, 0, 255), At(px, w, h, 0, 1));
            AreEqual(new Color32(255, 2, 2, 255), At(px, w, h, 3, 1));
            AreEqual(new Color32(2, 255, 2, 255), At(px, w, h, 3, 3));
        }

        [Test]
        public void AeiEtc1Differential()
        {
            // differential mode: R 16 (5 bits) -> (16 << 3) | (16 >> 2) = 132, dR -1 -> 15 -> 123; G and B 0; tables 0, no
            // flip: left (132 + 2, 2, 2), right (123 + 2, 2, 2).
            var block = new byte[] { (byte)(16 << 3 | 7), 0, 0, 0x02, 0, 0, 0, 0 };
            var px = Decode(Concat(AeiHeader(0x40, 4, 4), Sized(block)), "e.aei", out int w, out int h);
            AreEqual(new Color32(134, 2, 2, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(125, 2, 2, 255), At(px, w, h, 3, 0));
        }

        [Test]
        public void AeiDxt1Dxt3AndDxt5ByType()
        {
            // the PC version's types, as the engine's loader reads them: 0x20 / 0x22 DXT1, 0x21 / 0x23 DXT3, 0x24 / 0x26 DXT5
            var dxt1 = Concat(AeiHeader(0x20, 4, 4), Sized(ColourBlock(R565, B565, 0, 1)));
            var px = Decode(dxt1, "pc.aei", out int w, out int h);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Blue, At(px, w, h, 1, 0));
            var dxt3 = Concat(AeiHeader(0x23, 4, 4), Sized(Concat(new byte[] { 0x8F, 0, 0, 0, 0, 0, 0, 0 }, ColourBlock(R565, R565))));
            px = Decode(dxt3, "pc.aei", out w, out h);
            Assert.AreEqual(136, At(px, w, h, 1, 0).a);
            var dxt5 = Concat(AeiHeader(0x24, 4, 4), Sized(Concat(new byte[] { 255, 0, 1 << 3, 0, 0, 0, 0, 0 }, ColourBlock(White565, White565))));
            px = Decode(dxt5, "pc.aei", out w, out h);
            Assert.AreEqual(255, At(px, w, h, 0, 0).a);
            Assert.AreEqual(0, At(px, w, h, 1, 0).a);
        }

        [Test]
        public void AnAeiOfAnUnknownKindIsRefused()
        {
            var aei = Concat(AeiHeader(0x30, 4, 4), Sized(new byte[2]), new byte[8]);
            Assert.IsFalse(ImageImport.DecodePixels(aei, "x.aei", out _, out _, out _, out string e));
            StringAssert.Contains("isn't known", e);
        }

        // ---- PVRTC (the iOS version's .aei) ---------------------------------------------------------------------------
        // A word is 8 bytes: the modulation (u32), colour A (u16, bit 0 the mode) and colour B (u16), opaque when the top
        // bit is set (A RGB 554, B RGB 555). Words are in Morton order; with every word alike the colours aren't blended.

        const int PvrRedA = 0xFC00, PvrBlueB = 0x801F, PvrBlackA = 0x8000;

        static byte[] PvrWord(uint modulation, int colourA, int colourB)
        {
            var wd = new byte[8];
            W32(wd, 0, (int)modulation);
            W16(wd, 4, colourA);
            W16(wd, 6, colourB);
            return wd;
        }

        static byte[] PvrAei(int type, int w, int h, params byte[][] words) => Concat(AeiHeader(type, w, h, 1), Sized(Concat(words)));

        [Test]
        public void AeiPvrtc4bppStandardModulation()
        {
            // 2 bits a pixel, row by row: 0 = A, 3 = B, 1 = 3 / 8 of B, 2 = 5 / 8 of B
            uint mod = 0 | 3 << 2 | 1 << 4 | 2 << 6;
            var word = PvrWord(mod, PvrRedA, PvrBlueB);
            var px = Decode(PvrAei(0x12, 8, 8, word, word, word, word), "ios.aei", out int w, out int h);
            Assert.AreEqual(8, w);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Blue, At(px, w, h, 1, 0));
            AreEqual(new Color32(159, 0, 95, 255), At(px, w, h, 2, 0));
            AreEqual(new Color32(95, 0, 159, 255), At(px, w, h, 3, 0));
            AreEqual(Red, At(px, w, h, 4, 0), "the next word");
        }

        [Test]
        public void AeiPvrtc4bppPunchThrough()
        {
            // colour A's bit 0: 1 and 2 = half of B, 2 also see-through
            uint mod = 1 << 2 | 2 << 4 | 3 << 6;
            var word = PvrWord(mod, PvrRedA | 1, PvrBlueB);
            var px = Decode(PvrAei(0x10, 8, 8, word, word, word, word), "ios.aei", out int w, out int h);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(new Color32(127, 0, 127, 255), At(px, w, h, 1, 0));
            AreEqual(new Color32(127, 0, 127, 0), At(px, w, h, 2, 0));
            AreEqual(Blue, At(px, w, h, 3, 0));
        }

        [Test]
        public void AeiPvrtcBlendsTheWordsAroundAPixel()
        {
            // The first word red, the others black. A word's centre pixel (2, 2) is its own colour; its corner pixel (0, 0)
            // is 1 / 4 its own (2 / 4 across x 2 / 4 across y) and the rest from the neighbours, wrapping round the image:
            // red 31 x 4 of 16 -> 124 -> (124 >> 1) + (124 >> 6) = 63.
            var red = PvrWord(0, PvrRedA, PvrBlueB);
            var black = PvrWord(0, PvrBlackA, PvrBlueB);
            var px = Decode(PvrAei(0x12, 8, 8, red, black, black, black), "ios.aei", out int w, out int h);
            AreEqual(Red, At(px, w, h, 2, 2));
            AreEqual(new Color32(63, 0, 0, 255), At(px, w, h, 0, 0));
            // the far corner pixel (7, 7) is 1 / 16 the first word, wrapping round both ways: 31 -> 15 + 0
            AreEqual(new Color32(15, 0, 0, 255), At(px, w, h, 7, 7), "wrapped");
            AreEqual(new Color32(0, 0, 0, 255), At(px, w, h, 6, 6), "the last word's centre");
        }

        [Test]
        public void AeiPvrtc2bppDirect()
        {
            // 8 x 4 pixels a word, 1 bit each: 0 = A, 1 = B
            var word = PvrWord(1u << 1 | 1u << 9, PvrRedA, PvrBlueB);
            var px = Decode(PvrAei(0x0d, 16, 8, word, word, word, word), "ios.aei", out int w, out int h);
            Assert.AreEqual(16, w);
            AreEqual(Red, At(px, w, h, 0, 0));
            AreEqual(Blue, At(px, w, h, 1, 0));
            AreEqual(Blue, At(px, w, h, 1, 1));
            AreEqual(Red, At(px, w, h, 2, 1));
        }

        [Test]
        public void AeiPvrtc2bppInterpolatedModes()
        {
            // Colour A's bit 0 set: a checkerboard of 2-bit values (rows 0 and 2: x 0 2 4 6, rows 1 and 3: x 1 3 5 7), the
            // others from their neighbours. Here rows 0 and 2 store 3 (B) and rows 1 and 3 store 0 (A), so pixel (1, 0) is B
            // from left / right, A from up / down, half from all four. Bit 0 set = left / right or up / down, by bit 20 (the
            // stored (4, 2)'s low bit); those two stored pixels take their high bit twice.
            var hOnly = PvrWord(0x00EF00FF, PvrRedA | 1, PvrBlueB);
            var px = Decode(PvrAei(0x0f, 16, 8, hOnly, hOnly, hOnly, hOnly), "ios.aei", out int w, out int h);
            AreEqual(Blue, At(px, w, h, 1, 0), "left / right");
            AreEqual(Blue, At(px, w, h, 0, 0), "stored (0, 0): bit 1 twice");
            AreEqual(Blue, At(px, w, h, 4, 2), "stored (4, 2): bit 21 twice");
            AreEqual(Red, At(px, w, h, 1, 1), "stored row 1");
            var vOnly = PvrWord(0x00FF00FF, PvrRedA | 1, PvrBlueB);
            px = Decode(PvrAei(0x0f, 16, 8, vOnly, vOnly, vOnly, vOnly), "ios.aei", out w, out h);
            AreEqual(Red, At(px, w, h, 1, 0), "up / down");
            var both = PvrWord(0x00EF00FE, PvrRedA | 1, PvrBlueB);
            px = Decode(PvrAei(0x0f, 16, 8, both, both, both, both), "ios.aei", out w, out h);
            AreEqual(new Color32(127, 0, 127, 255), At(px, w, h, 1, 0), "all four");
        }

        // ---- ATC -------------------------------------------------------------------------------------------------------

        [Test]
        public void AeiAtcExplicitAlpha()
        {
            // colour 0 is RGB 555 with the method in its top bit. Method 0: c0, 5:3, 3:5, c1; the alpha half as DXT3's.
            var colour = ColourBlock(0x7C00, B565, 0, 1, 2, 3);
            var px = Decode(Concat(AeiHeader(0x11, 4, 4), Sized(Concat(new byte[] { 0x8F, 0, 0, 0, 0, 0, 0, 0 }, colour))), "atc.aei", out int w, out int h);
            AreEqual(new Color32(255, 0, 0, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(159, 0, 95, 136), At(px, w, h, 1, 0));
            AreEqual(new Color32(95, 0, 159, 0), At(px, w, h, 2, 0));
            AreEqual(new Color32(0, 0, 255, 0), At(px, w, h, 3, 0));
        }

        [Test]
        public void DdsAtcRgbMethod1()
        {
            // method 1: black, c0 - c1 / 4, c0, c1
            var dds = Concat(DdsHeader(4, 4, "ATC ", 0x4), ColourBlock(0x8000 | 0x7C00, White565, 0, 1, 2, 3));
            var px = Decode(dds, "atc.dds", out int w, out int h);
            AreEqual(new Color32(0, 0, 0, 255), At(px, w, h, 0, 0));
            AreEqual(new Color32(192, 0, 0, 255), At(px, w, h, 1, 0));
            AreEqual(Red, At(px, w, h, 2, 0));
            AreEqual(White, At(px, w, h, 3, 0));
        }
    }
}

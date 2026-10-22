// ImageImport.cs
// Remake-only: the image formats a mod may use, wherever the game reads a mod's image (ModMaterials: skins, portraits,
// icons, hull textures, planets, suns, skies; ModInfo's preview) and in the Mod Builder's pickers (which store a picked image
// as a PNG in the project):
//   PNG, JPG      Unity's own decoder;
//   TGA           uncompressed or RLE, 8-bit grey / 24 / 32-bit, either row order;
//   BMP           8-bit with a palette, 24 / 32-bit, either row order;
//   DDS           DXT1 / DXT3 / DXT5, ATC (RGB, explicit and interpolated alpha) and uncompressed 24 / 32-bit (not the
//                 DX10 header's BC7 and others);
//   AEI           the game's own "AEimage" files, by their type byte as AbyssEngine's loader reads them (the function
//                 before TextureCreateFromFile, 0x7870c's kinds; bit 1 = mipmaps): raw RGBA 1 / 3 / 0x81, ETC1 0x14 / 0x16 /
//                 0x17 / 0x40 / 0x42 (the Android version's), PVRTC 2bpp 0x0d / 0x0f and 4bpp 0x10 / 0x12 (the iOS
//                 version's), ATC with explicit alpha 0x11 / 0x13, DXT1 0x20 / 0x22, DXT3 0x21 / 0x23, DXT5 0x24 / 0x26 (the
//                 PC version's). The engine has no ATC RGB (4bpp) kind; DDS carries it ("ATC ", "ATCA", "ATCI").
// The format comes from the file's first bytes (TGA, which has none, from its extension). Only the top mip level is kept.
// PNG / JPG go to Unity's own decoders; DecodePixels reads the rest on any thread (ModMaterials decodes off the main thread).
// Plain C#.

using System;
using System.IO;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public static class ImageImport
    {
        /// <summary>The extensions the builder's image pickers offer (without the dot).</summary>
        public static readonly string[] Extensions = { "png", "jpg", "jpeg", "tga", "bmp", "dds", "aei" };

        /// <summary>The same with the dot, for the mods' folders (ModTextures).</summary>
        public static readonly string[] FileExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".aei" };

        /// <summary>A PNG or JPG: Unity's own decoders read it (LoadImage, UnityWebRequestTexture).</summary>
        public static bool IsNative(byte[] b) => b != null && (Starts(b, 0x89, (byte)'P', (byte)'N', (byte)'G') || Starts(b, 0xFF, 0xD8));

        /// <summary>The picked file as a PNG in the temporary folder (the caller copies it into the project and deletes it),
        /// null with the reason when it can't be read.</summary>
        public static string ToPng(string path, string originalName, out string error)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e) { error = e.Message; return null; }
            var tex = Decode(bytes, originalName ?? path, false, false, out error);
            if (tex == null) return null;
            try
            {
                string dir = Path.Combine(Application.temporaryCachePath, "ModBuilderImages");
                Directory.CreateDirectory(dir);
                string png = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(png, tex.EncodeToPNG());
                return png;
            }
            catch (Exception e) { error = e.Message; return null; }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        /// <summary>An image file's bytes as a readable RGBA32 texture (main thread; null with the reason when it can't be
        /// read): PNG / JPG by Unity's decoder, the rest by DecodePixels. 'name' gives a TGA away by its extension.</summary>
        public static Texture2D Decode(byte[] bytes, string name, bool linear, bool mipmaps, out string error)
        {
            error = null;
            if (IsNative(bytes))
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, mipmaps, linear);
                if (t.LoadImage(bytes, false)) return t;
                UnityEngine.Object.Destroy(t);
                error = "The PNG / JPG couldn't be read.";
                return null;
            }
            return DecodePixels(bytes, name, out int w, out int h, out var px, out error) ? MakeTexture(w, h, px, linear, mipmaps) : null;
        }

        /// <summary>A texture from DecodePixels' result (main thread).</summary>
        public static Texture2D MakeTexture(int w, int h, Color32[] bottomUp, bool linear, bool mipmaps)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipmaps, linear);
            tex.SetPixels32(bottomUp);
            tex.Apply(mipmaps);
            return tex;
        }

        /// <summary>The pixels of a TGA, BMP, DDS or .aei (not PNG / JPG), bottom row first like Unity's textures; false with
        /// the reason when it can't be read. Touches no Unity object: safe on a worker thread.</summary>
        public static bool DecodePixels(byte[] b, string name, out int w, out int h, out Color32[] bottomUp, out string error)
        {
            w = h = 0;
            bottomUp = null;
            error = null;
            try
            {
                Color32[] px;   // top row first
                if (b == null || b.Length < 18) { error = "The file is empty or cut short."; return false; }
                if (Starts(b, (byte)'A', (byte)'E', (byte)'i', (byte)'m', (byte)'a', (byte)'g', (byte)'e')) px = Aei(b, out w, out h, out error);
                else if (Starts(b, (byte)'D', (byte)'D', (byte)'S', (byte)' ')) px = Dds(b, out w, out h, out error);
                else if (Starts(b, (byte)'B', (byte)'M')) px = Bmp(b, out w, out h, out error);
                else if (string.Equals(Path.GetExtension(name ?? ""), ".tga", StringComparison.OrdinalIgnoreCase)) px = Tga(b, out w, out h, out error);
                else { error = "Not an image the game reads (PNG, JPG, TGA, BMP, DDS or the game's .aei)."; return false; }
                if (px == null) return false;
                bottomUp = new Color32[px.Length];
                for (int y = 0; y < h; y++) Array.Copy(px, (h - 1 - y) * w, bottomUp, y * w, w);
                return true;
            }
            catch (Exception e) { error = "The image couldn't be read: " + e.Message; return false; }
        }

        static bool Starts(byte[] b, params byte[] magic)
        {
            if (b.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++) if (b[i] != magic[i]) return false;
            return true;
        }

        static int U16(byte[] b, int o) => b[o] | b[o + 1] << 8;
        static int I32(byte[] b, int o) => b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24;

        // ---- AEI (the game's "AEimage") ----------------------------------------------------------------------

        static Color32[] Aei(byte[] b, out int w, out int h, out string error)
        {
            error = null;
            int type = b[8];
            w = U16(b, 9); h = U16(b, 11);
            int o = 15 + 8 * U16(b, 13);   // after the regions
            if (type == 1 || type == 3 || type == 0x81)
            {
                var px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++, o += 4) px[i] = new Color32(b[o], b[o + 1], b[o + 2], b[o + 3]);
                return px;
            }
            o += 4;   // the data's size
            switch (type)
            {
                case 0x14: case 0x16: case 0x17: case 0x40: case 0x42: return Etc1(b, o, w, h);
                case 0x0d: case 0x0f: return Pvrtc(b, o, w, h, true);
                case 0x10: case 0x12: return Pvrtc(b, o, w, h, false);
                case 0x20: case 0x22: return Bc(b, o, w, h, 1);
                case 0x21: case 0x23: return Bc(b, o, w, h, 3);
                case 0x24: case 0x26: return Bc(b, o, w, h, 5);
                case 0x11: case 0x13: return Bc(b, o, w, h, 3, true);   // ATC with explicit alpha
            }
            error = $"This .aei's kind (0x{type:x}) isn't known.";
            return null;
        }

        // ---- DDS ----------------------------------------------------------------------------------------------

        static Color32[] Dds(byte[] b, out int w, out int h, out string error)
        {
            error = null;
            h = I32(b, 12); w = I32(b, 16);
            int flags = I32(b, 80);
            string fourCC = System.Text.Encoding.ASCII.GetString(b, 84, 4);
            if ((flags & 0x4) != 0)
            {
                if (fourCC == "DXT1") return Bc(b, 128, w, h, 1);
                if (fourCC == "DXT3") return Bc(b, 128, w, h, 3);
                if (fourCC == "DXT5") return Bc(b, 128, w, h, 5);
                if (fourCC == "ATC ") return Bc(b, 128, w, h, 1, true);
                if (fourCC == "ATCA") return Bc(b, 128, w, h, 3, true);
                if (fourCC == "ATCI") return Bc(b, 128, w, h, 5, true);
                error = $"This DDS is {fourCC.Trim('\0')}: save it as PNG (the builder reads DXT1, DXT3, DXT5, ATC and uncompressed DDS).";
                return null;
            }
            int bits = I32(b, 88), rMask = I32(b, 92), gMask = I32(b, 96), bMask = I32(b, 100), aMask = I32(b, 104);
            if (bits != 24 && bits != 32) { error = $"This DDS has {bits} bits per pixel: save it as PNG."; return null; }
            var px = new Color32[w * h];
            int step = bits / 8, o = 128;
            byte Channel(int v, int mask)
            {
                if (mask == 0) return 255;
                int shift = 0;
                while ((mask >> shift & 1) == 0) shift++;
                int max = (int)((uint)mask >> shift);
                return (byte)(((int)((uint)v >> shift) & max) * 255 / max);
            }
            for (int i = 0; i < px.Length; i++, o += step)
            {
                int v = b[o] | b[o + 1] << 8 | b[o + 2] << 16 | (step == 4 ? b[o + 3] << 24 : 0);
                px[i] = new Color32(Channel(v, rMask), Channel(v, gMask), Channel(v, bMask), (flags & 0x1) != 0 ? Channel(v, aMask) : (byte)255);
            }
            return px;
        }

        // ---- TGA ----------------------------------------------------------------------------------------------

        static Color32[] Tga(byte[] b, out int w, out int h, out string error)
        {
            error = null;
            int idLength = b[0], colorMapType = b[1], type = b[2];
            w = U16(b, 12); h = U16(b, 14);
            int bpp = b[16], desc = b[17];
            bool topFirst = (desc & 0x20) != 0;
            if (colorMapType != 0 || (type != 2 && type != 3 && type != 10 && type != 11) || (bpp != 8 && bpp != 24 && bpp != 32))
            {
                error = "This TGA's kind isn't read here (uncompressed or RLE, grey / 24 / 32-bit): save it as PNG.";
                return null;
            }
            int o = 18 + idLength, step = bpp / 8, n = w * h;
            var raw = new Color32[n];
            Color32 Read(ref int p)
            {
                Color32 c = step == 1 ? new Color32(b[p], b[p], b[p], 255)
                          : new Color32(b[p + 2], b[p + 1], b[p], step == 4 ? b[p + 3] : (byte)255);
                p += step;
                return c;
            }
            if (type == 2 || type == 3)
                for (int i = 0; i < n; i++) raw[i] = Read(ref o);
            else
                for (int i = 0; i < n;)
                {
                    int head = b[o++], count = (head & 0x7F) + 1;
                    if ((head & 0x80) != 0) { var c = Read(ref o); for (int k = 0; k < count && i < n; k++) raw[i++] = c; }
                    else for (int k = 0; k < count && i < n; k++) raw[i++] = Read(ref o);
                }
            if (topFirst) return raw;
            var px = new Color32[n];
            for (int y = 0; y < h; y++) Array.Copy(raw, (h - 1 - y) * w, px, y * w, w);
            return px;
        }

        // ---- BMP ----------------------------------------------------------------------------------------------

        static Color32[] Bmp(byte[] b, out int w, out int h, out string error)
        {
            error = null;
            int dataAt = I32(b, 10), dib = I32(b, 14);
            w = I32(b, 18);
            int rawH = I32(b, 22);
            h = Math.Abs(rawH);
            int bpp = U16(b, 28), compression = I32(b, 30);
            if ((bpp != 8 && bpp != 24 && bpp != 32) || (compression != 0 && compression != 3))
            {
                error = "This BMP's kind isn't read here (8-bit with a palette, 24 / 32-bit, uncompressed): save it as PNG.";
                return null;
            }
            Color32[] palette = null;
            if (bpp == 8)
            {
                int colours = dib >= 36 && I32(b, 46) > 0 ? I32(b, 46) : 256;
                palette = new Color32[colours];
                for (int i = 0; i < colours; i++) { int p = 14 + dib + 4 * i; palette[i] = new Color32(b[p + 2], b[p + 1], b[p], 255); }
            }
            int rowBytes = (w * bpp / 8 + 3) & ~3;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int src = dataAt + (rawH > 0 ? h - 1 - y : y) * rowBytes;   // positive height: the bottom row first
                for (int x = 0; x < w; x++)
                {
                    int p = src + x * bpp / 8;
                    px[y * w + x] = bpp == 8 ? palette[b[p]] : new Color32(b[p + 2], b[p + 1], b[p], bpp == 32 && compression == 0 ? (byte)255 : bpp == 32 ? b[p + 3] : (byte)255);
                }
            }
            return px;
        }

        // ---- block compression ---------------------------------------------------------------------------------

        /// <summary>DXT1 (kind 1), DXT3 (3) or DXT5 (5) blocks, top row first.</summary>
        /// <summary>DXT1 / DXT3 / DXT5 blocks (kind 1 / 3 / 5), or with atc AMD's ATC: the same 4x4 blocks and alpha halves (ATC
        /// RGB = kind 1, explicit alpha = 3, interpolated alpha = 5) with ATC's own colour palette. Top row first.</summary>
        static Color32[] Bc(byte[] b, int o, int w, int h, int kind, bool atc = false)
        {
            var px = new Color32[w * h];
            int bw = (w + 3) / 4, bh = (h + 3) / 4, blockBytes = kind == 1 ? 8 : 16;
            var colours = new Color32[4];
            var alphas = new byte[8];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, o += blockBytes)
                {
                    int c = kind == 1 ? o : o + 8;
                    int c0 = U16(b, c), c1 = U16(b, c + 2);
                    if (atc)
                    {
                        // ATC: colour 0 is RGB 555 with its top bit the method; method 0 = c0, 5:3, 3:5, c1;
                        // method 1 = black, c0 - c1 / 4, c0, c1.
                        var a0 = new Color32((byte)((c0 >> 10 & 31) * 255 / 31), (byte)((c0 >> 5 & 31) * 255 / 31), (byte)((c0 & 31) * 255 / 31), 255);
                        var a1 = Rgb565(c1);
                        if ((c0 & 0x8000) == 0)
                        {
                            colours[0] = a0; colours[3] = a1;
                            colours[1] = Mix(a0, a1, 5, 3, 8);
                            colours[2] = Mix(a0, a1, 3, 5, 8);
                        }
                        else
                        {
                            colours[0] = new Color32(0, 0, 0, 255);
                            colours[1] = new Color32((byte)Math.Max(0, a0.r - a1.r / 4), (byte)Math.Max(0, a0.g - a1.g / 4), (byte)Math.Max(0, a0.b - a1.b / 4), 255);
                            colours[2] = a0; colours[3] = a1;
                        }
                    }
                    else
                    {
                        colours[0] = Rgb565(c0);
                        colours[1] = Rgb565(c1);
                        if (kind != 1 || c0 > c1)
                        {
                            colours[2] = Mix(colours[0], colours[1], 2, 1, 3);
                            colours[3] = Mix(colours[0], colours[1], 1, 2, 3);
                        }
                        else
                        {
                            colours[2] = Mix(colours[0], colours[1], 1, 1, 2);
                            colours[3] = new Color32(0, 0, 0, 0);
                        }
                    }
                    uint bits = (uint)I32(b, c + 4);
                    if (kind == 5)
                    {
                        int a0 = b[o], a1 = b[o + 1];
                        alphas[0] = (byte)a0; alphas[1] = (byte)a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) alphas[1 + i] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        else
                        {
                            for (int i = 1; i < 5; i++) alphas[1 + i] = (byte)(((5 - i) * a0 + i * a1) / 5);
                            alphas[6] = 0; alphas[7] = 255;
                        }
                    }
                    ulong abits = 0;
                    if (kind == 5) for (int i = 0; i < 6; i++) abits |= (ulong)b[o + 2 + i] << (8 * i);
                    for (int p = 0; p < 16; p++)
                    {
                        int x = bx * 4 + (p & 3), y = by * 4 + (p >> 2);
                        if (x >= w || y >= h) continue;
                        var col = colours[(bits >> (2 * p)) & 3];
                        if (kind == 5) col.a = alphas[(abits >> (3 * p)) & 7];
                        else if (kind == 3) col.a = (byte)(((b[o + p / 2] >> (4 * (p & 1))) & 15) * 17);
                        px[y * w + x] = col;
                    }
                }
            return px;
        }

        // ---- PVRTC (the iOS version's .aei) ------------------------------------------------------------------

        static readonly int[] PvrtcStandard = { 0, 3, 5, 8 }, PvrtcPunch = { 0, 4, 4, 8 };

        /// <summary>PVRTC 1 (2 or 4 bits a pixel, power-of-two sizes), top row first. Each 64-bit word holds two low-resolution
        /// colours, A and B, and a modulation per pixel; the words are in Morton order. A pixel's A and B are interpolated
        /// bilinearly between the four nearest words' centres (wrapping), then mixed by its modulation (n / 8).</summary>
        static Color32[] Pvrtc(byte[] b, int o, int w, int h, bool twoBpp)
        {
            int bw = twoBpp ? 8 : 4;
            int nbx = Math.Max(w / bw, 2), nby = Math.Max(h / 4, 2), words = nbx * nby;
            int minBlocks = Math.Min(nbx, nby);
            var colA = new int[words * 4];
            var colB = new int[words * 4];
            int W = nbx * bw, H = nby * 4;
            var mod = new int[W * H];     // n / 8 of B
            var punch = new bool[W * H];
            var fill = twoBpp ? new byte[W * H] : null;   // 2bpp: 1 = from all four neighbours, 2 = left / right, 3 = up / down
            for (int by = 0; by < nby; by++)
                for (int bx = 0; bx < nbx; bx++)
                {
                    int p = o + Morton(bx, by, minBlocks) * 8, k = (by * nbx + bx) * 4;
                    uint bits = (uint)I32(b, p);
                    int ca = U16(b, p + 4), cb = U16(b, p + 6);
                    if ((ca & 0x8000) != 0)
                    {
                        colA[k] = ca >> 10 & 31; colA[k + 1] = ca >> 5 & 31; colA[k + 2] = (ca & 0x1e) | (ca >> 4 & 1); colA[k + 3] = 15;
                    }
                    else
                    {
                        colA[k] = (ca >> 7 & 0x1e) | (ca >> 11 & 1); colA[k + 1] = (ca >> 3 & 0x1e) | (ca >> 7 & 1);
                        colA[k + 2] = (ca << 1 & 0x1c) | (ca >> 2 & 3); colA[k + 3] = ca >> 11 & 0xe;
                    }
                    if ((cb & 0x8000) != 0)
                    {
                        colB[k] = cb >> 10 & 31; colB[k + 1] = cb >> 5 & 31; colB[k + 2] = cb & 31; colB[k + 3] = 15;
                    }
                    else
                    {
                        colB[k] = (cb >> 7 & 0x1e) | (cb >> 11 & 1); colB[k + 1] = (cb >> 3 & 0x1e) | (cb >> 7 & 1);
                        colB[k + 2] = (cb << 1 & 0x1e) | (cb >> 3 & 1); colB[k + 3] = cb >> 11 & 0xe;
                    }
                    bool modeFlag = (ca & 1) != 0;
                    if (!twoBpp)
                        for (int i = 0; i < 16; i++)
                        {
                            int v = (int)(bits >> (2 * i) & 3), at = (by * 4 + (i >> 2)) * W + bx * 4 + (i & 3);
                            mod[at] = modeFlag ? PvrtcPunch[v] : PvrtcStandard[v];
                            punch[at] = modeFlag && v == 2;
                        }
                    else if (!modeFlag)
                        for (int i = 0; i < 32; i++)
                            mod[(by * 4 + (i >> 3)) * W + bx * 8 + (i & 7)] = (bits >> i & 1) != 0 ? 8 : 0;
                    else
                    {
                        // Half the pixels (a checkerboard) are stored with 2 bits; the others come from their neighbours.
                        // Bit 0 picks how (with bit 20 the centre pixel's low bit standing in for which), and those stored
                        // pixels take their high bit twice.
                        byte how = 1;
                        if ((bits & 1) != 0)
                        {
                            how = (bits & 1u << 20) != 0 ? (byte)3 : (byte)2;
                            bits = (bits & 1u << 21) != 0 ? bits | 1u << 20 : bits & ~(1u << 20);
                        }
                        bits = (bits & 2) != 0 ? bits | 1 : bits & ~1u;
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 8; x++)
                            {
                                int at = (by * 4 + y) * W + bx * 8 + x;
                                if (((x ^ y) & 1) == 0) { mod[at] = PvrtcStandard[bits & 3]; bits >>= 2; }
                                else fill[at] = how;
                            }
                    }
                }
            if (twoBpp)
            {
                var filled = (int[])mod.Clone();
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int how = fill[y * W + x];
                        if (how == 0) continue;
                        int l = mod[y * W + (x + W - 1) % W], r = mod[y * W + (x + 1) % W];
                        int u = mod[(y + H - 1) % H * W + x], d = mod[(y + 1) % H * W + x];
                        filled[y * W + x] = how == 1 ? (l + r + u + d + 2) / 4 : how == 2 ? (l + r + 1) / 2 : (u + d + 1) / 2;
                    }
                mod = filled;
            }
            // Each pixel takes the words around it by its distance from their centres: in a 4-wide word pixel 0 is 2 / 4 its
            // left neighbour and 2 / 4 its own, pixel 3 is 3 / 4 its own and 1 / 4 its right one.
            var px = new Color32[w * h];
            int shiftRgb = twoBpp ? 2 : 1, shiftA = twoBpp ? 1 : 0;   // a weighted sum of 5-bit (4-bit) values to 8 bits
            var sa = new int[4];
            var sb = new int[4];
            for (int y = 0; y < h; y++)
            {
                int cy = y % 4 - 2, by = y / 4;
                int by0 = cy < 0 ? by - 1 : by, wy0 = cy < 0 ? -cy : 4 - cy;
                int row0 = (by0 + nby) % nby * nbx, row1 = (by0 + 1) % nby * nbx;
                for (int x = 0; x < w; x++)
                {
                    int cx = x % bw - bw / 2, bx = x / bw;
                    int bx0 = cx < 0 ? bx - 1 : bx, wx0 = cx < 0 ? -cx : bw - cx;
                    int c0 = (bx0 + nbx) % nbx, c1 = (bx0 + 1) % nbx;
                    int k00 = (row0 + c0) * 4, k10 = (row0 + c1) * 4, k01 = (row1 + c0) * 4, k11 = (row1 + c1) * 4;
                    int w00 = wx0 * wy0, w10 = (bw - wx0) * wy0, w01 = wx0 * (4 - wy0), w11 = (bw - wx0) * (4 - wy0);
                    for (int c = 0; c < 4; c++)
                    {
                        sa[c] = colA[k00 + c] * w00 + colA[k10 + c] * w10 + colA[k01 + c] * w01 + colA[k11 + c] * w11;
                        sb[c] = colB[k00 + c] * w00 + colB[k10 + c] * w10 + colB[k01 + c] * w01 + colB[k11 + c] * w11;
                    }
                    int m = mod[y * W + x];
                    byte Mix8(int c)
                    {
                        int a = c < 3 ? (sa[c] >> shiftRgb) + (sa[c] >> (shiftRgb + 5)) : (sa[c] >> shiftA) + (sa[c] >> (shiftA + 4));
                        int bb = c < 3 ? (sb[c] >> shiftRgb) + (sb[c] >> (shiftRgb + 5)) : (sb[c] >> shiftA) + (sb[c] >> (shiftA + 4));
                        return (byte)((a * (8 - m) + bb * m) / 8);
                    }
                    px[y * w + x] = new Color32(Mix8(0), Mix8(1), Mix8(2), punch[y * W + x] ? (byte)0 : Mix8(3));
                }
            }
            return px;
        }

        /// <summary>A PVRTC word's place: the x and y bits interleaved (y the lower) up to the smaller side, the rest of the
        /// larger side above.</summary>
        static int Morton(int x, int y, int minBlocks)
        {
            int offset = 0, shift = 0;
            for (int mask = 1; mask < minBlocks; mask <<= 1, shift++)
                offset |= ((y & mask) | (x & mask) << 1) << shift;
            return offset | ((x | y) >> shift) << (shift * 2);
        }

        static Color32 Rgb565(int c) => new Color32((byte)((c >> 11 & 31) * 255 / 31), (byte)((c >> 5 & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), 255);

        static Color32 Mix(Color32 a, Color32 b, int wa, int wb, int div) =>
            new Color32((byte)((a.r * wa + b.r * wb) / div), (byte)((a.g * wa + b.g * wb) / div), (byte)((a.b * wa + b.b * wb) / div), 255);

        static readonly int[,] Etc1Table =
        {
            { 2, 8 }, { 5, 17 }, { 9, 29 }, { 13, 42 }, { 18, 60 }, { 24, 80 }, { 33, 106 }, { 47, 183 },
        };

        /// <summary>ETC1 blocks (the phone version's textures), top row first.</summary>
        static Color32[] Etc1(byte[] b, int o, int w, int h)
        {
            var px = new Color32[w * h];
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, o += 8)
                {
                    uint hi = (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
                    uint lo = (uint)(b[o + 4] << 24 | b[o + 5] << 16 | b[o + 6] << 8 | b[o + 7]);
                    bool diff = (hi & 2) != 0, flip = (hi & 1) != 0;
                    int r1, g1, b1, r2, g2, b2;
                    if (diff)
                    {
                        int R = (int)(hi >> 27 & 31), G = (int)(hi >> 19 & 31), B = (int)(hi >> 11 & 31);
                        int dR = (int)(hi >> 24 & 7), dG = (int)(hi >> 16 & 7), dB = (int)(hi >> 8 & 7);
                        if (dR >= 4) dR -= 8;
                        if (dG >= 4) dG -= 8;
                        if (dB >= 4) dB -= 8;
                        int E5(int v) => v << 3 | v >> 2;
                        r1 = E5(R); g1 = E5(G); b1 = E5(B);
                        r2 = E5((R + dR) & 31); g2 = E5((G + dG) & 31); b2 = E5((B + dB) & 31);
                    }
                    else
                    {
                        r1 = (int)(hi >> 28 & 15) * 17; r2 = (int)(hi >> 24 & 15) * 17;
                        g1 = (int)(hi >> 20 & 15) * 17; g2 = (int)(hi >> 16 & 15) * 17;
                        b1 = (int)(hi >> 12 & 15) * 17; b2 = (int)(hi >> 8 & 15) * 17;
                    }
                    int t1 = (int)(hi >> 5 & 7), t2 = (int)(hi >> 2 & 7);
                    for (int x = 0; x < 4; x++)
                        for (int y = 0; y < 4; y++)
                        {
                            int i = x * 4 + y;   // the pixel indices run down the columns
                            bool second = flip ? y >= 2 : x >= 2;
                            int table = second ? t2 : t1;
                            int msb = (int)(lo >> (16 + i) & 1), lsb = (int)(lo >> i & 1);
                            int mod = Etc1Table[table, lsb] * (msb != 0 ? -1 : 1);
                            int px0 = bx * 4 + x, py0 = by * 4 + y;
                            if (px0 >= w || py0 >= h) continue;
                            int r = second ? r2 : r1, g = second ? g2 : g1, bb = second ? b2 : b1;
                            px[py0 * w + px0] = new Color32((byte)Mathf.Clamp(r + mod, 0, 255), (byte)Mathf.Clamp(g + mod, 0, 255), (byte)Mathf.Clamp(bb + mod, 0, 255), 255);
                        }
                }
            return px;
        }
    }
}

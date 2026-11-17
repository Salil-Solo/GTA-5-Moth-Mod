using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// Decodes R_lut.hdr and T_lut.hdr from the entanglement-shader-v1 output ZIP
    /// and renders an in-game interference PNG texture for CustomSprite display
    /// (F9 Telemetry HUD thumbnail + optional F10 Quantum Elixir overlay).
    /// </summary>
    public static class ShaderTextureBuilder
    {
        public static void CleanupCacheDirectory(string cacheDir)
        {
            try
            {
                if (!Directory.Exists(cacheDir)) return;
                foreach (string file in Directory.GetFiles(cacheDir, "qshader_*.png"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        public static bool TryBuildTextureFromZip(
            byte[] zipBytes,
            string outputPngPath,
            out double meanR,
            out double meanT)
        {
            meanR = 0.0;
            meanT = 0.0;

            if (zipBytes == null || zipBytes.Length < 64)
                return false;

            try
            {
                byte[] rBytes = null;
                byte[] tBytes = null;

                using (var ms = new MemoryStream(zipBytes))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    rBytes = ReadEntryBytes(zip, "R_lut.hdr");
                    tBytes = ReadEntryBytes(zip, "T_lut.hdr");
                }

                if (rBytes == null || tBytes == null)
                    return false;

                if (!TryDecodeRadianceHdr(rBytes, out float[,] rLut, out int w, out int h) ||
                    !TryDecodeRadianceHdr(tBytes, out float[,] tLut, out int tw, out int th))
                {
                    return false;
                }

                meanR = ComputeMean(rLut, w, h);
                meanT = ComputeMean(tLut, tw, th);

                string dir = Path.GetDirectoryName(outputPngPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                RenderInterferencePng(rLut, w, h, tLut, tw, th, outputPngPath);
                return File.Exists(outputPngPath);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] ReadEntryBytes(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) return null;
            using (var s = entry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static void RenderInterferencePng(
            float[,] rLut, int rw, int rh,
            float[,] tLut, int tw, int th,
            string outputPngPath)
        {
            const int size = 160;
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                for (int py = 0; py < size; py++)
                {
                    double ny = (py / (double)(size - 1)) * 2.0 - 1.0;
                    for (int px = 0; px < size; px++)
                    {
                        double nx = (px / (double)(size - 1)) * 2.0 - 1.0;
                        double radius = Math.Sqrt(nx * nx + ny * ny);
                        double angle = Math.Atan2(ny, nx);

                        // Map radial interference field onto the Atlas (phase, cosTheta) LUT domain
                        double cosTheta = Math.Max(0.0, Math.Min(1.0, 1.0 - radius * 0.72));
                        double thicknessNm = 320.0 + 480.0 * (0.5 + 0.5 * Math.Sin(radius * 7.5 + angle * 2.0));

                        // Sample RGB wavelengths (630nm, 532nm, 465nm) using the real Atlas LUTs
                        double red = EvalBand(rLut, rw, rh, tLut, tw, th, thicknessNm / 630.0, cosTheta);
                        double green = EvalBand(rLut, rw, rh, tLut, tw, th, thicknessNm / 532.0, cosTheta);
                        double blue = EvalBand(rLut, rw, rh, tLut, tw, th, thicknessNm / 465.0, cosTheta);

                        // Subtle vignette border for crisp HUD card contrast
                        double vignette = radius <= 1.0 ? 1.0 : Math.Max(0.25, 1.0 - (radius - 1.0) * 1.4);

                        int cr = ClampByte(red * vignette * 255.0);
                        int cg = ClampByte(green * vignette * 255.0);
                        int cb = ClampByte(blue * vignette * 255.0);

                        bmp.SetPixel(px, py, Color.FromArgb(255, cr, cg, cb));
                    }
                }

                bmp.Save(outputPngPath, ImageFormat.Png);
            }
        }

        private static double EvalBand(
            float[,] rLut, int rw, int rh,
            float[,] tLut, int tw, int th,
            double uPhase, double vCosTheta)
        {
            double u = uPhase - Math.Floor(uPhase);
            double v = Math.Max(0.0, Math.Min(1.0, vCosTheta));

            double r = SampleBilinear(rLut, rw, rh, u, v);
            double t = SampleBilinear(tLut, tw, th, u, v);
            return Math.Max(0.0, Math.Min(1.0, r * 0.72 + t * 0.58));
        }

        private static double SampleBilinear(float[,] lut, int w, int h, double u, double v)
        {
            double fx = u * (w - 1);
            double fy = v * (h - 1);
            int x0 = Math.Max(0, Math.Min(w - 1, (int)Math.Floor(fx)));
            int y0 = Math.Max(0, Math.Min(h - 1, (int)Math.Floor(fy)));
            int x1 = (x0 + 1) % w;
            int y1 = Math.Min(h - 1, y0 + 1);
            double tx = fx - x0;
            double ty = fy - y0;

            double a = lut[y0, x0] * (1.0 - tx) + lut[y0, x1] * tx;
            double b = lut[y1, x0] * (1.0 - tx) + lut[y1, x1] * tx;
            return a * (1.0 - ty) + b * ty;
        }

        private static int ClampByte(double val)
        {
            int v = (int)Math.Round(val);
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        private static double ComputeMean(float[,] lut, int w, int h)
        {
            double sum = 0.0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    sum += lut[y, x];
            return sum / Math.Max(1, w * h);
        }

        private static bool TryDecodeRadianceHdr(byte[] data, out float[,] luminance, out int width, out int height)
        {
            luminance = null;
            width = 0;
            height = 0;

            // Parse ASCII header ending at empty line
            int pos = 0;
            while (pos < data.Length)
            {
                string line = ReadAsciiLine(data, ref pos);
                if (line == null) return false;
                if (line.Length == 0) break; // blank line terminates header
            }

            // Resolution line, e.g. "-Y 32 +X 32"
            string resLine = ReadAsciiLine(data, ref pos);
            if (string.IsNullOrWhiteSpace(resLine)) return false;

            string[] parts = resLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !int.TryParse(parts[1], out height) || !int.TryParse(parts[3], out width))
                return false;

            if (width <= 0 || height <= 0 || width > 2048 || height > 2048)
                return false;

            luminance = new float[height, width];
            byte[] scanline = new byte[width * 4];

            for (int y = 0; y < height; y++)
            {
                if (pos + 4 > data.Length) return false;

                // Detect standard Radiance new-style RLE scanline
                if (width >= 8 && width <= 32767 &&
                    data[pos] == 2 && data[pos + 1] == 2 && (data[pos + 2] & 0x80) == 0)
                {
                    int rleWidth = (data[pos + 2] << 8) | data[pos + 3];
                    if (rleWidth != width) return false;
                    pos += 4;

                    for (int ch = 0; ch < 4; ch++)
                    {
                        int x = 0;
                        while (x < width)
                        {
                            if (pos >= data.Length) return false;
                            byte code = data[pos++];
                            if (code > 128)
                            {
                                int count = code - 128;
                                if (pos >= data.Length || x + count > width) return false;
                                byte val = data[pos++];
                                for (int k = 0; k < count; k++)
                                    scanline[ch * width + x++] = val;
                            }
                            else
                            {
                                int count = code;
                                if (count == 0 || pos + count > data.Length || x + count > width) return false;
                                for (int k = 0; k < count; k++)
                                    scanline[ch * width + x++] = data[pos++];
                            }
                        }
                    }

                    for (int x = 0; x < width; x++)
                    {
                        luminance[y, x] = RgbeToLuminance(
                            scanline[x],
                            scanline[width + x],
                            scanline[2 * width + x],
                            scanline[3 * width + x]
                        );
                    }
                }
                else
                {
                    // Flat uncompressed RGBE pixels (what entanglement-shader-v1 emits at 32x32)
                    if (pos + width * 4 > data.Length) return false;
                    for (int x = 0; x < width; x++)
                    {
                        luminance[y, x] = RgbeToLuminance(
                            data[pos], data[pos + 1], data[pos + 2], data[pos + 3]
                        );
                        pos += 4;
                    }
                }
            }

            return true;
        }

        private static float RgbeToLuminance(byte r, byte g, byte b, byte e)
        {
            if (e == 0) return 0f;
            float scale = (float)Math.Pow(2.0, e - (128 + 8));
            float rf = r * scale;
            float gf = g * scale;
            float bf = b * scale;
            return 0.2126f * rf + 0.7152f * gf + 0.0722f * bf;
        }

        private static string ReadAsciiLine(byte[] data, ref int pos)
        {
            if (pos >= data.Length) return null;
            int start = pos;
            while (pos < data.Length && data[pos] != '\n')
                pos++;
            int end = pos;
            if (end > start && data[end - 1] == '\r')
                end--;
            if (pos < data.Length && data[pos] == '\n')
                pos++;
            return Encoding.ASCII.GetString(data, start, end - start);
        }
    }
}

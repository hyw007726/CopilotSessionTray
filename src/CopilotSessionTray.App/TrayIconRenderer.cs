using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CopilotSessionTray.App;

internal sealed class TrayIconRenderer
{
    private const int MasterSize = 256;
    private const double BoldnessPixels = 0.35;
    private const byte VisibleAlphaThreshold = 10;
    private const int SmallIconMetric = 49;
    private const int DefaultIconSizePx = 16;
    private const double PalmsCenterX = 0.499;
    private const double PalmsCenterY = 0.545;
    private const double GlowSigmaMin = 0.16;
    private const double GlowSigmaMax = 0.24;

    private static readonly Color GlowColor = Color.FromArgb(0xFF, 0xF2, 0xC0);

    private readonly float[] _master;
    private readonly Color _glyphColor;
    private readonly double _lightU;
    private readonly double _lightV;
    private readonly Dictionary<int, SizePlanes> _planesBySize = new();

    private sealed record SizePlanes(float[] Coverage, float[] LightDistance);

    public TrayIconRenderer(Bitmap glyph)
    {
        ArgumentNullException.ThrowIfNull(glyph);

        var width = glyph.Width;
        var height = glyph.Height;
        var data = glyph.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var stride = data.Stride;
        var pixels = new byte[stride * height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        glyph.UnlockBits(data);

        var alpha = new float[width * height];
        int minX = width, minY = height, maxX = -1, maxY = -1;
        long red = 0, green = 0, blue = 0, opaque = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                var a = pixels[i + 3];
                alpha[y * width + x] = a / 255f;
                if (a > VisibleAlphaThreshold)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }

                if (a == 255)
                {
                    blue += pixels[i];
                    green += pixels[i + 1];
                    red += pixels[i + 2];
                    opaque++;
                }
            }
        }

        if (maxX < 0 || opaque == 0)
        {
            throw new InvalidOperationException("The tray icon glyph has no visible content.");
        }

        _glyphColor = Color.FromArgb((int)(red / opaque), (int)(green / opaque), (int)(blue / opaque));

        var visibleWidth = maxX - minX + 1;
        var visibleHeight = maxY - minY + 1;
        var side = Math.Max(visibleWidth, visibleHeight);
        var left = minX - (side - visibleWidth) / 2.0;
        var top = minY - (side - visibleHeight) / 2.0;
        _master = Resample(alpha, width, height, left, top, left + side, top + side, MasterSize);
        _lightU = ((side - visibleWidth) / 2.0 + PalmsCenterX * visibleWidth) / side;
        _lightV = ((side - visibleHeight) / 2.0 + PalmsCenterY * visibleHeight) / side;
    }

    /// <summary>Renders the glyph at <paramref name="sizePx"/> square; <paramref name="glow"/> (0..1) lights up the folded palms at its centre, fading outward to the plain gold.</summary>
    public Bitmap Render(int sizePx, double glow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizePx);

        var planes = GetPlanes(sizePx);
        var level = Math.Clamp(glow, 0, 1);
        var sigma = GlowSigmaMin + (GlowSigmaMax - GlowSigmaMin) * level;

        var bitmap = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, sizePx, sizePx), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * sizePx];
            for (var y = 0; y < sizePx; y++)
            {
                for (var x = 0; x < sizePx; x++)
                {
                    var p = y * sizePx + x;
                    var i = y * stride + x * 4;
                    var reach = planes.LightDistance[p] / sigma;
                    var weight = level * Math.Exp(-reach * reach);
                    bytes[i] = Blend(_glyphColor.B, GlowColor.B, weight);
                    bytes[i + 1] = Blend(_glyphColor.G, GlowColor.G, weight);
                    bytes[i + 2] = Blend(_glyphColor.R, GlowColor.R, weight);
                    bytes[i + 3] = (byte)Math.Round(Math.Clamp(planes.Coverage[p], 0f, 1f) * 255);
                }
            }

            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>Creates a native icon from a straight-alpha 32bpp bitmap; the caller owns the handle.</summary>
    public static IntPtr CreateIconHandle(Bitmap bitmap)
    {
        var size = bitmap.Width;
        var source = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var header = new NativeMethods.BitmapInfoHeader { Size = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(), Width = size, Height = -size, Planes = 1, BitCount = 32 };
        var color = IntPtr.Zero;
        try
        {
            color = NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var row = new byte[size * 4];
            for (var y = 0; y < size; y++)
            {
                Marshal.Copy(source.Scan0 + y * source.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, bits + y * row.Length, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(source);
        }

        var mask = NativeMethods.CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        try
        {
            var info = new NativeMethods.IconInfo { IsIcon = true, Mask = mask, Color = color };
            var icon = NativeMethods.CreateIconIndirect(ref info);
            return icon == IntPtr.Zero ? throw new Win32Exception(Marshal.GetLastWin32Error()) : icon;
        }
        finally
        {
            NativeMethods.DeleteObject(color);
            NativeMethods.DeleteObject(mask);
        }
    }

    /// <summary>The pixel size the shell draws notification-area icons at on the taskbar's current DPI.</summary>
    public static int GetShellIconSizePx()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(taskbar);
        var size = dpi == 0
            ? NativeMethods.GetSystemMetrics(SmallIconMetric)
            : NativeMethods.GetSystemMetricsForDpi(SmallIconMetric, dpi);
        return size > 0 ? size : DefaultIconSizePx;
    }

    private SizePlanes GetPlanes(int sizePx)
    {
        if (!_planesBySize.TryGetValue(sizePx, out var planes))
        {
            var bold = Dilate(_master, MasterSize, BoldnessPixels * MasterSize / sizePx);
            var coverage = Resample(bold, MasterSize, MasterSize, 0, 0, MasterSize, MasterSize, sizePx);
            var lightDistance = new float[sizePx * sizePx];
            for (var y = 0; y < sizePx; y++)
            {
                for (var x = 0; x < sizePx; x++)
                {
                    var du = (x + 0.5) / sizePx - _lightU;
                    var dv = (y + 0.5) / sizePx - _lightV;
                    lightDistance[y * sizePx + x] = (float)Math.Sqrt(du * du + dv * dv);
                }
            }

            planes = new SizePlanes(coverage, lightDistance);
            _planesBySize[sizePx] = planes;
        }

        return planes;
    }

    private static byte Blend(byte from, byte to, double mix) => (byte)Math.Round(from + (to - from) * mix);

    // Grayscale dilation with a soft-edged disc.
    private static float[] Dilate(float[] source, int size, double radius)
    {
        var reach = (int)Math.Ceiling(radius + 0.5);
        var kernel = new List<(int Dx, int Dy, float Weight)>();
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var weight = (float)Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                if (weight > 0)
                {
                    kernel.Add((dx, dy, weight));
                }
            }
        }

        var result = new float[source.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var best = 0f;
                foreach (var (dx, dy, weight) in kernel)
                {
                    int sx = x + dx, sy = y + dy;
                    if (sx < 0 || sy < 0 || sx >= size || sy >= size)
                    {
                        continue;
                    }

                    best = Math.Max(best, source[sy * size + sx] * weight);
                }

                result[y * size + x] = best;
            }
        }

        return result;
    }

    // Exact area-average resample of a source rectangle (areas outside the source count as 0) into a square grid.
    private static float[] Resample(float[] source, int sourceWidth, int sourceHeight, double left, double top, double right, double bottom, int destSize)
    {
        var result = new float[destSize * destSize];
        var stepX = (right - left) / destSize;
        var stepY = (bottom - top) / destSize;
        var cellArea = stepX * stepY;
        for (var dy = 0; dy < destSize; dy++)
        {
            double y0 = top + dy * stepY, y1 = y0 + stepY;
            for (var dx = 0; dx < destSize; dx++)
            {
                double x0 = left + dx * stepX, x1 = x0 + stepX;
                double sum = 0;
                for (var sy = Math.Max(0, (int)Math.Floor(y0)); sy < Math.Min(sourceHeight, (int)Math.Ceiling(y1)); sy++)
                {
                    var weightY = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (var sx = Math.Max(0, (int)Math.Floor(x0)); sx < Math.Min(sourceWidth, (int)Math.Ceiling(x1)); sx++)
                    {
                        var weightX = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                        sum += source[sy * sourceWidth + sx] * weightX * weightY;
                    }
                }

                result[dy * destSize + dx] = (float)(sum / cellArea);
            }
        }

        return result;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool IsIcon;
            public int HotspotX;
            public int HotspotY;
            public IntPtr Mask;
            public IntPtr Color;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ColorsUsed;
            public int ColorsImportant;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreateIconIndirect(ref IconInfo info);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[]? bits);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);
    }
}

param(
    [Parameter(Mandatory)][string]$SourcePng,
    [Parameter(Mandatory)][string]$IcoOut,
    [string]$PngOut,
    [string]$ClearRect,
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

Add-Type -AssemblyName System.Drawing

if (-not ('IconBuilder' -as [type])) {
    $drawing = [System.Drawing.Bitmap].Assembly.Location
    Add-Type -ReferencedAssemblies @($drawing) -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconBuilder
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int X; public int Y; public int Width; public int Height; }

    private static byte[] Grab(Bitmap bmp, out int stride)
    {
        Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData d = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        stride = d.Stride;
        byte[] buf = new byte[stride * bmp.Height];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(d);
        return buf;
    }

    public static int[] FindTileBox(string path)
    {
        Bitmap bmp = new Bitmap(path);
        int stride;
        byte[] b = Grab(bmp, out stride);
        int w = bmp.Width, h = bmp.Height;
        bmp.Dispose();
        int top = -1, bottom = -1, left = -1, right = -1;
        for (int y = 0; y < h && top < 0; y++)
            for (int x = 0; x < w; x++) { int i = y * stride + x * 4; if (!White(b, i)) { top = y; break; } }
        for (int y = h - 1; y >= 0 && bottom < 0; y--)
            for (int x = 0; x < w; x++) { int i = y * stride + x * 4; if (!White(b, i)) { bottom = y; break; } }
        for (int x = 0; x < w && left < 0; x++)
            for (int y = 0; y < h; y++) { int i = y * stride + x * 4; if (!White(b, i)) { left = x; break; } }
        for (int x = w - 1; x >= 0 && right < 0; x--)
            for (int y = 0; y < h; y++) { int i = y * stride + x * 4; if (!White(b, i)) { right = x; break; } }
        return new int[] { left, top, right - left + 1, bottom - top + 1 };
    }

    private static bool White(byte[] b, int i)
    {
        return b[i] > 240 && b[i + 1] > 240 && b[i + 2] > 240;
    }

    public static int MeasureRadius(string path, int tx, int ty, int tw, int th)
    {
        Bitmap bmp = new Bitmap(path);
        int stride;
        byte[] b = Grab(bmp, out stride);
        bmp.Dispose();
        int radius = 0;
        for (int y = 0; y < th; y++)
        {
            int firstX = -1;
            for (int x = 0; x < tw; x++) { if (!White(b, (ty + y) * stride + (tx + x) * 4)) { firstX = x; break; } }
            if (firstX < 0) continue;
            if (firstX > 3) radius = y + 1; else break;
        }
        return radius;
    }

    public static void ClearAreas(string path, int[] rects)
    {
        Bitmap detached;
        using (Bitmap loaded = new Bitmap(path))
        {
            detached = new Bitmap(loaded);
        }
        Graphics g = Graphics.FromImage(detached);
        for (int i = 0; i + 3 < rects.Length; i += 4)
            g.FillRectangle(Brushes.White, rects[i], rects[i + 1], rects[i + 2], rects[i + 3]);
        g.Dispose();
        detached.Save(path, ImageFormat.Png);
        detached.Dispose();
    }

    // Square canvas, tile centred, everything outside its rounded outline made transparent with a 1px feather.
    public static Bitmap BuildMaster(string path, int tx, int ty, int tw, int th, int radius)
    {
        int side = Math.Max(tw, th);
        int ox = (side - tw) / 2, oy = (side - th) / 2;
        Bitmap canvas = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(canvas);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(new Bitmap(path), new Rectangle(ox, oy, tw, th), new Rectangle(tx, ty, tw, th), GraphicsUnit.Pixel);
        g.Dispose();

        Rectangle area = new Rectangle(0, 0, side, side);
        BitmapData d = canvas.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = d.Stride;
        byte[] buf = new byte[stride * side];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        float r = radius;
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = y * stride + x * 4;
                float fx = x - ox, fy = y - oy;
                float a = 0f;
                if (fx >= 0 && fy >= 0 && fx < tw && fy < th)
                {
                    float cx = Math.Min(Math.Max(fx, r), tw - r);
                    float cy = Math.Min(Math.Max(fy, r), th - r);
                    float dx = fx - cx, dy = fy - cy;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    a = Math.Max(0f, Math.Min(1f, r - dist + 0.5f));
                }
                buf[i] = (byte)(buf[i] * a);
                buf[i + 1] = (byte)(buf[i + 1] * a);
                buf[i + 2] = (byte)(buf[i + 2] * a);
                buf[i + 3] = (byte)(255 * a);
            }
        }
        Marshal.Copy(buf, 0, d.Scan0, buf.Length);
        canvas.UnlockBits(d);
        return canvas;
    }

    private static Bitmap Scale(Bitmap src, int size)
    {
        Bitmap cur = src;
        int w = cur.Width;
        while (w / 2 >= size)
        {
            int nw = Math.Max(w / 2, size);
            Bitmap next = new Bitmap(nw, nw, PixelFormat.Format32bppArgb);
            Graphics g = Graphics.FromImage(next);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(cur, new Rectangle(0, 0, nw, nw));
            g.Dispose();
            if (!ReferenceEquals(cur, src)) cur.Dispose();
            cur = next;
            w = nw;
        }
        if (w == size) return cur;
        Bitmap outBmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        Graphics og = Graphics.FromImage(outBmp);
        og.InterpolationMode = InterpolationMode.HighQualityBicubic;
        og.PixelOffsetMode = PixelOffsetMode.HighQuality;
        og.CompositingQuality = CompositingQuality.HighQuality;
        og.DrawImage(cur, new Rectangle(0, 0, size, size));
        og.Dispose();
        if (!ReferenceEquals(cur, src)) cur.Dispose();
        return outBmp;
    }

    private static byte[] Dib(Bitmap frame)
    {
        int s = frame.Width;
        int stride;
        byte[] raw = Grab(frame, out stride);
        byte[] px = new byte[s * s * 4];
        for (int y = 0; y < s; y++)
            Buffer.BlockCopy(raw, (s - 1 - y) * stride, px, y * s * 4, s * 4);
        int maskRow = ((s + 31) / 32) * 4;
        byte[] mask = new byte[maskRow * s];
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((uint)40);
            bw.Write((uint)s);
            bw.Write((uint)(s * 2));
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)0);
            bw.Write((uint)(px.Length + mask.Length));
            bw.Write((int)0); bw.Write((int)0); bw.Write((uint)0); bw.Write((uint)0);
            bw.Write(px);
            bw.Write(mask);
            return ms.ToArray();
        }
    }

    public static void WriteIco(Bitmap master, int[] sizes, string path)
    {
        List<int> order = new List<int>(sizes);
        order.Sort();
        List<byte[]> payloads = new List<byte[]>();
        foreach (int s in order)
        {
            Bitmap frame = Scale(master, s);
            payloads.Add(Dib(frame));
            frame.Dispose();
        }
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)payloads.Count);
            int offset = 6 + 16 * payloads.Count;
            for (int n = 0; n < order.Count; n++)
            {
                int s = order[n];
                byte[] p = payloads[n];
                byte dim = s >= 256 ? (byte)0 : (byte)s;
                bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)p.Length); bw.Write((uint)offset);
                offset += p.Length;
            }
            foreach (byte[] p in payloads) bw.Write(p);
        }
    }

    public static void SavePng(Bitmap master, string path)
    {
        master.Save(path, ImageFormat.Png);
    }
}
'@
}

# Watermark cleanup happens on a working copy so the generated master stays untouched.
Copy-Item $SourcePng $env:TEMP\icon-work.png -Force
if ($ClearRect) {
    $rects = @()
    foreach ($r in ($ClearRect -split ';')) { foreach ($n in ($r -split ',')) { $rects += [int]$n } }
    [IconBuilder]::ClearAreas("$env:TEMP\icon-work.png", $rects)
}

$box = [IconBuilder]::FindTileBox("$env:TEMP\icon-work.png")
$tx = [int]$box[0]; $ty = [int]$box[1]; $tw = [int]$box[2]; $th = [int]$box[3]
if ($tx -lt 0 -or $ty -lt 0) { throw 'no artwork found on a white field' }
$radius = [IconBuilder]::MeasureRadius("$env:TEMP\icon-work.png", $tx, $ty, $tw, $th)
Write-Output ('tile x=' + $tx + ' y=' + $ty + ' w=' + $tw + ' h=' + $th + ' radius=' + $radius)

$master = [IconBuilder]::BuildMaster("$env:TEMP\icon-work.png", $tx, $ty, $tw, $th, $radius)
if ($PngOut) { [IconBuilder]::SavePng($master, $PngOut) }
[IconBuilder]::WriteIco($master, $Sizes, $IcoOut)
$master.Dispose()
Remove-Item $env:TEMP\icon-work.png -Force -ErrorAction SilentlyContinue
Write-Output ('ico=' + $IcoOut + ' bytes=' + (Get-Item $IcoOut).Length)

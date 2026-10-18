// Cuts the character out of the framed illustration and adds a sticker outline.
// Usage: Cutout.exe <input.jpg> <output.png> [size]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

static class Cutout
{
    static int W, H;
    static byte[] px;

    static int Lum(int i) { return (px[i * 4 + 2] * 3 + px[i * 4 + 1] * 6 + px[i * 4]) / 10; }

    // Background: light warm paper and bokeh, never the saturated hair or the dark lines.
    static bool BgLike(int i)
    {
        int b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
        return b >= 135 && r - b <= 105 && g >= 175;
    }

    // Pale paper, skin, lace: light and only slightly warm.
    static bool PaperLike(int i)
    {
        int b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
        return b >= 160 && g >= 195 && r - b <= 80;
    }

    static bool HairLike(int i)
    {
        int b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
        return r - b > 80 && r > 150;
    }

    // Classifies the pixels 2–3 px around a region.
    static void Ring(List<int> comp, bool[] bg, out int hair, out int dark, out int other, out int bgN)
    {
        var inComp = new HashSet<int>(comp);
        var ring = new HashSet<int>();
        foreach (int i in comp)
        {
            int x = i % W, y = i / W;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) continue;
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                    int n = yy * W + xx;
                    if (!inComp.Contains(n)) ring.Add(n);
                }
        }
        hair = dark = other = bgN = 0;
        foreach (int n in ring)
        {
            if (bg[n]) bgN++;
            else if (HairLike(n)) hair++;
            else if (Lum(n) < 120) dark++;
            else other++;
        }
    }

    static int Main(string[] args)
    {
        var src = new Bitmap(args[0]);
        int size = args.Length > 2 ? int.Parse(args[2]) : 1000;

        // Find the drawn frame: first dark pixel from each side along the middle lines.
        int x0 = Scan(src, true, 1), x1 = Scan(src, true, -1), y0 = Scan(src, false, 1), y1 = Scan(src, false, -1);
        int inset = 5;
        var rect = Rectangle.FromLTRB(x0 + inset, y0 + inset, x1 - inset + 1, y1 - inset + 1);
        var crop = src.Clone(rect, PixelFormat.Format32bppArgb);
        W = crop.Width; H = crop.Height;
        px = Read(crop);
        Console.WriteLine("frame {0},{1} - {2},{3}", x0, y0, x1, y1);

        // Flood fill the background from the top, left and right edges.
        var bg = new bool[W * H];
        var q = new Queue<int>();
        Action<int> seed = i => { if (!bg[i] && BgLike(i)) { bg[i] = true; q.Enqueue(i); } };
        for (int x = 0; x < W; x++) seed(x);
        for (int y = 0; y < H * 0.72; y++) { seed(y * W); seed(y * W + W - 1); }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % W, y = i / W;
            if (x > 0) seed(i - 1);
            if (x < W - 1) seed(i + 1);
            if (y > 0) seed(i - W);
            if (y < H - 1) seed(i + W);
        }

        string debug = Environment.GetEnvironmentVariable("CUTOUT_DEBUG");
        if (!string.IsNullOrEmpty(debug))
        {
            // Labels every light region the flood did not reach, so pockets can be picked by eye.
            var dbg = (byte[])px.Clone();
            for (int i = 0; i < W * H; i++)
                if (bg[i]) { dbg[i * 4] = 60; dbg[i * 4 + 1] = 50; dbg[i * 4 + 2] = 40; }
            var labels = new List<Tuple<int, int, int>>();
            var dBg = Distance(bg);
            { var dFg = Distance(Invert(bg)); long r0 = 0, g0 = 0, b0 = 0, n0 = 0; for (int i = 0; i < W * H; i++) if (bg[i] && dFg[i] <= 12) { r0 += px[i * 4 + 2]; g0 += px[i * 4 + 1]; b0 += px[i * 4]; n0++; } Console.WriteLine("paper near edge rgb={0},{1},{2}", r0 / n0, g0 / n0, b0 / n0); }
            var seen = new bool[W * H];
            var rnd = new Random(3);
            int id = 0;
            for (int s = 0; s < W * H; s++)
            {
                if (bg[s] || seen[s] || !PaperLike(s)) continue;
                var comp = new List<int>();
                var cq = new Queue<int>(); cq.Enqueue(s); seen[s] = true;
                long sr = 0, sg = 0, sb = 0; int minX = W, minY = H, maxX = 0, maxY = 0;
                while (cq.Count > 0)
                {
                    int i = cq.Dequeue(), x = i % W, y = i / W;
                    comp.Add(i); sr += px[i * 4 + 2]; sg += px[i * 4 + 1]; sb += px[i * 4];
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    foreach (int n in new[] { x > 0 ? i - 1 : -1, x < W - 1 ? i + 1 : -1, y > 0 ? i - W : -1, y < H - 1 ? i + W : -1 })
                        if (n >= 0 && !seen[n] && !bg[n] && PaperLike(n)) { seen[n] = true; cq.Enqueue(n); }
                }
                if (comp.Count < 40) continue;
                id++;
                byte cr = (byte)rnd.Next(60, 256), cg = (byte)rnd.Next(0, 200), cb = (byte)rnd.Next(60, 256);
                foreach (int i in comp) { dbg[i * 4] = cb; dbg[i * 4 + 1] = cg; dbg[i * 4 + 2] = cr; }
                labels.Add(Tuple.Create(id, (minX + maxX) / 2, (minY + maxY) / 2));
                int near = 0; float dmax = 0; foreach (int i in comp) { if (dBg[i] <= 30) near++; dmax = Math.Max(dmax, dBg[i]); }
                Console.WriteLine("#{0} n={1} box={2},{3}-{4},{5} rgb={6},{7},{8} near={9:0.00} dmax={10:0}", id, comp.Count, minX, minY, maxX, maxY, sr / comp.Count, sg / comp.Count, sb / comp.Count, near / (double)comp.Count, dmax);
            }
            var img = Write(dbg, W, H);
            using (var gr = Graphics.FromImage(img))
            using (var font = new Font("Arial", 13, FontStyle.Bold))
                foreach (var l in labels)
                {
                    gr.DrawString(l.Item1.ToString(), font, Brushes.Black, l.Item2 - 7, l.Item3 - 7);
                    gr.DrawString(l.Item1.ToString(), font, Brushes.White, l.Item2 - 8, l.Item3 - 8);
                }
            img.Save(debug, ImageFormat.Png);
        }

        var pocket = FindPockets(bg);
        var toPocket = Distance(pocket);

        var dist = Distance(bg); // distance from foreground to background, in px

        // Sticker outline: cream band then a thin brown rim, drawn outside the silhouette.
        const float cream = 7f, rim = 2.6f;
        var outD = Distance(Invert(bg)); // distance from background pixel to the character
        int P = (int)Math.Ceiling(cream + rim) + 2;
        int CW = W + P * 2, CH = H + P;
        var outPx = new byte[CW * CH * 4];
        for (int y = 0; y < CH; y++)
            for (int x = 0; x < CW; x++)
            {
                int sx = x - P, sy = y - P;
                bool inside = sx >= 0 && sy >= 0 && sx < W && sy < H;
                // Below the crop (never) / outside crop counts as background.
                float d = inside ? (bg[sy * W + sx] ? outD[sy * W + sx] : 0f) : NearestOutside(sx, sy, outD, bg);
                int o = (y * CW + x) * 4;
                float a; int r, g, b;
                if (inside && pocket[sy * W + sx]) { r = g = b = 0; a = 0f; }
                else if (inside && !bg[sy * W + sx])
                {
                    int i = sy * W + sx;
                    r = px[i * 4 + 2]; g = px[i * 4 + 1]; b = px[i * 4];
                    // Blend the very edge toward the cream band.
                    float t = Math.Min(1f, dist[i] / 1.5f);
                    r = Mix(255, r, t); g = Mix(248, g, t); b = Mix(238, b, t);
                    a = 1f;
                    // Next to a see-through gap, paper-light anti-aliasing fades out
                    // and keeps the strand's own colour.
                    if (toPocket[i] <= 2.5f)
                    {
                        a = Math.Max(0f, Math.Min(1f, (230 - px[i * 4]) / 45f));
                        if (toPocket[i] > 1.5f) a = Math.Max(a, 0.6f);
                        if (a > 0 && a < 1)
                        {
                            r = Unmix(r, 252, a); g = Unmix(g, 238, a); b = Unmix(b, 218, a);
                        }
                    }
                }
                else if (d <= cream) { r = 255; g = 248; b = 238; a = 1f; }
                else if (d <= cream + rim) { r = 122; g = 62; b = 30; a = 1f; }
                else if (d <= cream + rim + 1f) { r = 122; g = 62; b = 30; a = 1f - (d - cream - rim); }
                else { r = g = b = 0; a = 0f; }
                outPx[o] = (byte)b; outPx[o + 1] = (byte)g; outPx[o + 2] = (byte)r; outPx[o + 3] = (byte)(a * 255);
            }

        var outline = Write(outPx, CW, CH);
        // Square canvas, character anchored to the bottom, scaled to the target size.
        int side = Math.Max(CW, CH);
        var sq = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(sq))
        {
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float k = size / (float)side;
            float w = CW * k, h = CH * k;
            gr.DrawImage(outline, new RectangleF((size - w) / 2f, size - h, w, h));
        }
        sq.Save(args[1], ImageFormat.Png);
        Console.WriteLine("saved {0} ({1}x{1})", args[1], size);
        return 0;
    }

    static float NearestOutside(int sx, int sy, float[] outD, bool[] bg)
    {
        // Outside the crop: treat the crop edge as background continuing outward,
        // except along the bottom where the bust is cut off.
        int cx = Math.Max(0, Math.Min(W - 1, sx)), cy = Math.Max(0, Math.Min(H - 1, sy));
        float extra = (float)Math.Sqrt((cx - sx) * (cx - sx) + (cy - sy) * (cy - sy));
        int i = cy * W + cx;
        return (bg[i] ? outD[i] : 0f) + extra;
    }

    static int Mix(int a, int b, float t) { return (int)(a + (b - a) * t + 0.5f); }

    // Removes the paper colour blended into a semi-transparent edge pixel.
    static int Unmix(int c, int paper, float a)
    {
        return Math.Max(0, Math.Min(255, (int)((c - paper * (1 - a)) / a + 0.5f)));
    }

    // Paper showing through between strands: regions the outside flood could not
    // reach because strands close them off. They match the paper colour, sit near
    // the outer edge and are hemmed in by strand outlines. Light spots painted on
    // the hair are warmer, deeper inside, and surrounded by hair only.
    static bool[] FindPockets(bool[] bg)
    {
        var pocket = new bool[W * H];
        var seen = new bool[W * H];
        var dBg = Distance(bg);
        var q = new Queue<int>();
        for (int s = 0; s < W * H; s++)
        {
            if (bg[s] || seen[s] || !PaperLike(s)) continue;
            var comp = new List<int>();
            q.Enqueue(s); seen[s] = true;
            long sr = 0, sg = 0, sb = 0; int near = 0; float dmax = 0;
            while (q.Count > 0)
            {
                int i = q.Dequeue(), x = i % W, y = i / W;
                comp.Add(i); sr += px[i * 4 + 2]; sg += px[i * 4 + 1]; sb += px[i * 4];
                if (dBg[i] <= 30) near++;
                dmax = Math.Max(dmax, dBg[i]);
                foreach (int n in new[] { x > 0 ? i - 1 : -1, x < W - 1 ? i + 1 : -1, y > 0 ? i - W : -1, y < H - 1 ? i + W : -1 })
                    if (n >= 0 && !seen[n] && !bg[n] && PaperLike(n)) { seen[n] = true; q.Enqueue(n); }
            }
            if (comp.Count < 30) continue;
            double r = sr / (double)comp.Count, g = sg / (double)comp.Count, b = sb / (double)comp.Count;
            if (r < 249 || g < 235 || b < 208 || b > 240 || dmax > 130) continue;
            bool isPocket = near >= comp.Count * 0.5;
            if (!isPocket)
            {
                int hair, dark, other, bgN; Ring(comp, bg, out hair, out dark, out other, out bgN);
                isPocket = dark >= (hair + dark + other) * 0.2;
            }
            if (!isPocket) continue;
            foreach (int i in comp) pocket[i] = true;
            Console.WriteLine("pocket n={0} rgb={1:0},{2:0},{3:0} at {4},{5}", comp.Count, r, g, b, comp[0] % W, comp[0] / W);
        }
        return pocket;
    }

    static bool[] Invert(bool[] m) { var r = new bool[m.Length]; for (int i = 0; i < m.Length; i++) r[i] = !m[i]; return r; }

    // Euclidean-ish distance (chamfer 3-4) from each pixel to the nearest pixel where mask is true.
    static float[] Distance(bool[] target)
    {
        var d = new float[W * H];
        const float INF = 1e9f;
        for (int i = 0; i < d.Length; i++) d[i] = target[i] ? 0 : INF;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x; float v = d[i];
                if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                if (y > 0) v = Math.Min(v, d[i - W] + 1);
                if (x > 0 && y > 0) v = Math.Min(v, d[i - W - 1] + 1.4142f);
                if (x < W - 1 && y > 0) v = Math.Min(v, d[i - W + 1] + 1.4142f);
                d[i] = v;
            }
        for (int y = H - 1; y >= 0; y--)
            for (int x = W - 1; x >= 0; x--)
            {
                int i = y * W + x; float v = d[i];
                if (x < W - 1) v = Math.Min(v, d[i + 1] + 1);
                if (y < H - 1) v = Math.Min(v, d[i + W] + 1);
                if (x < W - 1 && y < H - 1) v = Math.Min(v, d[i + W + 1] + 1.4142f);
                if (x > 0 && y < H - 1) v = Math.Min(v, d[i + W - 1] + 1.4142f);
                d[i] = v;
            }
        return d;
    }

    static int Scan(Bitmap b, bool horizontal, int dir)
    {
        int len = horizontal ? b.Width : b.Height;
        int span = horizontal ? b.Height : b.Width;
        int[] lines = { (int)(span * 0.3), (int)(span * 0.5) };
        int best = dir > 0 ? int.MaxValue : int.MinValue;
        foreach (int line in lines)
            for (int k = 0; k < len / 4; k++)
            {
                int p = dir > 0 ? k : len - 1 - k;
                var c = horizontal ? b.GetPixel(p, line) : b.GetPixel(line, p);
                if ((c.R * 3 + c.G * 6 + c.B) / 10 < 110)
                {
                    best = dir > 0 ? Math.Min(best, p) : Math.Max(best, p);
                    break;
                }
            }
        return best;
    }

    static byte[] Read(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var a = new byte[b.Width * b.Height * 4];
        for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, a, y * b.Width * 4, b.Width * 4);
        b.UnlockBits(d);
        return a;
    }

    static Bitmap Write(byte[] a, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(a, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        b.UnlockBits(d);
        return b;
    }
}

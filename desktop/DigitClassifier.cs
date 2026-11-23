using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LoopSolver;

/// <summary>
/// Tells 0, 1, 2 and 3 apart. Each number is scaled into a small square and compared with the same digits
/// drawn in the fonts installed on this PC. Then, because every number on one board uses one font, each
/// number is compared again with the board's own average of each digit, which fixes most odd ones out.
/// </summary>
internal static class DigitClassifier
{
    public const int N = 20;

    private static readonly Lazy<List<(sbyte Digit, float[] Glyph)>> Templates = new(BuildTemplates);

    /// <summary>Scales the ink in a box to fit N x N (keeping its shape) and returns how much of each square is ink.</summary>
    public static float[] Normalize(Func<int, int, bool> isInk, int x0, int y0, int w, int h)
    {
        var g = new float[N * N];
        double scale = (double)N / Math.Max(w, h);
        double ox = (N - w * scale) / 2, oy = (N - h * scale) / 2;
        const int S = 3;
        for (int ty = 0; ty < N; ty++)
            for (int tx = 0; tx < N; tx++)
            {
                int hits = 0;
                for (int sy = 0; sy < S; sy++)
                    for (int sx = 0; sx < S; sx++)
                    {
                        int ix = (int)Math.Floor((tx + (sx + 0.5) / S - ox) / scale);
                        int iy = (int)Math.Floor((ty + (sy + 0.5) / S - oy) / scale);
                        if (ix >= 0 && iy >= 0 && ix < w && iy < h && isInk(x0 + ix, y0 + iy)) hits++;
                    }
                g[ty * N + tx] = hits / (float)(S * S);
            }
        return Blur(g);
    }

    public static List<ClueRead> ClassifyBoard(List<float[]> glyphs)
    {
        var templates = Templates.Value;
        int n = glyphs.Count;

        // Distance from each number to the closest drawing of each digit.
        var fontDist = new double[n, 4];
        for (int i = 0; i < n; i++)
        {
            for (int d = 0; d < 4; d++) fontDist[i, d] = double.MaxValue;
            foreach (var (digit, glyph) in templates)
                fontDist[i, digit] = Math.Min(fontDist[i, digit], Distance(glyphs[i], glyph));
        }

        var labels = new sbyte[n];
        for (int i = 0; i < n; i++) labels[i] = Best(i, d => fontDist[i, d]).Best;

        var dist = new double[4];
        for (int round = 0; round < 4; round++)
        {
            var sums = new float[4][];
            var counts = new int[4];
            for (int d = 0; d < 4; d++) sums[d] = new float[N * N];
            for (int i = 0; i < n; i++)
            {
                counts[labels[i]]++;
                Add(sums[labels[i]], glyphs[i], 1);
            }

            bool changed = false;
            var next = new sbyte[n];
            for (int i = 0; i < n; i++)
            {
                for (int d = 0; d < 4; d++)
                {
                    // The board's own average of digit d, leaving this number out of it.
                    int others = counts[d] - (labels[i] == d ? 1 : 0);
                    double boardDist = double.MaxValue;
                    if (others > 0)
                    {
                        var mean = (float[])sums[d].Clone();
                        if (labels[i] == d) Add(mean, glyphs[i], -1);
                        for (int k = 0; k < mean.Length; k++) mean[k] /= others;
                        boardDist = Distance(glyphs[i], mean);
                    }
                    dist[d] = Math.Min(fontDist[i, d], boardDist);
                }
                next[i] = Best(i, d => dist[d]).Best;
                changed |= next[i] != labels[i];
            }
            labels = next;
            if (!changed) break;
        }

        // Final answer with how clearly it won, for retrying doubtful numbers later.
        var result = new List<ClueRead>(n);
        {
            var sums = new float[4][];
            var counts = new int[4];
            for (int d = 0; d < 4; d++) sums[d] = new float[N * N];
            for (int i = 0; i < n; i++) { counts[labels[i]]++; Add(sums[labels[i]], glyphs[i], 1); }
            for (int i = 0; i < n; i++)
            {
                for (int d = 0; d < 4; d++)
                {
                    int others = counts[d] - (labels[i] == d ? 1 : 0);
                    double boardDist = double.MaxValue;
                    if (others > 0)
                    {
                        var mean = (float[])sums[d].Clone();
                        if (labels[i] == d) Add(mean, glyphs[i], -1);
                        for (int k = 0; k < mean.Length; k++) mean[k] /= others;
                        boardDist = Distance(glyphs[i], mean);
                    }
                    dist[d] = Math.Min(fontDist[i, d], boardDist);
                }
                var (best, second, margin) = Best(i, d => dist[d]);
                result.Add(new ClueRead(best, second, margin));
            }
        }
        return result;
    }

    private static (sbyte Best, sbyte Second, double Margin) Best(int i, Func<int, double> score)
    {
        int best = 0, second = 1;
        if (score(second) < score(best)) (best, second) = (second, best);
        for (int d = 2; d < 4; d++)
        {
            if (score(d) < score(best)) { second = best; best = d; }
            else if (score(d) < score(second)) second = d;
        }
        return ((sbyte)best, (sbyte)second, score(second) - score(best));
    }

    private static void Add(float[] into, float[] g, float sign)
    {
        for (int k = 0; k < into.Length; k++) into[k] += sign * g[k];
    }

    private static double Distance(float[] a, float[] b)
    {
        double s = 0;
        for (int k = 0; k < a.Length; k++)
        {
            double d = a[k] - b[k];
            s += d * d;
        }
        return s;
    }

    private static float[] Blur(float[] g)
    {
        var o = new float[g.Length];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float s = 0;
                int c = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= N || yy >= N) continue;
                        s += g[yy * N + xx];
                        c++;
                    }
                o[y * N + x] = s / c;
            }
        return o;
    }

    private static List<(sbyte, float[])> BuildTemplates()
    {
        string[] fonts =
        [
            "Arial", "Segoe UI", "Verdana", "Tahoma", "Calibri", "Trebuchet MS", "Century Gothic", "Franklin Gothic Medium",
            "Times New Roman", "Georgia", "Cambria", "Palatino Linotype", "Courier New", "Consolas", "Lucida Console",
            "Meiryo", "Yu Gothic", "MS Gothic", "Comic Sans MS",
        ];
        using var installed = new InstalledFontCollection();
        var have = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<(sbyte, float[])>();
        foreach (string name in fonts.Where(have.Contains).DefaultIfEmpty(FontFamily.GenericSansSerif.Name))
            foreach (FontStyle style in new[] { FontStyle.Regular, FontStyle.Bold })
            {
                using var font = new Font(name, 48, style, GraphicsUnit.Pixel);
                for (sbyte d = 0; d <= 3; d++)
                {
                    using var bmp = new Bitmap(100, 100, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.DrawString(d.ToString(), font, Brushes.Black, 20, 10);
                    }
                    byte[] lum = ImageTools.Luminance(bmp);
                    int x0 = 100, y0 = 100, x1 = -1, y1 = -1;
                    for (int y = 0; y < 100; y++)
                        for (int x = 0; x < 100; x++)
                            if (lum[y * 100 + x] < 128)
                            {
                                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                            }
                    if (x1 < 0) continue;
                    list.Add((d, Normalize((x, y) => lum[y * 100 + x] < 128, x0, y0, x1 - x0 + 1, y1 - y0 + 1)));
                }
            }
        return list;
    }
}

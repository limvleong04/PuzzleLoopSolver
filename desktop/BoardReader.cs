using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LoopSolver;

/// <summary>How a number was read: the best guess, the runner-up, and how clearly the best one won.</summary>
internal sealed record ClueRead(sbyte Best, sbyte Second, double Margin);

/// <summary>A board read from a picture. Positions are in the picture's pixels.</summary>
internal sealed class BoardReading
{
    public int Width { get; init; }          // cells across
    public int Height { get; init; }         // cells down
    public double X0 { get; init; }          // dot (row i, column j) sits at (X0 + j*Sx, Y0 + i*Sy)
    public double Y0 { get; init; }
    public double Sx { get; init; }
    public double Sy { get; init; }
    public double DotSize { get; init; }
    public sbyte[] Clues { get; set; } = [];        // row by row, -1 = empty
    public bool[] DrawnLines { get; init; } = [];   // edges already drawn in the picture, Grid order
    public ClueRead?[] Reads { get; set; } = [];    // per cell, for retrying doubtful numbers
    public string Debug { get; init; } = "";        // how the grid was found, for --read

    public PointF Dot(int row, int col) => new((float)(X0 + col * Sx), (float)(Y0 + row * Sy));

    public PointF EdgeMiddle(int e)
    {
        var (r1, c1, r2, c2) = Grid.Ends(e, Width, Height);
        PointF a = Dot(r1, c1), b = Dot(r2, c2);
        return new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    public string CluesAsText()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < Height; r++)
        {
            for (int c = 0; c < Width; c++)
            {
                if (c > 0) sb.Append(' ');
                sbyte v = Clues[r * Width + c];
                sb.Append(v < 0 ? '.' : (char)('0' + v));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Reads the text form back: one line per row, a digit or '.' per cell, spaces optional.</summary>
    public static sbyte[]? ParseClues(string text, int width, int height, out string error)
    {
        error = "";
        var rows = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (rows.Count != height)
        {
            error = $"Expected {height} rows, found {rows.Count}.";
            return null;
        }
        var clues = new sbyte[width * height];
        for (int r = 0; r < height; r++)
        {
            var cells = rows[r].Where(ch => !char.IsWhiteSpace(ch)).ToList();
            if (cells.Count != width)
            {
                error = $"Row {r + 1} has {cells.Count} entries, expected {width}.";
                return null;
            }
            for (int c = 0; c < width; c++)
            {
                char ch = cells[c];
                if (ch is >= '0' and <= '4') clues[r * width + c] = (sbyte)(ch - '0');
                else if (ch is '.' or '-' or '_') clues[r * width + c] = -1;
                else
                {
                    error = $"Row {r + 1}: '{ch}' isn't a number or a dot.";
                    return null;
                }
            }
        }
        return clues;
    }
}

/// <summary>
/// Reads a Slitherlink board from a screenshot of any site or app: finds the grid of dots, reads the
/// numbers inside the cells, and notes lines that are already drawn. No site-specific code.
/// </summary>
internal static class BoardReader
{
    private sealed class Blob
    {
        public int Id, Count;
        public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
        public long SumX, SumY;
        public int W => MaxX - MinX + 1;
        public int H => MaxY - MinY + 1;
        public int Size => Math.Max(W, H);
        public double Cx => (double)SumX / Count + 0.5;
        public double Cy => (double)SumY / Count + 0.5;
    }

    private sealed class Lattice
    {
        public double X0, Y0, Sx, Sy, DotSize;
        public int MinJ, MaxJ, MinI, MaxI, Support;
        public HashSet<int> DotIds = [];
    }

    public static BoardReading? Read(Bitmap picture, out string error)
    {
        error = "";
        int w = picture.Width, h = picture.Height;
        byte[] lum = ImageTools.Luminance(picture);

        // Ink = clearly darker than the background. A dark theme is flipped first.
        var hist = new int[256];
        foreach (byte b in lum) hist[b]++;
        if (Percentile(hist, lum.Length, 0.5) < 110)
        {
            for (int i = 0; i < lum.Length; i++) lum[i] = (byte)(255 - lum[i]);
            Array.Reverse(hist);
        }
        int background = Percentile(hist, lum.Length, 0.5);
        int darkest = Percentile(hist, lum.Length, 0.01);
        if (background - darkest < 40)
        {
            error = "The picked area has almost no contrast. Pick the puzzle board itself.";
            return null;
        }
        int threshold = (background + darkest) / 2;
        var ink = new bool[lum.Length];
        for (int i = 0; i < lum.Length; i++) ink[i] = lum[i] < threshold;

        var labels = new int[lum.Length];
        List<Blob> blobs = FindBlobs(ink, w, h, labels);

        // The dots: small, solid, all the same size, on an evenly spaced grid.
        var candidates = blobs.Where(b => b.Size <= Math.Max(4, Math.Min(w, h) / 4) && LooksLikeDot(b)).ToList();
        Lattice? lat = null;
        foreach (int size in candidates.GroupBy(b => b.Size).OrderByDescending(g => g.Count()).Take(8).Select(g => g.Key))
        {
            int tol = Math.Max(1, size / 4);
            Lattice? fit = FitLattice(candidates.Where(b => Math.Abs(b.Size - size) <= tol).ToList(), size);
            if (fit != null && (lat == null || fit.Support > lat.Support)) lat = fit;
        }
        if (lat == null)
        {
            error = "Couldn't find the board's grid of dots. Pick just the board, ideally before drawing on it.";
            return null;
        }

        // Grow the grid outwards one row or column at a time while nearly every grid point along it has ink:
        // on a board every point is a dot, or a drawn line runs through it. Text and buttons around the
        // board don't line up like that. This also recovers rows whose dots are all hidden under lines.
        double reachDot = Math.Max(1.5, lat.DotSize * 0.6);
        int minJ = lat.MinJ, maxJ = lat.MaxJ, minI = lat.MinI, maxI = lat.MaxI;
        bool InkAt(int i, int j)
        {
            double x = lat.X0 + j * lat.Sx, y = lat.Y0 + i * lat.Sy;
            return x - reachDot >= 0 && y - reachDot >= 0 && x + reachDot < w && y + reachDot < h
                && InkNear(ink, w, h, x, y, reachDot, reachDot);
        }
        double RowFill(int i)
        {
            int hit = 0;
            for (int j = minJ; j <= maxJ; j++) if (InkAt(i, j)) hit++;
            return hit / (double)(maxJ - minJ + 1);
        }
        double ColumnFill(int j)
        {
            int hit = 0;
            for (int i = minI; i <= maxI; i++) if (InkAt(i, j)) hit++;
            return hit / (double)(maxI - minI + 1);
        }
        bool FullRow(int i) => RowFill(i) >= 0.9;
        bool FullColumn(int j) => ColumnFill(j) >= 0.9;
        string debug = $"core dots: columns {lat.MinJ}..{lat.MaxJ}, rows {lat.MinI}..{lat.MaxI}, size {lat.DotSize}, step {lat.Sx:F1} x {lat.Sy:F1}, origin {lat.X0:F1},{lat.Y0:F1}, {lat.Support} supported";

        // Letters near the board can pass for dots and stretch the starting grid. Trim its emptiest edge
        // row or column until all four edges are nearly full, then grow.
        while (true)
        {
            double top = RowFill(minI), bottom = RowFill(maxI), left = ColumnFill(minJ), right = ColumnFill(maxJ);
            double worst = Math.Min(Math.Min(top, bottom), Math.Min(left, right));
            if (worst >= 0.9) break;
            if (worst == top && maxI > minI) minI++;
            else if (worst == bottom && maxI > minI) maxI--;
            else if (worst == left && maxJ > minJ) minJ++;
            else if (worst == right && maxJ > minJ) maxJ--;
            else break;
        }
        for (bool grew = true; grew;)
        {
            grew = false;
            if (FullRow(minI - 1)) { minI--; grew = true; }
            if (FullRow(maxI + 1)) { maxI++; grew = true; }
            if (FullColumn(minJ - 1)) { minJ--; grew = true; }
            if (FullColumn(maxJ + 1)) { maxJ++; grew = true; }
        }

        int cols = maxJ - minJ, rows = maxI - minI;
        if (cols < 1 || rows < 1)
        {
            error = "Found only one row or column of dots. Pick the whole board.";
            return null;
        }
        if (cols > 150 || rows > 150)
        {
            error = $"That looks like a {cols} x {rows} grid, which is too big to be right. Pick just the board.";
            return null;
        }
        double sx = lat.Sx, sy = lat.Sy;
        double x0 = lat.X0 + minJ * sx, y0 = lat.Y0 + minI * sy;

        // Lines already drawn: ink along most of the edge between two dots. A cross mark only covers the middle.
        int edgeCount = Grid.EdgeCount(cols, rows);
        var drawn = new bool[edgeCount];
        double reach = Math.Max(1, lat.DotSize / 3);
        for (int e = 0; e < edgeCount; e++)
        {
            var (r1, c1, r2, c2) = Grid.Ends(e, cols, rows);
            double ax = x0 + c1 * sx, ay = y0 + r1 * sy, bx = x0 + c2 * sx, by = y0 + r2 * sy;
            bool horizontal = r1 == r2;
            int hits = 0;
            for (int k = 0; k < 9; k++)
            {
                double f = 0.3 + 0.4 * k / 8;
                if (InkNear(ink, w, h, ax + (bx - ax) * f, ay + (by - ay) * f, horizontal ? 0 : reach, horizontal ? reach : 0)) hits++;
            }
            drawn[e] = hits >= 8;
        }

        // The numbers: ink whose middle sits well inside a cell, away from the edges and dots.
        var perCell = new List<Blob>?[cols * rows];
        foreach (var b in blobs)
        {
            if (lat.DotIds.Contains(b.Id) || b.W >= 0.85 * sx || b.H >= 0.85 * sy) continue;
            double fx = (b.Cx - x0) / sx, fy = (b.Cy - y0) / sy;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fy);
            if (c < 0 || r < 0 || c >= cols || r >= rows) continue;
            double ux = fx - c, uy = fy - r;
            if (ux < 0.2 || ux > 0.8 || uy < 0.2 || uy > 0.8) continue;
            (perCell[r * cols + c] ??= []).Add(b);
        }

        var clues = new sbyte[cols * rows];
        Array.Fill(clues, (sbyte)-1);
        var glyphCells = new List<int>();
        var glyphs = new List<float[]>();
        for (int i = 0; i < perCell.Length; i++)
        {
            var list = perCell[i];
            if (list == null) continue;
            int gx0 = list.Min(b => b.MinX), gy0 = list.Min(b => b.MinY);
            int gx1 = list.Max(b => b.MaxX), gy1 = list.Max(b => b.MaxY);
            int count = list.Sum(b => b.Count);
            if (gy1 - gy0 + 1 < 0.2 * sy || count < 0.004 * sx * sy) continue; // specks, not a number
            var ids = list.Select(b => b.Id).ToHashSet();
            glyphCells.Add(i);
            glyphs.Add(DigitClassifier.Normalize((x, y) => ids.Contains(labels[y * w + x]), gx0, gy0, gx1 - gx0 + 1, gy1 - gy0 + 1));
        }

        var reads = new ClueRead?[cols * rows];
        List<ClueRead> results = DigitClassifier.ClassifyBoard(glyphs);
        for (int k = 0; k < glyphCells.Count; k++)
        {
            reads[glyphCells[k]] = results[k];
            clues[glyphCells[k]] = results[k].Best;
        }

        return new BoardReading
        {
            Width = cols, Height = rows, X0 = x0, Y0 = y0, Sx = sx, Sy = sy, DotSize = lat.DotSize,
            Clues = clues, DrawnLines = drawn, Reads = reads,
            Debug = debug + $"; grown to columns {minJ}..{maxJ}, rows {minI}..{maxI}",
        };
    }

    private static bool LooksLikeDot(Blob b)
    {
        int s = b.Size;
        return Math.Abs(b.W - b.H) <= Math.Max(1, s / 3) && b.Count >= 0.55 * b.W * b.H;
    }

    private static Lattice? FitLattice(List<Blob> pts, int size)
    {
        if (pts.Count < 4) return null;
        double tol = Math.Max(2, size);

        // Step between neighbouring dots, from each dot's nearest neighbour to the right and below.
        var dxs = new List<double>();
        var dys = new List<double>();
        foreach (var p in pts)
        {
            double bx = double.MaxValue, by = double.MaxValue;
            foreach (var q in pts)
            {
                double dx = q.Cx - p.Cx, dy = q.Cy - p.Cy;
                if (Math.Abs(dy) <= tol && dx > tol * 1.5 && dx < bx) bx = dx;
                if (Math.Abs(dx) <= tol && dy > tol * 1.5 && dy < by) by = dy;
            }
            if (bx < double.MaxValue) dxs.Add(bx);
            if (by < double.MaxValue) dys.Add(by);
        }
        if (dxs.Count == 0 || dys.Count == 0) return null;
        double sx = Median(dxs), sy = Median(dys);
        if (sx < size * 2 || sy < size * 2) return null;

        // A dot belongs to the grid if another dot sits exactly one step away.
        var supported = pts.Where(p => pts.Any(q =>
            (Math.Abs(q.Cy - p.Cy) <= tol && Math.Abs(Math.Abs(q.Cx - p.Cx) - sx) <= 0.2 * sx) ||
            (Math.Abs(q.Cx - p.Cx) <= tol && Math.Abs(Math.Abs(q.Cy - p.Cy) - sy) <= 0.2 * sy))).ToList();
        if (supported.Count < 3) return null;

        var (x0, fsx) = FitAxis(supported.Select(p => p.Cx), supported.Min(p => p.Cx), sx);
        var (y0, fsy) = FitAxis(supported.Select(p => p.Cy), supported.Min(p => p.Cy), sy);
        var on = supported.Where(p => OnGrid(p.Cx, x0, fsx) && OnGrid(p.Cy, y0, fsy)).ToList();
        if (on.Count < 3) return null;
        (x0, fsx) = FitAxis(on.Select(p => p.Cx), x0, fsx);
        (y0, fsy) = FitAxis(on.Select(p => p.Cy), y0, fsy);

        // The board's extent starts from the dots that have a neighbour; Read grows it from there.
        var all = pts.Where(p => OnGrid(p.Cx, x0, fsx) && OnGrid(p.Cy, y0, fsy)).ToList();
        return new Lattice
        {
            X0 = x0, Sx = fsx, Y0 = y0, Sy = fsy,
            DotSize = Median(on.Select(p => (double)p.Size).ToList()),
            Support = on.Count,
            MinJ = on.Min(p => Index(p.Cx, x0, fsx)), MaxJ = on.Max(p => Index(p.Cx, x0, fsx)),
            MinI = on.Min(p => Index(p.Cy, y0, fsy)), MaxI = on.Max(p => Index(p.Cy, y0, fsy)),
            DotIds = all.Select(p => p.Id).ToHashSet(),
        };
    }

    private static int Index(double v, double origin, double step) => (int)Math.Round((v - origin) / step);

    private static bool OnGrid(double v, double origin, double step)
    {
        double f = (v - origin) / step;
        return Math.Abs(f - Math.Round(f)) <= 0.2;
    }

    /// <summary>Least-squares fit of position = origin + index * step.</summary>
    private static (double Origin, double Step) FitAxis(IEnumerable<double> values, double origin, double step)
    {
        double n = 0, sk = 0, sv = 0, skk = 0, skv = 0;
        foreach (double v in values)
        {
            double k = Math.Round((v - origin) / step);
            n++; sk += k; sv += v; skk += k * k; skv += k * v;
        }
        double den = n * skk - sk * sk;
        if (n < 2 || Math.Abs(den) < 1e-9)
            return ((sv - step * sk) / Math.Max(1, n), step); // every dot in one line: keep the step
        double s = (n * skv - sk * sv) / den;
        return ((sv - s * sk) / n, s);
    }

    private static bool InkNear(bool[] ink, int w, int h, double px, double py, double rx, double ry)
    {
        int x0 = (int)Math.Floor(px - rx), x1 = (int)Math.Floor(px + rx);
        int y0 = (int)Math.Floor(py - ry), y1 = (int)Math.Floor(py + ry);
        for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                if (ink[y * w + x]) return true;
        return false;
    }

    private static List<Blob> FindBlobs(bool[] ink, int w, int h, int[] labels)
    {
        var blobs = new List<Blob>();
        var stack = new Stack<int>();
        for (int start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || labels[start] != 0) continue;
            var b = new Blob { Id = blobs.Count + 1 };
            labels[start] = b.Id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                b.Count++; b.SumX += x; b.SumY += y;
                if (x < b.MinX) b.MinX = x;
                if (x > b.MaxX) b.MaxX = x;
                if (y < b.MinY) b.MinY = y;
                if (y > b.MaxY) b.MaxY = y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int q = ny * w + nx;
                        if (ink[q] && labels[q] == 0)
                        {
                            labels[q] = b.Id;
                            stack.Push(q);
                        }
                    }
            }
            blobs.Add(b);
        }
        return blobs;
    }

    private static int Percentile(int[] hist, int total, double fraction)
    {
        long target = (long)(total * fraction), seen = 0;
        for (int i = 0; i < 256; i++)
        {
            seen += hist[i];
            if (seen > target) return i;
        }
        return 255;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}

internal static class ImageTools
{
    /// <summary>Brightness 0..255 of every pixel, row by row.</summary>
    public static byte[] Luminance(Bitmap source)
    {
        int w = source.Width, h = source.Height;
        using Bitmap bmp = source.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format24bppRgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var raw = new byte[stride * h];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var lum = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * stride + x * 3;
                    lum[y * w + x] = (byte)((raw[p + 2] * 299 + raw[p + 1] * 587 + raw[p] * 114) / 1000);
                }
            return lum;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}

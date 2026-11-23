using System.Diagnostics;

namespace LoopSolver;

/// <summary>Outcome of a solve. <see cref="Edges"/> follows the <see cref="Grid"/> edge order.</summary>
internal sealed record SolveResult(bool Ok, sbyte[]? Edges, bool Unique, string Message, int Guesses, long Milliseconds);

/// <summary>
/// Edge numbering shared by the reader, the solver and the drawing code. Horizontal edges first
/// (dot row 0..H, column 0..W-1, index r*W + c), then vertical edges (row 0..H-1, dot column 0..W,
/// index HE + r*(W+1) + c). It is the same order as puzzle-loop.com's answer string.
/// </summary>
internal static class Grid
{
    public static int EdgeCount(int width, int height) => (height + 1) * width + height * (width + 1);

    /// <summary>The two dots (row, column) at the ends of edge <paramref name="e"/>.</summary>
    public static (int R1, int C1, int R2, int C2) Ends(int e, int width, int height)
    {
        int he = (height + 1) * width;
        if (e < he)
            return (e / width, e % width, e / width, e % width + 1);
        e -= he;
        return (e / (width + 1), e % (width + 1), e / (width + 1) + 1, e % (width + 1));
    }

    /// <summary>Task text as puzzle-loop.com and Simon Tatham's Loopy write it: digits are clues, a..z skip 1..26 cells.</summary>
    public static sbyte[] DecodeTask(string task, int width, int height)
    {
        var clues = new sbyte[width * height];
        Array.Fill(clues, (sbyte)-1);
        int i = 0;
        foreach (char ch in task)
        {
            if (ch is >= '0' and <= '9')
            {
                if (i < clues.Length) clues[i] = (sbyte)(ch - '0');
                i++;
            }
            else if (ch is >= 'a' and <= 'z')
                i += ch - 'a' + 1;
        }
        if (i != width * height)
            throw new FormatException($"The task describes {i} cells but the board is {width} x {height}.");
        return clues;
    }
}

/// <summary>
/// Slitherlink solver, the same algorithm as the browser tool. Simple rules first (numbers, dots, no early
/// loop), then trial (try both values of an edge; a contradiction forces the other), then guessing.
/// It keeps searching after the first loop to tell whether the answer is the only one, which is how a
/// misread number gets noticed.
/// </summary>
internal sealed class Solver
{
    public const sbyte Unknown = 0, Line = 1, Cross = 2;

    private readonly int _nc, _nv, _e, _maxGuesses, _wanted;
    private readonly int _w;
    private readonly sbyte[] _clues;
    private readonly int[] _eA, _eB, _eC1, _eC2, _cellE, _vE, _vDeg;
    private readonly sbyte[] _val;
    private readonly int[] _cLine, _cCross, _vLine, _vCross, _mate, _plen;
    private int _totalLines, _unknown, _closed, _unsat;
    private readonly int[] _trail, _trailM, _mt;
    private int _tl, _ml;
    private readonly int[] _q;
    private readonly bool[] _inQ;
    private readonly int _qSize;
    private int _qh, _qt, _qn;
    private readonly int[] _stamp, _common;
    private readonly sbyte[] _sval, _commonVal;
    private int _curStamp;
    private int _guesses, _found;
    private bool _aborted;
    private sbyte[]? _first;

    public static SolveResult Solve(int width, int height, sbyte[] clues, int maxGuesses = 200_000, bool checkUnique = true)
    {
        var sw = Stopwatch.StartNew();
        var s = new Solver(width, height, clues, maxGuesses, checkUnique ? 2 : 1);
        s.Run();
        sw.Stop();
        if (s._found == 0)
        {
            string why = s._aborted ? $"I gave up after {s._guesses:N0} guesses." : "No loop fits these numbers.";
            return new SolveResult(false, null, false, why, s._guesses, sw.ElapsedMilliseconds);
        }
        bool unique = s._found == 1 && !s._aborted;
        string message = !checkUnique || unique ? ""
            : s._found > 1 ? "More than one loop fits these numbers."
            : "Found a loop, but couldn't confirm it is the only one.";
        return new SolveResult(true, s._first, unique, message, s._guesses, sw.ElapsedMilliseconds);
    }

    private Solver(int w, int h, sbyte[] clues, int maxGuesses, int wanted)
    {
        _w = w;
        _clues = clues;
        _maxGuesses = maxGuesses;
        _wanted = wanted;
        _nc = w * h;
        _nv = (w + 1) * (h + 1);
        int he = (h + 1) * w;
        _e = he + h * (w + 1);

        _eA = new int[_e]; _eB = new int[_e]; _eC1 = new int[_e]; _eC2 = new int[_e];
        Array.Fill(_eC1, -1); Array.Fill(_eC2, -1);
        _cellE = new int[_nc * 4];
        _vE = new int[_nv * 4];
        Array.Fill(_vE, -1);
        _vDeg = new int[_nv];

        for (int r = 0; r <= h; r++)
            for (int c = 0; c < w; c++)
            {
                int e = r * w + c;
                _eA[e] = Dot(r, c); _eB[e] = Dot(r, c + 1);
                if (r > 0) _eC1[e] = (r - 1) * w + c;
                if (r < h) _eC2[e] = r * w + c;
            }
        for (int r = 0; r < h; r++)
            for (int c = 0; c <= w; c++)
            {
                int e = he + r * (w + 1) + c;
                _eA[e] = Dot(r, c); _eB[e] = Dot(r + 1, c);
                if (c > 0) _eC1[e] = r * w + c - 1;
                if (c < w) _eC2[e] = r * w + c;
            }
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = (r * w + c) * 4;
                _cellE[i] = r * w + c;
                _cellE[i + 1] = (r + 1) * w + c;
                _cellE[i + 2] = he + r * (w + 1) + c;
                _cellE[i + 3] = he + r * (w + 1) + c + 1;
            }
        for (int e = 0; e < _e; e++)
        {
            _vE[_eA[e] * 4 + _vDeg[_eA[e]]++] = e;
            _vE[_eB[e] * 4 + _vDeg[_eB[e]]++] = e;
        }

        _val = new sbyte[_e];
        _cLine = new int[_nc]; _cCross = new int[_nc];
        _vLine = new int[_nv]; _vCross = new int[_nv];
        _mate = new int[_nv]; _plen = new int[_nv];
        _unknown = _e;
        for (int c = 0; c < _nc; c++)
            if (clues[c] > 0) _unsat++;

        _trail = new int[_e]; _trailM = new int[_e]; _mt = new int[_e * 6 + 6];
        _qSize = _nc + _nv; _q = new int[_qSize]; _inQ = new bool[_qSize];
        _stamp = new int[_e]; _common = new int[_e]; _sval = new sbyte[_e]; _commonVal = new sbyte[_e];
    }

    private int Dot(int r, int c) => r * (_w + 1) + c;

    private void Run()
    {
        for (int c = 0; c < _nc; c++) Push(c);
        for (int v = 0; v < _nv; v++) Push(_nc + v);
        Search();
    }

    private void Push(int i)
    {
        if (_inQ[i]) return;
        _inQ[i] = true;
        _q[_qt] = i;
        _qt = _qt + 1 == _qSize ? 0 : _qt + 1;
        _qn++;
    }

    private void ClearQueue()
    {
        while (_qn > 0)
        {
            _inQ[_q[_qh]] = false;
            _qh = _qh + 1 == _qSize ? 0 : _qh + 1;
            _qn--;
        }
        _qh = _qt = 0;
    }

    private bool LineInCell(int c)
    {
        int k = _clues[c], n = ++_cLine[c];
        if (k < 0) return true;
        if (n - 1 == k) _unsat++; else if (n == k) _unsat--;
        return n <= k;
    }

    private void LineOutOfCell(int c)
    {
        int k = _clues[c], n = _cLine[c]--;
        if (k < 0) return;
        if (n == k) _unsat++; else if (n - 1 == k) _unsat--;
    }

    /// <summary>Sets one edge and records it for undo. Returns false on a contradiction.</summary>
    private bool Assign(int e, sbyte x)
    {
        _val[e] = x; _trail[_tl] = e; _trailM[_tl] = _ml; _tl++; _unknown--;
        int a = _eA[e], b = _eB[e], c1 = _eC1[e], c2 = _eC2[e];
        bool ok = true;
        if (x == Line)
        {
            if (_closed != 0) ok = false; // a finished loop can't take another line
            _totalLines++;
            if (c1 >= 0 && !LineInCell(c1)) ok = false;
            if (c2 >= 0 && !LineInCell(c2)) ok = false;
            int la = ++_vLine[a], lb = ++_vLine[b];
            if (la > 2 || lb > 2)
                ok = false;
            else if (la == 2 && lb == 2 && _mate[a] == b)
            {
                // Joins the two ends of one segment: only allowed if that loop is every line and meets every number.
                if (_totalLines != _plen[a] + 1 || _unsat != 0) ok = false;
                _mt[_ml++] = -1; _mt[_ml++] = _closed; _mt[_ml++] = 0;
                _closed = 1;
            }
            else
            {
                int ea = la == 1 ? a : _mate[a];
                int eb = lb == 1 ? b : _mate[b];
                int len = (la == 1 ? 0 : _plen[a]) + (lb == 1 ? 0 : _plen[b]) + 1;
                _mt[_ml++] = ea; _mt[_ml++] = _mate[ea]; _mt[_ml++] = _plen[ea];
                _mt[_ml++] = eb; _mt[_ml++] = _mate[eb]; _mt[_ml++] = _plen[eb];
                _mate[ea] = eb; _mate[eb] = ea; _plen[ea] = len; _plen[eb] = len;
                Push(_nc + ea); Push(_nc + eb);
            }
        }
        else
        {
            if (c1 >= 0) { _cCross[c1]++; if (_clues[c1] > 4 - _cCross[c1]) ok = false; }
            if (c2 >= 0) { _cCross[c2]++; if (_clues[c2] > 4 - _cCross[c2]) ok = false; }
            _vCross[a]++; _vCross[b]++;
        }
        if (c1 >= 0) Push(c1);
        if (c2 >= 0) Push(c2);
        Push(_nc + a); Push(_nc + b);
        return ok;
    }

    private void Undo(int t)
    {
        while (_tl > t)
        {
            _tl--;
            int e = _trail[_tl];
            sbyte x = _val[e];
            _val[e] = Unknown; _unknown++;
            int a = _eA[e], b = _eB[e], c1 = _eC1[e], c2 = _eC2[e];
            if (x == Line)
            {
                _totalLines--;
                if (c1 >= 0) LineOutOfCell(c1);
                if (c2 >= 0) LineOutOfCell(c2);
                _vLine[a]--; _vLine[b]--;
            }
            else
            {
                if (c1 >= 0) _cCross[c1]--;
                if (c2 >= 0) _cCross[c2]--;
                _vCross[a]--; _vCross[b]--;
            }
            int m0 = _trailM[_tl];
            while (_ml > m0)
            {
                _ml -= 3;
                int v = _mt[_ml];
                if (v < 0) _closed = _mt[_ml + 1];
                else { _mate[v] = _mt[_ml + 1]; _plen[v] = _mt[_ml + 2]; }
            }
        }
        ClearQueue();
    }

    private bool ClosureWouldBeValid(int e, int v)
    {
        if (_closed != 0 || _totalLines != _plen[v]) return false; // lines exist outside this segment
        int d = _unsat;
        foreach (int c in new[] { _eC1[e], _eC2[e] })
        {
            if (c < 0 || _clues[c] < 0) continue;
            int n = _cLine[c], k = _clues[c];
            if (n == k) d++; else if (n + 1 == k) d--;
        }
        return d == 0;
    }

    private bool Propagate()
    {
        while (true)
        {
            while (_qn > 0)
            {
                int i = _q[_qh];
                _qh = _qh + 1 == _qSize ? 0 : _qh + 1;
                _qn--;
                _inQ[i] = false;
                if (i < _nc)
                {
                    int k = _clues[i];
                    if (k < 0) continue;
                    int l = _cLine[i], u = 4 - l - _cCross[i];
                    if (l > k || l + u < k) return false;
                    if (u == 0) continue;
                    sbyte want = l == k ? Cross : l + u == k ? Line : Unknown;
                    if (want == Unknown) continue;
                    for (int j = i * 4, end = j + 4; j < end; j++)
                    {
                        int e = _cellE[j];
                        if (_val[e] == Unknown && !Assign(e, want)) return false;
                    }
                }
                else
                {
                    int v = i - _nc;
                    int l = _vLine[v], u = _vDeg[v] - l - _vCross[v];
                    if (l > 2 || (l == 1 && u == 0)) return false;
                    if (u == 0) continue;
                    int b = v * 4, deg = _vDeg[v];
                    if (l == 2 || (l == 0 && u == 1))
                    {
                        for (int j = 0; j < deg; j++)
                        {
                            int e = _vE[b + j];
                            if (_val[e] == Unknown && !Assign(e, Cross)) return false;
                        }
                    }
                    else if (l == 1)
                    {
                        if (u == 1)
                        {
                            for (int j = 0; j < deg; j++)
                            {
                                int e = _vE[b + j];
                                if (_val[e] == Unknown && !Assign(e, Line)) return false;
                            }
                        }
                        else
                        {
                            int m = _mate[v];
                            for (int j = 0; j < deg; j++)
                            {
                                int e = _vE[b + j];
                                if (_val[e] != Unknown) continue;
                                int other = _eA[e] == v ? _eB[e] : _eA[e];
                                if (other == m && !ClosureWouldBeValid(e, v) && !Assign(e, Cross)) return false;
                            }
                        }
                    }
                }
            }
            if (_closed != 0 && _unknown > 0)
            {
                for (int e = 0; e < _e; e++)
                    if (_val[e] == Unknown && !Assign(e, Cross)) return false;
                continue;
            }
            return true;
        }
    }

    /// <summary>One trial pass over every open edge. -1 contradiction, 0 nothing learned, 1 progress.</summary>
    private int ProbePass()
    {
        bool progress = false;
        for (int e = 0; e < _e; e++)
        {
            if (_val[e] != Unknown) continue;
            int t = _tl;
            bool okL = Assign(e, Line) && Propagate();
            if (okL)
            {
                _curStamp++;
                for (int i = t + 1; i < _tl; i++) { int f = _trail[i]; _stamp[f] = _curStamp; _sval[f] = _val[f]; }
            }
            Undo(t);
            if (!okL)
            {
                if (!(Assign(e, Cross) && Propagate())) return -1;
                progress = true;
                continue;
            }
            bool okX = Assign(e, Cross) && Propagate();
            int nc = 0;
            if (okX)
                for (int i = t + 1; i < _tl; i++)
                {
                    int f = _trail[i];
                    if (_stamp[f] == _curStamp && _sval[f] == _val[f]) { _common[nc] = f; _commonVal[nc] = _val[f]; nc++; }
                }
            Undo(t);
            if (!okX)
            {
                if (!(Assign(e, Line) && Propagate())) return -1;
                progress = true;
                continue;
            }
            if (nc > 0)
            {
                for (int i = 0; i < nc; i++)
                {
                    int f = _common[i];
                    if (_val[f] == Unknown && !Assign(f, _commonVal[i])) return -1;
                }
                if (!Propagate()) return -1;
                progress = true;
            }
        }
        return progress ? 1 : 0;
    }

    private int PickBranchEdge()
    {
        for (int v = 0; v < _nv; v++)
        {
            if (_vLine[v] != 1) continue;
            for (int j = 0; j < _vDeg[v]; j++)
            {
                int e = _vE[v * 4 + j];
                if (_val[e] == Unknown) return e;
            }
        }
        for (int e = 0; e < _e; e++)
            if (_val[e] == Unknown) return e;
        return -1;
    }

    /// <summary>Returns true when the search should stop (enough loops found, or out of guesses).</summary>
    private bool Search()
    {
        if (!Propagate()) return false;
        while (_unknown > 0)
        {
            int r = ProbePass();
            if (r < 0) return false;
            if (r == 0) break;
        }
        if (_unknown == 0)
        {
            if (_closed == 0) return false;
            _found++;
            _first ??= (sbyte[])_val.Clone();
            return _found >= _wanted;
        }
        if (++_guesses > _maxGuesses) { _aborted = true; return true; }
        int e = PickBranchEdge();
        int t = _tl;
        if (Assign(e, Line) && Search()) return true;
        Undo(t);
        if (Assign(e, Cross) && Search()) return true;
        Undo(t);
        return false;
    }
}

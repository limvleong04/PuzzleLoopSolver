using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LoopSolver;

/// <summary>Checks you can run from a terminal, without opening the window.</summary>
internal static class ConsoleCommands
{
    public static int Run(string[] args)
    {
        if (!Console.IsOutputRedirected) NativeMethods.AttachConsole(-1);
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "--selftest" when args.Length >= 2 => SelfTest(args[1]),
                "--check" when args.Length >= 2 => CheckBoards(args[1]),
                "--read" when args.Length >= 2 => ReadOne(args[1], args.Skip(2).Select(int.Parse).ToArray()),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("LoopSolver                               open the window");
        Console.WriteLine("LoopSolver --read <image> [x y w h]      read a board from a picture (optionally just that area) and solve it");
        Console.WriteLine("LoopSolver --check <boards.json>         read every test picture and compare with the right numbers");
        Console.WriteLine("LoopSolver --selftest <puzzles.json>     solve every test puzzle and check it against the site's hash");
        return 1;
    }

    private static int SelfTest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int failed = 0, total = 0;
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            total++;
            string name = p.GetProperty("name").GetString()!, task = p.GetProperty("task").GetString()!, hash = p.GetProperty("hash").GetString()!;
            int w = p.GetProperty("w").GetInt32(), h = p.GetProperty("h").GetInt32();
            SolveResult r = Solver.Solve(w, h, Grid.DecodeTask(task, w, h));
            string verdict;
            if (!r.Ok) verdict = "FAIL  " + r.Message;
            else
            {
                string answer = new(r.Edges!.Select(e => e == Solver.Line ? 'y' : 'n').ToArray());
                string md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(task + answer)));
                verdict = md5 != hash ? "FAIL  answer does not match the hash" : r.Unique ? "ok" : "FAIL  " + r.Message;
            }
            if (verdict != "ok") failed++;
            Console.WriteLine($"{name,-24}{verdict,-8}{r.Milliseconds,6} ms   guesses {r.Guesses,4}");
        }
        Console.WriteLine(failed == 0 ? $"All {total} solved, each the only answer, and match the site's hash." : $"{failed} of {total} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static int CheckBoards(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int failed = 0, total = 0;
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            total++;
            string file = b.GetProperty("file").GetString()!, task = b.GetProperty("task").GetString()!;
            int[] a = b.GetProperty("area").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            int w = b.GetProperty("w").GetInt32(), h = b.GetProperty("h").GetInt32();

            using var full = new Bitmap(Path.Combine(dir, file));
            using var crop = full.Clone(new Rectangle(a[0], a[1], a[2], a[3]), PixelFormat.Format24bppRgb);
            var sw = Stopwatch.StartNew();
            BoardReading? rd = BoardReader.Read(crop, out string error);
            long readMs = sw.ElapsedMilliseconds;
            if (rd == null)
            {
                failed++;
                Console.WriteLine($"{file}: FAIL  {error}");
                continue;
            }
            if (rd.Width != w || rd.Height != h)
            {
                failed++;
                Console.WriteLine($"{file}: FAIL  read a {rd.Width} x {rd.Height} board, expected {w} x {h}");
                continue;
            }

            sbyte[] expected = Grid.DecodeTask(task, w, h);
            sbyte[] asRead = (sbyte[])rd.Clues.Clone();
            var misread = Enumerable.Range(0, expected.Length).Where(i => expected[i] != asRead[i]).ToList();
            var (r, corrected) = AnswerFinder.Solve(rd);
            int stillWrong = Enumerable.Range(0, expected.Length).Count(i => expected[i] != rd.Clues[i]);
            int drawn = rd.DrawnLines.Count(x => x);
            string linesNote = "";
            if (drawn > 0 && r.Ok)
            {
                int wrongLines = Enumerable.Range(0, r.Edges!.Length).Count(e => rd.DrawnLines[e] && r.Edges[e] != Solver.Line);
                int answerLines = r.Edges.Count(e => e == Solver.Line);
                linesNote = wrongLines == 0 && drawn == answerLines ? " (exactly the answer)"
                    : $" ({wrongLines} not part of the answer, {answerLines - (drawn - wrongLines)} still to draw)";
            }
            bool ok = r.Ok && r.Unique && stillWrong == 0;
            if (!ok) failed++;
            string solve = !r.Ok ? "no solution" : r.Unique ? "one answer" : "not unique";
            Console.WriteLine($"{file}: {(ok ? "ok" : "FAIL")}  {w} x {h}, {expected.Count(x => x >= 0)} numbers, {misread.Count} misread" +
                              (corrected.Count > 0 ? $" ({corrected.Count} fixed by solving)" : "") +
                              $", {drawn} lines already drawn{linesNote}, read {readMs} ms, {solve}");
            foreach (int i in misread)
                Console.WriteLine($"    row {i / w + 1}, column {i % w + 1}: read {Show(asRead[i])}, should be {Show(expected[i])}" +
                                  (rd.Reads[i] is { } cr ? $" (runner-up {cr.Second}, margin {cr.Margin:F2})" : ""));
        }
        Console.WriteLine(failed == 0 ? $"All {total} pictures read correctly." : $"{failed} of {total} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static int ReadOne(string file, int[] area)
    {
        using var full = new Bitmap(file);
        Rectangle rect = area.Length == 4 ? new Rectangle(area[0], area[1], area[2], area[3]) : new Rectangle(0, 0, full.Width, full.Height);
        using var crop = full.Clone(rect, PixelFormat.Format24bppRgb);
        BoardReading? rd = BoardReader.Read(crop, out string error);
        if (rd == null)
        {
            Console.WriteLine(error);
            return 1;
        }
        Console.WriteLine($"{rd.Width} x {rd.Height} board, dots every {rd.Sx:F1} x {rd.Sy:F1} px, {rd.DrawnLines.Count(x => x)} lines already drawn:");
        Console.WriteLine("  " + rd.Debug);
        Console.Write(rd.CluesAsText());
        var (r, corrected) = AnswerFinder.Solve(rd);
        if (corrected.Count > 0) Console.WriteLine($"Re-read {corrected.Count} doubtful number(s) to make it solvable.");
        Console.WriteLine(r.Ok ? $"Solved in {r.Milliseconds} ms{(r.Unique ? ", the only answer." : ". " + r.Message)}" : r.Message);
        return r.Ok ? 0 : 1;
    }

    private static string Show(sbyte v) => v < 0 ? "empty" : v.ToString();
}

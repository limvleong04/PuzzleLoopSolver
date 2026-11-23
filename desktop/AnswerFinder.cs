namespace LoopSolver;

internal static class AnswerFinder
{
    /// <summary>
    /// Solves the numbers that were read. If no loop fits, or more than one does, a number was probably misread:
    /// the least certain numbers are tried as their second-best reading, one at a time, until exactly one loop fits.
    /// Returns the cells it changed (already applied to <paramref name="reading"/>).
    /// </summary>
    public static (SolveResult Result, List<int> Corrected) Solve(BoardReading reading)
    {
        SolveResult first = Solver.Solve(reading.Width, reading.Height, reading.Clues, maxGuesses: 50_000);
        if (first.Ok && first.Unique) return (first, []);

        var doubtful = Enumerable.Range(0, reading.Reads.Length)
            .Where(i => reading.Reads[i] != null)
            .OrderBy(i => reading.Reads[i]!.Margin)
            .Take(15);
        foreach (int i in doubtful)
        {
            var clues = (sbyte[])reading.Clues.Clone();
            clues[i] = reading.Reads[i]!.Second;
            SolveResult r = Solver.Solve(reading.Width, reading.Height, clues, maxGuesses: 5_000);
            if (r.Ok && r.Unique)
            {
                reading.Clues = clues;
                return (r, [i]);
            }
        }
        return (first, []);
    }
}

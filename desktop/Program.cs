namespace LoopSolver;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Command-line checks (--selftest, --check, --read) print to the console and exit.
        if (args.Length > 0) return ConsoleCommands.Run(args);

        // Two copies would fight over the same hotkeys.
        using var mutex = new Mutex(true, @"Local\LoopSolver-single-instance", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Loop Solver is already running.", "Loop Solver", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new ControlForm(AppSettings.Load()));
        return 0;
    }
}

using System.Runtime.InteropServices;
using static LoopSolver.NativeMethods;

namespace LoopSolver;

/// <summary>
/// Draws by moving the real mouse, so it works on any site or app. Press Esc, or move the mouse yourself,
/// to stop part-way.
/// </summary>
internal static class MouseDriver
{
    /// <summary>Clicks each point (the middle of an edge). Returns null when finished, or why it stopped.</summary>
    public static async Task<string?> ClickAsync(IReadOnlyList<Point> points, int delayMs, Point focus)
    {
        await WaitForModifiersUp();
        GetCursorPos(out POINT home);
        GetAsyncKeyState(VK_ESCAPE); // clear the "pressed since last time" bit
        Focus(focus);
        await Task.Delay(120);
        Point last = default;
        for (int i = 0; i < points.Count; i++)
        {
            string? stop = ShouldStop(i, last);
            if (stop != null) return $"{stop} {i} of {points.Count} edges done.";
            MoveTo(points[i]);
            Send(MOUSEEVENTF_LEFTDOWN);
            Send(MOUSEEVENTF_LEFTUP);
            last = points[i];
            await Task.Delay(delayMs);
        }
        SetCursorPos(home.X, home.Y);
        return null;
    }

    /// <summary>Holds the button down along each path of dots (for sites where you drag to draw).</summary>
    public static async Task<string?> DragAsync(IReadOnlyList<Point[]> paths, int delayMs, Point focus)
    {
        await WaitForModifiersUp();
        GetCursorPos(out POINT home);
        GetAsyncKeyState(VK_ESCAPE);
        Focus(focus);
        await Task.Delay(120);
        Point last = default;
        for (int i = 0; i < paths.Count; i++)
        {
            string? stop = ShouldStop(i, last);
            if (stop != null) return $"{stop} {i} of {paths.Count} strokes done.";
            Point[] path = paths[i];
            MoveTo(path[0]);
            Send(MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(delayMs);
            for (int k = 1; k < path.Length; k++)
            {
                // A few steps per edge, so the page sees the mouse cross the edge.
                for (int s = 1; s <= 4; s++)
                {
                    MoveTo(new Point(path[k - 1].X + (path[k].X - path[k - 1].X) * s / 4, path[k - 1].Y + (path[k].Y - path[k - 1].Y) * s / 4));
                    await Task.Delay(Math.Max(5, delayMs / 4));
                }
            }
            Send(MOUSEEVENTF_LEFTUP);
            last = path[^1];
            await Task.Delay(delayMs);
        }
        SetCursorPos(home.X, home.Y);
        return null;
    }

    /// <summary>
    /// Waits (up to about 3 s) for Ctrl, Alt and Shift to be let go. A hotkey's Ctrl still held down would turn
    /// every click into Ctrl+click, which many sites treat as "mark as not a line".
    /// </summary>
    private static async Task WaitForModifiersUp()
    {
        for (int i = 0; i < 100; i++)
        {
            if ((GetAsyncKeyState(0x11) & 0x8000) == 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0)
                return;
            await Task.Delay(30);
        }
    }

    private static string? ShouldStop(int i, Point last)
    {
        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8001) != 0) return "Stopped (Esc).";
        if (i == 0) return null;
        GetCursorPos(out POINT now);
        if (Math.Abs(now.X - last.X) > 4 || Math.Abs(now.Y - last.Y) > 4) return "Stopped because the mouse moved.";
        return null;
    }

    /// <summary>Brings the window under the board to the front, so the first click isn't spent activating it.</summary>
    private static void Focus(Point p)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
        if (hwnd != IntPtr.Zero) SetForegroundWindow(GetAncestor(hwnd, GA_ROOT));
    }

    private static void MoveTo(Point p)
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = (int)Math.Round((p.X - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1)),
                dy = (int)Math.Round((p.Y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
        SetCursorPos(p.X, p.Y); // exact pixel, in case the scaled move rounded
    }

    private static void Send(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}

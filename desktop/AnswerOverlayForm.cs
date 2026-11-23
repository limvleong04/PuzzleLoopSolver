using System.Drawing.Drawing2D;
using static LoopSolver.NativeMethods;

namespace LoopSolver;

/// <summary>
/// The answer drawn as see-through lines exactly over the board. Clicks go straight through it to the page,
/// so you can trace the loop yourself. It is also left out of screenshots, so reading the board again never
/// mistakes it for drawn lines.
/// </summary>
internal sealed class AnswerOverlayForm : Form
{
    private readonly BoardReading _reading;
    private readonly sbyte[] _edges;
    private readonly Color _color;

    public AnswerOverlayForm(Rectangle area, BoardReading reading, sbyte[] edges, Color color)
    {
        _reading = reading;
        _edges = edges;
        _color = color;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = area;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 0.7;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None; // anti-aliased edges would blend with the see-through key colour
        float thickness = (float)Math.Max(3, Math.Min(_reading.Sx, _reading.Sy) * 0.14);
        using var pen = new Pen(_color, thickness) { StartCap = LineCap.Square, EndCap = LineCap.Square };
        for (int e2 = 0; e2 < _edges.Length; e2++)
        {
            if (_edges[e2] != Solver.Line) continue;
            var (r1, c1, r2, c2) = Grid.Ends(e2, _reading.Width, _reading.Height);
            g.DrawLine(pen, _reading.Dot(r1, c1), _reading.Dot(r2, c2));
        }
    }
}

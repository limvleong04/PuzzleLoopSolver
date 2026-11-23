using static LoopSolver.NativeMethods;

namespace LoopSolver;

/// <summary>The small always-on-top window: mode, actions, status and the numbers that were read.</summary>
internal sealed class ControlForm : Form
{
    private const int HkPick = 1, HkAgain = 2, HkHide = 3, HkMode = 4;
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);

    private readonly AppSettings _settings;
    private readonly Button _btnShow = new(), _btnDraw = new(), _btnPick = new(), _btnAgain = new(), _btnToggle = new(), _btnSolveText = new();
    private readonly Label _status = new();
    private readonly TextBox _numbers = new();
    private readonly ToolTip _tips = new();

    private AnswerOverlayForm? _overlay;
    private Rectangle _area;          // screen area of the board read last
    private BoardReading? _reading;
    private sbyte[]? _answer;
    private bool _busy;

    public ControlForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Loop Solver";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BuildLayout();
        PlaceWindow();
        RefreshButtons();
        SetStatus("Press Ctrl+Alt+S (or Pick board), then drag a box around a Slitherlink board anywhere on screen.");
    }

    private void BuildLayout()
    {
        int width = LogicalToDeviceUnits(340);
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8),
        };

        var modeRow = Row();
        modeRow.Controls.Add(new Label { Text = "Mode", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        MakeToggle(_btnShow, "Show", "Show the answer over the board. Nothing on the page changes. (Ctrl+Alt+M switches)");
        MakeToggle(_btnDraw, "Draw", "Click the answer into the board for you. (Ctrl+Alt+M switches)");
        _btnShow.Click += (_, _) => SetMode("Show");
        _btnDraw.Click += (_, _) => SetMode("Draw");
        modeRow.Controls.AddRange([_btnShow, _btnDraw]);
        root.Controls.Add(modeRow);

        var actions = Row();
        MakeButton(_btnPick, "Pick board", "Drag a box around a board on screen (Ctrl+Alt+S)");
        MakeButton(_btnAgain, "Read again", "Read the same area again, for example after a new puzzle loads (Ctrl+Alt+A)");
        MakeButton(_btnToggle, "Hide answer", "Hide or show the answer over the board (Ctrl+Alt+H)");
        _btnPick.Click += (_, _) => PickBoard();
        _btnAgain.Click += (_, _) => ReadAgain();
        _btnToggle.Click += (_, _) => ToggleAnswer();
        actions.Controls.AddRange([_btnPick, _btnAgain, _btnToggle]);
        root.Controls.Add(actions);

        _status.AutoSize = true;
        _status.MaximumSize = new Size(width, 0);
        _status.Margin = new Padding(3, 6, 3, 10);
        root.Controls.Add(_status);

        root.Controls.Add(new Label
        {
            Text = "Numbers read (a dot means empty). If one is wrong, fix it here and solve again.",
            AutoSize = true, MaximumSize = new Size(width, 0),
        });
        _numbers.Multiline = true;
        _numbers.ScrollBars = ScrollBars.Both;
        _numbers.WordWrap = false;
        _numbers.Font = new Font("Consolas", 9.5f);
        _numbers.Size = new Size(width, LogicalToDeviceUnits(150));
        root.Controls.Add(_numbers);

        MakeButton(_btnSolveText, "Solve these numbers", "Solve with the numbers in the box, on the board read last");
        _btnSolveText.Click += (_, _) => SolveFromText();
        root.Controls.Add(_btnSolveText);

        Controls.Add(root);
    }

    private static FlowLayoutPanel Row() => new()
    {
        FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 4),
    };

    private void MakeButton(Button b, string text, string tip)
    {
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(6, 2, 6, 2);
        b.UseVisualStyleBackColor = true;
        _tips.SetToolTip(b, tip);
    }

    private void MakeToggle(Button b, string text, string tip)
    {
        MakeButton(b, text, tip);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Accent;
        b.Margin = new Padding(0, 3, 0, 3);
        b.MinimumSize = new Size(LogicalToDeviceUnits(64), 0);
    }

    private void RefreshButtons()
    {
        bool draw = _settings.DrawMode;
        Highlight(_btnShow, !draw);
        Highlight(_btnDraw, draw);
        _btnToggle.Text = _overlay != null ? "Hide answer" : "Show answer";
        _btnToggle.Enabled = _answer != null;
        _btnAgain.Enabled = _settings.AreaRect != null;
        _btnSolveText.Enabled = _reading != null;
    }

    private static void Highlight(Button b, bool on)
    {
        b.BackColor = on ? Accent : Color.White;
        b.ForeColor = on ? Color.White : Accent;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void SetMode(string mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        RefreshButtons();
        SetStatus(_settings.DrawMode
            ? "Draw mode: the board is clicked in for you. Pick board, or Read again (Ctrl+Alt+A) for the board read last."
            : "Show mode: the answer appears over the board and nothing on the page changes.");
    }

    // ---- actions ----

    private async void PickBoard()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            HideAnswer();
            Hide();
            await Task.Delay(200); // let this window disappear before the screen is frozen
            Rectangle? area = null;
            Bitmap? shot = null;
            using (var picker = new RegionPickerForm())
            {
                if (picker.ShowDialog() == DialogResult.OK && picker.SelectedArea is { } chosen)
                {
                    area = chosen;
                    shot = picker.CropSelection();
                }
            }
            Show();
            if (area is not { } a || shot is null)
            {
                SetStatus("Cancelled.");
                return;
            }
            _settings.Area = [a.X, a.Y, a.Width, a.Height];
            _settings.Save();
            using (shot) await ReadAndAnswer(a, shot);
        }
        finally
        {
            _busy = false;
            RefreshButtons();
        }
    }

    private async void ReadAgain()
    {
        if (_busy) return;
        if (_settings.AreaRect is not { } area)
        {
            PickBoard();
            return;
        }
        _busy = true;
        try
        {
            HideAnswer();
            bool hide = Visible && Bounds.IntersectsWith(area);
            if (hide)
            {
                Hide();
                await Task.Delay(200);
            }
            using Bitmap shot = ScreenGrabber.Capture(area);
            if (hide) Show();
            await ReadAndAnswer(area, shot);
        }
        finally
        {
            _busy = false;
            RefreshButtons();
        }
    }

    private async void SolveFromText()
    {
        if (_busy || _reading == null) return;
        sbyte[]? clues = BoardReading.ParseClues(_numbers.Text, _reading.Width, _reading.Height, out string error);
        if (clues == null)
        {
            SetStatus(error);
            return;
        }
        _busy = true;
        try
        {
            HideAnswer();
            _reading.Clues = clues;
            _reading.Reads = new ClueRead?[clues.Length]; // your numbers stand; don't second-guess them
            await SolveAndApply();
        }
        finally
        {
            _busy = false;
            RefreshButtons();
        }
    }

    private async Task ReadAndAnswer(Rectangle area, Bitmap shot)
    {
        SetStatus("Reading the board...");
        await Task.Delay(1); // let the status paint
        BoardReading? reading = BoardReader.Read(shot, out string error);
        if (reading == null)
        {
            SetStatus(error);
            return;
        }
        _area = area;
        _reading = reading;
        _answer = null;
        _numbers.Text = reading.CluesAsText();
        await SolveAndApply();
    }

    private async Task SolveAndApply()
    {
        BoardReading rd = _reading!;
        SetStatus($"Solving {rd.Width} x {rd.Height}...");
        await Task.Delay(1);
        var (result, corrected) = AnswerFinder.Solve(rd);
        if (corrected.Count > 0) _numbers.Text = rd.CluesAsText();
        if (!result.Ok)
        {
            SetStatus($"Read a {rd.Width} x {rd.Height} board, but no loop fits the numbers I read, so one is probably misread. " +
                      "Compare the box below with the board, fix it, then click Solve these numbers.");
            return;
        }
        _answer = result.Edges;
        string what = $"{rd.Width} x {rd.Height}, solved in {result.Milliseconds} ms.";
        if (corrected.Count > 0)
        {
            int c = corrected[0];
            what += $" I re-read the number in row {c / rd.Width + 1}, column {c % rd.Width + 1} as {rd.Clues[c]}.";
        }
        if (!result.Unique) what += $" Careful: {result.Message} A number may be misread; check the box below.";

        if (_settings.DrawMode)
            await DrawAnswer(what);
        else
        {
            ShowAnswer();
            SetStatus("Answer shown in blue. " + what);
        }
    }

    private async Task DrawAnswer(string what)
    {
        BoardReading rd = _reading!;
        sbyte[] edges = _answer!;
        var toAdd = new List<int>();
        var toRemove = new List<int>();
        for (int e = 0; e < edges.Length; e++)
        {
            bool want = edges[e] == Solver.Line, has = rd.DrawnLines[e];
            if (want && !has) toAdd.Add(e);
            else if (!want && has) toRemove.Add(e);
        }
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            SetStatus("The board already shows the answer. " + what);
            return;
        }

        Point focus = ToScreen(new PointF((float)(rd.X0 + rd.Width * rd.Sx / 2), (float)(rd.Y0 + rd.Height * rd.Sy / 2)));
        bool hide = Bounds.IntersectsWith(_area);
        SetStatus($"Drawing {toAdd.Count} lines. Press Esc or move the mouse to stop.");
        if (hide) Hide();
        string? stopped = _settings.DragToDraw
            ? await MouseDriver.DragAsync(DragPaths(rd, toAdd), _settings.ClickDelayMs, focus)
            : await MouseDriver.ClickAsync(toRemove.Concat(toAdd).Select(e => ToScreen(rd.EdgeMiddle(e))).ToList(), _settings.ClickDelayMs, focus);
        if (hide) Show();

        string removed = !_settings.DragToDraw && toRemove.Count > 0 ? $" and removed {toRemove.Count} wrong ones" : "";
        string leftOver = _settings.DragToDraw && toRemove.Count > 0
            ? $" {toRemove.Count} lines already on the board aren't part of the answer; remove those yourself." : "";
        SetStatus(stopped ?? $"Drew {toAdd.Count} lines{removed}. {what}{leftOver}");
    }

    /// <summary>Straight runs of edges to add, as dot-to-dot paths, for sites where you draw by dragging.</summary>
    private List<Point[]> DragPaths(BoardReading rd, List<int> edges)
    {
        var set = edges.ToHashSet();
        var paths = new List<Point[]>();
        int w = rd.Width, h = rd.Height, he = (h + 1) * w;
        for (int r = 0; r <= h; r++)
            for (int c = 0; c < w; c++)
            {
                if (!set.Contains(r * w + c) || (c > 0 && set.Contains(r * w + c - 1))) continue;
                int end = c;
                while (end + 1 < w && set.Contains(r * w + end + 1)) end++;
                int row = r;
                paths.Add(Enumerable.Range(c, end - c + 2).Select(j => ToScreen(rd.Dot(row, j))).ToArray());
            }
        for (int c = 0; c <= w; c++)
            for (int r = 0; r < h; r++)
            {
                int e = he + r * (w + 1) + c;
                if (!set.Contains(e) || (r > 0 && set.Contains(e - (w + 1)))) continue;
                int end = r;
                while (end + 1 < h && set.Contains(he + (end + 1) * (w + 1) + c)) end++;
                int col = c;
                paths.Add(Enumerable.Range(r, end - r + 2).Select(i => ToScreen(rd.Dot(i, col))).ToArray());
            }
        return paths;
    }

    private Point ToScreen(PointF p) => new(_area.X + (int)Math.Floor(p.X), _area.Y + (int)Math.Floor(p.Y));

    private void ShowAnswer()
    {
        HideAnswer();
        if (_reading == null || _answer == null) return;
        _overlay = new AnswerOverlayForm(_area, _reading, _answer, _settings.LineColorValue);
        _overlay.Show();
        RefreshButtons();
    }

    private void HideAnswer()
    {
        if (_overlay == null) return;
        _overlay.Close();
        _overlay.Dispose();
        _overlay = null;
        RefreshButtons();
    }

    private void ToggleAnswer()
    {
        if (_busy) return;
        if (_overlay != null) HideAnswer();
        else ShowAnswer();
    }

    // ---- window plumbing ----

    private void PlaceWindow()
    {
        if (_settings.WindowPosition is { Length: 2 } p && SystemInformation.VirtualScreen.Contains(p[0] + 20, p[1] + 20))
            Location = new Point(p[0], p[1]);
        else
        {
            Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(wa.Right - LogicalToDeviceUnits(390), wa.Bottom - LogicalToDeviceUnits(470));
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var taken = new List<string>();
        foreach (var (id, key, name) in new[] { (HkPick, Keys.S, "Ctrl+Alt+S"), (HkAgain, Keys.A, "Ctrl+Alt+A"), (HkHide, Keys.H, "Ctrl+Alt+H"), (HkMode, Keys.M, "Ctrl+Alt+M") })
            if (!RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key))
                taken.Add(name);
        if (taken.Count > 0)
            SetStatus($"{string.Join(", ", taken)} {(taken.Count == 1 ? "is" : "are")} already used by another app. The buttons still work.");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        for (int id = HkPick; id <= HkMode; id++) UnregisterHotKey(Handle, id);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch ((int)m.WParam)
            {
                case HkPick: PickBoard(); break;
                case HkAgain: ReadAgain(); break;
                case HkHide: ToggleAnswer(); break;
                case HkMode: SetMode(_settings.DrawMode ? "Show" : "Draw"); break;
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        HideAnswer();
        _settings.WindowPosition = [Location.X, Location.Y];
        _settings.Save();
        base.OnFormClosing(e);
    }
}

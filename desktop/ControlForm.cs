using static LoopSolver.NativeMethods;

namespace LoopSolver;

/// <summary>The small always-on-top window: mode, actions, status and the numbers that were read.</summary>
internal sealed class ControlForm : Form
{
    private const int HkPick = 1, HkAgain = 2, HkHide = 3, HkMode = 4;

    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    private static readonly Color AccentDown = Color.FromArgb(30, 64, 175);
    private static readonly Color Border = Color.FromArgb(209, 213, 219);
    private static readonly Color Text1 = Color.FromArgb(31, 41, 55);
    private static readonly Color Text2 = Color.FromArgb(107, 114, 128);
    private static readonly Color Panel = Color.FromArgb(243, 244, 246);

    private readonly AppSettings _settings;
    private readonly bool _preview;
    private readonly Button _btnShow = new(), _btnDraw = new(), _btnPick = new(), _btnAgain = new(), _btnToggle = new(), _btnSolveText = new();
    private readonly Label _status = new();
    private readonly TextBox _numbers = new();
    private readonly ToolTip _tips = new();

    private AnswerOverlayForm? _overlay;
    private Rectangle _area;          // screen area of the board read last
    private BoardReading? _reading;
    private sbyte[]? _answer;
    private bool _busy;

    /// <param name="preview">Build the window only to take a picture of it: no hotkeys.</param>
    public ControlForm(AppSettings settings, bool preview = false)
    {
        _settings = settings;
        _preview = preview;
        Text = "Loop Solver";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        BuildLayout();
        PlaceWindow();
        RefreshButtons();
        SetStatus("Press Ctrl+Alt+S (or Pick board), then drag a box around a Slitherlink board anywhere on screen.");
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    // ---- layout ----

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(12)), BackColor = Color.White };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int height = 0;
        void AddRow(Control c, int rowHeight, int gapAfter)
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 0, 0, S(gapAfter));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(rowHeight + gapAfter)));
            root.Controls.Add(c);
            height += rowHeight + gapAfter;
        }

        // Show | Draw
        StyleSegment(_btnShow, "Show", "Show the answer over the board. Nothing on the page changes. (Ctrl+Alt+M switches)");
        StyleSegment(_btnDraw, "Draw", "Click the answer into the board for you. (Ctrl+Alt+M switches)");
        _btnShow.Click += (_, _) => SetMode("Show");
        _btnDraw.Click += (_, _) => SetMode("Draw");
        AddRow(TwoUp(_btnShow, _btnDraw, gap: 0), 30, 12);

        // Actions
        StylePrimary(_btnPick, "Pick board", "Drag a box around a board on screen (Ctrl+Alt+S)");
        _btnPick.Click += (_, _) => PickBoard();
        AddRow(_btnPick, 36, 6);

        StyleSecondary(_btnAgain, "Read again", "Read the same area again, for example after a new puzzle loads (Ctrl+Alt+A)");
        StyleSecondary(_btnToggle, "Hide answer", "Hide or show the answer over the board (Ctrl+Alt+H)");
        _btnAgain.Click += (_, _) => ReadAgain();
        _btnToggle.Click += (_, _) => ToggleAnswer();
        AddRow(TwoUp(_btnAgain, _btnToggle, gap: 6), 30, 12);

        // Status
        _status.AutoSize = false;
        _status.BackColor = Panel;
        _status.ForeColor = Text1;
        _status.Padding = new Padding(S(8), S(6), S(8), S(6));
        _status.TextAlign = ContentAlignment.TopLeft;
        _status.AutoEllipsis = true;
        AddRow(_status, 76, 14);

        // Numbers read
        var caption = new Label { Text = "Numbers read (a dot is an empty cell)", ForeColor = Text2, TextAlign = ContentAlignment.BottomLeft, AutoSize = false };
        AddRow(caption, 20, 4);
        _numbers.Multiline = true;
        _numbers.ScrollBars = ScrollBars.None; // FitScrollBars adds them when a board doesn't fit
        _numbers.WordWrap = false;
        _numbers.BorderStyle = BorderStyle.FixedSingle;
        _numbers.Font = new Font("Consolas", 10f);
        _numbers.ForeColor = Text1;
        _numbers.PlaceholderText = "The numbers appear here after a board is read. If one is wrong, fix it and click Solve these numbers.";
        AddRow(_numbers, 166, 6);

        StyleSecondary(_btnSolveText, "Solve these numbers", "Solve with the numbers in the box, on the board read last");
        _btnSolveText.Click += (_, _) => SolveFromText();
        AddRow(_btnSolveText, 30, 12);

        // Hotkeys
        var keys = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, BackColor = Color.White };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        keys.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        keys.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (string k in new[] { "Ctrl+Alt+S  pick a board", "Ctrl+Alt+A  read again", "Ctrl+Alt+H  hide or show", "Ctrl+Alt+M  switch mode" })
            keys.Controls.Add(new Label
            {
                Text = k, ForeColor = Text2, Font = new Font("Segoe UI", 8f), Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, AutoSize = false,
            });
        AddRow(keys, 34, 0);

        Controls.Add(root);
        ClientSize = new Size(S(340), S(height + 24));
    }

    private static TableLayoutPanel TwoUp(Control left, Control right, int gap)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, BackColor = Color.White };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Dock = right.Dock = DockStyle.Fill;
        left.Margin = new Padding(0, 0, gap / 2, 0);
        right.Margin = new Padding(gap - gap / 2, 0, 0, 0);
        t.Controls.Add(left, 0, 0);
        t.Controls.Add(right, 1, 0);
        return t;
    }

    private void StyleBase(Button b, string text, string tip)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        _tips.SetToolTip(b, tip);
    }

    private void StylePrimary(Button b, string text, string tip)
    {
        StyleBase(b, text, tip);
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 9.5f);
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentDown;
    }

    private void StyleSecondary(Button b, string text, string tip)
    {
        StyleBase(b, text, tip);
        b.BackColor = Color.White;
        b.ForeColor = Text1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = Panel;
        b.FlatAppearance.MouseDownBackColor = Border;
    }

    private void StyleSegment(Button b, string text, string tip)
    {
        StyleBase(b, text, tip);
        b.Font = new Font("Segoe UI Semibold", 9f);
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
        b.ForeColor = on ? Color.White : Text1;
        b.FlatAppearance.BorderColor = on ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = on ? Accent : Panel;
        b.FlatAppearance.MouseDownBackColor = on ? Accent : Border;
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>Puts the numbers in the box, with scroll bars only when a big board doesn't fit.</summary>
    private void ShowNumbers(string text)
    {
        _numbers.Text = text;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        string longest = lines.Count == 0 ? "" : lines.MaxBy(l => l.Length)!;
        int textW = TextRenderer.MeasureText(longest, _numbers.Font, Size.Empty, TextFormatFlags.NoPadding).Width + S(6);
        int textH = lines.Count * _numbers.Font.Height + S(4);
        Size inner = _numbers.ClientSize;
        bool tall = false, wide = false;
        for (int pass = 0; pass < 2; pass++) // a scroll bar on one side takes room from the other
        {
            tall = textH > inner.Height - (wide ? SystemInformation.HorizontalScrollBarHeight : 0);
            wide = textW > inner.Width - (tall ? SystemInformation.VerticalScrollBarWidth : 0);
        }
        var want = tall && wide ? ScrollBars.Both : tall ? ScrollBars.Vertical : wide ? ScrollBars.Horizontal : ScrollBars.None;
        if (_numbers.ScrollBars != want) _numbers.ScrollBars = want;
    }

    private void SetMode(string mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        RefreshButtons();
        SetStatus(_settings.DrawMode
            ? "Draw mode: the answer is clicked in for you. Pick a board, or press Read again for the one read last."
            : "Show mode: the answer appears over the board and nothing on the page changes.");
    }

    /// <summary>Fills the window as if a board had just been read, for a picture of it (--snapshot).</summary>
    internal void PreviewReading(BoardReading reading)
    {
        _reading = reading;
        ShowNumbers(reading.CluesAsText());
        var (result, _) = AnswerFinder.Solve(reading);
        _answer = result.Edges;
        _settings.Area = [0, 0, 1, 1];
        SetStatus(result.Ok ? $"Answer shown in blue. {reading.Width} x {reading.Height}, solved in {result.Milliseconds} ms." : result.Message);
        RefreshButtons();
        _btnToggle.Text = "Hide answer";
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
        ShowNumbers(reading.CluesAsText());
        await SolveAndApply();
    }

    private async Task SolveAndApply()
    {
        BoardReading rd = _reading!;
        SetStatus($"Solving {rd.Width} x {rd.Height}...");
        await Task.Delay(1);
        var (result, corrected) = AnswerFinder.Solve(rd);
        if (corrected.Count > 0) ShowNumbers(rd.CluesAsText());
        if (!result.Ok)
        {
            SetStatus("No loop fits the numbers I read, so one is probably misread. Fix it in the box below, then click Solve these numbers.");
            return;
        }
        _answer = result.Edges;
        string what = $"{rd.Width} x {rd.Height}, solved in {result.Milliseconds} ms.";
        if (corrected.Count > 0)
        {
            int c = corrected[0];
            what += $" Re-read row {c / rd.Width + 1}, column {c % rd.Width + 1} as {rd.Clues[c]}.";
        }
        if (!result.Unique) what += " More than one loop fits, so a number may be misread. Check the box below.";

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
            ? $" {toRemove.Count} lines on the board aren't part of the answer; remove those yourself." : "";
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
            Location = new Point(wa.Right - Width - S(16), wa.Bottom - Height - S(16));
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_preview) return;
        var taken = new List<string>();
        foreach (var (id, key, name) in new[] { (HkPick, Keys.S, "Ctrl+Alt+S"), (HkAgain, Keys.A, "Ctrl+Alt+A"), (HkHide, Keys.H, "Ctrl+Alt+H"), (HkMode, Keys.M, "Ctrl+Alt+M") })
            if (!RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key))
                taken.Add(name);
        if (taken.Count > 0)
            SetStatus($"{string.Join(", ", taken)} {(taken.Count == 1 ? "is" : "are")} already used by another app. The buttons still work.");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!_preview)
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
        if (!_preview)
        {
            _settings.WindowPosition = [Location.X, Location.Y];
            _settings.Save();
        }
        base.OnFormClosing(e);
    }
}

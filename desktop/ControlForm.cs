using System.Diagnostics;
using static LoopSolver.NativeMethods;

namespace LoopSolver;

/// <summary>
/// The small always-on-top window, in the same style as Puffer Helper: dark, borderless, out of the taskbar and
/// Alt+Tab. Hotkeys do the work and right-click opens the menu. Drag it by its text. Clicking it doesn't take
/// focus from the page, except in the numbers box, which you can type into.
/// </summary>
internal sealed class ControlForm : Form
{
    private enum HotkeyId { Solve = 1, Again = 2, Mode = 3, Hide = 4 }

    private static readonly Color Background = Color.FromArgb(28, 28, 34);
    private static readonly Color Field = Color.FromArgb(40, 40, 48);
    private static readonly Color Muted = Color.FromArgb(160, 160, 172);
    private const string Title = "Loop Solver";
    private const int MaxRowsShown = 20;

    private readonly AppSettings _settings;
    private readonly bool _preview;
    private readonly Dictionary<HotkeyId, HotkeyBinding> _hotkeys = [];

    private readonly Panel _header;
    private readonly Label _titleLabel, _modeLabel, _statusLabel, _numbersCaption, _footerLabel;
    private readonly Panel _numbersPanel;
    private readonly TextBox _numbers;
    private readonly ToolTip _tips = new() { InitialDelay = 300 };
    private int _numbersHeight;

    private AnswerOverlayForm? _overlay;
    private bool _restoreAnswer;      // the hide hotkey brings the answer back with the window
    private Rectangle _area;          // screen area of the board read last
    private BoardReading? _reading;
    private sbyte[]? _answer;
    private bool _busy, _dragging;
    private Point _dragOffset;

    /// <param name="preview">Build the window only to take a picture of it: no hotkeys, nothing saved.</param>
    public ControlForm(AppSettings settings, bool preview = false)
    {
        _settings = settings;
        _preview = preview;

        Text = Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(S(12), S(8), S(12), S(6));
        Opacity = 0.97;
        Width = S(360);

        _titleLabel = new Label
        {
            Dock = DockStyle.Fill, Text = Title, Font = new Font("Segoe UI Semibold", 13f),
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        _modeLabel = new Label
        {
            Dock = DockStyle.Right, Width = S(110), ForeColor = Muted, Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand,
        };
        _modeLabel.Click += (_, _) => SetMode(_settings.DrawMode ? "Show" : "Draw");
        _header = new Panel { Dock = DockStyle.Top, Height = S(32) };
        _header.Controls.Add(_titleLabel);
        _header.Controls.Add(_modeLabel);

        _statusLabel = new Label { Dock = DockStyle.Top, Height = S(52), ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), AutoEllipsis = true };
        _numbersCaption = new Label
        {
            Dock = DockStyle.Top, Height = S(22), ForeColor = Muted, Font = new Font("Segoe UI", 8f), Visible = false,
            Text = "Numbers read. Fix a wrong one, then press Ctrl+Enter.", TextAlign = ContentAlignment.BottomLeft,
        };
        _numbers = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None,
            BackColor = Field, ForeColor = Color.White, Font = new Font("Consolas", 10f),
        };
        _numbers.HandleCreated += (_, _) => SetWindowTheme(_numbers.Handle, "DarkMode_Explorer", null);
        _numbers.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SolveFromText();
            }
        };
        _numbersPanel = new Panel { Dock = DockStyle.Fill, BackColor = Field, Padding = new Padding(S(8), S(6), S(4), S(4)), Visible = false };
        _numbersPanel.Controls.Add(_numbers);
        _footerLabel = new Label { Dock = DockStyle.Bottom, Height = S(40), ForeColor = Muted, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft };

        // Docking is laid out from the last-added control to the first: Fill goes in first, the header last.
        Controls.Add(_numbersPanel);
        Controls.Add(_footerLabel);
        Controls.Add(_numbersCaption);
        Controls.Add(_statusLabel);
        Controls.Add(_header);

        ContextMenuStrip menu = BuildMenu();
        foreach (Control c in new Control[] { this, _header, _titleLabel, _modeLabel, _statusLabel, _numbersCaption, _footerLabel })
            c.ContextMenuStrip = menu;
        foreach (Control c in new Control[] { this, _header, _titleLabel, _statusLabel, _numbersCaption, _footerLabel })
        {
            c.MouseDown += OnDragStart;
            c.MouseMove += OnDragMove;
            c.MouseUp += OnDragEnd;
        }

        RefreshMode();
        FitHeight();
        PlaceInitially();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    // ---- window behaviour ---------------------------------------------------------------------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        List<string> problems = RegisterHotkeys();
        _footerLabel.Text = $"{Key(HotkeyId.Solve)} solve    {Key(HotkeyId.Again)} again    {Key(HotkeyId.Mode)} mode\n" +
                            $"{Key(HotkeyId.Hide)} hide    Right-click for the menu";
        _tips.SetToolTip(_modeLabel, $"Click to switch between Show and Draw ({Key(HotkeyId.Mode)})");
        SetStatus(problems.Count > 0
            ? "Hotkey problem: " + string.Join("; ", problems) + ". Right-click > Open settings file."
            : $"Press {Key(HotkeyId.Solve)}, then drag a box around a Slitherlink board. Right-click here for the menu.");
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                // Clicking the window leaves the page focused, except in the numbers box, which you type into.
                bool typing = _numbersPanel.Visible && _numbersPanel.RectangleToScreen(_numbersPanel.ClientRectangle).Contains(Cursor.Position);
                m.Result = typing ? MA_ACTIVATE : MA_NOACTIVATE;
                return;
            case WM_HOTKEY:
                OnHotkey((HotkeyId)m.WParam.ToInt32());
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        HideAnswer();
        foreach (HotkeyId id in _hotkeys.Keys)
            if (!_preview) UnregisterHotKey(Handle, (int)id);
        if (!_preview)
        {
            _settings.WindowPosition = [Left, Top];
            _settings.Save();
        }
        base.OnFormClosed(e);
    }

    // ---- hotkeys ------------------------------------------------------------------------------------

    private List<string> RegisterHotkeys()
    {
        var problems = new List<string>();
        Register(HotkeyId.Solve, _settings.HotkeySolve, nameof(AppSettings.HotkeySolve));
        Register(HotkeyId.Again, _settings.HotkeyAgain, nameof(AppSettings.HotkeyAgain));
        Register(HotkeyId.Mode, _settings.HotkeyMode, nameof(AppSettings.HotkeyMode));
        Register(HotkeyId.Hide, _settings.HotkeyHide, nameof(AppSettings.HotkeyHide));
        return problems;

        void Register(HotkeyId id, string? text, string settingName)
        {
            if (!HotkeyBinding.TryParse(text, out HotkeyBinding binding))
                problems.Add($"{settingName} \"{text}\" isn't a valid hotkey");
            else if (!_preview && !RegisterHotKey(Handle, (int)id, binding.Modifiers | MOD_NOREPEAT, (uint)binding.Key))
                problems.Add($"{binding.Text} is taken by another app (change {settingName})");
            else
                _hotkeys[id] = binding;
        }
    }

    private string Key(HotkeyId id) => _hotkeys.TryGetValue(id, out HotkeyBinding b) ? b.Text : "(no hotkey)";

    private void OnHotkey(HotkeyId id)
    {
        switch (id)
        {
            case HotkeyId.Solve: PickBoard(); break;
            case HotkeyId.Again: ReadAgain(); break;
            case HotkeyId.Mode: SetMode(_settings.DrawMode ? "Show" : "Draw"); break;
            case HotkeyId.Hide: ToggleVisible(); break;
        }
    }

    /// <summary>The hide hotkey: hides the window and the answer together, and brings both back.</summary>
    private void ToggleVisible()
    {
        if (Visible)
        {
            _restoreAnswer = _overlay != null;
            HideAnswer();
            Hide();
        }
        else
        {
            Show();
            if (_restoreAnswer) ShowAnswer();
            _restoreAnswer = false;
        }
    }

    // ---- actions ------------------------------------------------------------------------------------

    private void SetMode(string mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        RefreshMode();
        SetStatus(_settings.DrawMode
            ? $"Draw mode: the answer is clicked in for you. {Key(HotkeyId.Again)} draws the board read last."
            : "Show mode: the answer appears over the board, and the page is left alone.");
    }

    private void RefreshMode() => _modeLabel.Text = _settings.DrawMode ? "Draw mode" : "Show mode";

    private async void PickBoard()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            HideAnswer();
            _restoreAnswer = false;
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
            _restoreAnswer = false;
            bool stepAside = Visible && Bounds.IntersectsWith(area);
            if (stepAside)
            {
                Hide();
                await Task.Delay(200);
            }
            using Bitmap shot = ScreenGrabber.Capture(area);
            if (!Visible) Show();
            await ReadAndAnswer(area, shot);
        }
        finally
        {
            _busy = false;
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
            SetStatus("No loop fits the numbers I read, so one is probably misread. Fix it in the box below, then press Ctrl+Enter.");
            return;
        }
        _answer = result.Edges;
        string what = $"{rd.Width} x {rd.Height}, solved in {result.Milliseconds} ms.";
        if (corrected.Count > 0)
        {
            int c = corrected[0];
            what += $" Re-read row {c / rd.Width + 1}, column {c % rd.Width + 1} as {rd.Clues[c]}.";
        }
        if (!result.Unique) what += " More than one loop fits, so a number may be misread.";

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
        bool stepAside = Bounds.IntersectsWith(_area);
        SetStatus($"Drawing {toAdd.Count} lines. Esc or moving the mouse stops it.");
        if (stepAside) Hide();
        string? stopped = _settings.DragToDraw
            ? await MouseDriver.DragAsync(DragPaths(rd, toAdd), _settings.ClickDelayMs, focus)
            : await MouseDriver.ClickAsync(toRemove.Concat(toAdd).Select(e => ToScreen(rd.EdgeMiddle(e))).ToList(), _settings.ClickDelayMs, focus);
        if (stepAside) Show();

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
    }

    private void HideAnswer()
    {
        if (_overlay == null) return;
        _overlay.Close();
        _overlay.Dispose();
        _overlay = null;
    }

    private void ToggleAnswer()
    {
        if (_busy) return;
        if (_overlay != null) HideAnswer();
        else ShowAnswer();
    }

    /// <summary>Fills the window as if a board had just been read, for a picture of it (--snapshot).</summary>
    internal void PreviewReading(BoardReading reading)
    {
        _reading = reading;
        ShowNumbers(reading.CluesAsText());
        var (result, _) = AnswerFinder.Solve(reading);
        _answer = result.Edges;
        SetStatus(result.Ok ? $"Answer shown in blue. {reading.Width} x {reading.Height}, solved in {result.Milliseconds} ms." : result.Message);
    }

    // ---- layout, menu, dragging ---------------------------------------------------------------------

    private void SetStatus(string text) => _statusLabel.Text = text;

    /// <summary>Puts the numbers in the box and sizes it: up to 20 rows, scroll bars only when a board doesn't fit.</summary>
    private void ShowNumbers(string text)
    {
        _numbers.Text = text;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        string longest = lines.Count == 0 ? "" : lines.MaxBy(l => l.Length)!;
        int textW = TextRenderer.MeasureText(longest, _numbers.Font, Size.Empty, TextFormatFlags.NoPadding).Width + S(6);
        int available = Width - Padding.Horizontal - _numbersPanel.Padding.Horizontal;
        bool tall = lines.Count > MaxRowsShown;
        bool wide = textW > available - (tall ? SystemInformation.VerticalScrollBarWidth : 0);
        var want = tall && wide ? ScrollBars.Both : tall ? ScrollBars.Vertical : wide ? ScrollBars.Horizontal : ScrollBars.None;
        if (_numbers.ScrollBars != want) _numbers.ScrollBars = want;

        _numbersHeight = Math.Min(lines.Count, MaxRowsShown) * _numbers.Font.Height + _numbersPanel.Padding.Vertical + S(4)
                         + (wide ? SystemInformation.HorizontalScrollBarHeight : 0);
        _numbersCaption.Visible = _numbersPanel.Visible = true;
        FitHeight();
    }

    private void FitHeight()
    {
        int numbers = _numbersPanel.Visible ? _numbersCaption.Height + _numbersHeight : 0;
        Height = Padding.Vertical + _header.Height + _statusLabel.Height + numbers + _footerLabel.Height + S(4);
        KeepOnScreen();
    }

    private void PlaceInitially()
    {
        if (_settings.WindowPosition is { Length: 2 } p && Screen.AllScreens.Any(s => s.WorkingArea.Contains(p[0] + 20, p[1] + 20)))
        {
            Location = new Point(p[0], p[1]);
            return;
        }
        // Bottom right, so it doesn't sit on top of Puffer Helper (top right).
        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen;
        Location = new Point(work.Right - Width - S(16), work.Bottom - S(520));
    }

    private void KeepOnScreen()
    {
        if (_preview) return; // the picture is taken off-screen
        Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
        int left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        int top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        Location = new Point(left, top);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var solve = new ToolStripMenuItem("Pick a board and solve", null, (_, _) => PickBoard());
        var again = new ToolStripMenuItem("Read the same area again", null, (_, _) => ReadAgain());
        var answer = new ToolStripMenuItem("Hide the answer", null, (_, _) => ToggleAnswer());
        var showMode = new ToolStripMenuItem("Show mode: answer over the board", null, (_, _) => SetMode("Show"));
        var drawMode = new ToolStripMenuItem("Draw mode: click it in", null, (_, _) => SetMode("Draw"));
        var solveText = new ToolStripMenuItem("Solve the numbers in the box", null, (_, _) => SolveFromText()) { ShortcutKeyDisplayString = "Ctrl+Enter" };
        menu.Items.AddRange(
        [
            solve, again, answer,
            new ToolStripSeparator(),
            showMode, drawMode,
            new ToolStripSeparator(),
            solveText,
            new ToolStripMenuItem("Open settings file", null, (_, _) => OpenSettingsFile()),
            new ToolStripMenuItem("Exit", null, (_, _) => Close()),
        ]);
        menu.Opening += (_, _) =>
        {
            solve.ShortcutKeyDisplayString = Key(HotkeyId.Solve);
            again.ShortcutKeyDisplayString = Key(HotkeyId.Again);
            again.Enabled = _settings.AreaRect != null;
            answer.Text = _overlay != null ? "Hide the answer" : "Show the answer";
            answer.Enabled = _answer != null;
            showMode.Checked = !_settings.DrawMode;
            drawMode.Checked = _settings.DrawMode;
            solveText.Enabled = _reading != null;
        };
        return menu;
    }

    private void OpenSettingsFile()
    {
        _settings.Save();
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"") { UseShellExecute = true });
            SetStatus("Settings changes take effect after you restart Loop Solver.");
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open {AppSettings.FilePath}: {ex.Message}");
        }
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
            Location = new Point(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y);
    }

    private void OnDragEnd(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        KeepOnScreen();
        _settings.WindowPosition = [Left, Top];
        _settings.Save();
    }
}

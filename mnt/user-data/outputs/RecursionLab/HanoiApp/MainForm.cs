using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace HanoiApp;

/// <summary>
/// Игра «Ханойские башни».
/// Управление: клик по стержню (взять верхний диск) → клик по другому стержню (положить),
/// либо перетаскивание диска мышью. Кнопки «Решить» запускают анимацию решения
/// (рекурсивным алгоритмом или алгоритмом со стеком) из ТЕКУЩЕГО положения дисков.
/// </summary>
public sealed class MainForm : Form
{
    private sealed class BoardPanel : Panel
    {
        public BoardPanel() { DoubleBuffered = true; }
    }

    private sealed class Anim
    {
        public Move Move;
        public double T;        // 0..1: подъём → перенос → опускание
        public int FromSlot;    // этаж, откуда взят диск
        public int ToSlot;      // этаж, куда ляжет диск
    }

    private readonly record struct Lay(int BaseY, int DiskH, int MaxW, int MinW, int PegTop);

    // ── модель и анимация ──
    private readonly HanoiGame _game = new(5);
    private readonly Queue<Move> _queue = new();
    private Anim? _anim;
    private bool _autoSolving, _usedAuto, _winShown;

    // ── ввод ──
    private int _selectedPeg = -1;
    private bool _dragging, _dragMoved;
    private int _dragPeg, _dragDisk;
    private Point _mouse, _down;
    private int _flashPeg = -1, _flashTicks;

    // ── UI ──
    private readonly BoardPanel _board = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 6, 0, 0) };
    private readonly Label _lblInfo = new() { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
    private readonly Label _lblStatus = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), BackColor = Color.WhiteSmoke };
    private readonly NumericUpDown _numDisks = new() { Minimum = 3, Maximum = 10, Value = 5, Width = 50 };
    private readonly TrackBar _trkSpeed = new() { Minimum = 1, Maximum = 12, Value = 6, Width = 150, AutoSize = false, Height = 30 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _sw = new();

    public MainForm()
    {
        Text = "Ханойские башни";
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;

        BuildControls();

        _board.Paint += (_, e) => DrawBoard(e.Graphics);
        _board.MouseDown += OnBoardMouseDown;
        _board.MouseMove += OnBoardMouseMove;
        _board.MouseUp += OnBoardMouseUp;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        SetStatus("Нажмите на стержень, затем на другой — или перетащите верхний диск мышью.");
    }

    // ───────────────────────── интерфейс ─────────────────────────
    private void BuildControls()
    {
        Button Btn(string text, int width, EventHandler click)
        {
            var b = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(3, 0, 3, 0) };
            b.Click += click;
            _flow.Controls.Add(b);
            return b;
        }
        Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 6, 2, 0) };

        _flow.Controls.Add(Lbl("Дисков:"));
        _numDisks.Margin = new Padding(0, 3, 6, 0);
        _flow.Controls.Add(_numDisks);
        _numDisks.ValueChanged += (_, _) => NewGame();

        Btn("Новая игра", 90, (_, _) => NewGame());
        Btn("Отменить ход", 100, (_, _) => Undo());
        Btn("Решить (рекурсия)", 130, (_, _) => AutoSolve(useStack: false));
        Btn("Решить (стек)", 110, (_, _) => AutoSolve(useStack: true));
        Btn("Стоп", 60, (_, _) => StopAuto());

        _flow.Controls.Add(Lbl("Скорость:"));
        _flow.Controls.Add(_trkSpeed);

        Controls.Add(_board);
        Controls.Add(_lblStatus);
        Controls.Add(_lblInfo);
        Controls.Add(_flow);
    }

    private void SetStatus(string text, bool error = false)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Color.Firebrick : Color.Black;
    }

    private bool Busy => _anim != null || _queue.Count > 0 || _autoSolving;

    // ───────────────────────── действия ─────────────────────────
    private void NewGame()
    {
        _queue.Clear();
        _anim = null;
        _autoSolving = _usedAuto = _winShown = false;
        _selectedPeg = -1;
        _dragging = false;
        _sw.Reset();
        _game.Reset((int)_numDisks.Value);
        SetStatus("Новая игра. Перенесите башню на стержень B или C.");
    }

    private void Undo()
    {
        if (Busy) return;
        if (_game.Undo())
        {
            _winShown = false;
            _selectedPeg = -1;
            SetStatus("Ход отменён.");
        }
    }

    private void StopAuto()
    {
        _queue.Clear();
        _autoSolving = false;
        SetStatus("Автоматическое решение остановлено.");
    }

    private void AutoSolve(bool useStack)
    {
        if (Busy) return;
        if (_game.IsSolved) { SetStatus("Головоломка уже решена."); return; }

        _selectedPeg = -1;
        var pegOf = _game.GetPegOfDisk();
        int n = _game.DiskCount;

        // Цель: стержень, на котором уже лежит самый большой диск (его тогда двигать не придётся);
        // если он на стержне A — переносим на C.
        int target = pegOf[n] == 0 ? 2 : pegOf[n];

        var res = useStack ? HanoiSolver.SolveWithStack(pegOf, n, target)
                           : HanoiSolver.SolveRecursive(pegOf, n, target);

        foreach (var m in res.Moves) _queue.Enqueue(m);
        _autoSolving = true;
        _usedAuto = true;

        string depthText = useStack ? $"макс. размер стека: {res.MaxDepth}" : $"макс. глубина рекурсии: {res.MaxDepth}";
        SetStatus($"Алгоритм «{res.Algorithm}»: {res.Moves.Count} ходов, {depthText}.");
    }

    private void TryMove(int from, int to, bool animate)
    {
        if (!_game.CanMove(from, to))
        {
            _flashPeg = to; _flashTicks = 25;
            if (_game.Pegs[from].Count == 0) SetStatus("На этом стержне нет дисков.", true);
            else SetStatus("Нельзя класть больший диск на меньший!", true);
            return;
        }

        if (animate)
        {
            _queue.Enqueue(new Move(_game.Pegs[from][^1], from, to));
        }
        else
        {
            _game.Move(from, to);
            EnsureTimer();
            CheckWin();
        }
        SetStatus("");
    }

    private void EnsureTimer() { if (!_sw.IsRunning && !_winShown) _sw.Start(); }

    private void CheckWin()
    {
        if (_winShown || !_game.IsSolved || _game.History.Count == 0) return;
        _winShown = true;
        _sw.Stop();

        string time = _sw.Elapsed.ToString(@"mm\:ss");
        if (_usedAuto)
        {
            SetStatus($"Решено! Ходов: {_game.History.Count} (минимум {_game.MinMoves}).");
            return;
        }

        SetStatus($"Победа! Ходов: {_game.History.Count} из {_game.MinMoves} возможных, время {time}.");
        string verdict = _game.History.Count == _game.MinMoves
            ? "Идеально — минимальное число ходов!"
            : $"Минимум для {_game.DiskCount} дисков — {_game.MinMoves} ходов. Сможете лучше?";
        MessageBox.Show(this, $"Вы собрали башню!\n\nХодов: {_game.History.Count}\nВремя: {time}\n\n{verdict}", "Победа!", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ───────────────────────── таймер и анимация ─────────────────────────
    private double AnimDurationMs => Math.Max(25, 1000 * Math.Pow(0.72, _trkSpeed.Value - 1));

    private void OnTick()
    {
        if (_anim != null)
        {
            _anim.T += 16.0 / AnimDurationMs;
            if (_anim.T >= 1) { _anim = null; CheckWin(); }
        }

        if (_anim == null && _queue.Count > 0)
        {
            var m = _queue.Dequeue();
            if (_game.Move(m.From, m.To))
            {
                EnsureTimer();
                _anim = new Anim { Move = m, FromSlot = _game.Pegs[m.From].Count, ToSlot = _game.Pegs[m.To].Count - 1 };
            }
            else { _queue.Clear(); _autoSolving = false; }
        }

        if (_anim == null && _queue.Count == 0 && _autoSolving)
        {
            _autoSolving = false;
            CheckWin();
        }

        if (_flashTicks > 0 && --_flashTicks == 0) _flashPeg = -1;

        _lblInfo.Text = $"Ходов: {_game.History.Count}    Минимум: {_game.MinMoves}    Время: {_sw.Elapsed:mm\\:ss}";
        _board.Invalidate();
    }

    // ───────────────────────── мышь ─────────────────────────
    private int PegAt(int x) => Math.Clamp(x * 3 / Math.Max(1, _board.Width), 0, 2);

    private void OnBoardMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { _selectedPeg = -1; return; }
        if (e.Button != MouseButtons.Left || Busy) return;

        int p = PegAt(e.X);
        if (_selectedPeg >= 0)
        {
            int from = _selectedPeg;
            _selectedPeg = -1;
            if (p != from) TryMove(from, p, animate: true);
            return;
        }

        if (_game.Pegs[p].Count == 0) return;
        _dragging = true;
        _dragMoved = false;
        _dragPeg = p;
        _dragDisk = _game.Pegs[p][^1];
        _down = _mouse = e.Location;
    }

    private void OnBoardMouseMove(object? sender, MouseEventArgs e)
    {
        _mouse = e.Location;
        if (_dragging && !_dragMoved)
        {
            int dx = e.X - _down.X, dy = e.Y - _down.Y;
            if (dx * dx + dy * dy > 36) _dragMoved = true;
        }
    }

    private void OnBoardMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        if (_dragMoved)
        {
            int p = PegAt(e.X);
            if (p != _dragPeg) TryMove(_dragPeg, p, animate: false);
        }
        else
        {
            _selectedPeg = _dragPeg;   // простой клик — «взяли» диск, ждём второго клика
            SetStatus("Диск взят. Выберите стержень, на который его положить (ПКМ — отмена).");
        }
    }

    // ───────────────────────── отрисовка ─────────────────────────
    private Lay GetLayout()
    {
        int n = _game.DiskCount;
        int h = _board.Height;
        int diskH = Math.Clamp((h - 170) / (n + 1), 14, 30);
        int baseY = h - 55;
        int pegTop = baseY - (n + 1) * diskH - 10;
        int maxW = Math.Clamp(_board.Width / 3 - 40, 80, 280);
        return new Lay(baseY, diskH, maxW, 50, pegTop);
    }

    private int PegX(int p) => _board.Width * (2 * p + 1) / 6;
    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);
    private static double Ease(double t) => t * t * (3 - 2 * t);

    private int DiskWidth(int k, Lay L)
    {
        int n = _game.DiskCount;
        return n == 1 ? L.MaxW : L.MinW + (L.MaxW - L.MinW) * (k - 1) / (n - 1);
    }

    private static int SlotY(int slot, Lay L) => L.BaseY - (slot + 1) * L.DiskH;

    private void DrawBoard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var L = GetLayout();
        int liftY = L.PegTop - L.DiskH - 12;

        // стержни и основания
        for (int p = 0; p < 3; p++)
        {
            int cx = PegX(p);
            Color rod = p == _flashPeg ? Color.Red : Color.Black;
            using var br = new SolidBrush(rod);
            int baseW = L.MaxW + 40;
            g.FillRectangle(br, cx - baseW / 2, L.BaseY, baseW, 14);
            using var path = Rounded(new Rectangle(cx - 7, L.PegTop, 14, L.BaseY - L.PegTop), 7);
            g.FillPath(br, path);
            string name = ((char)('A' + p)).ToString();
            using var f = new Font("Segoe UI", 14f, FontStyle.Bold);
            var sz = g.MeasureString(name, f);
            g.DrawString(name, f, Brushes.DimGray, cx - sz.Width / 2, L.BaseY + 18);
        }

        // диски, лежащие на стержнях
        for (int p = 0; p < 3; p++)
        {
            var stack = _game.Pegs[p];
            for (int s = 0; s < stack.Count; s++)
            {
                bool top = s == stack.Count - 1;
                if (top && _anim != null && _anim.Move.To == p) continue;          // этот диск «летит»
                if (top && _dragging && _dragMoved && _dragPeg == p) continue;     // этот диск тащат
                int y = SlotY(s, L);
                if (top && _selectedPeg == p && !_dragging) y = liftY;             // выбранный — приподнят
                DrawDisk(g, stack[s], PegX(p), y, L);
            }
        }

        // летящий (анимированный) диск
        if (_anim != null)
        {
            var a = _anim;
            int fx = PegX(a.Move.From), tx = PegX(a.Move.To);
            int fy = SlotY(a.FromSlot, L), ty = SlotY(a.ToSlot, L);
            int x, y;
            if (a.T < 0.25) { x = fx; y = Lerp(fy, liftY, Ease(a.T / 0.25)); }
            else if (a.T < 0.75) { x = Lerp(fx, tx, Ease((a.T - 0.25) / 0.5)); y = liftY; }
            else { x = tx; y = Lerp(liftY, ty, Ease((a.T - 0.75) / 0.25)); }
            DrawDisk(g, a.Move.Disk, x, y, L);
        }

        // диск, который тащит мышь
        if (_dragging && _dragMoved)
            DrawDisk(g, _dragDisk, _mouse.X, _mouse.Y - L.DiskH / 2, L);
    }

    private void DrawDisk(Graphics g, int k, int cx, int topY, Lay L)
    {
        int w = DiskWidth(k, L);
        var r = new Rectangle(cx - w / 2, topY, w, L.DiskH - 3);
        Color c = DiskColor(k);
        using var path = Rounded(r, r.Height / 2);
        using var br = new LinearGradientBrush(r, Blend(c, Color.White, 0.55), c, 90f);
        g.FillPath(br, path);
        using var pen = new Pen(Blend(c, Color.Black, 0.45), 2);
        g.DrawPath(pen, path);
    }

    private Color DiskColor(int k)
    {
        double hue = 340.0 * (k - 1) / Math.Max(1, _game.DiskCount);
        return FromHsv((hue + 5) % 360, 0.55, 0.95);
    }

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double gg, double b) = ((int)(h / 60) % 6) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((gg + m) * 255), (int)((b + m) * 255));
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        d = Math.Min(d, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

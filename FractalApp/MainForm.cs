using System.Diagnostics;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace FractalApp;

/// <summary>
/// Главное окно: холст 800×600 + панель управления.
/// Рекурсивный расчёт идёт в фоновом потоке и пишет в общие буферы,
/// а UI-таймер (30 мс) перерисовывает картинку — так видно, как «растёт» рекурсия.
/// </summary>
public sealed class MainForm : Form
{
    private const int W = 800, H = 600;
    private const double InitScale = 3.2 / W;
    private static readonly int EmptyColor = unchecked((int)0xFF202020);

    // Предел отдаления: ширина кадра не больше ~12.8 единиц. Раньше предела не было, и при сильном
    // отдалении ВСЕ пиксели убегали за 1–2 итерации и заливались одним цветом (зелёный экран).
    private const double MaxScale = InitScale * 4;
    // Предел приближения: глубже уже не хватает диапазона double для малых добавок δ.
    private const double MinScale = 1e-280;
    // Глубже этого масштаба обычный double-алгоритм даёт «кубики» — включается теория возмущений.
    private const double DoubleLimit = 1e-12;

    private enum RenderMode { Recursive, Classic, Perturbation }

    private sealed class CanvasPanel : Panel
    {
        public CanvasPanel() { DoubleBuffered = true; }
    }

    // ── буферы изображения ──
    private readonly Bitmap _bmp = new(W, H, PixelFormat.Format32bppPArgb);
    private readonly int[] _pixels = new int[W * H];   // цвета фрактала
    private readonly int[] _overlay = new int[W * H];  // границы блоков (цвет по глубине)
    private readonly int[] _display = new int[W * H];  // итог: фрактал + оверлей
    private readonly object _sync = new();

    // ── UI ──
    private readonly CanvasPanel _canvas = new() { Location = new Point(0, 0), Size = new Size(W, H) };
    private readonly NumericUpDown _numIter = new() { Minimum = 50, Maximum = 100000, Increment = 50, Value = 300 };
    private readonly CheckBox _chkAutoIter = new() { Text = "Авто-итерации по глубине зума", AutoSize = true };
    private readonly NumericUpDown _numMin = new() { Minimum = 2, Maximum = 32, Value = 4 };
    private readonly CheckBox _chkVis = new() { Text = "Визуализировать рекурсию", Checked = true, AutoSize = true };
    private readonly CheckBox _chkBorders = new() { Text = "Показывать границы блоков", Checked = true, AutoSize = true };
    private readonly TrackBar _trkSpeed = new() { Minimum = 1, Maximum = 100, Value = 25, TickFrequency = 10 };
    private readonly Label _lblSpeed = new() { AutoSize = true };
    private readonly Label _lblStats = new() { Font = new Font("Consolas", 8f), AutoSize = false };
    private readonly Panel _legend = new() { Height = 44 };
    private readonly Panel _side = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };

    // ── состояние расчёта ──
    private CancellationTokenSource? _cts;
    private Task? _task;
    private RenderStats _stats = new();
    private Stopwatch _sw = new();
    // Центр кадра хранится с высокой точностью (BigInteger, фикс. запятая), иначе при глубоком зуме
    // клики мышью «теряли» бы координаты. Масштаб — double (хватает до ~1e-300).
    private BigInteger _cx = FixedPoint.FromDouble(-0.5), _cy = BigInteger.Zero;
    private double _scale = InitScale;
    private RenderMode _lastMode = RenderMode.Recursive;
    private string _note = "";
    private string _modeName = "—";
    private bool _running;
    private bool _dirty = true;
    private Rectangle? _current;
    private int _currentDepth;
    private volatile int _stepsPerFrame = 7;
    private int _stepCounter;

    public MainForm()
    {
        Text = "Множество Мандельброта — рекурсивный алгоритм Мариани–Сильвера";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(W + 320, H + 50);

        BuildControls();

        _canvas.Paint += OnCanvasPaint;
        _canvas.MouseDown += OnCanvasMouseDown;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        FormClosing += (_, _) => { _timer.Stop(); StopRender(); };

        Array.Fill(_pixels, EmptyColor);
        Shown += (_, _) => StartRender(RenderMode.Recursive, _chkVis.Checked);
    }

    // ───────────────────────── построение интерфейса ─────────────────────────
    private void BuildControls()
    {
        var panel = _side;
        panel.Location = new Point(W, 0);
        panel.Size = new Size(320, H + 50);
        panel.BackColor = SystemColors.Control;
        Controls.Add(_canvas);
        Controls.Add(panel);

        int y = 8;
        AddLabel(new Label { Text = "Макс. итераций:", AutoSize = true });
        _numIter.SetBounds(150, y - 22, 150, 24);
        panel.Controls.Add(_numIter);
        y += 8;
        _chkAutoIter.Location = new Point(10, y); panel.Controls.Add(_chkAutoIter); y += 24;

        AddLabel(new Label { Text = "Мин. размер блока (px):", AutoSize = true });
        _numMin.SetBounds(150, y - 22, 150, 24);
        panel.Controls.Add(_numMin);
        y += 8;

        _chkVis.Location = new Point(10, y); panel.Controls.Add(_chkVis); y += 24;
        _chkBorders.Location = new Point(10, y); panel.Controls.Add(_chkBorders); y += 28;
        _chkBorders.CheckedChanged += (_, _) => _dirty = true;

        _lblSpeed.Location = new Point(10, y); panel.Controls.Add(_lblSpeed); y += 20;
        _trkSpeed.SetBounds(6, y, 300, 40); panel.Controls.Add(_trkSpeed); y += 42;
        _trkSpeed.ValueChanged += (_, _) => UpdateSpeed();
        UpdateSpeed();

        Button B(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Location = new Point(10, y), Size = new Size(296, 28) };
            b.Click += onClick;
            panel.Controls.Add(b);
            y += 31;
            return b;
        }

        B("▶ Рекурсивный рендер (Мариани–Сильвер)", (_, _) => StartRender(RenderMode.Recursive, _chkVis.Checked));
        B("Классический попиксельный рендер", (_, _) => StartRender(RenderMode.Classic, false));
        B("∞ Теория возмущений (глубокий зум)", (_, _) => StartRender(RenderMode.Perturbation, _chkVis.Checked));
        B("■ Остановить", (_, _) => { StopRender(); _dirty = true; });
        B("⌂ Сбросить вид", (_, _) =>
        {
            _cx = FixedPoint.FromDouble(-0.5); _cy = BigInteger.Zero; _scale = InitScale;
            StartRender(RenderMode.Recursive, _chkVis.Checked);
        });

        var hint = new Label
        {
            Text = "ЛКМ по картинке — приблизить ×2\nПКМ — отдалить ×2 (до предела кадра)",
            Location = new Point(10, y), AutoSize = true, ForeColor = Color.DimGray
        };
        panel.Controls.Add(hint); y += 36;

        // легенда глубин — под холстом, чтобы не отнимать место у статистики
        _legend.Location = new Point(10, H + 4);
        _legend.Width = W - 20;
        _legend.Paint += OnLegendPaint;
        Controls.Add(_legend);

        _lblStats.SetBounds(10, y, 300, H + 50 - y - 6);
        panel.Controls.Add(_lblStats);

        void AddLabel(Label l) { l.Location = new Point(10, y); panel.Controls.Add(l); y += 22; }
    }

    private void UpdateSpeed()
    {
        // нелинейная шкала: от 1 до ~1250 блоков за кадр
        int v = _trkSpeed.Value;
        _stepsPerFrame = v * v / 8 + 1;
        _lblSpeed.Text = $"Скорость визуализации: {_stepsPerFrame} блок/кадр";
    }

    // ───────────────────────── запуск и остановка расчёта ─────────────────────────
    private void StartRender(RenderMode mode, bool visualize)
    {
        StopRender();

        // Глубже предела double обычные алгоритмы дают «кубики» — переключаемся на теорию возмущений.
        _note = "";
        if (_scale < DoubleLimit && mode != RenderMode.Perturbation)
        {
            mode = RenderMode.Perturbation;
            _note = "масштаб < 1e-12 → теория возмущений";
        }
        _lastMode = mode;

        if (_chkAutoIter.Checked)
        {
            double zoom = InitScale / _scale;
            _numIter.Value = (decimal)Math.Clamp(300 + 150 * Math.Log10(Math.Max(1, zoom)), (double)_numIter.Minimum, (double)_numIter.Maximum);
        }

        Array.Fill(_pixels, EmptyColor);
        Array.Clear(_overlay);
        lock (_sync) { _current = null; }

        // Снимок состояния: рабочий поток не должен читать поля, которые UI может поменять.
        BigInteger cxFix = _cx, cyFix = _cy;
        double scale = _scale;
        var view = new MandelbrotView(W, H, FixedPoint.ToDouble(cxFix), FixedPoint.ToDouble(cyFix), scale, (int)_numIter.Value);
        var palette = MarianiSilverRenderer.BuildPalette(view.MaxIter);
        int minBlock = (int)_numMin.Value;
        var stats = new RenderStats();
        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        _stats = stats;
        _sw = sw;
        _cts = cts;
        _modeName = mode switch
        {
            RenderMode.Classic => "Классический (попиксельный)",
            RenderMode.Perturbation => visualize ? "Возмущения + МС (визуализация)" : "Возмущения + МС (║ потоки)",
            _ => visualize ? "Мариани–Сильвер (с визуализацией)" : "Мариани–Сильвер (║ потоки)"
        };
        _running = true;
        _stepCounter = 0;

        _task = Task.Run(() =>
        {
            try
            {
                if (mode == RenderMode.Classic)
                {
                    MarianiSilverRenderer.RenderClassic(view, _pixels, palette, stats, ct);
                    return;
                }

                Func<int, int, int>? iterate = null;
                if (mode == RenderMode.Perturbation)
                {
                    // Шаг 1: одна опорная орбита с огромной точностью (в этом же фоновом потоке).
                    var swRef = Stopwatch.StartNew();
                    var orbit = ReferenceOrbit.Compute(cxFix, cyFix, scale, view.MaxIter, ct);
                    stats.PrecisionBits = orbit.PrecisionBits;
                    stats.RefOrbitLength = orbit.Length;
                    stats.RefOrbitMs = swRef.ElapsedMilliseconds;
                    iterate = new PerturbationIterator(orbit, view).Iterate;
                }

                // Шаг 2: квадродерево Мариани–Сильвера, но число итераций пикселя считается выбранным способом.
                var r = new MarianiSilverRenderer(view, _pixels, palette, minBlock, stats, iterate);
                if (visualize)
                {
                    r.BlockProcessed = (x0, y0, x1, y1, d, kind) => OnBlock(x0, y0, x1, y1, d, kind, ct);
                    r.Run(ct);
                }
                else
                {
                    r.RunParallel(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
            finally { sw.Stop(); }
        });
    }

    private void StopRender()
    {
        _cts?.Cancel();
        try { _task?.Wait(); } catch (AggregateException) { }
        _running = false;
        lock (_sync) { _current = null; }
    }

    // Вызывается из рабочего потока после КАЖДОГО обработанного блока.
    private void OnBlock(int x0, int y0, int x1, int y1, int depth, BlockKind kind, CancellationToken ct)
    {
        DrawBorder(x0, y0, x1, y1, DepthColor(depth));
        lock (_sync)
        {
            _current = new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            _currentDepth = depth;
        }

        // «Замедлитель»: после N блоков отдаём время UI, чтобы рекурсию можно было разглядеть.
        if (++_stepCounter >= _stepsPerFrame)
        {
            _stepCounter = 0;
            Thread.Sleep(16);
            ct.ThrowIfCancellationRequested();
        }
    }

    private void DrawBorder(int x0, int y0, int x1, int y1, int color)
    {
        for (int x = x0; x <= x1; x++) { _overlay[y0 * W + x] = color; _overlay[y1 * W + x] = color; }
        for (int y = y0; y <= y1; y++) { _overlay[y * W + x0] = color; _overlay[y * W + x1] = color; }
    }

    // ───────────────────────── обновление экрана ─────────────────────────
    private void OnTick()
    {
        if (_running && _task is { IsCompleted: true })
        {
            _running = false;
            _dirty = true;
        }

        if (_running || _dirty)
        {
            RefreshBitmap();
            _dirty = false;
        }

        _lblStats.Text = BuildStats();
    }

    private void RefreshBitmap()
    {
        bool overlay = _chkBorders.Checked && _chkVis.Checked;
        var px = _pixels; var o = _overlay; var d = _display;
        for (int i = 0; i < d.Length; i++)
            d[i] = (overlay && o[i] != 0) ? o[i] : px[i];

        var bd = _bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        Marshal.Copy(d, 0, bd.Scan0, d.Length);
        _bmp.UnlockBits(bd);
        _canvas.Invalidate();
    }

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_bmp, 0, 0);

        Rectangle? cur;
        lock (_sync) { cur = _current; }
        if (_running && cur is { } r)
        {
            using var pen = new Pen(Color.White, 2);
            e.Graphics.DrawRectangle(pen, r);
        }
    }

    private void OnLegendPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawString("Глубина рекурсии (цвет границы блока):", Font, Brushes.Black, 0, 0);
        for (int d = 0; d <= 10; d++)
        {
            var rect = new Rectangle(d * 27, 18, 25, 20);
            using var br = new SolidBrush(Color.FromArgb(DepthColor(d)));
            g.FillRectangle(br, rect);
            g.DrawString(d.ToString(), Font, Brushes.Black, rect.X + 5, rect.Y + 3);
        }
    }

    private string BuildStats()
    {
        var s = _stats;
        long total = (long)W * H;
        double pct = 100.0 * s.PixelsComputed / total;
        bool pert = _lastMode == RenderMode.Perturbation;

        string status = !_running ? "готово"
            : pert && s.RefOrbitLength == 0 ? "считается опорная орбита…"
            : "идёт расчёт…";

        int digits = Math.Clamp((int)Math.Ceiling(-Math.Log10(_scale)) + 4, 10, 300);
        string re = FixedPoint.ToDecimalString(_cx, digits);
        string im = FixedPoint.ToDecimalString(_cy, digits);
        string Cut(string t) => t.Length > 34 ? t[..34] + "…" : t;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Режим: {_modeName}");
        sb.AppendLine($"Статус: {status}");
        if (_note.Length > 0) sb.AppendLine($"! {_note}");
        sb.AppendLine($"Время: {_sw.ElapsedMilliseconds} мс");
        sb.AppendLine($"Блоков (вызовов): {s.Blocks}");
        sb.AppendLine($"  ├ разбито:        {s.Splits}");
        sb.AppendLine($"  ├ залито целиком: {s.Filled}");
        sb.AppendLine($"  └ листьев:        {s.Leaves}");
        sb.AppendLine($"Макс. глубина: {s.MaxDepth}   Текущая: {(_running ? s.CurrentDepth : 0)}");
        sb.AppendLine($"Вычислено пикселей: {s.PixelsComputed:N0}");
        sb.AppendLine($"из {total:N0} ({pct:F1} %)");
        if (pert && s.RefOrbitLength > 0)
        {
            sb.AppendLine($"Опорная орбита: {s.RefOrbitLength:N0} точек, {s.RefOrbitMs} мс");
            sb.AppendLine($"Точность: {s.PrecisionBits} бит (~{s.PrecisionBits * 0.30103:F0} цифр)");
        }
        sb.AppendLine();
        sb.AppendLine($"Re: {Cut(re)}");
        sb.AppendLine($"Im: {Cut(im)}");
        sb.Append($"Увеличение: {InitScale / _scale:G5}x   Итераций: {_numIter.Value}");
        return sb.ToString();
    }

    // ───────────────────────── мышь: зум ─────────────────────────
    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
        bool zoomIn = e.Button == MouseButtons.Left;

        double newScale = zoomIn ? _scale * 0.5 : _scale * 2.0;
        if (zoomIn && newScale < MinScale) return;        // предел диапазона double для δ
        if (!zoomIn && _scale >= MaxScale) return;        // дальше — всё изображение одного цвета
        newScale = Math.Min(newScale, MaxScale);

        // Сдвиг центра в точку клика — в высокой точности (смещение может быть ~1e-200).
        _cx += FixedPoint.FromDouble((e.X - W / 2.0) * _scale);
        _cy += FixedPoint.FromDouble((e.Y - H / 2.0) * _scale);
        _scale = newScale;

        // Центр не даём унести за пределы области, где вообще есть множество.
        var lim = FixedPoint.FromDouble(2.5);
        _cx = BigInteger.Min(lim, BigInteger.Max(-lim, _cx));
        _cy = BigInteger.Min(lim, BigInteger.Max(-lim, _cy));

        // Сохраняем выбранный режим: если были в глубоком зуме — остаёмся в нём.
        StartRender(_lastMode == RenderMode.Perturbation ? RenderMode.Perturbation : RenderMode.Recursive, _chkVis.Checked);
    }

    // ───────────────────────── цвета ─────────────────────────
    private static int DepthColor(int depth) => HsvToArgb((depth * 47) % 360, 1.0, 1.0);

    private static int HsvToArgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = ((int)(h / 60) % 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return unchecked((int)0xFF000000) | ((int)((r + m) * 255) << 16) | ((int)((g + m) * 255) << 8) | (int)((b + m) * 255);
    }
}

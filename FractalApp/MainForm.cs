using System.Diagnostics;
using System.Drawing.Imaging;
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
    private readonly NumericUpDown _numIter = new() { Minimum = 50, Maximum = 5000, Increment = 50, Value = 300 };
    private readonly NumericUpDown _numMin = new() { Minimum = 2, Maximum = 32, Value = 4 };
    private readonly CheckBox _chkVis = new() { Text = "Визуализировать рекурсию", Checked = true, AutoSize = true };
    private readonly CheckBox _chkBorders = new() { Text = "Показывать границы блоков", Checked = true, AutoSize = true };
    private readonly TrackBar _trkSpeed = new() { Minimum = 1, Maximum = 100, Value = 25, TickFrequency = 10 };
    private readonly Label _lblSpeed = new() { AutoSize = true };
    private readonly Label _lblStats = new() { Font = new Font("Consolas", 9f), AutoSize = false };
    private readonly Panel _legend = new() { Height = 44 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };

    // ── состояние расчёта ──
    private CancellationTokenSource? _cts;
    private Task? _task;
    private RenderStats _stats = new();
    private Stopwatch _sw = new();
    private double _cx = -0.5, _cy = 0, _scale = InitScale;
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
        ClientSize = new Size(W + 320, H);

        BuildControls();

        _canvas.Paint += OnCanvasPaint;
        _canvas.MouseDown += OnCanvasMouseDown;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        FormClosing += (_, _) => { _timer.Stop(); StopRender(); };

        Array.Fill(_pixels, EmptyColor);
        Shown += (_, _) => StartRender(recursive: true, visualize: _chkVis.Checked);
    }

    // ───────────────────────── построение интерфейса ─────────────────────────
    private void BuildControls()
    {
        var panel = new Panel { Location = new Point(W, 0), Size = new Size(320, H), BackColor = SystemColors.Control };
        Controls.Add(_canvas);
        Controls.Add(panel);

        int y = 8;
        AddLabel(new Label { Text = "Макс. итераций:", AutoSize = true });
        _numIter.SetBounds(150, y - 22, 150, 24);
        panel.Controls.Add(_numIter);
        y += 8;

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
            var b = new Button { Text = text, Location = new Point(10, y), Size = new Size(296, 30) };
            b.Click += onClick;
            panel.Controls.Add(b);
            y += 34;
            return b;
        }

        B("▶ Рекурсивный рендер (Мариани–Сильвер)", (_, _) => StartRender(true, _chkVis.Checked));
        B("Классический попиксельный рендер", (_, _) => StartRender(false, false));
        B("■ Остановить", (_, _) => { StopRender(); _dirty = true; });
        B("⌂ Сбросить вид", (_, _) => { _cx = -0.5; _cy = 0; _scale = InitScale; StartRender(true, _chkVis.Checked); });

        var hint = new Label
        {
            Text = "ЛКМ по картинке — приблизить ×2\nПКМ — отдалить ×2",
            Location = new Point(10, y), AutoSize = true, ForeColor = Color.DimGray
        };
        panel.Controls.Add(hint); y += 40;

        // легенда глубин
        _legend.Location = new Point(10, y);
        _legend.Width = 296;
        _legend.Paint += OnLegendPaint;
        panel.Controls.Add(_legend);
        y += 48;

        _lblStats.SetBounds(10, y, 300, H - y - 6);
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
    private void StartRender(bool recursive, bool visualize)
    {
        StopRender();

        Array.Fill(_pixels, EmptyColor);
        Array.Clear(_overlay);
        lock (_sync) { _current = null; }

        var view = new MandelbrotView(W, H, _cx, _cy, _scale, (int)_numIter.Value);
        var palette = MarianiSilverRenderer.BuildPalette(view.MaxIter);
        int minBlock = (int)_numMin.Value;
        var stats = new RenderStats();
        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        _stats = stats;
        _sw = sw;
        _cts = cts;
        _modeName = recursive ? (visualize ? "Мариани–Сильвер (с визуализацией)" : "Мариани–Сильвер (быстро)") : "Классический (попиксельный)";
        _running = true;
        _stepCounter = 0;

        _task = Task.Run(() =>
        {
            try
            {
                if (recursive)
                {
                    var r = new MarianiSilverRenderer(view, _pixels, palette, minBlock, stats);
                    if (visualize)
                        r.BlockProcessed = (x0, y0, x1, y1, d, kind) => OnBlock(x0, y0, x1, y1, d, kind, ct);
                    r.Run(ct);
                }
                else
                {
                    MarianiSilverRenderer.RenderClassic(view, _pixels, palette, stats, ct);
                }
            }
            catch (OperationCanceledException) { }
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
        int depth; lock (_sync) { depth = _currentDepth; }
        return
            $"Режим: {_modeName}\n" +
            $"Статус: {(_running ? "идёт расчёт…" : "готово")}\n" +
            $"Время: {_sw.ElapsedMilliseconds} мс\n\n" +
            $"Блоков (вызовов): {s.Blocks}\n" +
            $"  ├ разбито:        {s.Splits}\n" +
            $"  ├ залито целиком: {s.Filled}\n" +
            $"  └ листьев:        {s.Leaves}\n" +
            $"Макс. глубина: {s.MaxDepth}\n" +
            $"Текущая глубина: {(_running ? s.CurrentDepth : 0)}\n\n" +
            $"Вычислено пикселей: {s.PixelsComputed:N0}\n" +
            $"из {total:N0} ({pct:F1} %)\n\n" +
            $"Центр: {_cx:G10}\n       {_cy:G10}\n" +
            $"Увеличение: {InitScale / _scale:G5}x";
    }

    // ───────────────────────── мышь: зум ─────────────────────────
    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
        if (e.Button == MouseButtons.Left && _scale < 1e-13) return; // предел точности double

        _cx += (e.X - W / 2.0) * _scale;
        _cy += (e.Y - H / 2.0) * _scale;
        _scale *= e.Button == MouseButtons.Left ? 0.5 : 2.0;
        StartRender(true, _chkVis.Checked);
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

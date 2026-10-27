// Ядро расчёта множества Мандельброта.
// Файл намеренно НЕ зависит от WinForms / System.Drawing — его можно тестировать в консоли.
namespace FractalApp;

/// <summary>Что произошло с блоком (прямоугольником) на текущем шаге рекурсии.</summary>
public enum BlockKind
{
    /// <summary>Граница неоднородна — блок делится на 4 подблока (рекурсивный спуск).</summary>
    Split,
    /// <summary>Вся граница одного цвета — внутренность закрашена без вычислений (база рекурсии №1).</summary>
    Filled,
    /// <summary>Блок слишком мал — все пиксели считаются напрямую (база рекурсии №2).</summary>
    Leaf
}

/// <summary>Счётчики, по которым видно «цену» алгоритма.</summary>
public sealed class RenderStats
{
    public long PixelsComputed;   // сколько раз реально считалась формула z = z² + c
    public int Blocks;            // всего вызовов рекурсивной функции
    public int Splits;            // из них — разбиений
    public int Filled;            // из них — «залито целиком»
    public int Leaves;            // из них — листья
    public int MaxDepth;          // максимальная глубина рекурсии
    public int CurrentDepth;      // текущая глубина (для визуализации)
}

/// <summary>Окно просмотра: какая область комплексной плоскости отображается.</summary>
public sealed record MandelbrotView(int Width, int Height, double CenterX, double CenterY, double Scale, int MaxIter)
{
    /// <summary>Действительная часть c для пикселя по оси X. Scale — «единиц плоскости на пиксель».</summary>
    public double ReAt(int px) => CenterX + (px - Width / 2.0) * Scale;

    /// <summary>Мнимая часть c для пикселя по оси Y.</summary>
    public double ImAt(int py) => CenterY + (py - Height / 2.0) * Scale;
}

public sealed class MarianiSilverRenderer
{
    private readonly MandelbrotView _view;
    private readonly int[] _iter;      // кэш числа итераций (−1 = ещё не считали)
    private readonly int[] _pixels;    // выходной буфер ARGB
    private readonly int[] _palette;
    private readonly int _minBlock;
    private readonly RenderStats _stats;
    private CancellationToken _ct;

    /// <summary>Вызывается после обработки каждого блока: x0, y0, x1, y1, глубина, тип.</summary>
    public Action<int, int, int, int, int, BlockKind>? BlockProcessed { get; set; }

    public MarianiSilverRenderer(MandelbrotView view, int[] pixels, int[] palette, int minBlock, RenderStats stats)
    {
        _view = view;
        _pixels = pixels;
        _palette = palette;
        _minBlock = Math.Max(2, minBlock);
        _stats = stats;
        _iter = new int[view.Width * view.Height];
        Array.Fill(_iter, -1);
    }

    /// <summary>Запуск рекурсии на всём изображении.</summary>
    public void Run(CancellationToken ct)
    {
        _ct = ct;
        Process(0, 0, _view.Width - 1, _view.Height - 1, 0);
    }

    // ───────────────────────── РЕКУРСИВНАЯ ФУНКЦИЯ ─────────────────────────
    // Прямоугольник задаётся включительно: [x0..x1] × [y0..y1].
    private void Process(int x0, int y0, int x1, int y1, int depth)
    {
        _ct.ThrowIfCancellationRequested();

        _stats.Blocks++;
        _stats.CurrentDepth = depth;
        if (depth > _stats.MaxDepth) _stats.MaxDepth = depth;

        int w = x1 - x0 + 1;
        int h = y1 - y0 + 1;

        // База рекурсии №2: блок мал — считаем «в лоб».
        if (w <= _minBlock || h <= _minBlock)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Get(x, y);
            _stats.Leaves++;
            BlockProcessed?.Invoke(x0, y0, x1, y1, depth, BlockKind.Leaf);
            return;
        }

        // Считаем ТОЛЬКО периметр блока и проверяем, одинаков ли он по числу итераций.
        int first = Get(x0, y0);
        bool uniform = true;
        for (int x = x0; x <= x1 && uniform; x++)
            if (Get(x, y0) != first || Get(x, y1) != first) uniform = false;
        for (int y = y0; y <= y1 && uniform; y++)
            if (Get(x0, y) != first || Get(x1, y) != first) uniform = false;

        // База рекурсии №1: граница однородна ⇒ (множество Мандельброта связно и односвязно)
        // внутри тот же цвет. Заливаем без вычислений.
        if (uniform)
        {
            int color = _palette[first];
            for (int y = y0; y <= y1; y++)
            {
                int row = y * _view.Width;
                for (int x = x0; x <= x1; x++)
                    _pixels[row + x] = color;
            }
            _stats.Filled++;
            BlockProcessed?.Invoke(x0, y0, x1, y1, depth, BlockKind.Filled);
            return;
        }

        // Рекурсивный шаг: делим на 4 квадранта.
        _stats.Splits++;
        BlockProcessed?.Invoke(x0, y0, x1, y1, depth, BlockKind.Split);

        int mx = (x0 + x1) / 2;
        int my = (y0 + y1) / 2;
        Process(x0, y0, mx, my, depth + 1);          // верх-лево
        Process(mx + 1, y0, x1, my, depth + 1);      // верх-право
        Process(x0, my + 1, mx, y1, depth + 1);      // низ-лево
        Process(mx + 1, my + 1, x1, y1, depth + 1);  // низ-право

        _stats.CurrentDepth = depth; // «вернулись» из рекурсии
    }

    /// <summary>Число итераций для пикселя с кэшированием (общие границы блоков не считаются дважды).</summary>
    private int Get(int x, int y)
    {
        int idx = y * _view.Width + x;
        int v = _iter[idx];
        if (v >= 0) return v;

        v = Iterate(_view.ReAt(x), _view.ImAt(y), _view.MaxIter);
        _iter[idx] = v;
        _pixels[idx] = _palette[v];
        _stats.PixelsComputed++;
        return v;
    }

    // ───────────────────────── Базовые функции ─────────────────────────

    /// <summary>Escape-time: сколько итераций z → z² + c нужно, чтобы |z| превысил 2.</summary>
    public static int Iterate(double cr, double ci, int maxIter)
    {
        // Быстрая проверка главной кардиоиды и круглой «луковицы» периода 2.
        double q = (cr - 0.25) * (cr - 0.25) + ci * ci;
        if (q * (q + (cr - 0.25)) <= 0.25 * ci * ci) return maxIter;
        if ((cr + 1) * (cr + 1) + ci * ci <= 0.0625) return maxIter;

        double x = 0, y = 0, x2 = 0, y2 = 0;
        int i = 0;
        while (i < maxIter && x2 + y2 <= 4.0)
        {
            y = 2 * x * y + ci;
            x = x2 - y2 + cr;
            x2 = x * x;
            y2 = y * y;
            i++;
        }
        return i;
    }

    /// <summary>Классический попиксельный рендер — для сравнения скорости и корректности.</summary>
    public static void RenderClassic(MandelbrotView v, int[] pixels, int[] palette, RenderStats stats, CancellationToken ct)
    {
        for (int y = 0; y < v.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            double ci = v.ImAt(y);
            int row = y * v.Width;
            for (int x = 0; x < v.Width; x++)
                pixels[row + x] = palette[Iterate(v.ReAt(x), ci, v.MaxIter)];
            stats.PixelsComputed += v.Width;
        }
    }

    /// <summary>Палитра: индекс = число итераций, последний элемент (maxIter) = чёрный (точка внутри множества).</summary>
    public static int[] BuildPalette(int maxIter)
    {
        var p = new int[maxIter + 1];
        for (int i = 0; i < maxIter; i++)
        {
            double t = i * 0.07;
            int r = (int)(127.5 + 127.5 * Math.Sin(t));
            int g = (int)(127.5 + 127.5 * Math.Sin(t + 2.1));
            int b = (int)(127.5 + 127.5 * Math.Sin(t + 4.2));
            p[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        p[maxIter] = unchecked((int)0xFF000000);
        return p;
    }
}

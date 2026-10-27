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

    // Только для режима «теория возмущений»
    public int PrecisionBits;     // разрядность опорной орбиты (бит)
    public int RefOrbitLength;    // длина опорной орбиты (число точек)
    public long RefOrbitMs;       // время расчёта опорной орбиты
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
    private readonly Func<int, int, int> _iterate;   // как считать число итераций пикселя (x, y)
    private CancellationToken _ct;

    /// <summary>Вызывается после обработки каждого блока: x0, y0, x1, y1, глубина, тип.</summary>
    public Action<int, int, int, int, int, BlockKind>? BlockProcessed { get; set; }

    /// <param name="iterate">
    /// Функция «число итераций для пикселя (x, y)». По умолчанию — обычный escape-time в double;
    /// для глубокого зума сюда подставляется <see cref="PerturbationIterator.Iterate"/>.
    /// </param>
    public MarianiSilverRenderer(MandelbrotView view, int[] pixels, int[] palette, int minBlock, RenderStats stats,
                                 Func<int, int, int>? iterate = null)
    {
        _iterate = iterate ?? ((x, y) => Iterate(view.ReAt(x), view.ImAt(y), view.MaxIter));
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

    /// <summary>
    /// Многопоточный вариант: верхние 3 уровня квадродерева (сетка 8×8) раздаются по потокам,
    /// ниже каждый поток рекурсивно работает со своим тайлом. Тайлы идут от центра к краям —
    /// самые тяжёлые (у границы множества) стартуют первыми, нагрузка выравнивается.
    /// Общие границы соседних тайлов могут посчитать оба потока — результат одинаков, вреда нет.
    /// </summary>
    public void RunParallel(CancellationToken ct, int tilesX = 8, int tilesY = 8)
    {
        _ct = ct;
        int w = _view.Width, h = _view.Height;
        var tiles = new List<(int x0, int y0, int x1, int y1)>();
        for (int ty = 0; ty < tilesY; ty++)
            for (int tx = 0; tx < tilesX; tx++)
                tiles.Add((tx * w / tilesX, ty * h / tilesY, (tx + 1) * w / tilesX - 1, (ty + 1) * h / tilesY - 1));

        tiles.Sort((a, b) => Dist(a).CompareTo(Dist(b)));
        double Dist((int x0, int y0, int x1, int y1) t) =>
            Math.Pow((t.x0 + t.x1) / 2.0 - w / 2.0, 2) + Math.Pow((t.y0 + t.y1) / 2.0 - h / 2.0, 2);

        var opt = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.ForEach(tiles, opt, t => Process(t.x0, t.y0, t.x1, t.y1, 3));
    }

    // ───────────────────────── РЕКУРСИВНАЯ ФУНКЦИЯ ─────────────────────────
    // Прямоугольник задаётся включительно: [x0..x1] × [y0..y1].
    private void Process(int x0, int y0, int x1, int y1, int depth)
    {
        _ct.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _stats.Blocks);
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
            Interlocked.Increment(ref _stats.Leaves);
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
            Interlocked.Increment(ref _stats.Filled);
            BlockProcessed?.Invoke(x0, y0, x1, y1, depth, BlockKind.Filled);
            return;
        }

        // Рекурсивный шаг: делим на 4 квадранта.
        Interlocked.Increment(ref _stats.Splits);
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

        v = _iterate(x, y);
        _iter[idx] = v;
        _pixels[idx] = _palette[v];
        Interlocked.Increment(ref _stats.PixelsComputed);
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

    // Опорные цвета палитры (R, G, B). Начинается с тёмно-синего и замыкается на него же,
    // поэтому цикл бесшовный.
    private static readonly (double r, double g, double b)[] Stops =
    {
        (0, 7, 100), (32, 107, 203), (237, 255, 255), (255, 170, 0), (60, 10, 60), (0, 7, 100)
    };

    /// <summary>
    /// Палитра: индекс = число итераций, последний элемент (maxIter) = чёрный (точка внутри множества).
    /// Цвета циклически перетекают друг в друга каждые <c>Period</c> итераций.
    ///
    /// БАГ, который был раньше: цвет считался как sin(i·0.07 + фаза). При сильном отдалении все пиксели
    /// «убегают» за 1–2 итерации, то есть всё изображение получало цвет палитры[1] — а это насыщенный
    /// ярко-зелёный (R≈136, G≈232, B≈12). Теперь начало палитры — тёмно-синее, а при отдалении ещё и
    /// ограничивается сам масштаб (см. MainForm), так что множество всегда остаётся в кадре.
    /// </summary>
    public static int[] BuildPalette(int maxIter)
    {
        const double Period = 64.0;
        var p = new int[maxIter + 1];
        int n = Stops.Length - 1;
        for (int i = 0; i < maxIter; i++)
        {
            double pos = (i % Period) / Period * n;   // 0..n
            int k = Math.Min((int)pos, n - 1);
            double f = pos - k;
            f = f * f * (3 - 2 * f);                   // smoothstep — мягче переходы
            var a = Stops[k]; var b = Stops[k + 1];
            int r = (int)(a.r + (b.r - a.r) * f);
            int g = (int)(a.g + (b.g - a.g) * f);
            int bl = (int)(a.b + (b.b - a.b) * f);
            p[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
        }
        p[maxIter] = unchecked((int)0xFF000000);
        return p;
    }
}

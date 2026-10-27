// Теория возмущений (perturbation theory) для глубокого зума по множеству Мандельброта.
// Файл НЕ зависит от WinForms / System.Drawing.
//
// Проблема: double хранит ~16 десятичных цифр, поэтому глубже масштаба ~1e-13 соседние пиксели
// получают одинаковые координаты и картинка превращается в «кубики».
//
// Идея метода:
//   1. Одну точку C (центр кадра) считаем ОДИН раз с огромной точностью (BigInteger, числа с фиксированной
//      запятой) и запоминаем всю орбиту Z₀, Z₁, … в обычных double — сами Zₙ ограничены |Z| ≲ 2.
//   2. Любой другой пиксель c = C + δc отличается от центра на ничтожную величину δc (порядка размера пикселя).
//      Его орбиту записываем как zₙ = Zₙ + δₙ и считаем только малую добавку δₙ:
//
//          δₙ₊₁ = 2·Zₙ·δₙ + δₙ² + δc
//
//      Здесь все числа — обычные double, потому что δ мал по величине, а не потому что у него мало знаков.
//      Точность хранится в самих «малых» числах, и 53 бит мантиссы им хватает.
//   3. Глитчи (когда опорная орбита «не подходит» пикселю) устраняются перебазированием (rebasing, Zhuoran):
//      если |z| < |δ| — продолжаем орбиту от начала опорной орбиты, подставив δ := z.
//
// Итог: стоимость пикселя — те же ~10 flop на итерацию, что и у обычного double-алгоритма,
// а глубина зума ограничена не 1e-13, а диапазоном double для δ (≈1e-300).
using System.Numerics;

namespace FractalApp;

/// <summary>Числа с фиксированной запятой на BigInteger: значение = целое / 2^Frac.</summary>
public static class FixedPoint
{
    /// <summary>Сколько двоичных разрядов после запятой хранится в центре кадра (≈330 десятичных цифр).</summary>
    public const int Frac = 1100;

    /// <summary>double → фиксированная запятая (точно, без потери мантиссы, пока не «уходит» за 2^-Frac).</summary>
    public static BigInteger FromDouble(double d)
    {
        if (d == 0 || double.IsNaN(d) || double.IsInfinity(d)) return BigInteger.Zero;

        long bits = BitConverter.DoubleToInt64Bits(d);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else mant |= 1L << 52;

        int e = exp - 1075;               // d = mant · 2^e
        int shift = Frac + e;
        BigInteger r = shift >= 0 ? (BigInteger)mant << shift : (BigInteger)mant >> -shift;
        return neg ? -r : r;
    }

    /// <summary>Фиксированная запятая с <paramref name="fracBits"/> разрядами → double.</summary>
    public static double ToDouble(BigInteger v, int fracBits)
    {
        if (v.IsZero) return 0;
        long bl = (long)v.GetBitLength();
        int drop = (int)Math.Max(0, bl - 62);      // оставляем ~62 значащих бита, чтобы не переполнить double
        return Math.ScaleB((double)(v >> drop), drop - fracBits);
    }

    public static double ToDouble(BigInteger v) => ToDouble(v, Frac);

    /// <summary>Десятичная запись с заданным числом знаков после запятой (для показа координат центра).</summary>
    public static string ToDecimalString(BigInteger v, int digits)
    {
        bool neg = v.Sign < 0;
        var a = BigInteger.Abs(v);
        var ip = a >> Frac;
        var fp = a - (ip << Frac);
        var scaled = (fp * BigInteger.Pow(10, digits)) >> Frac;
        return (neg ? "-" : "") + ip + "." + scaled.ToString().PadLeft(digits, '0');
    }
}

/// <summary>Опорная орбита Z₀=0, Z₁, …, посчитанная с высокой точностью и сохранённая в double.</summary>
public sealed class ReferenceOrbit
{
    public double[] Zr { get; }
    public double[] Zi { get; }

    /// <summary>
    /// Excess[n] = |Zₙ|² − 4, посчитанное в высокой точности. Нужен для проверки побега:
    /// |Zₙ+δ|² − 4 = Excess[n] + 2·Re(Zₙ·conj δ) + |δ|². Если считать «в лоб» zₙ = Zₙ + δ в double,
    /// то при |Zₙ| = 2 (например, опорная точка c = −2) малая добавка δ теряется при сложении,
    /// и побег замечается с запозданием на десятки–сотни итераций.
    /// </summary>
    public double[] Excess { get; }

    /// <summary>Сколько точек орбиты реально записано (≤ maxIter + 1). Если орбита «убежала», последняя точка — первая с |Z| &gt; 2.</summary>
    public int Length { get; }

    /// <summary>Разрядность, с которой считалась орбита.</summary>
    public int PrecisionBits { get; }

    private ReferenceOrbit(double[] zr, double[] zi, double[] excess, int length, int bits)
    {
        Zr = zr; Zi = zi; Excess = excess; Length = length; PrecisionBits = bits;
    }

    /// <summary>
    /// Считает орбиту для C = (cx, cy), заданного в формате <see cref="FixedPoint"/>.
    /// Разрядность подбирается по масштабу: нужно «видеть» хотя бы размер пикселя плюс запас на хаотическое
    /// усиление ошибок вдоль орбиты.
    /// </summary>
    public static ReferenceOrbit Compute(BigInteger cx, BigInteger cy, double scale, int maxIter, CancellationToken ct)
    {
        int bits = Math.Clamp((int)Math.Ceiling(-Math.Log2(scale)) + 128, 128, FixedPoint.Frac);
        int sh = FixedPoint.Frac - bits;
        BigInteger cr = cx >> sh, ci = cy >> sh;
        BigInteger four = (BigInteger)4 << bits;

        var zrD = new double[maxIter + 1];
        var ziD = new double[maxIter + 1];
        var exD = new double[maxIter + 1];

        BigInteger zr = BigInteger.Zero, zi = BigInteger.Zero;
        int n = 0;
        while (true)
        {
            BigInteger zr2 = (zr * zr) >> bits;
            BigInteger zi2 = (zi * zi) >> bits;
            BigInteger mod2 = zr2 + zi2;

            zrD[n] = FixedPoint.ToDouble(zr, bits);
            ziD[n] = FixedPoint.ToDouble(zi, bits);
            exD[n] = FixedPoint.ToDouble(mod2 - four, bits);
            n++;
            if (n > maxIter) break;                       // записали Z₀ … Z_maxIter
            if (mod2 > four) break;                       // орбита опорной точки убежала (Z уже записан)

            BigInteger zri = (zr * zi) >> (bits - 1);     // 2·zr·zi
            zi = zri + ci;
            zr = zr2 - zi2 + cr;

            if ((n & 255) == 0) ct.ThrowIfCancellationRequested();
        }
        return new ReferenceOrbit(zrD, ziD, exD, n, bits);
    }
}

/// <summary>Считает число итераций пикселя через малую добавку δ относительно опорной орбиты.</summary>
public sealed class PerturbationIterator
{
    private readonly ReferenceOrbit _ref;
    private readonly MandelbrotView _view;

    public PerturbationIterator(ReferenceOrbit orbit, MandelbrotView view)
    {
        _ref = orbit;
        _view = view;
    }

    /// <summary>
    /// Число итераций для пикселя (px, py). Семантика та же, что у <see cref="MarianiSilverRenderer.Iterate"/>:
    /// номер итерации, на которой |z|² впервые превысил 4, либо MaxIter. Потокобезопасно (только чтение).
    /// </summary>
    public int Iterate(int px, int py)
    {
        // Смещение пикселя от центра кадра (= от опорной точки). Масштаб ≥ ~1e-300, double справляется.
        double dcr = (px - _view.Width / 2.0) * _view.Scale;
        double dci = (py - _view.Height / 2.0) * _view.Scale;

        double[] zr = _ref.Zr, zi = _ref.Zi, ex = _ref.Excess;
        int last = _ref.Length - 1;
        int max = _view.MaxIter;

        double dr = 0, di = 0;     // δₙ
        int n = 0;                 // индекс в опорной орбите
        int i = 0;                 // сколько итераций сделал именно этот пиксель

        while (i < max)
        {
            double Zr = zr[n], Zi = zi[n];

            // δₙ₊₁ = 2·Zₙ·δₙ + δₙ² + δc
            double nr = 2 * (Zr * dr - Zi * di) + dr * dr - di * di + dcr;
            double ni = 2 * (Zr * di + Zi * dr) + 2 * dr * di + dci;
            dr = nr; di = ni;
            n++; i++;

            // Полное значение zₙ = Zₙ + δₙ
            double fr = zr[n] + dr, fi = zi[n] + di;
            double d2 = dr * dr + di * di;

            // |zₙ|² − 4 без потери δ (см. ReferenceOrbit.Excess)
            if (ex[n] + 2 * (zr[n] * dr + zi[n] * di) + d2 > 0) return i;

            double f2 = fr * fr + fi * fi;

            // Перебазирование: если z стал меньше добавки (или опорная орбита кончилась) —
            // дальше считаем так, будто опорная орбита стартует заново из нуля, а δ := z.
            if (f2 < d2 || n == last)
            {
                dr = fr; di = fi;
                n = 0;
            }
        }
        return max;
    }
}

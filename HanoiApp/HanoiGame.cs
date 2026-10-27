// Модель игры. Не зависит от WinForms — можно тестировать в консоли.
namespace HanoiApp;

/// <summary>Один ход: диск № Disk (1 — самый маленький) переносится со стержня From на стержень To.</summary>
public readonly record struct Move(int Disk, int From, int To);

public sealed class HanoiGame
{
    public const int PegCount = 3;

    /// <summary>Стержни. В каждом списке диски лежат снизу вверх; номер диска = его размер.</summary>
    public List<int>[] Pegs { get; } = new List<int>[] { new(), new(), new() };

    public List<Move> History { get; } = new();
    public int DiskCount { get; private set; }

    /// <summary>Теоретический минимум ходов: 2^n − 1.</summary>
    public int MinMoves => (1 << DiskCount) - 1;

    public HanoiGame(int diskCount) => Reset(diskCount);

    public void Reset(int diskCount)
    {
        DiskCount = diskCount;
        foreach (var p in Pegs) p.Clear();
        History.Clear();
        for (int k = diskCount; k >= 1; k--) Pegs[0].Add(k);
    }

    /// <summary>Правила: берём верхний диск; класть можно только на пустой стержень или на больший диск.</summary>
    public bool CanMove(int from, int to)
    {
        if (from == to || Pegs[from].Count == 0) return false;
        if (Pegs[to].Count == 0) return true;
        return Pegs[to][^1] > Pegs[from][^1];
    }

    public bool Move(int from, int to)
    {
        if (!CanMove(from, to)) return false;
        int disk = Pegs[from][^1];
        Pegs[from].RemoveAt(Pegs[from].Count - 1);
        Pegs[to].Add(disk);
        History.Add(new Move(disk, from, to));
        return true;
    }

    public bool Undo()
    {
        if (History.Count == 0) return false;
        var m = History[^1];
        History.RemoveAt(History.Count - 1);
        Pegs[m.To].RemoveAt(Pegs[m.To].Count - 1);
        Pegs[m.From].Add(m.Disk);
        return true;
    }

    /// <summary>Победа: все диски перенесены на другой стержень (B или C).</summary>
    public bool IsSolved => Pegs[1].Count == DiskCount || Pegs[2].Count == DiskCount;

    /// <summary>pegOf[k] — номер стержня, на котором лежит диск k (индексы 1..n).</summary>
    public int[] GetPegOfDisk()
    {
        var pegOf = new int[DiskCount + 1];
        for (int p = 0; p < PegCount; p++)
            foreach (int k in Pegs[p]) pegOf[k] = p;
        return pegOf;
    }
}

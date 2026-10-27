// Два решателя: рекурсивный и итеративный (с явным стеком).
// Оба работают из ПРОИЗВОЛЬНОГО допустимого положения дисков, а не только из начального.
namespace HanoiApp;

public sealed record SolveResult(string Algorithm, List<Move> Moves, int MaxDepth);

public static class HanoiSolver
{
    /// <summary>
    /// Рекурсивное решение: «чтобы переместить диски 1..k на стержень t:
    ///   1) переместить диски 1..k−1 на запасной стержень,
    ///   2) переложить диск k на t,
    ///   3) переместить диски 1..k−1 поверх него».
    /// Если диск k уже на t — он вообще не двигается, решаем задачу для k−1.
    /// </summary>
    public static SolveResult SolveRecursive(int[] pegOf, int n, int target)
    {
        var p = (int[])pegOf.Clone();
        var moves = new List<Move>();
        int maxDepth = 0;

        void Rec(int k, int t, int depth)
        {
            if (depth > maxDepth) maxDepth = depth;
            if (k == 0) return;                       // база рекурсии

            int src = p[k];
            if (src == t) { Rec(k - 1, t, depth + 1); return; }

            int other = 3 - src - t;                  // индексы 0+1+2 = 3
            Rec(k - 1, other, depth + 1);             // шаг 1
            moves.Add(new Move(k, src, t));           // шаг 2
            p[k] = t;
            Rec(k - 1, t, depth + 1);                 // шаг 3
        }

        Rec(n, target, 1);
        return new SolveResult("Рекурсия", moves, maxDepth);
    }

    private readonly record struct Frame(int K, int Target, int Stage);

    /// <summary>
    /// То же решение, но без рекурсивных вызовов: стек вызовов эмулируется явным Stack&lt;Frame&gt;.
    /// Stage 0 — «ещё не обработан», Stage 1 — «подзадача k−1 решена, пора перекладывать диск k».
    /// </summary>
    public static SolveResult SolveWithStack(int[] pegOf, int n, int target)
    {
        var p = (int[])pegOf.Clone();
        var moves = new List<Move>();
        var stack = new Stack<Frame>();
        stack.Push(new Frame(n, target, 0));
        int maxStack = 1;

        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if (f.K == 0) continue;

            int src = p[f.K];
            if (f.Stage == 0)
            {
                if (src == f.Target)
                {
                    stack.Push(new Frame(f.K - 1, f.Target, 0));
                }
                else
                {
                    int other = 3 - src - f.Target;
                    // Кладём в стек в обратном порядке: сначала «потом», затем «сейчас».
                    stack.Push(new Frame(f.K, f.Target, 1));       // после подзадачи — переложить диск k
                    stack.Push(new Frame(f.K - 1, other, 0));      // сначала — убрать диски 1..k−1
                }
            }
            else
            {
                moves.Add(new Move(f.K, src, f.Target));
                p[f.K] = f.Target;
                stack.Push(new Frame(f.K - 1, f.Target, 0));       // теперь вернуть меньшие диски поверх
            }
            if (stack.Count > maxStack) maxStack = stack.Count;
        }

        return new SolveResult("Стек", moves, maxStack);
    }
}

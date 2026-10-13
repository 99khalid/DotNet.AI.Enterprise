namespace DotNet.AI.Enterprise.Evaluation;

/// <summary>One character per case while a suite runs: · pass, x fail.</summary>
public static class Progress
{
    public static void Start(string suite) => Console.Write($"  {suite,-12} ");

    public static void Tick(bool passed) => Console.Write(passed ? "·" : "x");

    public static void End() => Console.WriteLine();
}

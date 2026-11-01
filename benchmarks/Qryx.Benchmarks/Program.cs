using BenchmarkDotNet.Running;
using Qryx.Benchmarks.Scenarios;

namespace Qryx.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--postgres") || args.Contains("--pg"))
        {
            FastDiagnosticRunner.RunPostgresExecutionSuite();
            return;
        }

        if (args.Length > 0 && (args.Contains("--bdn") || args.Contains("-b") || args.Any(a => a.StartsWith("--filter"))))
        {
            var filteredArgs = args.Where(a => a != "--bdn" && a != "-b").ToArray();
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(filteredArgs);
            return;
        }

        if (args.Length == 0 || args.Contains("--fast") || args.Contains("-f"))
        {
            FastDiagnosticRunner.RunAll();
            Console.WriteLine("\nMaslahat: BenchmarkDotNet to'liq hisobotlarini ishga tushirish uchun:");
            Console.WriteLine("  dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --bdn");
            return;
        }

        // Interactive Menu if unknown argument
        Console.WriteLine("Qryx Benchmark Loyihasi:");
        Console.WriteLine("1. Barcha testlar (Construction, InMemory, PostgreSQL, Concurrency)");
        Console.WriteLine("2. Faqat PostgreSQL Real DB testi (500 iters)");
        Console.WriteLine("3. BenchmarkDotNet: PostgreSQL Real DB Benchmarks");
        Console.WriteLine("4. BenchmarkDotNet: Query Construction Benchmarks");
        Console.WriteLine("5. BenchmarkDotNet: Query Execution Benchmarks (InMemory)");
        Console.WriteLine("6. Barcha BenchmarkDotNet testlari");
        Console.Write("Tanlang (1-6): ");
        var choice = Console.ReadLine()?.Trim();

        switch (choice)
        {
            case "2":
                FastDiagnosticRunner.RunPostgresExecutionSuite();
                break;
            case "3":
                BenchmarkRunner.Run<PostgresExecutionBenchmarks>();
                break;
            case "4":
                BenchmarkRunner.Run<ConstructionBenchmarks>();
                break;
            case "5":
                BenchmarkRunner.Run<ExecutionBenchmarks>();
                break;
            case "6":
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
                break;
            case "1":
            default:
                FastDiagnosticRunner.RunAll();
                break;
        }
    }
}

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BenchmarkBuddy;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        var repoDirectoryOption = new Option<DirectoryInfo>(
            aliases: ["--path", "-p"],
            getDefaultValue: () => new DirectoryInfo(Directory.GetCurrentDirectory()),
            description: "Path to the git repository (defaults to current directory)")
            { 
                IsRequired = false,
                Arity = ArgumentArity.ZeroOrOne 
            };
        repoDirectoryOption.AddValidator(r =>
        {
            if (r.GetValueOrDefault<DirectoryInfo>() is { Exists: false })
                r.ErrorMessage = "Specified path does not exist.";
        });

        var baselineOption = new Option<string>(
            aliases: ["--baseline", "-b"],
            getDefaultValue: () => "main",
            description: "Branch or commit to compare against (defaults to 'main')");

        var thresholdOption = new Option<double>(
            aliases: ["--threshold", "-t"],
            getDefaultValue: () => 1.0,
            description: "Minimum absolute % difference in execution time to report (defaults to 1%)");

        var filterOption = new Option<string>(
            aliases: ["--filter", "-f"],
            getDefaultValue: () => "*",
            description: "Custom filter for which benchmarks to run. Same as the --filter option in BenchmarkDotNet.");

        var useFullNamesOption = new Option<bool>(
            aliases: ["--full-names"],
            getDefaultValue: () => false,
            description: "If true, the printed table will use the full name of the benchmark including its namespace.");

        var rootCmd = new RootCommand("Benchmark Buddy – compare BenchmarkDotNet results across git revisions")
        {
            repoDirectoryOption,
            baselineOption,
            thresholdOption,
            filterOption,
            useFullNamesOption,
        };

        rootCmd.SetHandler(async (repoDirectory, baseline, thresholdPercent, filter, useFullNames) =>
        {
            Console.WriteLine($"Welcome to Benchmark Buddy. {MOTDs[Random.Shared.Next(0, MOTDs.Length)]}");
            Console.WriteLine();

            var repoPath = repoDirectory.FullName;
            Console.WriteLine($"Repository: {repoPath}");

            var originalRef = (await RunGit("rev-parse --abbrev-ref HEAD", repoPath, writeOutput: false)).Trim();
            Console.WriteLine($"Current ref: `{originalRef}`");
            Console.WriteLine();

            // 1. Run benchmarks on HEAD
            var headResults = await CollectBenchmarks(repoPath, filter, useFullNames);
            Console.WriteLine();

            // 2. Stash
            var hadChanges = !string.IsNullOrWhiteSpace(await RunGit("status --porcelain", repoPath, writeOutput: false));
            if (hadChanges)
            {
                Console.WriteLine("Stashing working tree changes...");
                await RunGit("stash push -u -k -m \"benchmark-buddy\"", repoPath, writeOutput: true);
                Console.WriteLine();
            }

            // 3. Checkout baseline
            Console.WriteLine($"Checking out baseline `{baseline}`...");
            await RunGit($"checkout {baseline}", repoPath, writeOutput: true);
            Console.WriteLine();

            // 4. Run benchmarks on baseline
            var baselineResults = await CollectBenchmarks(repoPath, filter, useFullNames);
            Console.WriteLine();

            // 5. Restore workspace
            Console.WriteLine($"Returning to {originalRef}...");
            await RunGit($"checkout {originalRef}", repoPath, writeOutput: true);
            if (hadChanges)
            {
                Console.WriteLine();
                Console.WriteLine("Popping stash...");
                await RunGit("stash pop", repoPath, writeOutput: true);
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("=========================================");
            Console.WriteLine();
            Console.WriteLine();

            var executionTimeDiffs = new List<BenchmarkDiff>();
            int benchmarksBelowThresholdCount = 0;

            foreach (var (benchmarkName, headStats) in headResults)
            {
                // Only report benchmarks that are present in both baseline and head
                if (!baselineResults.TryGetValue(benchmarkName, out var baselineStats))
                    continue;


                var diffPercentage = (headStats.ExecutionTimeNanoseconds - baselineStats.ExecutionTimeNanoseconds) / baselineStats.ExecutionTimeNanoseconds * 100.0;

                if (Math.Abs(diffPercentage) >= thresholdPercent)
                    executionTimeDiffs.Add(new(benchmarkName, baselineStats, headStats));
                else
                    benchmarksBelowThresholdCount++;
            }

            var faster = executionTimeDiffs.Where(d => d.ExecutionTimeRatio < 1.0).OrderBy(d => d.ExecutionTimeRatio).ToList();
            var slower = executionTimeDiffs.Where(d => d.ExecutionTimeRatio >= 1.0).OrderByDescending(d => d.ExecutionTimeRatio).ToList();

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Faster benchmarks:");
            Console.WriteLine();
            PrintTable(faster, Measurement.ExecutionTime);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Slower benchmarks:");
            Console.WriteLine();
            PrintTable(slower, Measurement.ExecutionTime);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"{benchmarksBelowThresholdCount} benchmarks not shown because they were below the difference threshold of {thresholdPercent}%.");


            if (baselineResults.Values.Any(s => s.HasAllocatedMeasurement) && headResults.Values.Any(s => s.HasAllocatedMeasurement))
            {
                var allocationsDiffs = new List<BenchmarkDiff>();
                int benchmarksWithSameAllocation = 0;

                foreach (var (benchmarkName, headStats) in headResults)
                {
                    // Only report benchmarks that are present in both baseline and head
                    if (!baselineResults.TryGetValue(benchmarkName, out var baselineStats))
                        continue;

                    var diff = new BenchmarkDiff(benchmarkName, baselineStats, headStats);
                    if (diff.Baseline.HasAllocatedMeasurement && diff.Head.HasAllocatedMeasurement)
                    {
                        if (diff.AllocationsDifference == 0)
                            benchmarksWithSameAllocation++;
                        else
                            allocationsDiffs.Add(diff);
                    }
                }

                var lessAlloc = allocationsDiffs.Where(d => d.AllocationsDifference < 1.0).OrderBy(d => d.ExecutionTimeRatio).ToList(); // same order as the other tables for easy comparison
                var moreAlloc = allocationsDiffs.Where(d => d.AllocationsDifference >= 1.0).OrderByDescending(d => d.ExecutionTimeRatio).ToList();

                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("Benchmarks with less allocation:");
                Console.WriteLine();
                PrintTable(lessAlloc, Measurement.Allocations);

                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("Benchmarks with more allocation:");
                Console.WriteLine();
                PrintTable(moreAlloc, Measurement.Allocations);

                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine($"{benchmarksWithSameAllocation} benchmarks not shown because they had identical allocation.");
            }




            var benchmarksOnlyInHead = headResults.Where(kvp => !baselineResults.ContainsKey(kvp.Key)).Select(kvp => new BenchmarkResult(kvp.Key, kvp.Value)).ToArray();
            var benchmarksOnlyInBaseline = baselineResults.Where(kvp => !headResults.ContainsKey(kvp.Key)).Select(kvp => new BenchmarkResult(kvp.Key, kvp.Value)).ToArray();

            if (benchmarksOnlyInHead.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("Benchmarks only in head:");
                Console.WriteLine();
                PrintTable(benchmarksOnlyInHead);
            }

            if (benchmarksOnlyInBaseline.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("Benchmarks only in baseline:");
                Console.WriteLine();
                PrintTable(benchmarksOnlyInBaseline);
            }


            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("=========================================");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("And thus ends the output of Benchmark Buddy. Have a nice day!");

        }, repoDirectoryOption, baselineOption, thresholdOption, filterOption, useFullNamesOption);

        return await rootCmd.InvokeAsync(args);
    }


    static async Task<Dictionary<string, BenchmarkStats>> CollectBenchmarks(string repoPath, string filter, bool useFullNames)
    {
        var artifactsPath = Path.Combine(Path.GetTempPath(), "benchmark-buddy-temp");

        if (Directory.Exists(artifactsPath))
            Directory.Delete(artifactsPath, true);

        Directory.CreateDirectory(artifactsPath);


        var benchmarkProjectPaths = Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
            .Where(IsBenchmarkDotNetProject)
            .ToList();

        if (benchmarkProjectPaths.Count == 0)
        {
            Console.WriteLine("No BenchmarkDotNet projects found.");
            return new();
        }

        var results = new Dictionary<string, BenchmarkStats>();
        foreach (string benchmarkProjectPath in benchmarkProjectPaths)
        {
            Console.WriteLine($"Running benchmark project {Path.GetFileName(benchmarkProjectPath)}...");

            var startInfo = new ProcessStartInfo(
                "dotnet",
                $"run -c Release --filter {filter} --exporters json --artifacts \"{artifactsPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(benchmarkProjectPath) ?? repoPath
            };
            await RunProcess(startInfo, writeOutput: true);

            foreach (string jsonFile in Directory.GetFiles(artifactsPath, "*.json", SearchOption.AllDirectories))
            {
                var parsed = ParseBenchmarkJson(jsonFile);

                foreach (var (name, stats) in parsed)
                    results.Add(name, stats);
            }
        }
        return results;


        static bool IsBenchmarkDotNetProject(string csprojPath)
        {
            try
            {
                var doc = XDocument.Load(csprojPath);
                return doc.Descendants("PackageReference")
                    .Any(x => (x.Attribute("Include")?.Value?.Trim() ?? "")
                        .Equals("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse {csprojPath}: {ex.Message}");
                return false;
            }
        }
        Dictionary<string, BenchmarkStats> ParseBenchmarkJson(string path)
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("Benchmarks", out var benches))
                return new();

            var dict = new Dictionary<string, BenchmarkStats>();
            foreach (var bench in benches.EnumerateArray())
            {
                string name;
                if (useFullNames)
                {
                    name = bench.GetProperty("FullName").GetString() ?? "<unknown>";
                }
                else
                {
                    string typeName = bench.GetProperty("Type").GetString() ?? "<unknown>";
                    string methodTitle = bench.GetProperty("MethodTitle").GetString() ?? "<unknown>";
                    name = $"{typeName} - {methodTitle}";

                    string? parameters = bench.GetProperty("Parameters").GetString();
                    if (!string.IsNullOrWhiteSpace(parameters))
                        name += $" - {parameters}";
                }

                var meanNs = bench.GetProperty("Statistics").GetProperty("Mean").GetDouble();

                int? allocatedBytes = null;
                if (bench.TryGetProperty("Memory", out var memoryProperty))
                    allocatedBytes = memoryProperty.GetProperty("BytesAllocatedPerOperation").GetInt32();

                dict[name] = new BenchmarkStats(meanNs, allocatedBytes);
            }
            return dict;
        }
    }


    readonly record struct BenchmarkDiff(string BenchmarkName, BenchmarkStats Baseline, BenchmarkStats Head)
    {
        public double ExecutionTimeRatio => Head.ExecutionTimeNanoseconds / Baseline.ExecutionTimeNanoseconds;
        public int? AllocationsDifference => (Head.AllocatedBytes == null || Baseline.AllocatedBytes == null) ? null : Head.AllocatedBytes.Value - Baseline.AllocatedBytes.Value;
    }

    readonly record struct BenchmarkResult(string BenchmarkName, BenchmarkStats Result);

    readonly record struct BenchmarkStats(double ExecutionTimeNanoseconds, int? AllocatedBytes)
    {
        public string FormatExecutionTime()
        {
            string unit;
            double value;

            if (ExecutionTimeNanoseconds < 1_000)
            {
                unit = "ns";
                value = ExecutionTimeNanoseconds;
            }
            else if (ExecutionTimeNanoseconds < 1_000_000)
            {
                unit = "µs";
                value = ExecutionTimeNanoseconds / 1_000;
            }
            else if (ExecutionTimeNanoseconds < 1_000_000_000)
            {
                unit = "ms";
                value = ExecutionTimeNanoseconds / 1_000_000;
            }
            else
            {
                unit = "s";
                value = ExecutionTimeNanoseconds / 1_000_000_000;
            }

            string format = value < 1_000 ? "F2" : value < 100_000 ? "F1" : "F0";
            return $"{value.ToString(format)} {unit}";
        }

        public bool HasAllocatedMeasurement => AllocatedBytes != null;
        public string FormatAllocated()
        {
            if (AllocatedBytes is not int byteCount)
                return "<no measurement>";

            return byteCount + "B";
        }
    }

    enum Measurement { ExecutionTime, Allocations }

    static void PrintTable(IReadOnlyList<BenchmarkDiff> rows, Measurement measurement)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("<none>");
            return;
        }

        // Print a nice table that looks great in raw console monospace AND markdown.

        int longestBenchmarkName = rows.Select(r => r.BenchmarkName.Length).Max();
        int columnWidth_Name = Math.Max(longestBenchmarkName + 1, 30);
        const int columnWidth_Measurement = 12;
        const int columnWidth_Comparison = 12;

        string comparisonColumnTitle = measurement switch
        {
            Measurement.ExecutionTime => "Ratio",
            Measurement.Allocations => "Change",
            _ => throw new Exception()
        };

        Console.WriteLine($"| {"Benchmark".PadRight(columnWidth_Name)}| {"Baseline",-columnWidth_Measurement}| {"Head",-columnWidth_Measurement}| {comparisonColumnTitle,-columnWidth_Comparison}|");
        Console.WriteLine($"|{new string('-', columnWidth_Name)} |{new string('-', columnWidth_Measurement)} |{new string('-', columnWidth_Measurement)} |{new string('-', columnWidth_Comparison)} |");
        foreach (var row in rows)
        {
            string measurementBaseline = measurement switch
            {
                Measurement.ExecutionTime => row.Baseline.FormatExecutionTime(),
                Measurement.Allocations => row.Baseline.FormatAllocated(),
                _ => throw new Exception()
            };
            string measurementHead = measurement switch
            {
                Measurement.ExecutionTime => row.Head.FormatExecutionTime(),
                Measurement.Allocations => row.Head.FormatAllocated(),
                _ => throw new Exception()
            };
            string comparison = measurement switch
            {
                Measurement.ExecutionTime => row.ExecutionTimeRatio.ToString("F2"),
                Measurement.Allocations => row.AllocationsDifference?.ToString() ?? "--",
                _ => throw new Exception()
            };

            Console.WriteLine($"| {row.BenchmarkName.PadRight(columnWidth_Name)}|{measurementBaseline,columnWidth_Measurement} |{measurementHead,columnWidth_Measurement} |{comparison,columnWidth_Comparison} |");
        }
    }

    static void PrintTable(IReadOnlyList<BenchmarkResult> rows)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("<none>");
            return;
        }

        bool printAllocations = rows.Any(r => r.Result.HasAllocatedMeasurement);

        // Print a nice table that looks great in raw console monospace AND markdown.

        int longestBenchmarkName = rows.Select(r => r.BenchmarkName.Length).Max();
        int columnWidth_Name = Math.Max(longestBenchmarkName + 1, 30);
        const int columnWidth_Times = 12;
        const int columnWidth_Allocated = 12;

        string header = $"| {"Benchmark".PadRight(columnWidth_Name)}| {"Time",-columnWidth_Times}|";
        string line = $"|{new string('-', columnWidth_Name)} |{new string('-', columnWidth_Times)} |";

        if (printAllocations)
        {
            header += $" {"Allocated",-columnWidth_Allocated}";
            line += $"{new string('-', columnWidth_Allocated)} |";
        }

        Console.WriteLine(header);
        Console.WriteLine(line);
        foreach (var row in rows)
        {
            string rowText = $"| {row.BenchmarkName.PadRight(columnWidth_Name)}|{row.Result.FormatExecutionTime(),columnWidth_Times} |";
            if (printAllocations)
                rowText += $"{row.Result.FormatAllocated(),columnWidth_Allocated} |";

            Console.WriteLine(rowText);
        }
    }



    static async Task<string> RunGit(string arguments, string workingDir, bool writeOutput)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir
        };
        return await RunProcess(psi, writeOutput);
    }

    static async Task<string> RunProcess(ProcessStartInfo psi, bool writeOutput)
    {
        if (writeOutput)
            Console.WriteLine();
        int updateLine = Console.CursorTop - 1;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start process");

        var stdoutLines = new List<string>();
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                stdoutLines.Add(line);

                if (!writeOutput)
                    continue;

                // Move cursor to the reserved line and clear it
                Console.SetCursorPosition(0, updateLine);
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, updateLine);

                // Write the new line, truncated to fit the console width
                if (line.Length > Console.WindowWidth)
                    Console.Write(line.Substring(0, Console.WindowWidth - 1));
                else
                    Console.Write(line);
            }
        });

        var stderr = await proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, proc.WaitForExitAsync());

        if (writeOutput)
        {
            // After process is done, clear and print "done!"
            Console.SetCursorPosition(0, updateLine);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, updateLine);
            Console.Write("done!");
            Console.WriteLine();
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Process '{psi.FileName} {psi.Arguments}' exited with code {proc.ExitCode}: {stderr}");

        return string.Join(Environment.NewLine, stdoutLines);
    }


    static readonly string[] MOTDs = 
    [
        "Remember, the real Benchmark Buddy was the friends we made along the way.",
        "Life is more fun when you put things on top of your head.",
        "Why not Zoidberg?",
        "I used to think that orthopedic inserts were not for me, but I stand corrected.",
        "Anything is Turing-complete if you're patient enough!",
        "Knowledge weighs nothing; carry all you can!",
        "Sufficiently crude magic is indistinguishable from technology.",
        "What's the DEAL with linear time?",
        "Do you think William Shakespeare ever picked up a spear and shook it?",
        "I am kinda interested in everything that is unknown to me",
        "Why are you the way that you are?",
        "Remember: whatever happens, at the end of the day, it's night.",
        "Somehow, Benchmark Buddy returned",
        "I just learned about recency bias and it's my favorite thing ever",
        "Give a man a fish, just because it is a nice thing to do.",
        "How am I supposed to Live Laugh Love under these conditions?",
        "Meow meow meow meow",
        "A monad is just a monoid in the category of endofunctors!",

        "Please keep in mind that the universe will be cold and dead for infinitely longer than it will be warm and lifebearing",
        "You will always struggle with not feeling productive until you accept that your own joy can be something you produce.",
        "And if my only “career goal” is to simply eat a brie grilled cheese with fig jam and sit with my friends in a meadow and read poetry? What then?",
        "What if everything I believe is wrong?",
        "1984 is my favorite book, I think everybody should be forced to read it.",
        "I just checked my privilege... it looks fine to me!",
        "As an AI language model, I cannot maintain an erection.",
        "They have played us for absolute fools",
        "Pee pee poo poo",
        "Feels a little too convenient that an odyssey would happen to a guy named Odysseus",
        "We add more genders every time you complain",

        "You must be more critical of that which you cherish than of that which you disdain.",
        "Life is hard and then you die.",
        "By convention there is sweetness, by convention bitterness, by convention color, in reality only atoms and the void.",
        "Is the system going to flatten you out and deny you your humanity, or are you going to be able to make use of the system to the attainment of human purposes?",
    ];
}

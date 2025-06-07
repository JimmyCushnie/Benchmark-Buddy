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
            description: "Minimum absolute % difference to report (defaults to 1%)");

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
                Console.WriteLine("Popping stash...");
                await RunGit("stash pop", repoPath, writeOutput: true);
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("=========================================");
            Console.WriteLine();
            Console.WriteLine();

            var diffs = new List<BenchmarkResults>();
            int benchmarksBelowThresholdCount = 0;

            foreach (var (benchmarkName, headStats) in headResults)
            {
                // Only report benchmarks that are present in both baseline and head
                if (!baselineResults.TryGetValue(benchmarkName, out var baselineStats))
                    continue;


                var diffPercentage = (headStats.MeanNs - baselineStats.MeanNs) / baselineStats.MeanNs * 100.0;

                if (Math.Abs(diffPercentage) >= thresholdPercent)
                    diffs.Add(new(benchmarkName, baselineStats, headStats));
                else
                    benchmarksBelowThresholdCount++;
            }

            var better = diffs.Where(d => d.Ratio < 1.0).OrderBy(d => d.Ratio).ToList();
            var worse = diffs.Where(d => d.Ratio >= 1.0).OrderByDescending(d => d.Ratio).ToList();

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Faster benchmarks:");
            Console.WriteLine();
            PrintTable(better);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Slower benchmarks:");
            Console.WriteLine();
            PrintTable(worse);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"{benchmarksBelowThresholdCount} benchmarks not shown because they were below the difference threshold of {thresholdPercent}%.");
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
                    results[name] = stats;
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
                }

                var mean = bench.GetProperty("Statistics").GetProperty("Mean").GetDouble();
                dict[name] = new BenchmarkStats(mean);
            }
            return dict;
        }
    }


    readonly record struct BenchmarkResults(string BenchmarkName, BenchmarkStats Baseline, BenchmarkStats Head)
    {
        public double Ratio => Head.MeanNs / Baseline.MeanNs;
    }

    readonly record struct BenchmarkStats(double MeanNs)
    {
        public string FormatTime()
        {
            string unit;
            double value;

            if (MeanNs < 1_000)
            {
                unit = "ns";
                value = MeanNs;
            }
            else if (MeanNs < 1_000_000)
            {
                unit = "µs";
                value = MeanNs / 1_000;
            }
            else if (MeanNs < 1_000_000_000)
            {
                unit = "ms";
                value = MeanNs / 1_000_000;
            }
            else
            {
                unit = "s";
                value = MeanNs / 1_000_000_000;
            }

            string format = value < 1_000 ? "F2" : value < 100_000 ? "F1" : "F0";
            return $"{value.ToString(format)} {unit}";
        }
    }

    static void PrintTable(List<BenchmarkResults> rows)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("<none>");
            return;
        }

        // Print a nice table that looks great in raw console monospace AND markdown.

        int longestBenchmarkName = rows.Select(r => r.BenchmarkName.Length).Max();
        int columnWidth_Name = Math.Max(longestBenchmarkName + 1, 30);
        const int columnWidth_Times = 10;
        const int columnWidth_Ratio = 10;

        Console.WriteLine($"| {"Benchmark".PadRight(columnWidth_Name)}| {"Baseline",-columnWidth_Times}| {"Head",-columnWidth_Times}| {"Ratio",-columnWidth_Ratio}|");
        Console.WriteLine($"|{new string('-', columnWidth_Name)} |{new string('-', columnWidth_Times)}:|{new string('-', columnWidth_Times)}:|{new string('-', columnWidth_Ratio)}:|");
        foreach (var row in rows)
        {
            Console.WriteLine($"| {row.BenchmarkName.PadRight(columnWidth_Name)}|{row.Baseline.FormatTime().PadLeft(columnWidth_Times)} |{row.Head.FormatTime().PadLeft(columnWidth_Times)} |{row.Ratio,columnWidth_Ratio:F2} |");
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
    {
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
    };
}

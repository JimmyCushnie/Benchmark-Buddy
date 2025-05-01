# Benchmark Buddy

Benchmark Buddy is an automation utility for [BenchmarkDotNet](https://benchmarkdotnet.org/). It compares the results of your benchmarks between two different git revisions, so you can quickly check what effect your changes have on performance.

I use Benchmark Buddy to check for performance regressions before merging PRs. It is also useful for validating performance improvements.

Here's an example output:

> Faster benchmarks:
>
> | Benchmark                                                |  Baseline |      Head | Ratio |
> | -------------------------------------------------------- | --------: | --------: | ----: |
> | StructureModificationBenchmarks - ModifyHashableData     | 290.79 ns |  11.20 ns |  0.04 |
> | SerializationBenchmarks - SerializeStructureData_Large   |   3.08 µs | 258.12 ns |  0.08 |
> | SerializationBenchmarks - DeserializeStructureData_Large |   4.50 µs | 548.17 ns |  0.12 |
> | StructureModificationBenchmarks - MoveChild              | 285.22 ns |  42.67 ns |  0.15 |
> | StructureModificationBenchmarks - DeleteChild            | 278.10 ns |  55.57 ns |  0.20 |
> | SerializationBenchmarks - DeserializeStructureHash       |   3.44 ns |   1.07 ns |  0.31 |
> | StructureModificationBenchmarks - AddChild               | 340.44 ns | 109.58 ns |  0.32 |
> | HashingBenchmarks - HashStructureData_Large              |   8.82 µs |   5.80 µs |  0.66 |
> | SerializationBenchmarks - DeserializeStructureData_Small |  78.52 ns |  63.24 ns |  0.81 |
> | HashingBenchmarks - HashStructureData_Small              | 213.72 ns | 194.30 ns |  0.91 |
>
>
> Slower benchmarks:
>
> | Benchmark                                              | Baseline |     Head | Ratio |
> | ------------------------------------------------------ | -------: | -------: | ----: |
> | SerializationBenchmarks - SerializeStructureData_Small | 39.80 ns | 60.04 ns |  1.51 |
> | SerializationBenchmarks - SerializeStructureHash       |  2.66 ns |  3.11 ns |  1.17 |
>
> 19 benchmarks not shown because they were below the difference threshold of 1%.



## Making your project compatible

Your project folder should:

- Be a git repo.
- Contain at least one `.csproj` file with a package reference to `BenchmarkDotNet`.

Your BenchmarkDotNet project(s) should:

* Use [BenchmarkSwitcher](https://benchmarkdotnet.org/articles/guides/how-to-run.html#benchmarkswitcher) to run the benchmarks, so that when Benchmark Buddy passes CLI arguments to the program they work as intended.
* Export the results as JSON, so that Benchmark Buddy can find and parse them. BDN does this by default, but if you're using a custom config, you'll need to do `config.AddExporter(JsonExporter.Default)`.



## Usage

Checkout the revision you want to measure against the baseline, then run the program in your repo folder. If you need more control (i.e. to choose a different baseline revision than the default of `main`), you can view a list of command-line options with `--help`.

As always with benchmarking, you'll get the most reliable results if you close everything else on your computer and (if applicable) disable power saving mode before running the benchmarks, then don't touch your computer until they're done.

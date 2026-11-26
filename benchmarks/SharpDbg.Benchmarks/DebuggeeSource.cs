namespace SharpDbg.Benchmarks;

/// <summary>The debuggee's source file and the lines in it that the benchmarks use.</summary>
public static class DebuggeeSource
{
	public static string ProgramPath => RepoPaths.Join("benchmarks", "BenchmarkDebuggee", "Program.cs");
	public static string ExecutablePath => RepoPaths.Join("artifacts", "bin", "BenchmarkDebuggee", "debug", OperatingSystem.IsWindows() ? "BenchmarkDebuggee.exe" : "BenchmarkDebuggee");

	// Program.cs:25 - the first line Launch runs
	public static readonly SourceLine LaunchBreakpoint = new(25, "Sink = 1;");

	// Program.cs:34-35 - consecutive calls in StepLoop's loop
	public static readonly SourceLine StepStart = new(34, "counter = Add(counter, 1);");
	public static readonly SourceLine StepOverTarget = new(35, "counter = Add(counter, 2);");

	// Program.cs:42-45 - the body of Add, braces included (debuggers differ on whether a step in stops on the opening brace)
	public static readonly LineRange AddBody = new(42, 45);
	// Program.cs:43 - the first statement in Add
	public static readonly SourceLine StepOutStart = new(43, "var sum = a + b;");
	// Program.cs:33-37 - the body of StepLoop's loop, where a step out of Add returns to
	public static readonly LineRange StepLoopBody = new(33, 37);

	// Program.cs:64 - the last line of Recurse, reached 21 frames deep, with locals in scope
	public static readonly SourceLine InspectBreakpoint = new(64, "Sink = total + text.Length;");

	// Program.cs:73 - inside Conditional's inner loop of 1000
	public static readonly SourceLine ConditionalBreakpoint = new(73, "Sink = i;");

	// Program.cs:91 - the last line of PlainLocals, whose locals need no code run in the debuggee to show
	public static readonly SourceLine PlainInspectBreakpoint = new(91, "Sink = total + text.Length; // plain inspect breakpoint");

	/// <summary>Fails if any line above no longer holds the code a benchmark expects, e.g. after Program.cs was edited.</summary>
	public static void Verify()
	{
		if (File.Exists(ExecutablePath) is false) throw new FileNotFoundException("The benchmark debuggee has not been built", ExecutablePath);
		var lines = File.ReadAllLines(ProgramPath);
		foreach (var line in (SourceLine[])[LaunchBreakpoint, StepStart, StepOverTarget, StepOutStart, InspectBreakpoint, ConditionalBreakpoint, PlainInspectBreakpoint])
		{
			var actual = line.Line <= lines.Length ? lines[line.Line - 1] : "<past the end of the file>";
			if (actual.Contains(line.ExpectedText, StringComparison.Ordinal) is false)
			{
				throw new InvalidOperationException($"{ProgramPath}:{line.Line} should contain '{line.ExpectedText}' but is '{actual.Trim()}'. Update the line numbers in {nameof(DebuggeeSource)}.cs.");
			}
		}
	}
}

public readonly record struct SourceLine(int Line, string ExpectedText);

public readonly record struct LineRange(int First, int Last)
{
	public bool Contains(int line) => line >= First && line <= Last;
	public override string ToString() => First == Last ? $"{First}" : $"{First}-{Last}";
}

public static class RepoPaths
{
	public static string Root { get; } = FindRoot();

	public static string Join(params string[] parts) => Path.Join([Root, .. parts]);

	private static string FindRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			var dotGit = Path.Join(directory, ".git");
			if (Directory.Exists(dotGit) || File.Exists(dotGit)) return directory;
			directory = Path.GetDirectoryName(directory);
		}
		throw new InvalidOperationException("Could not find the repository root");
	}
}

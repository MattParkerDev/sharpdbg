namespace SharpDbg.Benchmarks;

/// <summary>The debuggee's source file and the lines in it that the benchmarks use.</summary>
public static class DebuggeeSource
{
	public static string ProgramPath => RepoPaths.Join("benchmarks", "BenchmarkDebuggee", "Program.cs");
	public static string ExecutablePath => RepoPaths.Join("artifacts", "bin", "BenchmarkDebuggee", "debug", OperatingSystem.IsWindows() ? "BenchmarkDebuggee.exe" : "BenchmarkDebuggee");

	// Program.cs:26 - the first line Launch runs
	public static readonly SourceLine LaunchBreakpoint = new(26, "Sink = 1;");

	// Program.cs:35-36 - consecutive calls in StepLoop's loop
	public static readonly SourceLine StepStart = new(35, "counter = Add(counter, 1);");
	public static readonly SourceLine StepOverTarget = new(36, "counter = Add(counter, 2);");

	// Program.cs:43-46 - the body of Add, braces included (debuggers differ on whether a step in stops on the opening brace)
	public static readonly LineRange AddBody = new(43, 46);
	// Program.cs:44 - the first statement in Add
	public static readonly SourceLine StepOutStart = new(44, "var sum = a + b;");
	// Program.cs:34-38 - the body of StepLoop's loop, where a step out of Add returns to
	public static readonly LineRange StepLoopBody = new(34, 38);

	// Program.cs:65 - the last line of Recurse, reached 21 frames deep, with locals in scope
	public static readonly SourceLine InspectBreakpoint = new(65, "Sink = total + text.Length;");

	// Program.cs:74 - inside Conditional's inner loop of 1000
	public static readonly SourceLine ConditionalBreakpoint = new(74, "Sink = i;");

	// Program.cs:92 - the last line of PlainLocals, whose locals need no code run in the debuggee to show
	public static readonly SourceLine PlainInspectBreakpoint = new(92, "Sink = total + text.Length; // plain inspect breakpoint");

	// Program.cs:133 - the only line of DisplayScenario.Stop, with arrays of objects that have a [DebuggerDisplay] in scope
	public static readonly SourceLine DisplayBreakpoint = new(133, "Scenarios.Sink = identifiers.Length + expressions.Length; // display breakpoint");

	/// <summary>Fails if any line above no longer holds the code a benchmark expects, e.g. after Program.cs was edited.</summary>
	public static void Verify()
	{
		if (File.Exists(ExecutablePath) is false) throw new FileNotFoundException("The benchmark debuggee has not been built", ExecutablePath);
		var lines = File.ReadAllLines(ProgramPath);
		foreach (var line in (SourceLine[])[LaunchBreakpoint, StepStart, StepOverTarget, StepOutStart, InspectBreakpoint, ConditionalBreakpoint, PlainInspectBreakpoint, DisplayBreakpoint])
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

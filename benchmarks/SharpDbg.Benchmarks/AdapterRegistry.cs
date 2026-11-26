using System.Text.Json;

namespace SharpDbg.Benchmarks;

/// <param name="Name">Shown in the results' Adapter column.</param>
/// <param name="Path">The adapter executable, which speaks DAP over stdio.</param>
/// <param name="Args">Command line arguments, usually <c>--interpreter=vscode</c>.</param>
/// <param name="Version">Shown in the reports. When not given, the executable's product version is used (a proxy's version, for a proxy).</param>
public sealed record AdapterDefinition(string Name, string Path, string[] Args, string? Version = null);

/// <summary>
/// The adapters to benchmark, read from <c>adapters.json</c> next to this project (see <c>adapters.example.json</c>).
/// Without that file, only the sharpdbg built from this repository is benchmarked.
/// </summary>
public static class AdapterRegistry
{
	public static string ConfigPath => RepoPaths.Join("benchmarks", "SharpDbg.Benchmarks", "adapters.json");

	private static IReadOnlyList<AdapterDefinition>? _all;
	public static IReadOnlyList<AdapterDefinition> All => _all ??= Load();

	/// <summary>Names given with <c>--adapters</c>; null for all of them.</summary>
	public static HashSet<string>? Filter { get; set; }

	public static IEnumerable<string> SelectedNames => All.Select(a => a.Name).Where(name => Filter is null || Filter.Contains(name));

	public static AdapterDefinition Get(string name) => All.Single(a => a.Name == name);

	public static AdapterDefinition BuiltSharpDbg => new("sharpdbg",
		RepoPaths.Join("artifacts", "bin", "SharpDbg.Cli", ThisConfiguration, OperatingSystem.IsWindows() ? "SharpDbg.Cli.exe" : "SharpDbg.Cli"),
		["--interpreter=vscode"]);

#if DEBUG
	private const string ThisConfiguration = "debug";
#else
	private const string ThisConfiguration = "release";
#endif

	private static IReadOnlyList<AdapterDefinition> Load()
	{
		if (File.Exists(ConfigPath) is false) return [BuiltSharpDbg];
		var config = JsonSerializer.Deserialize<AdapterConfig>(File.ReadAllText(ConfigPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
			?? throw new InvalidOperationException($"{ConfigPath} is empty");
		return config.Adapters
			.Select(a => a.Path is "built" ? BuiltSharpDbg with { Name = a.Name, Args = a.Args ?? BuiltSharpDbg.Args, Version = a.Version } : new AdapterDefinition(a.Name, a.Path, a.Args ?? [], a.Version))
			.ToList();
	}

	public static void Validate()
	{
		var unknown = Filter?.Except(All.Select(a => a.Name)).ToList();
		if (unknown is { Count: > 0 }) throw new InvalidOperationException($"Unknown adapter(s) '{string.Join("', '", unknown)}'. Configured: {string.Join(", ", All.Select(a => a.Name))}");
		foreach (var adapter in All.Where(a => SelectedNames.Contains(a.Name)))
		{
			if (File.Exists(adapter.Path) is false) throw new FileNotFoundException($"Adapter '{adapter.Name}' not found", adapter.Path);
		}
	}

	private sealed record AdapterConfig(List<AdapterConfigEntry> Adapters);
	private sealed record AdapterConfigEntry(string Name, string Path, string[]? Args, string? Version);
}

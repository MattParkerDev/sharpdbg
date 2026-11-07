using System.Collections.Immutable;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Manages breakpoint tracking and mapping
/// </summary>
public class BreakpointManager
{
	private int _nextBreakpointId = 1;
	private readonly Dictionary<int, BreakpointInfo> _breakpoints = new();
	private readonly Dictionary<string, List<int>> _breakpointsByFile = new();
	private readonly Lock _lock = new();

	public const string NotProcessedMessage = "Breakpoint has not been processed by the debugger.";

	/// <summary>
	/// Where a breakpoint stands in binding to code. A breakpoint is verified exactly when it is bound
	/// </summary>
	/// <remarks>
	/// <code>
	/// Unbound ──bind──▶ SourceBound ──module unloaded──▶ Unbound
	/// Unbound ──bind──▶ FunctionBound ──bind in another module──▶ FunctionBound
	///                   FunctionBound ──last bound module unloaded──▶ Unbound
	/// Rejected: terminal
	/// </code>
	/// </remarks>
	public closed record BreakpointBinding
	{
		/// <summary>Not bound to code yet. Binding is tried again whenever a module with symbols loads</summary>
		public sealed record Unbound(string Message) : BreakpointBinding;

		/// <summary>Can never be bound, e.g. its condition is not supported. Never tried again</summary>
		public sealed record Rejected(string Message) : BreakpointBinding;

		/// <summary>A source breakpoint bound to the location the PDB resolved its line to</summary>
		public sealed record SourceBound(ICorDebugFunctionBreakpoint CorBreakpoint, CORDB_ADDRESS ModuleBaseAddress, ModuleMetadataReader.ResolvedBreakpoint Resolved) : BreakpointBinding;

		/// <summary>A function breakpoint bound to every matching method in the loaded modules. Always has at least one binding</summary>
		public sealed record FunctionBound(ImmutableArray<FunctionBreakpointBinding> Bindings) : BreakpointBinding
		{
			public ImmutableArray<FunctionBreakpointBinding> Bindings { get; } = Bindings.IsDefaultOrEmpty
				? throw new ArgumentException("A bound function breakpoint must have at least one binding", nameof(Bindings))
				: Bindings;
		}
	}

	public class BreakpointInfo
	{
		public int Id { get; init; }
		public string FilePath { get; init; } = string.Empty;
		/// <summary>The line the client asked for, which binding resolves to the nearest line with code</summary>
		public int RequestedLine { get; init; }
		public int? RequestedColumn { get; init; }
		public string? FunctionName { get; init; }
		public bool IsFunctionBreakpoint => FunctionName is not null;

		public BreakpointBinding Binding
		{
			get;
			set
			{
				if (field is BreakpointBinding.Rejected) throw new InvalidOperationException($"Breakpoint {Id} was rejected, so it cannot change state");
				if (value is BreakpointBinding.SourceBound && IsFunctionBreakpoint || value is BreakpointBinding.FunctionBound && IsFunctionBreakpoint is false)
					throw new InvalidOperationException($"Breakpoint {Id} cannot be bound as a {(IsFunctionBreakpoint ? "source" : "function")} breakpoint");
				field = value;
			}
		} = new BreakpointBinding.Unbound(NotProcessedMessage);

		public bool Verified => Binding is BreakpointBinding.SourceBound or BreakpointBinding.FunctionBound;

		public string? Message => Binding switch
		{
			BreakpointBinding.Unbound unbound => unbound.Message,
			BreakpointBinding.Rejected rejected => rejected.Message,
			BreakpointBinding.SourceBound or BreakpointBinding.FunctionBound => null
		};

		// The resolved location once bound, otherwise the requested one
		public int Line => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.StartLine : RequestedLine;
		public int? Column => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.StartColumn : RequestedColumn;
		public int? EndLine => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.EndLine : null;
		public int? EndColumn => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.EndColumn : null;

		public IEnumerable<ICorDebugFunctionBreakpoint> CorBreakpoints => Binding switch
		{
			BreakpointBinding.SourceBound bound => [bound.CorBreakpoint],
			BreakpointBinding.FunctionBound bound => bound.Bindings.Select(binding => binding.CorBreakpoint),
			BreakpointBinding.Unbound or BreakpointBinding.Rejected => []
		};

		/// <summary>Conditional expression to evaluate when breakpoint is hit</summary>
		public string? Condition { get; set; }

		/// <summary>Hit count condition (e.g., ">=10", "==5", "%3")</summary>
		public string? HitCondition { get; set; }

		/// <summary>Current hit count for this breakpoint</summary>
		public int HitCount { get; set; }
	}

	public record FunctionBreakpointBinding(
		ICorDebugFunctionBreakpoint CorBreakpoint,
		CORDB_ADDRESS ModuleBaseAddress,
		int MethodToken,
		ModuleMetadataReader.ResolvedBreakpoint Source);

	/// <summary>
	/// Create a new breakpoint
	/// </summary>
	public BreakpointInfo CreateBreakpoint(string filePath, int line, int? column = null, string? condition = null, string? hitCondition = null)
	{
		lock (_lock)
		{
			var id = _nextBreakpointId++;
			if (string.IsNullOrWhiteSpace(condition)) condition = null;
			if (string.IsNullOrWhiteSpace(hitCondition)) hitCondition = null;
			var bp = new BreakpointInfo
			{
				Id = id,
				FilePath = filePath,
				RequestedLine = line,
				RequestedColumn = column,
				Condition = condition,
				HitCondition = hitCondition,
				HitCount = 0
			};

			_breakpoints[id] = bp;

			if (!_breakpointsByFile.ContainsKey(filePath))
			{
				_breakpointsByFile[filePath] = [];
			}
			_breakpointsByFile[filePath].Add(id);

			return bp;
		}
	}

	public BreakpointInfo CreateFunctionBreakpoint(string functionName, string? condition = null, string? hitCondition = null)
	{
		lock (_lock)
		{
			var bp = new BreakpointInfo
			{
				Id = _nextBreakpointId++,
				FunctionName = functionName,
				Condition = string.IsNullOrWhiteSpace(condition) ? null : condition,
				HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition
			};
			_breakpoints[bp.Id] = bp;
			return bp;
		}
	}

	public List<BreakpointInfo> GetFunctionBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.Where(bp => bp.IsFunctionBreakpoint).ToList();
		}
	}

	public void ClearFunctionBreakpoints()
	{
		lock (_lock)
		{
			foreach (var id in _breakpoints.Values.Where(bp => bp.IsFunctionBreakpoint).Select(bp => bp.Id).ToList())
			{
				_breakpoints.Remove(id);
			}
		}
	}

	/// <summary>
	/// Get breakpoint by ID
	/// </summary>
	public BreakpointInfo? GetBreakpoint(int id)
	{
		lock (_lock)
		{
			return _breakpoints.TryGetValue(id, out var bp) ? bp : null;
		}
	}

	/// <summary>
	/// Get all breakpoints for a file
	/// </summary>
	public List<BreakpointInfo> GetBreakpointsForFile(string filePath)
	{
		lock (_lock)
		{
			if (_breakpointsByFile.TryGetValue(filePath, out var ids))
			{
				return ids.Select(id => _breakpoints[id]).ToList();
			}
			return [];
		}
	}

	/// <summary>
	/// Clear all breakpoints for a file
	/// </summary>
	public void ClearBreakpointsForFile(string filePath)
	{
		lock (_lock)
		{
			if (_breakpointsByFile.TryGetValue(filePath, out var ids))
			{
				foreach (var id in ids)
				{
					_breakpoints.Remove(id);
				}
				_breakpointsByFile.Remove(filePath);
			}
		}
	}

	/// <summary>
	/// Find breakpoint by ClrDebug breakpoint
	/// </summary>
	public BreakpointInfo? FindByCorBreakpoint(ICorDebugFunctionBreakpoint corBreakpoint)
	{
		lock (_lock)
		{
			return _breakpoints.Values.FirstOrDefault(bp => bp.CorBreakpoints.Contains(corBreakpoint));
		}
	}

	/// <summary>
	/// Find all breakpoints sharing a ClrDebug breakpoint, in the order they were created
	/// </summary>
	public List<BreakpointInfo> FindAllByCorBreakpoint(ICorDebugFunctionBreakpoint corBreakpoint)
	{
		lock (_lock)
		{
			return _breakpoints.Values
				.Where(bp => bp.CorBreakpoints.Contains(corBreakpoint))
				.OrderBy(bp => bp.Id)
				.ToList();
		}
	}

	/// <summary>
	/// The ClrDebug breakpoint of a source breakpoint already bound to this location, if any
	/// </summary>
	public ICorDebugFunctionBreakpoint? FindSourceCorBreakpointAt(CORDB_ADDRESS moduleBaseAddress, int methodToken, int ilOffset)
	{
		lock (_lock)
		{
			return _breakpoints.Values
				.Select(bp => bp.Binding)
				.OfType<BreakpointBinding.SourceBound>()
				.FirstOrDefault(bound => bound.ModuleBaseAddress == moduleBaseAddress && bound.Resolved.MethodToken == methodToken && bound.Resolved.ILOffset == ilOffset)
				?.CorBreakpoint;
		}
	}

	/// <summary>
	/// Get the source breakpoints still waiting to be bound. Rejected breakpoints are never bound, so are not included
	/// </summary>
	public List<BreakpointInfo> GetUnboundSourceBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.Where(bp => bp is { IsFunctionBreakpoint: false, Binding: BreakpointBinding.Unbound }).ToList();
		}
	}

	/// <summary>
	/// Get all breakpoints
	/// </summary>
	public List<BreakpointInfo> GetAllBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.ToList();
		}
	}

	/// <summary>
	/// Remove a breakpoint by id
	/// </summary>
	public bool RemoveBreakpoint(int id)
	{
		lock (_lock)
		{
			if (!_breakpoints.TryGetValue(id, out var bp)) return false;
			_breakpoints.Remove(id);
			if (_breakpointsByFile.TryGetValue(bp.FilePath, out var ids))
			{
				ids.Remove(id);
				if (ids.Count == 0) _breakpointsByFile.Remove(bp.FilePath);
			}
			return true;
		}
	}

	/// <summary>
	/// Clear all breakpoints
	/// </summary>
	public void Clear()
	{
		lock (_lock)
		{
			_breakpoints.Clear();
			_breakpointsByFile.Clear();
			_nextBreakpointId = 1;
		}
	}
}

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
	private readonly Dictionary<string, List<int>> _functionBreakpoints = new();
	private readonly Lock _lock = new();

	public class BreakpointInfo
	{
		public int Id { get; set; }
		public string FilePath { get; set; } = string.Empty;
		public int Line { get; set; }
		public int? Column { get; set; }
		public int EndLine { get; set; }
		public int? EndColumn { get; set; }
		public bool Verified { get; set; }
		public ICorDebugFunctionBreakpoint? CorBreakpoint { get; set; }
		public string? Message { get; set; }
		public SymbolReader.ResolvedBreakpoint? ResolvedBreakpointFromPdb { get; set; }
		public CORDB_ADDRESS? ModuleBaseAddress { get; set; }

		/// <summary>Function name for SetFunctionBreakpoints support</summary>
		public string? FunctionName { get; set; }

		/// <summary>Conditional expression to evaluate when breakpoint is hit</summary>
		public string? Condition { get; set; }

		/// <summary>Hit count condition (e.g., ">=10", "==5", "%3")</summary>
		public string? HitCondition { get; set; }

		/// <summary>Current hit count for this breakpoint</summary>
		public int HitCount { get; set; }
	}

	/// <summary>
	/// Create a new source breakpoint
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
				Line = line,
				Column = column,
				Verified = false,
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

	/// <summary>
	/// Create a new function breakpoint
	/// </summary>
	public BreakpointInfo CreateFunctionBreakpoint(string functionName, string? condition = null, string? hitCondition = null)
	{
		lock (_lock)
		{
			var id = _nextBreakpointId++;
			if (string.IsNullOrWhiteSpace(condition)) condition = null;
			if (string.IsNullOrWhiteSpace(hitCondition)) hitCondition = null;
			var bp = new BreakpointInfo
			{
				Id = id,
				FunctionName = functionName,
				FilePath = functionName, // use function name as file path for event routing compatibility
				Verified = false,
				Condition = condition,
				HitCondition = hitCondition,
				HitCount = 0
			};

			_breakpoints[id] = bp;

			if (!_functionBreakpoints.ContainsKey(functionName))
			{
				_functionBreakpoints[functionName] = [];
			}
			_functionBreakpoints[functionName].Add(id);

			return bp;
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
	/// Clear all function breakpoints
	/// </summary>
	public void ClearAllFunctionBreakpoints()
	{
		lock (_lock)
		{
			foreach (var (_, ids) in _functionBreakpoints)
			{
				foreach (var id in ids)
				{
					_breakpoints.Remove(id);
				}
			}
			_functionBreakpoints.Clear();
		}
	}

	/// <summary>
	/// Get all function breakpoints
	/// </summary>
	public List<BreakpointInfo> GetAllFunctionBreakpoints()
	{
		lock (_lock)
		{
			return _functionBreakpoints.Values.SelectMany(ids => ids).Select(id => _breakpoints[id]).ToList();
		}
	}

	/// <summary>
	/// Find breakpoint by ClrDebug breakpoint
	/// </summary>
	public BreakpointInfo? FindByCorBreakpoint(ICorDebugFunctionBreakpoint corBreakpoint)
	{
		lock (_lock)
		{
			return _breakpoints.Values.FirstOrDefault(bp => bp.CorBreakpoint == corBreakpoint);
		}
	}

	/// <summary>
	/// Get all pending (unverified) breakpoints
	/// </summary>
	public List<BreakpointInfo> GetPendingBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.Where(bp => !bp.Verified).ToList();
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
			if (bp.FunctionName is not null && _functionBreakpoints.TryGetValue(bp.FunctionName, out var fnIds))
			{
				fnIds.Remove(id);
				if (fnIds.Count == 0) _functionBreakpoints.Remove(bp.FunctionName);
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
			_functionBreakpoints.Clear();
			_nextBreakpointId = 1;
		}
	}
}

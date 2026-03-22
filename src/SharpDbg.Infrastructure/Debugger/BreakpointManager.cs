using ClrDebug;

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

	public class BreakpointInfo
	{
		public int Id { get; set; }
		public string FilePath { get; set; } = string.Empty;
		public int Line { get; set; }
		public bool Verified { get; set; }
		public CorDebugFunctionBreakpoint? CorBreakpoint { get; set; }
		public string? Message { get; set; }
		public int? ResolvedLine { get; set; }
		public int? ResolvedEndLine { get; set; }
		public int? MethodToken { get; set; }
		public int? ILOffset { get; set; }
		public CORDB_ADDRESS? ModuleBaseAddress { get; set; }

		/// <summary>Conditional expression to evaluate when breakpoint is hit</summary>
		public string? Condition { get; set; }

		/// <summary>Hit count condition (e.g., ">=10", "==5", "%3")</summary>
		public string? HitCondition { get; set; }

		/// <summary>Current hit count for this breakpoint</summary>
		public int HitCount { get; set; }
	}

	/// <summary>
	/// Create a new breakpoint
	/// </summary>
	public BreakpointInfo CreateBreakpoint(string filePath, int line, string? condition = null, string? hitCondition = null)
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
				Verified = false,
				Condition = condition,
				HitCondition = hitCondition,
				HitCount = 0
			};

			_breakpoints[id] = bp;

			if (!_breakpointsByFile.ContainsKey(filePath))
			{
				_breakpointsByFile[filePath] = new List<int>();
			}
			_breakpointsByFile[filePath].Add(id);

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
			return new List<BreakpointInfo>();
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
			return _breakpoints.Values.FirstOrDefault(bp => bp.CorBreakpoint?.Raw == corBreakpoint);
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

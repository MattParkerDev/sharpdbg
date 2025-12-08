using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

/// <summary>
/// Manages breakpoint tracking and mapping
/// </summary>
public class BreakpointManager
{
    private int _nextBreakpointId = 1;
    private readonly Dictionary<int, BreakpointInfo> _breakpoints = new();
    private readonly Dictionary<string, List<int>> _breakpointsByFile = new();
    private readonly object _lock = new();

    public class BreakpointInfo
    {
        public int Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public bool Verified { get; set; }
        public CorDebugFunctionBreakpoint? CorBreakpoint { get; set; }
        public ICorDebugBreakpoint? RawBreakpoint { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Create a new breakpoint
    /// </summary>
    public BreakpointInfo CreateBreakpoint(string filePath, int line)
    {
        lock (_lock)
        {
            var id = _nextBreakpointId++;
            var bp = new BreakpointInfo
            {
                Id = id,
                FilePath = filePath,
                Line = line,
                Verified = false
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
    /// Update breakpoint with ClrDebug breakpoint
    /// </summary>
    public void SetCorBreakpoint(int id, CorDebugFunctionBreakpoint corBreakpoint)
    {
        lock (_lock)
        {
            if (_breakpoints.TryGetValue(id, out var bp))
            {
                bp.CorBreakpoint = corBreakpoint;
                bp.Verified = true;
            }
        }
    }

    /// <summary>
    /// Set breakpoint verification status
    /// </summary>
    public void SetVerified(int id, bool verified, string? message = null)
    {
        lock (_lock)
        {
            if (_breakpoints.TryGetValue(id, out var bp))
            {
                bp.Verified = verified;
                bp.Message = message;
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
    public BreakpointInfo? FindByCorBreakpoint(CorDebugFunctionBreakpoint corBreakpoint)
    {
        lock (_lock)
        {
            return _breakpoints.Values.FirstOrDefault(bp => bp.CorBreakpoint == corBreakpoint);
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

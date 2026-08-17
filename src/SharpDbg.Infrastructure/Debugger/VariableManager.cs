using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public enum StoredReferenceKind
{
	Scope,
	StackVariable,
	StaticClassVariable, // This reference was stored as a pseudo variable for the static members of a "StackVariable" class
	RawView,
	EnumerableResults,
}

public readonly record struct ThreadId
{
	public readonly int Value;
	public ThreadId() => throw new ArgumentException("ThreadId must be initialized with a valid value");
	public ThreadId(int value)
	{
		if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "ThreadId value must be greater than zero");
		Value = value;
	}
};
public readonly record struct FrameStackDepth
{
	public readonly int Value;
	public FrameStackDepth() => throw new ArgumentException("FrameStackDepth must be initialized with a valid value");
	public FrameStackDepth(int value)
	{
		if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "FrameStackDepth value must be zero or greater");
		Value = value;
	}
};
public record struct VariablesReference(StoredReferenceKind ReferenceKind, ICorDebugValue? ObjectValue, ThreadId ThreadId, FrameStackDepth FrameStackDepth, ICorDebugValue? DebuggerProxyInstance);
/// <summary>
/// Manages variable references for scopes and variables
/// </summary>
public class VariableManager
{
	private int _nextReference = 1;
	private readonly Dictionary<int, VariablesReference> _references = new();
	private readonly Lock _lock = new();

	/// <summary>
	/// Create a reference for an object
	/// </summary>
	public int CreateReference(VariablesReference obj)
	{
		lock (_lock)
		{
			var reference = _nextReference++;
			_references[reference] = obj;
			return reference;
		}
	}

	/// <summary>
	/// Get an object by reference
	/// </summary>
	public VariablesReference? GetReference(int reference)
	{
		lock (_lock)
		{
			if (_references.TryGetValue(reference, out var obj))
			{
				return obj;
			}
			return null;
		}
	}

	/// <summary>
	/// Clear all references
	/// </summary>
	public void ClearAndDisposeHandleValues()
	{
		lock (_lock)
		{
			// Distinct because if an ICorDebugHandleValue (result of property or eval) has static members, we store the same ICorDebugHandleValue as a 'Static Members' alias variable.
			// Meaning that without distinct, we would attempt to dispose the same ICorDebugHandleValue twice.
			var handleReferences = _references.Values.SelectMany(GetHandleValues).Distinct().ToList();
			handleReferences.ForEach(h => h.Dispose());
			_references.Clear();
			_nextReference = 1;
		}
	}

	public void ClearAndTryDisposeHandleValues()
	{
		lock (_lock)
		{
			// Distinct because if an ICorDebugHandleValue (result of property or eval) has static members, we store the same ICorDebugHandleValue as a 'Static Members' alias variable.
			// Meaning that without distinct, we would attempt to dispose the same ICorDebugHandleValue twice.
			var handleReferences = _references.Values.SelectMany(GetHandleValues).Distinct().ToList();
			handleReferences.ForEach(h => h.TryDispose());
			_references.Clear();
			_nextReference = 1;
		}
	}

	private static IEnumerable<ICorDebugHandleValue> GetHandleValues(VariablesReference r)
	{
		if (r.ObjectValue is ICorDebugHandleValue ov)
			yield return ov;

		if (r.DebuggerProxyInstance is ICorDebugHandleValue dp)
			yield return dp;
	}
}

namespace DotnetDbg.Infrastructure.Debugger;

/// <summary>
/// Manages variable references for scopes and variables
/// </summary>
public class VariableManager
{
    private int _nextReference = 1;
    private readonly Dictionary<int, object> _references = new();
    private readonly object _lock = new();

    /// <summary>
    /// Create a reference for an object
    /// </summary>
    public int CreateReference(object obj)
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
    public T? GetReference<T>(int reference) where T : class
    {
        lock (_lock)
        {
            if (_references.TryGetValue(reference, out var obj))
            {
                return obj as T;
            }
            return null;
        }
    }

    /// <summary>
    /// Clear all references
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _references.Clear();
            _nextReference = 1;
        }
    }
}

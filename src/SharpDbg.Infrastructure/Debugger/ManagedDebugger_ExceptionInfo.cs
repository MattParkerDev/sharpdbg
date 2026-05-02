using System;
using System.Collections.Generic;
using System.Linq;
using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
    // Minimal per-thread exception state stored when an exception callback occurs.
    private readonly Dictionary<int, ExceptionState> _threadExceptions = new();

    public record ExceptionState(string ExceptionId, string? Message, string? TypeName, string? FullTypeName, string? EvaluateName, string? StackTrace, CorDebugValue? ExceptionValue);

    public ExceptionState? GetExceptionInfoForThread(int threadId)
    {
        lock (_threadExceptions)
        {
            _threadExceptions.TryGetValue(threadId, out var state);
            return state;
        }
    }

    private void StoreExceptionForThread(int threadId, string? message, string? typeName, string? fullTypeName, string? evaluateName, string? stackTrace, CorDebugValue? exceptionValue)
    {
        var id = Guid.NewGuid().ToString();
        var state = new ExceptionState(id, message, typeName, fullTypeName, evaluateName, stackTrace, exceptionValue);
        lock (_threadExceptions)
        {
            _threadExceptions[threadId] = state;
        }

        // Log stored exception metadata for diagnostics
        try
        {
            _logger?.Invoke($"Stored exception for thread {threadId}: hasValue={(exceptionValue is not null)}, isHandle={(exceptionValue is CorDebugHandleValue)}");
        }
        catch
        {
            // ignore logging failures
        }
    }

    private void ClearAllThreadExceptions()
    {
        lock (_threadExceptions)
        {
            // Dispose any handle values if necessary
            foreach (var s in _threadExceptions.Values)
            {
                if (s.ExceptionValue is CorDebugHandleValue hv)
                {
                    try { hv.Dispose(); } catch { }
                }
            }
            _threadExceptions.Clear();
        }
    }
}

using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

// 🤖
internal sealed class CilExpressionEvaluator(RuntimeAssemblyPrimitiveTypeClasses primitiveTypes, ManagedDebugger debugger)
{
	private readonly CilExpressionCompiler _compiler = new(debugger);
	private readonly CilInterpreter _interpreter = new(debugger, primitiveTypes);

	internal async Task<EvaluationResult> Evaluate(string expression, CompiledExpressionEvaluationContext context)
	{
		try
		{
			if (expression == "$exception" && debugger.GetCurrentException(context.ThreadId) is { } currentException)
			{
				if (currentException is not ICorDebugReferenceValue { IsNull: false } reference)
				{
					return new EvaluationResult { Value = currentException };
				}
				using var handles = new EvaluationHandleScope();
				var handle = handles.CreateOwnedHandle(reference);
				return new EvaluationResult { Value = handle, OwnedResultHandle = handles.DetachIfOwned(handle) };
			}
			using var compiled = _compiler.TryCompile(expression, context, out var error);
			if (compiled is null) return new EvaluationResult { Error = $"error: {error}" };
			var interpretationResult = await _interpreter.InterpretAsync(compiled, context);
			return new EvaluationResult { Value = interpretationResult.Value, OwnedResultHandle = interpretationResult.OwnedResultHandle };
		}
		catch (Exception ex)
		{
			return new EvaluationResult { Error = $"error: {ex.Message}" };
		}
	}
}

internal sealed class EvaluationResult : IDisposable
{
	private ICorDebugHandleValue? _ownedResultHandle;

	public ICorDebugValue? Value { get; init; }
	public string? Error { get; init; }
	public ICorDebugHandleValue? OwnedResultHandle
	{
		init => _ownedResultHandle = value;
	}

	public void RelinquishResultHandleOwnership()
	{
		_ownedResultHandle = null;
	}

	public void Dispose()
	{
		_ownedResultHandle?.TryDispose();
		_ownedResultHandle = null;
	}
}

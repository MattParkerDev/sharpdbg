using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

internal sealed class EvaluationHandleScope : IDisposable
{
	private readonly HashSet<ICorDebugHandleValue> _handles = new(ReferenceEqualityComparer.Instance);

	public T? Track<T>(T? value) where T : ICorDebugValue
	{
		if (value is ICorDebugHandleValue handle) _handles.Add(handle);
		return value;
	}

	public CilValue Root(CilValue value)
	{
		if (value.CorValue is ICorDebugHandleValue) return value;
		if (value.CorValue is not ICorDebugReferenceValue { IsNull: false, Type: not CorElementType.BYREF } reference) return value;

		var handle = CreateOwnedHandle(reference);
		return CilValue.FromCorValue(handle);
	}

	public ICorDebugHandleValue CreateOwnedHandle(ICorDebugReferenceValue reference)
	{
		var heap = reference.Dereference() as ICorDebugHeapValue2
			?? throw new InvalidOperationException("The referenced debuggee value cannot be rooted");
		var handle = heap.CreateHandle(CorDebugHandleType.HANDLE_STRONG);
		_handles.Add(handle);
		return handle;
	}

	/// <summary>
	/// Removes from _handles and returns <paramref name="value"/> when it is a handle owned by this scope.
	/// This means the handle will not be disposed when this scope is disposed, and the caller is responsible for disposing it.
	/// </summary>
	public ICorDebugHandleValue? DetachIfOwned(ICorDebugValue value)
	{
		if (value is not ICorDebugHandleValue handle) return null;
		return _handles.Remove(handle) ? handle : null;
	}

	public void Dispose()
	{
		foreach (var handle in _handles)
		{
			handle.TryDispose();
		}
		_handles.Clear();
	}
}

using System.Diagnostics.CodeAnalysis;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// Reads the field that <paramref name="getter"/> returns when it is a <see cref="TrivialGetter"/>, instead of calling it with a func eval, which is far slower.
	/// The field's value is returned as read, so it is neutered by the next func eval: a caller that holds it across one roots it first.
	/// Returns false when the getter has to be called, e.g. for a null receiver, so the func eval raises the NullReferenceException
	/// <param name="receiver">The receiver, as a reference or already unwrapped: a reference is only dereferenced once the getter is known to be trivial,
	/// and an object already unwrapped is used as it is, without copying it out of the debuggee again</param>
	internal bool TryReadTrivialGetter(ICorDebugFunction getter, ICorDebugValue receiver, [NotNullWhen(true)] out ICorDebugValue? value)
	{
		value = null;
		if (_modules.TryGetValue(getter.Module.BaseAddress, out var module) is false) return false;
		if (module.MetadataReader.GetTrivialGetter((int)getter.Token.Value) is not { } trivialGetter) return false;
		// The getter's IL is read from the module's image, which is stale once Edit and Continue has replaced it
		if (getter.TryGetCurrentVersionNumber(out var version) is not Cor.S_OK || version is not 1) return false;
		// ICorDebug cannot read the fields of a string, e.g. for string.Length
		if (receiver.UnwrapDebugValue() is not ICorDebugObjectValue objectValue || objectValue is ICorDebugStringValue) return false;
		var getterClass = getter.Class;
		if (trivialGetter.IsOverridable)
		{
			var receiverClass = objectValue.Class;
			if (receiverClass.Token != getterClass.Token || receiverClass.Module.BaseAddress != getter.Module.BaseAddress) return false;
		}
		value = objectValue.GetFieldValue(getterClass, new mdFieldDef((uint)trivialGetter.FieldToken));
		return true;
	}

	/// Roots a reference in a strong handle that the caller owns, so it outlives later func evals. Other values are returned as they are
	private static ICorDebugValue InStrongHandleIfReference(ICorDebugValue value)
	{
		return value is ICorDebugReferenceValue { IsNull: false, Type: not CorElementType.BYREF } reference && reference.Dereference() is ICorDebugHeapValue2 heapValue
			? heapValue.CreateHandle(CorDebugHandleType.HANDLE_STRONG)
			: value;
	}

	/// <see cref="InStrongHandleIfReference"/>, except a string, which is formatted without running code in the debuggee, so is not neutered while it is
	/// formatted, and has no children to keep it for. A handle costs two round trips to the debuggee
	private static ICorDebugValue InStrongHandleUnlessString(ICorDebugValue value)
	{
		if (value is not ICorDebugReferenceValue { IsNull: false } reference) return value;
		return reference.Type switch
		{
			CorElementType.STRING or CorElementType.BYREF => value,
			_ => reference.Dereference() switch
			{
				ICorDebugStringValue => value,
				ICorDebugHeapValue2 heapValue => heapValue.CreateHandle(CorDebugHandleType.HANDLE_STRONG),
				_ => value
			}
		};
	}
}

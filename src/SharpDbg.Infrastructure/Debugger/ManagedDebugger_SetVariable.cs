using Ardalis.GuardClauses;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.Models.Response;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	public async Task<VariableInfo> SetVariable(int variablesReferenceInt, string name, string value)
	{
		Guard.Against.NullOrWhiteSpace(name);

		var variablesReference = _variableManager.GetReference(variablesReferenceInt)
			?? throw new ArgumentException("Invalid variables reference", nameof(variablesReferenceInt));

		ICorDebugILFrame? ilFrame = variablesReference.ReferenceKind switch
		{
			StoredReferenceKind.Scope => GetIlFrameForThreadIdAndStackDepth(variablesReference.ThreadId, variablesReference.FrameStackDepth),
			StoredReferenceKind.SyntheticAsyncScope when variablesReference.Frame is not null => variablesReference.Frame,
			_ => null
		};

		if (ilFrame is null)
			throw new NotSupportedException("setVariable is only supported for stack (Locals) scopes");

		// The frame's 'this' is not a slot that can be replaced safely: in async and lambda frames
		// variable enumeration shows a proxy field, not arguments[0]. Reject it rather than write
		// the wrong object.
		if (name is "this")
			throw new NotSupportedException("Setting 'this' is not supported; edit the object's fields instead.");

		var corDebugFunction = ilFrame.Function;
		var module = _modules[corDebugFunction.Module.BaseAddress];

		if (!TryFindVariable(ilFrame, module, corDebugFunction, name, out var isLocal, out var slot, out var existing))
			throw new InvalidOperationException($"Variable '{name}' was not found in the current frame");

		var thread = ilFrame.Chain.Thread;
		var readFrame = ilFrame;

		if (existing is ICorDebugGenericValue generic)
		{
			var elementType = existing.ExactType.Type;
			if (value == "null")
				throw new NotSupportedException($"Cannot assign null to the value-type variable '{name}'");
			SetPrimitive(generic, elementType, name, value);
		}
		else if (existing is ICorDebugReferenceValue)
		{
			if (value == "null")
			{
				var reference = GetFrameValue(ilFrame, isLocal, slot) as ICorDebugReferenceValue
					?? throw new NotSupportedException($"Variable '{name}' is not a reference value");
				SetReference(reference, default);
			}
			else if (existing.ExactType?.Type == CorElementType.STRING)
			{
				// NewStringAsync resumes the debuggee, which invalidates both the reference value and
				// the frame it came from. Create the string first, then re-acquire the frame and its
				// slot before assigning the fresh reference.
				var eval = thread.CreateEval();
				var newString = await eval.NewStringAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, value, throwOnException: true);
				readFrame = ReacquireFrame(variablesReference);
				var reference = GetFrameValue(readFrame, isLocal, slot) as ICorDebugReferenceValue
					?? throw new NotSupportedException($"Variable '{name}' is not a reference value");
				// NewString returns a handle; SetValue needs the string object's address, not the handle's.
				var stringObject = newString.UnwrapDebugValue();
				SetReference(reference, stringObject.Address);
			}
			else
			{
				throw new NotSupportedException($"Setting reference variables of type '{existing.ExactType}' is not supported");
			}
		}
		else
		{
			throw new NotSupportedException($"Variable '{name}' cannot be set");
		}

		// Format the value the frame actually holds now, not the pre-write value (an eval may have
		// replaced the reference object).
		var updated = GetFrameValue(readFrame, isLocal, slot);
		var (friendlyTypeName, newValue, _, _) = await GetValueForCorDebugValueAsync(updated, variablesReference.ThreadId, variablesReference.FrameStackDepth, true);
		return new VariableInfo
		{
			Name = name,
			Value = newValue,
			Type = friendlyTypeName,
			VariablesReference = 0
		};
	}

	bool TryFindVariable(ICorDebugILFrame ilFrame, ModuleInfo module, ICorDebugFunction function, string name,
		out bool isLocal, out int slot, out ICorDebugValue value)
	{
		isLocal = false;
		slot = -1;
		value = null!;

		foreach (var (index, localValue) in ilFrame.LocalVariables.Index())
		{
			var localName = module.MetadataReader.GetLocalVariableName(function.Token, index, ilFrame.IP.pnOffset);
			if (localName != name) continue;
			// Enumeration expands a lambda display class into its members; the local itself is not
			// shown and replacing its closure reference would be a different edit.
			if (IsLambdaDisplayClass(localValue)) return false;
			isLocal = true;
			slot = index;
			value = localValue;
			return true;
		}

		var metadataImport = module.Module.GetMetaDataInterface<IMetaDataImport>();
		var methodProps = metadataImport.GetMethodProps(function.Token);
		var isStatic = methodProps.pdwAttr.IsMdStatic();
		var arguments = ilFrame.Arguments;
		var skipCount = isStatic ? 0 : 1;
		var hoisted = GetNamesHoistedIntoFrameClosures(module, function, ilFrame);
		foreach (var (relativeIndex, argumentValue) in arguments.Skip(skipCount).Index())
		{
			var paramDef = metadataImport.GetParamForMethodIndex(function.Token, relativeIndex + 1);
			var paramProps = metadataImport.GetParamProps(paramDef);
			var argumentName = paramProps.szName;
			if (argumentName is null || hoisted.Contains(argumentName) || argumentName != name) continue;
			isLocal = false;
			slot = skipCount + relativeIndex;
			value = argumentValue;
			return true;
		}

		return false;
	}

	ICorDebugILFrame ReacquireFrame(VariablesReference variablesReference)
	{
		if (variablesReference.ReferenceKind is StoredReferenceKind.Scope)
			return GetIlFrameForThreadIdAndStackDepth(variablesReference.ThreadId, variablesReference.FrameStackDepth);
		// A runtime-async caller frame is not on the physical stack, so there is no frame object to
		// re-read after an eval. Primitive writes still work there; string creation cannot.
		throw new NotSupportedException("Setting string variables is not supported in synthetic async caller frames");
	}

	static ICorDebugValue GetFrameValue(ICorDebugILFrame ilFrame, bool isLocal, int slot)
		=> isLocal ? ilFrame.LocalVariables[slot] : ilFrame.Arguments[slot];

	static void SetReference(ICorDebugReferenceValue reference, CORDB_ADDRESS address)
	{
		var hr = reference.TrySetValue(address);
		if (hr is not Cor.S_OK)
			throw new InvalidOperationException($"Could not update the variable reference (HRESULT 0x{hr:X8})");
	}

	static unsafe void SetPrimitive(ICorDebugGenericValue generic, CorElementType elementType, string name, string value)
	{
		switch (elementType)
		{
			case CorElementType.BOOLEAN:
				if (!bool.TryParse(value, out var boolean)) throw new FormatException($"'{value}' is not a valid bool");
				generic.SetValue((nint)(&boolean));
				break;
			case CorElementType.I1:
				if (!sbyte.TryParse(value, out var sbyteValue)) throw new FormatException($"'{value}' is not a valid sbyte");
				generic.SetValue((nint)(&sbyteValue));
				break;
			case CorElementType.U1:
				if (!byte.TryParse(value, out var byteValue)) throw new FormatException($"'{value}' is not a valid byte");
				generic.SetValue((nint)(&byteValue));
				break;
			case CorElementType.I2:
				if (!short.TryParse(value, out var shortValue)) throw new FormatException($"'{value}' is not a valid short");
				generic.SetValue((nint)(&shortValue));
				break;
			case CorElementType.U2:
				if (!ushort.TryParse(value, out var ushortValue)) throw new FormatException($"'{value}' is not a valid ushort");
				generic.SetValue((nint)(&ushortValue));
				break;
			case CorElementType.I4:
				if (!int.TryParse(value, out var intValue)) throw new FormatException($"'{value}' is not a valid int");
				generic.SetValue((nint)(&intValue));
				break;
			case CorElementType.U4:
				if (!uint.TryParse(value, out var uintValue)) throw new FormatException($"'{value}' is not a valid uint");
				generic.SetValue((nint)(&uintValue));
				break;
			case CorElementType.I8:
				if (!long.TryParse(value, out var longValue)) throw new FormatException($"'{value}' is not a valid long");
				generic.SetValue((nint)(&longValue));
				break;
			case CorElementType.U8:
				if (!ulong.TryParse(value, out var ulongValue)) throw new FormatException($"'{value}' is not a valid ulong");
				generic.SetValue((nint)(&ulongValue));
				break;
			case CorElementType.R4:
				if (!float.TryParse(value, out var floatValue)) throw new FormatException($"'{value}' is not a valid float");
				generic.SetValue((nint)(&floatValue));
				break;
			case CorElementType.R8:
				if (!double.TryParse(value, out var doubleValue)) throw new FormatException($"'{value}' is not a valid double");
				generic.SetValue((nint)(&doubleValue));
				break;
			case CorElementType.CHAR:
				if (value.Length != 1) throw new FormatException($"'{value}' is not a valid char");
				var charValue = value[0];
				generic.SetValue((nint)(&charValue));
				break;
			default:
				throw new NotSupportedException($"Setting variables of type '{name}' ({elementType}) is not supported");
		}
	}
}

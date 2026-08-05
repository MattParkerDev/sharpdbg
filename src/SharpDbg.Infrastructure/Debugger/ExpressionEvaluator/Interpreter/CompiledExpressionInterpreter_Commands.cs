using System.Text;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	private Task IdentifierName(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var identifier = command.Argument as string ?? "";
		identifier = ReplaceInternalNames(identifier, true);

		evalStack.AddFirst(new EvalStackEntry
		{
			Identifiers = [identifier],
			Editable = true
		});

		return Task.CompletedTask;
	}

	private async Task GenericName(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var argCount = command.Arguments[1] as int? ?? 0;
		var name = command.Arguments[0] as string ?? "";

		var genericTypes = new List<ICorDebugType>();
		var generics = new StringBuilder(">");
		genericTypes.Capacity = argCount;

		for (int i = 0; i < argCount; i++)
		{
			var value = await GetFrontStackEntryValue(evalStack);
			var type = value.ExactType;

			generics.Insert(0, "," + type?.GetType().Name ?? "");
			genericTypes.Add(type);
			evalStack.RemoveFirst();
		}

		generics.Remove(0, 1);
		name += "<" + generics;

		evalStack.AddFirst(new EvalStackEntry
		{
			Identifiers = [name],
			GenericTypeCache = genericTypes,
			Editable = true
		});
	}

	private async Task InvocationExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var argCount = command.Argument as int? ?? 0;

		if (argCount < 0)
			throw new ArgumentException("Invalid argument count");

		var args = new ICorDebugValue[argCount];
		for (var i = argCount - 1; i >= 0; i--)
		{
			args[i] = await GetFrontStackEntryValue(evalStack);
			evalStack.RemoveFirst();
		}

		var entry = evalStack.First!.Value;
		if (entry.PreventBinding)
			return;

		if (entry.Identifiers.Count == 0)
			throw new InvalidOperationException("No method name provided");

		var methodNameGenerics = entry.Identifiers.Last();
		entry.Identifiers.RemoveAt(entry.Identifiers.Count - 1);

		var methodName = methodNameGenerics;
		var pos = methodName.IndexOf('`');
		if (pos >= 0)
			methodName = methodName.Substring(0, pos);

		bool idsEmpty = false;
		bool isInstance = true;

		if (entry.CorDebugValue is null && entry.Identifiers.Count == 0)
		{
			idsEmpty = true;
			// We don't know if this is a static or instance method, but it's fine to add "this" - if the method is not
			// found as an instance method, it will continue and search for static methods
			entry.Identifiers.Add("this");
		}

		var (resolvedTarget, targetIsType, _) = await GetFrontStackEntryResolution(evalStack);
		ICorDebugValue? objValue = targetIsType ? null : resolvedTarget;

		if (objValue is not null)
		{
			var elemType = objValue.UnwrapDebugValue().Type;

			if (_runtimeAssemblyPrimitiveTypeClasses.CorElementToValueClassMap.TryGetValue(elemType, out var boxedClass))
			{
				var size = objValue.Size;
				var data = objValue.UnwrapDebugValue() is ICorDebugGenericValue genValue
					? genValue.GetValueAsBytes()
					: null;

				if (data is not null)
				{
					objValue = await CreateValueType(boxedClass, data);
				}
			}
		}

		var objType = (objValue ?? resolvedTarget).ExactType;
		if (objType is null) throw new InvalidOperationException("Could not resolve target type for method invocation");

		ICorDebugFunction? function = null;
		function = _debugger.FindMethodOnType(objType, methodName, args, targetIsType, idsEmpty);

		if (function is null)
		{
			throw new InvalidOperationException($"Method '{methodName}' with {args.Length} parameters not found");
		}

		var metaDataInterface = function.Class.Module.GetMetaDataInterface<IMetaDataImport>();
		var methodProps2 = metaDataInterface!.GetMethodProps(function.Token);
		isInstance = methodProps2.pdwAttr.IsMdStatic() is false;
		var isExtensionMethod = isInstance is false && metaDataInterface.IsExtensionMethod(function.Token);

		var typeArgsCount = entry.GenericTypeCache?.Count ?? 0;
		var realArgsCount = args.Length + (isInstance || isExtensionMethod ? 1 : 0);
		var typeArgs = new List<ICorDebugType>(typeArgsCount);
		var valueArgs = new List<ICorDebugValue>(realArgsCount);

		if (isInstance || isExtensionMethod)
		{
			// Extension methods are static, but the receiver still becomes the first argument
			valueArgs.Add(objValue);
		}

		foreach (var arg in args)
		{
			valueArgs.Add(arg);
		}

		if (objType is not null)
		{
			var typeParamsEnum = objType.EnumerateTypeParameters();
			foreach (var typeParam in typeParamsEnum)
			{
				typeArgs.Add(typeParam);
			}
		}

		if (entry.GenericTypeCache is not null)
		{
			for (int i = entry.GenericTypeCache.Count - 1; i >= 0; i--)
			{
				if (entry.GenericTypeCache[i] is not null)
				{
					typeArgs.Add(entry.GenericTypeCache[i]);
				}
			}
		}

		entry.ResetEntry();
		var eval = _context.Thread.CreateEval();
		var result = await eval.CallParameterizedFunctionAsync(
			_debugger.ProcessRuntimeEventsUntilEvalEvent,
			_debugger.EvalStatus,
			function,
			typeArgs.Count,
			typeArgs.Count > 0 ? typeArgs.ToArray() : null,
			valueArgs.Count,
			valueArgs.ToArray());

		if (result is null && _runtimeAssemblyPrimitiveTypeClasses.CorVoidClass is not null)
		{
			entry.CorDebugValue = await CreateValueType(_runtimeAssemblyPrimitiveTypeClasses.CorVoidClass, null);
		}
		else
		{
			entry.CorDebugValue = result;
		}
	}

	private async Task ElementAccessExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var indexCount = command.Argument as int? ?? 0;

		var indexes = new List<uint>();
		for (int i = indexCount - 1; i >= 0; i--)
		{
			var indexValue = await GetFrontStackEntryValue(evalStack);
			indexes.Insert(0, await GetElementIndex(indexValue!));
			evalStack.RemoveFirst();
		}

		var entry = evalStack.First!.Value;
		if (entry.PreventBinding)
			return;

		var objValue = await GetFrontStackEntryValue(evalStack);
		var realValue = await GetRealValueWithType(objValue!);
		var elemType = realValue.Type;

		if (elemType == CorElementType.SZARRAY || elemType == CorElementType.ARRAY)
		{
			throw new NotImplementedException("Array element access not yet fully implemented");
		}
		else
		{
			throw new NotImplementedException("Indexer access not yet fully implemented");
		}
	}

	private async Task NumericLiteralExpression(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var typeArg = command.Arguments[0] as ePredefinedType? ?? ePredefinedType.IntKeyword;
		var value = command.Arguments[1];

		var elemType = typeArg switch
		{
			ePredefinedType.DoubleKeyword => CorElementType.R8,
			ePredefinedType.FloatKeyword => CorElementType.R4,
			ePredefinedType.IntKeyword => CorElementType.I4,
			ePredefinedType.UIntKeyword => CorElementType.U4,
			ePredefinedType.LongKeyword => CorElementType.I8,
			ePredefinedType.ULongKeyword => CorElementType.U8,
			ePredefinedType.ShortKeyword => CorElementType.I2,
			ePredefinedType.UShortKeyword => CorElementType.U2,
			ePredefinedType.SByteKeyword => CorElementType.I1,
			ePredefinedType.ByteKeyword => CorElementType.U1,
			ePredefinedType.CharKeyword => CorElementType.CHAR,
			ePredefinedType.DecimalKeyword => CorElementType.VALUETYPE,
			_ => throw new ArgumentException($"Unsupported numeric literal type: {typeArg}")
		};

		byte[]? data = null;
		if (value is not null)
		{
			data = value switch
			{
				double d => BitConverter.GetBytes(d),
				float f => BitConverter.GetBytes(f),
				int i => BitConverter.GetBytes(i),
				uint ui => BitConverter.GetBytes(ui),
				long l => BitConverter.GetBytes(l),
				ulong ul => BitConverter.GetBytes(ul),
				short s => BitConverter.GetBytes(s),
				ushort us => BitConverter.GetBytes(us),
				sbyte sb => new[] { (byte)sb },
				byte b => new[] { b },
				char c => BitConverter.GetBytes(c),
				_ => throw new ArgumentException($"Unsupported numeric literal value type: {value.GetType()}")
			};
		}

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = elemType == CorElementType.VALUETYPE && typeArg == ePredefinedType.DecimalKeyword
				? await CreateValueType(_runtimeAssemblyPrimitiveTypeClasses.CorDecimalClass!, data)
				: await CreatePrimitiveValue(elemType, data)
		});
	}

	private async Task StringLiteralExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var str = command.Argument as string ?? "";
		str = ReplaceInternalNames(str, true);

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = await CreateString(str)
		});
	}

	private async Task InterpolatedStringExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var componentCount = command.Argument as int? ?? 0;

		if (componentCount < 0)
			throw new ArgumentException("Invalid component count for interpolated string");

		var stringBuilder = new StringBuilder();

		var components = new ICorDebugValue[componentCount];
		// Retrieve components in reverse order
		for (var i = componentCount - 1; i >= 0; i--)
		{
			components[i] = await GetFrontStackEntryValue(evalStack);
			evalStack.RemoveFirst();
		}

		foreach (var value in components)
		{
			var unwrapped = value.UnwrapDebugValue();
			if (unwrapped is null || unwrapped is ICorDebugReferenceValue { IsNull: true })
			{
				stringBuilder.Append("null");
			}
			else if (unwrapped is ICorDebugStringValue stringValue)
			{
				stringBuilder.Append(stringValue.String);
			}
			else
			{
				var toStringResult = await GetToStringResult(value);
				stringBuilder.Append(toStringResult);
			}
		}

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = await CreateString(stringBuilder.ToString())
		});
	}

	private async Task<string> GetToStringResult(ICorDebugValue value)
	{
		var unwrappedValue = value.UnwrapDebugValue();
		if (_runtimeAssemblyPrimitiveTypeClasses.CorElementToValueClassMap.TryGetValue(unwrappedValue.Type, out var boxedClass))
		{
			var data = unwrappedValue is ICorDebugGenericValue genValue
				? genValue.GetValueAsBytes()
				: null;

			if (data is not null)
			{
				value = await CreateValueType(boxedClass, data);
			}
		}
		var corDebugFunction = _debugger.FindMethodOnType(value.ExactType, "ToString", [], false, true);
		if (corDebugFunction is null) throw new InvalidOperationException("ToString method not found");
		var eval = _context.Thread.CreateEval();
		var result = await eval.CallParameterlessInstanceMethodAsync(_debugger.ProcessRuntimeEventsUntilEvalEvent, _debugger.EvalStatus, corDebugFunction, value);
		var unwrappedResult = result!.UnwrapDebugValue();
		if (unwrappedResult is not ICorDebugStringValue stringValue) throw new InvalidOperationException("ToString did not return a string");

		var stringResult = stringValue.String;
		return stringResult;
	}

	private async Task CharacterLiteralExpression(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var value = command.Arguments[1];
		var data = value is char c ? BitConverter.GetBytes(c) : null;

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = await CreatePrimitiveValue(CorElementType.CHAR, data)
		});
	}

	private async Task PredefinedType(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var typeArg = command.Argument as ePredefinedType? ?? ePredefinedType.IntKeyword;

		var elemType = typeArg switch
		{
			ePredefinedType.BoolKeyword => CorElementType.BOOLEAN,
			ePredefinedType.ByteKeyword => CorElementType.U1,
			ePredefinedType.CharKeyword => CorElementType.CHAR,
			ePredefinedType.DoubleKeyword => CorElementType.R8,
			ePredefinedType.FloatKeyword => CorElementType.R4,
			ePredefinedType.IntKeyword => CorElementType.I4,
			ePredefinedType.LongKeyword => CorElementType.I8,
			ePredefinedType.SByteKeyword => CorElementType.I1,
			ePredefinedType.ShortKeyword => CorElementType.I2,
			ePredefinedType.StringKeyword => CorElementType.STRING,
			ePredefinedType.UShortKeyword => CorElementType.U2,
			ePredefinedType.UIntKeyword => CorElementType.U4,
			ePredefinedType.ULongKeyword => CorElementType.U8,
			ePredefinedType.DecimalKeyword => CorElementType.VALUETYPE,
			_ => throw new ArgumentException($"Unsupported predefined type: {typeArg}")
		};

		evalStack.AddFirst(new EvalStackEntry
		{
			CorDebugValue = elemType == CorElementType.VALUETYPE && typeArg == ePredefinedType.DecimalKeyword
				? await CreateValueType(_runtimeAssemblyPrimitiveTypeClasses.CorDecimalClass!, null)
				: elemType == CorElementType.STRING
					? await CreateString("")
					: await CreatePrimitiveValue(elemType, null)
		});
	}

	private Task SimpleMemberAccessExpression(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		if (evalStack.Count < 2)
			throw new InvalidOperationException("Stack underflow in SimpleMemberAccessExpression");

		var identifier = evalStack.First!.Value.Identifiers.FirstOrDefault() ?? "";
		var genericTypes = evalStack.First.Value.GenericTypeCache;
		evalStack.RemoveFirst();

		if (!evalStack.First.Value.PreventBinding)
		{
			evalStack.First.Value.Identifiers.Add(identifier);
			if (genericTypes is not null) evalStack.First.Value.GenericTypeCache = genericTypes;
		}

		return Task.CompletedTask;
	}

	private Task QualifiedName(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		return SimpleMemberAccessExpression(command, evalStack);
	}

	private async Task MemberBindingExpression(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		if (evalStack.Count < 2)
			throw new InvalidOperationException("Stack underflow in MemberBindingExpression");

		var identifier = evalStack.First!.Value.Identifiers.FirstOrDefault() ?? "";
		evalStack.RemoveFirst();

		var entry = evalStack.First.Value;
		if (entry.PreventBinding)
			return;

		var value = await GetFrontStackEntryValue(evalStack);
		entry.CorDebugValue = value;
		entry.Identifiers.Clear();

		if (value is ICorDebugReferenceValue refValue && !refValue.IsNull)
		{
			entry.Identifiers.Add(identifier);
		}
		else
		{
			entry.PreventBinding = true;
		}
	}

	private async Task SizeOfExpression(LinkedList<EvalStackEntry> evalStack)
	{
		var entry = evalStack.First!.Value;
		var size = 0;

		if (entry.CorDebugValue is not null)
		{
			var elemType = entry.CorDebugValue.Type;
			if (elemType == CorElementType.CLASS)
			{
				var unwrapped = entry.CorDebugValue.UnwrapDebugValue();
				size = unwrapped.Size;
			}
			else
			{
				size = entry.CorDebugValue.Size;
			}
		}
		else
		{
			throw new NotImplementedException("SizeOf for types not yet fully implemented");
		}

		entry.ResetEntry();
		entry.CorDebugValue = await CreatePrimitiveValue(CorElementType.U4, BitConverter.GetBytes((uint)size));
	}

	private async Task SimpleAssignmentExpression(LinkedList<EvalStackEntry> evalStack)
	{
		// Stack: RHS is on top, LHS is underneath
		var rhsValue = await GetFrontStackEntryValue(evalStack);
		evalStack.RemoveFirst();

		var lhsEntry = evalStack.First!.Value;
		if (!lhsEntry.Editable) throw new InvalidOperationException("Left-hand side of assignment is not editable");

		var lhsResolution = await GetFrontStackEntryResolution(evalStack);
		var lhsValue = lhsResolution.Value;
		var setterData = lhsResolution.SetterData;

		if (setterData is not null)
		{
			if (setterData.SetterFunction is null) throw new InvalidOperationException("Property does not have a setter");
			if (setterData.OwnerValue is null) throw new InvalidOperationException("Property owner is unavailable");

			var setterProps = setterData.SetterFunction.Class.Module.GetMetaDataInterface<IMetaDataImport>()!.GetMethodProps(setterData.SetterFunction.Token);
			var setterIsStatic = setterProps.pdwAttr.IsMdStatic();
			ICorDebugValue[] setterArguments = setterIsStatic ? [rhsValue] : [setterData.OwnerValue, rhsValue];
			var typeArguments = setterData.OwnerValue.ExactType.TypeParameters;
			var eval = _context.Thread.CreateEval();
			await eval.CallParameterizedFunctionAsync(
				_debugger.ProcessRuntimeEventsUntilEvalEvent,
				_debugger.EvalStatus,
				setterData.SetterFunction,
				typeArguments.Length,
				typeArguments,
				setterArguments.Length,
				setterArguments);

			lhsEntry.CorDebugValue = rhsValue;
			lhsEntry.Identifiers.Clear();
			return;
		}

		var unwrappedLhs = lhsValue.UnwrapDebugValue();
		var unwrappedRhs = rhsValue.UnwrapDebugValue();

		if (unwrappedLhs is ICorDebugGenericValue lhsGeneric && unwrappedRhs is ICorDebugGenericValue rhsGeneric)
		{
			// Primitive / value type assignment: copy raw bytes from RHS into LHS
			var data = rhsGeneric.GetValueAsBytes();
			unsafe
			{
				fixed (byte* p = data)
				{
					lhsGeneric.SetValue((IntPtr)p);
				}
			}
		}
		else if (lhsValue is ICorDebugReferenceValue lhsRef && rhsValue is ICorDebugReferenceValue rhsRef)
		{
			// Reference type assignment: point LHS reference at the same object as RHS
			lhsRef.Value = rhsRef.Value;
		}
		else
		{
			throw new NotImplementedException($"SimpleAssignmentExpression: unsupported combination of LHS type '{unwrappedLhs.GetType().Name}' and RHS type '{unwrappedRhs.GetType().Name}'");
		}

		// Leave the assigned value on the stack (assignment expressions return the assigned value)
		lhsEntry.CorDebugValue = lhsValue;
		lhsEntry.Identifiers.Clear();
	}

	private async Task CoalesceExpression(LinkedList<EvalStackEntry> evalStack)
	{
		var rightEntry = evalStack.First!.Value;
		var rightValue = await GetFrontStackEntryValue(evalStack);
		var realRight = await GetRealValueWithType(rightValue!);
		evalStack.RemoveFirst();

		var leftEntry = evalStack.First.Value;
		var leftValue = await GetFrontStackEntryValue(evalStack);
		var realLeft = await GetRealValueWithType(leftValue!);

		var rightType = realRight.Type;
		var leftType = realLeft.Type;

		if ((rightType == CorElementType.STRING && leftType == CorElementType.STRING) ||
			(rightType == CorElementType.CLASS && leftType == CorElementType.CLASS))
		{
			if (leftValue is ICorDebugReferenceValue refValue && refValue.IsNull)
			{
				evalStack.RemoveFirst();
				evalStack.AddFirst(rightEntry);
			}
		}
		else
		{
			throw new ArgumentException("Operator ?? cannot be applied to operands of these types");
		}
	}
}

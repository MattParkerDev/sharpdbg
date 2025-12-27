using ClrDebug;
using System.Text;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{
	public partial class StackMachine
	{
		private readonly EvalData _evalData;
		private readonly ValueCreator _valueCreator;
		private readonly ExpressionExecutor _executor;
		private readonly OperatorEvaluator _operatorEvaluator;

		public StackMachine(EvalData evalData, ManagedDebugger debugger)
		{
			_evalData = evalData;
			_valueCreator = new ValueCreator(evalData);
			_executor = new ExpressionExecutor(evalData, debugger);
			_operatorEvaluator = new OperatorEvaluator(evalData, debugger);
		}

		public async Task<EvaluationResult> Run(string expression)
		{
			var evalStack = new LinkedList<EvalStackEntry>();
			var output = new StringBuilder();

			try
			{
				var fixedExpression = ReplaceInternalNames(expression, false);
				var program = GenerateStackMachineProgram(fixedExpression);

				foreach (var command in program.Commands)
				{
					await ExecuteCommand(command, evalStack, output);
				}

				if (evalStack.Count != 1)
				{
					throw new InvalidOperationException("Expression evaluation did not produce a single result");
				}

				var resultValue = await _executor.GetFrontStackEntryValue(evalStack, true);
				var setterData = evalStack.First.Value.SetterData;

				return new EvaluationResult
				{
					Value = resultValue,
					Editable = evalStack.First.Value.Editable && (setterData == null || setterData.SetterFunction != null),
					SetterData = setterData
				};
			}
			catch (Exception ex)
			{
				output.AppendLine($"error: {ex.Message}");
				return new EvaluationResult
				{
					Error = output.ToString()
				};
			}
		}

		private async Task ExecuteCommand(ICommand command, LinkedList<EvalStackEntry> evalStack, StringBuilder output)
		{
			switch (command.OpCode)
			{
				case eOpCode.IdentifierName: await IdentifierName((command as OneOperandCommand)!, evalStack); break;
				case eOpCode.GenericName: await GenericName((command as TwoOperandCommand)!, evalStack); break;
				case eOpCode.InvocationExpression: await InvocationExpression((command as OneOperandCommand)!, evalStack); break;
				case eOpCode.ElementAccessExpression: await ElementAccessExpression((command as OneOperandCommand)!, evalStack); break;
				case eOpCode.NumericLiteralExpression: await NumericLiteralExpression((command as TwoOperandCommand)!, evalStack); break;
				case eOpCode.StringLiteralExpression: await StringLiteralExpression((command as OneOperandCommand)!, evalStack); break;
				case eOpCode.CharacterLiteralExpression: await CharacterLiteralExpression((command as TwoOperandCommand)!, evalStack); break;
				case eOpCode.PredefinedType: await PredefinedType((command as OneOperandCommand)!, evalStack); break;
				case eOpCode.SimpleMemberAccessExpression: await SimpleMemberAccessExpression(command, evalStack); break;
				case eOpCode.QualifiedName: await QualifiedName(command, evalStack); break;
				case eOpCode.MemberBindingExpression: await MemberBindingExpression(command, evalStack); break;
				case eOpCode.AddExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.AddExpression, evalStack); break;
				case eOpCode.SubtractExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.SubtractExpression, evalStack); break;
				case eOpCode.MultiplyExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.MultiplyExpression, evalStack); break;
				case eOpCode.DivideExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.DivideExpression, evalStack); break;
				case eOpCode.ModuloExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.ModuloExpression, evalStack); break;
				case eOpCode.LeftShiftExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.LeftShiftExpression, evalStack); break;
				case eOpCode.RightShiftExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.RightShiftExpression, evalStack); break;
				case eOpCode.BitwiseAndExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.BitwiseAndExpression, evalStack); break;
				case eOpCode.BitwiseOrExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.BitwiseOrExpression, evalStack); break;
				case eOpCode.ExclusiveOrExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.ExclusiveOrExpression, evalStack); break;
				case eOpCode.LogicalAndExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.LogicalAndExpression, evalStack); break;
				case eOpCode.LogicalOrExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.LogicalOrExpression, evalStack); break;
				case eOpCode.EqualsExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.EqualsExpression, evalStack); break;
				case eOpCode.NotEqualsExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.NotEqualsExpression, evalStack); break;
				case eOpCode.LessThanExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.LessThanExpression, evalStack); break;
				case eOpCode.GreaterThanExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.GreaterThanExpression, evalStack); break;
				case eOpCode.LessThanOrEqualExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.LessThanOrEqualExpression, evalStack); break;
				case eOpCode.GreaterThanOrEqualExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateTwoOperands(OperationType.GreaterThanOrEqualExpression, evalStack); break;
				case eOpCode.UnaryPlusExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateOneOperand(OperationType.UnaryPlusExpression, evalStack); break;
				case eOpCode.UnaryMinusExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateOneOperand(OperationType.UnaryMinusExpression, evalStack); break;
				case eOpCode.LogicalNotExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateOneOperand(OperationType.LogicalNotExpression, evalStack); break;
				case eOpCode.BitwiseNotExpression: evalStack.First.Value.CorDebugValue = await _operatorEvaluator.CalculateOneOperand(OperationType.BitwiseNotExpression, evalStack); break;
				case eOpCode.TrueLiteralExpression: evalStack.AddFirst(new EvalStackEntry { Literal = true, CorDebugValue = await _valueCreator.CreateBooleanValue(true) }); break;
				case eOpCode.FalseLiteralExpression: evalStack.AddFirst(new EvalStackEntry { Literal = true, CorDebugValue = await _valueCreator.CreateBooleanValue(false) }); break;
				case eOpCode.NullLiteralExpression: evalStack.AddFirst(new EvalStackEntry { Literal = true, CorDebugValue = await _valueCreator.CreateNullValue() }); break;
				case eOpCode.SizeOfExpression: await SizeOfExpression(evalStack); break;
				case eOpCode.CoalesceExpression: await CoalesceExpression(evalStack); break;
				case eOpCode.ThisExpression: evalStack.AddFirst(new EvalStackEntry { Identifiers = ["this"], Editable = true }); break;
				case eOpCode.ElementBindingExpression: await ElementAccessExpression((command as OneOperandCommand)!, evalStack); break;
				default: throw new NotImplementedException($"OpCode {command.OpCode} is not implemented");
			}
		}

		private string ReplaceInternalNames(string expression, bool restore)
		{
			var result = expression;
			var internalNamesMap = new Dictionary<string, string>
			{
				{ "$exception", "__INTERNAL_NCDB_EXCEPTION_VARIABLE" }
			};

			foreach (var entry in internalNamesMap)
			{
				if (restore)
					result = result.Replace(entry.Value, entry.Key);
				else
					result = result.Replace(entry.Key, entry.Value);
			}

			return result;
		}

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

			var genericTypes = new List<CorDebugType?>();
			var generics = new StringBuilder(">");
			genericTypes.Capacity = argCount;

			for (int i = 0; i < argCount; i++)
			{
				var value = await _executor.GetFrontStackEntryValue(evalStack);
				CorDebugType? type = value?.ExactType;

				generics.Insert(0, "," + type?.GetType().Name ?? "");
				genericTypes.Add(type);
				evalStack.RemoveFirst();
			}

			generics.Remove(0, 1);
			name += "<" + generics;

			evalStack.AddFirst(new EvalStackEntry
			{
				Identifiers = new List<string> { name },
				GenericTypeCache = genericTypes,
				Editable = true
			});
		}

		private async Task InvocationExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
		{
			var argCount = command.Argument as int? ?? 0;

			if (argCount < 0)
				throw new ArgumentException("Invalid argument count");

			var args = new List<CorDebugValue?>(argCount);
			for (int i = argCount - 1; i >= 0; i--)
			{
				args.Add(await _executor.GetFrontStackEntryValue(evalStack));
				evalStack.RemoveFirst();
			}

			var entry = evalStack.First.Value;
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

			CorDebugValue? objValue;
			CorDebugType? objType;

			if (entry.CorDebugValue == null && entry.Identifiers.Count == 0)
			{
				idsEmpty = true;
				objValue = await _executor.GetFrontStackEntryValue(evalStack);
				var isStaticMethod = objValue == null;
				objType = objValue?.ExactType;

				if (!isStaticMethod)
				{
					entry.Identifiers.Add("this");
				}
				else
				{
					var corDebugFunction = _evalData.ILFrame.Function;
					var module = corDebugFunction.Class.Module;
					var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
					var methodProps = metaDataImport!.GetMethodProps(corDebugFunction.Token);
					var declaringTypeDef = methodProps.pClass;
					var typeProps = metaDataImport!.GetTypeDefProps(declaringTypeDef);
					var className = typeProps.szTypeDef;
					entry.Identifiers.AddRange(className.Split('.'));
				}
			}

			objValue = await _executor.GetFrontStackEntryValue(evalStack);

			if (objValue != null)
			{
				var elemType = objValue.UnwrapDebugValue().Type;

				if (_evalData.CorElementToValueClassMap.TryGetValue(elemType, out var boxedClass))
				{
					var size = objValue.Size;
					var data = objValue.UnwrapDebugValue() is CorDebugGenericValue genValue
						? genValue.GetValueAsBytes()
						: null;

					if (data != null)
					{
						objValue = await _valueCreator.CreateValueType(boxedClass, data);
					}
				}

				objType = objValue.ExactType;
			}
			else
			{
				objType = await _executor.GetFrontStackEntryType(evalStack);
			}

			if (objType == null && objValue == null) throw new InvalidOperationException("Could not resolve target type for method invocation");

			CorDebugFunction? function = null;
			bool? searchStatic = objType is null;

			if (objType != null)
			{
				function = await FindMethodOnType(objType, methodName, args, searchStatic.Value, idsEmpty);
			}

			if (function == null)
			{
				throw new InvalidOperationException($"Method '{methodName}' with {args.Count} parameters not found");
			}

			var methodProps2 = function.Class.Module.GetMetaDataInterface().MetaDataImport!.GetMethodProps(function.Token);
			isInstance = (methodProps2.pdwAttr & CorMethodAttr.mdStatic) == 0;

			var typeArgsCount = entry.GenericTypeCache?.Count ?? 0;
			var realArgsCount = args.Count + (isInstance ? 1 : 0);
			var typeArgs = new List<ICorDebugType>(typeArgsCount);
			var valueArgs = new List<ICorDebugValue>(realArgsCount);

			if (isInstance)
			{
				valueArgs.Add(objValue!.Raw);
			}

			foreach (var arg in args)
			{
				valueArgs.Add(arg!.Raw);
			}

			if (objType != null)
			{
				var typeParamsEnum = objType.EnumerateTypeParameters();
				foreach (var typeParam in typeParamsEnum)
				{
					typeArgs.Add(typeParam.Raw);
				}
			}

			if (entry.GenericTypeCache != null)
			{
				for (int i = entry.GenericTypeCache.Count - 1; i >= 0; i--)
				{
					if (entry.GenericTypeCache[i] != null)
					{
						typeArgs.Add(entry.GenericTypeCache[i]!.Raw);
					}
				}
			}

			entry.ResetEntry();
			var eval = _evalData.Thread.CreateEval();
			var result = await eval.CallParameterizedFunctionAsync(
				_evalData.ManagedCallback,
				function,
				typeArgs.Count,
				typeArgs.Count > 0 ? typeArgs.ToArray() : null,
				valueArgs.Count,
				valueArgs.ToArray(),
				_evalData.ILFrame);

			if (result == null && _evalData.ICorVoidClass != null)
			{
				entry.CorDebugValue = await _valueCreator.CreateValueType(_evalData.ICorVoidClass, null);
			}
			else
			{
				entry.CorDebugValue = result;
			}
		}

		private async Task<CorDebugFunction?> FindMethodOnType(
			CorDebugType type,
			string methodName,
			List<CorDebugValue?> args,
			bool searchStatic,
			bool idsEmpty)
		{
			var typeClass = type.Class;
			var module = typeClass.Module;
			var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
			var classToken = typeClass.Token;

			var methods = metaDataImport!.EnumMethods(classToken);
			foreach (var methodToken in methods)
			{
				var methodProps = metaDataImport!.GetMethodProps(methodToken);

				if (methodProps.szMethod != methodName)
					continue;

				var isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;

				if ((searchStatic && !isStatic) || (!searchStatic && isStatic && !idsEmpty))
					continue;

				var method = module.GetFunctionFromToken(methodToken);

				if (IsMethodParameterMatch(method, args))
					return method;

				var baseType = type.Base;
				while (baseType != null)
				{
					var baseMethod = await FindMethodOnType(baseType, methodName, args, searchStatic, idsEmpty);
					if (baseMethod != null)
						return baseMethod;

					baseType = baseType.Base;
				}
			}

			return null;
		}

		private bool IsMethodParameterMatch(CorDebugFunction method, List<CorDebugValue? > args)
		{
			var metaDataImport = method. Class.Module.GetMetaDataInterface().MetaDataImport;

			// Get the method signature blob
			var methodProps = metaDataImport.GetMethodProps(method.Token);

			// Parse the signature using System.Reflection.Metadata
			var parameterTypes = ParseMethodSignatureWithMetadata(methodProps.ppvSigBlob, methodProps.pcbSigBlob);

			// Compare parameter count
			if (parameterTypes.Count != args.Count)
				return false;

			// Compare each parameter type
			for (var i = 0; i < args.Count; i++)
			{
				if (args[i] == null)
					continue;

				var argType = args[i].ExactType?. Type ??  args[i].Type; // Get the actual type

				if (!IsTypeMatch(parameterTypes[i], argType, args[i]))
					return false;
			}

			return true;
		}

		private async Task ElementAccessExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
		{
			var indexCount = command.Argument as int? ?? 0;

			var indexes = new List<uint>();
			for (int i = indexCount - 1; i >= 0; i--)
			{
				var indexValue = await _executor.GetFrontStackEntryValue(evalStack);
				indexes.Insert(0, await _executor.GetElementIndex(indexValue!));
				evalStack.RemoveFirst();
			}

			var entry = evalStack.First.Value;
			if (entry.PreventBinding)
				return;

			var objValue = await _executor.GetFrontStackEntryValue(evalStack);
			var realValue = await _executor.GetRealValueWithType(objValue!);
			var elemType = realValue.Type;

			if (elemType == CorElementType.SZArray || elemType == CorElementType.Array)
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
				ePredefinedType.CharKeyword => CorElementType.Char,
				ePredefinedType.DecimalKeyword => CorElementType.ValueType,
				_ => throw new ArgumentException($"Unsupported numeric literal type: {typeArg}")
			};

			byte[]? data = null;
			if (value != null)
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
				CorDebugValue = elemType == CorElementType.ValueType && typeArg == ePredefinedType.DecimalKeyword
					? await _valueCreator.CreateValueType(_evalData.ICorDecimalClass!, data)
					: await _valueCreator.CreatePrimitiveValue(elemType, data)
			});
		}

		private async Task StringLiteralExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
		{
			var str = command.Argument as string ?? "";
			str = ReplaceInternalNames(str, true);

			evalStack.AddFirst(new EvalStackEntry
			{
				Literal = true,
				CorDebugValue = await _valueCreator.CreateString(str)
			});
		}

		private async Task CharacterLiteralExpression(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
		{
			var value = command.Arguments[1];
			var data = value is char c ? BitConverter.GetBytes(c) : null;

			evalStack.AddFirst(new EvalStackEntry
			{
				Literal = true,
				CorDebugValue = await _valueCreator.CreatePrimitiveValue(CorElementType.Char, data)
			});
		}

		private async Task PredefinedType(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
		{
			var typeArg = command.Argument as ePredefinedType? ?? ePredefinedType.IntKeyword;

			var elemType = typeArg switch
			{
				ePredefinedType.BoolKeyword => CorElementType.Boolean,
				ePredefinedType.ByteKeyword => CorElementType.U1,
				ePredefinedType.CharKeyword => CorElementType.Char,
				ePredefinedType.DoubleKeyword => CorElementType.R8,
				ePredefinedType.FloatKeyword => CorElementType.R4,
				ePredefinedType.IntKeyword => CorElementType.I4,
				ePredefinedType.LongKeyword => CorElementType.I8,
				ePredefinedType.SByteKeyword => CorElementType.I1,
				ePredefinedType.ShortKeyword => CorElementType.I2,
				ePredefinedType.StringKeyword => CorElementType.String,
				ePredefinedType.UShortKeyword => CorElementType.U2,
				ePredefinedType.UIntKeyword => CorElementType.U4,
				ePredefinedType.ULongKeyword => CorElementType.U8,
				ePredefinedType.DecimalKeyword => CorElementType.ValueType,
				_ => throw new ArgumentException($"Unsupported predefined type: {typeArg}")
			};

			evalStack.AddFirst(new EvalStackEntry
			{
				CorDebugValue = elemType == CorElementType.ValueType && typeArg == ePredefinedType.DecimalKeyword
					? await _valueCreator.CreateValueType(_evalData.ICorDecimalClass!, null)
					: elemType == CorElementType.String
						? await _valueCreator.CreateString("")
						: await _valueCreator.CreatePrimitiveValue(elemType, null)
			});
		}

		private Task SimpleMemberAccessExpression(ICommand command, LinkedList<EvalStackEntry> evalStack)
		{
			if (evalStack.Count < 2)
				throw new InvalidOperationException("Stack underflow in SimpleMemberAccessExpression");

			var identifier = evalStack.First.Value.Identifiers.FirstOrDefault() ?? "";
			var genericTypes = evalStack.First.Value.GenericTypeCache;
			evalStack.RemoveFirst();

			if (!evalStack.First.Value.PreventBinding)
			{
				evalStack.First.Value.Identifiers.Add(identifier);
				evalStack.First.Value.GenericTypeCache = genericTypes;
			}

			return Task.CompletedTask;
		}

		private Task QualifiedName(ICommand command, LinkedList<EvalStackEntry> evalStack)
		{
			return SimpleMemberAccessExpression(command, evalStack);
		}

		private async Task MemberBindingExpression(ICommand command, LinkedList<EvalStackEntry> evalStack)
		{
			if (evalStack.Count < 2)
				throw new InvalidOperationException("Stack underflow in MemberBindingExpression");

			var identifier = evalStack.First.Value.Identifiers.FirstOrDefault() ?? "";
			evalStack.RemoveFirst();

			var entry = evalStack.First.Value;
			if (entry.PreventBinding)
				return;

			var value = await _executor.GetFrontStackEntryValue(evalStack, true);
			entry.CorDebugValue = value;
			entry.Identifiers.Clear();

			if (value is CorDebugReferenceValue refValue && !refValue.IsNull)
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
			var entry = evalStack.First.Value;
			var size = 0;

			if (entry.CorDebugValue != null)
			{
				var elemType = entry.CorDebugValue.Type;
				if (elemType == CorElementType.Class)
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
			entry.CorDebugValue = await _valueCreator.CreatePrimitiveValue(CorElementType.U4, BitConverter.GetBytes((uint)size));
		}

		private async Task CoalesceExpression(LinkedList<EvalStackEntry> evalStack)
		{
			var rightEntry = evalStack.First.Value;
			var rightValue = await _executor.GetFrontStackEntryValue(evalStack);
			var realRight = await _executor.GetRealValueWithType(rightValue!);
			evalStack.RemoveFirst();

			var leftEntry = evalStack.First.Value;
			var leftValue = await _executor.GetFrontStackEntryValue(evalStack);
			var realLeft = await _executor.GetRealValueWithType(leftValue!);

			var rightType = realRight.Type;
			var leftType = realLeft.Type;

			if ((rightType == CorElementType.String && leftType == CorElementType.String) ||
				(rightType == CorElementType.Class && leftType == CorElementType.Class))
			{
				if (leftValue is CorDebugReferenceValue refValue && refValue.IsNull)
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

	public class EvaluationResult
	{
		public CorDebugValue? Value { get; set; }
		public bool Editable { get; set; }
		public SetterData? SetterData { get; set; }
		public string? Error { get; set; }
	}
}

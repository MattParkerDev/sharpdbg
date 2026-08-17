using System.Diagnostics;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.Infrastructure.Debugger.PresentationHintModels;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private async Task AddLocalVariables(ModuleInfo module, ICorDebugFunction corDebugFunction, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? classContainingHoistedLocalsValue)
	{
		if (classContainingHoistedLocalsValue is not null)
		{
			// If we have a classContainingHoistedLocalsValue, it means captured variables from the outer scope are stored
			// as fields on the compiler-generated closure class - read those first, walking the full closure chain
			// so that variables captured from enclosing lambdas are also included.
			// We do NOT return here: non-captured locals declared inside the lambda body are still plain IL locals
			// on the lambda method frame and must also be read below.
			await AddClosureChainMembers(classContainingHoistedLocalsValue, threadId, stackDepth, result);
		}
		var corDebugIlFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
		if (corDebugIlFrame.LocalVariables.Length is 0) return;
		var currentIlOffset = corDebugIlFrame.IP.pnOffset;
		foreach (var (index, localVariableCorDebugValue) in corDebugIlFrame.LocalVariables.Index())
		{
			var localVariableName = module.MetadataReader.GetLocalVariableName(corDebugFunction.Token, index, currentIlOffset);
			if (localVariableName is null) continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
			await WithFailureHandling(result, localVariableName, async () =>
			{
				var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(localVariableCorDebugValue, threadId, stackDepth, true);
				VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
				result.Add(new VariableInfo
				{
					Name = localVariableName,
					Value = value,
					Type = friendlyTypeName,
					PresentationHint = variablePresentationHint,
					VariablesReference = GetVariablesReference(localVariableCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
				});
			});
		}
	}

	/// Walks the compiler-generated closure chain starting at <paramref name="closureValue"/>,
	/// calling AddMembers on each closure class. Parent closures are linked via a field of
	/// kind <see cref="GeneratedNameKind.DisplayClassLocalOrField"/> (e.g. "&lt;&gt;8__1").
	private async Task AddClosureChainMembers(ICorDebugValue closureValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
	{
		await AddMembers(closureValue, closureValue.ExactType, threadId, stackDepth, result);

		// Follow the DisplayClassLocalOrField link to the parent closure, if any
		var objectValue = closureValue.UnwrapDebugValueToObject();
		var metadataImport = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
		var fields = metadataImport.EnumFields(objectValue.Class.Token);
		foreach (var field in fields)
		{
			var fieldProps = metadataImport.GetFieldProps(field);
			if (GeneratedNameParser.GetKind(fieldProps.szField) is GeneratedNameKind.DisplayClassLocalOrField)
			{
				var parentClosureValue = objectValue.GetFieldValue(objectValue.Class, field);
				await AddClosureChainMembers(parentClosureValue, threadId, stackDepth, result);
				break; // only one parent link per closure class
			}
		}
	}

	/// Returns classContainingHoistedLocalsValue if applicable
	private async Task<ICorDebugValue?> AddArguments(ModuleInfo module, ICorDebugFunction corDebugFunction, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var corDebugIlFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var arguments = corDebugIlFrame.Arguments;
		if (arguments.Length is 0) return null;
		var metadataImport = module.Module.GetMetaDataInterface<IMetaDataImport>();

		// localsScope.Frame.Arguments includes the implicit "this" parameter for instance methods,
		// but GetParamForMethodIndex does NOT include it - it is named by convention
		// so we need to check the method attributes to see if it's static or instance, to conditionally handle "this"
		var methodProps = metadataImport!.GetMethodProps(corDebugFunction.Token);
		var isStatic = methodProps.pdwAttr.IsMdStatic();
		ICorDebugValue? classContainingHoistedLocalsValue = null;
		if (isStatic is false)
		{
			var methodName = methodProps.szMethod;
			var implicitThisValue = arguments[0];
			if (methodName is "MoveNext" || methodName.Contains(">b")) // async or lambda
			{
				var containingClassName = metadataImport.GetTypeDefProps(corDebugFunction.Class.Token).szTypeDef;
				var classGeneratedNameKind = GeneratedNameParser.GetKind(containingClassName);
				if (classGeneratedNameKind is GeneratedNameKind.StateMachineType or GeneratedNameKind.LambdaDisplayClass)
				{
					// In this case, 'this' is actually a compiler generated class that contains a field pointing to the 'this' that the user expects
					// We are also going to use this to decide that the containing class contains hoisted locals, so we should return it
					classContainingHoistedLocalsValue = implicitThisValue;
					// This may return null, as even though we have checked isStatic is true, that is for the MoveNext method - the user's method may be static, and therefore would have no 'this' proxy field
					implicitThisValue = GetAsyncOrLambdaProxyFieldValue(implicitThisValue, metadataImport);
				}
			}
			if (implicitThisValue is not null)
			{
				await WithFailureHandling(result, "this", async () =>
				{
					var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(implicitThisValue, threadId, stackDepth, true);
					VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
					result.Add(new VariableInfo
					{
						Name = "this", // Hardcoded - 'this' has no metadata
						Value = value,
						Type = friendlyTypeName,
						PresentationHint = variablePresentationHint,
						VariablesReference = GetVariablesReference(implicitThisValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
					});
				});
			}
		}
		var skipCount = isStatic ? 0 : 1; // Skip 'this' for instance methods, as we already handled it
		foreach (var (index, argumentCorDebugValue) in arguments.Skip(skipCount).Index())
		{
			// index 0 is the return value, so we add 1 to get to the arguments
			// GetParamForMethodIndex does not include the instance 'this' parameter
			var paramDef = metadataImport!.GetParamForMethodIndex(corDebugFunction.Token, index + 1);
			var paramProps = metadataImport.GetParamProps(paramDef);
			var argumentName = paramProps.szName;
			if (argumentName is null) continue;
			await WithFailureHandling(result, argumentName, async () =>
			{
				var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(argumentCorDebugValue, threadId, stackDepth, true);
				VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
				result.Add(new VariableInfo
				{
					Name = argumentName,
					Value = value,
					Type = friendlyTypeName,
					PresentationHint = variablePresentationHint,
					VariablesReference = GetVariablesReference(argumentCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
				});
			});
		}
		return classContainingHoistedLocalsValue;
	}

	private async Task AddCurrentException(List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var thread = _threads.GetValueOrDefault(threadId.Value);
		Guard.Against.Null(thread);
		thread.TryGetCurrentException(out var currentException);
		if (currentException is not null)
		{
			await WithFailureHandling(result, "$exception", async () =>
			{
				var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(currentException, threadId, stackDepth, true);
				VariablePresentationHint? presentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
				result.Add(new VariableInfo
				{
					Name = "$exception",
					Value = value,
					Type = friendlyTypeName,
					PresentationHint = presentationHint,
					VariablesReference = GetVariablesReference(currentException, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
				});
			});
		}
	}

	private int GetVariablesReference(ICorDebugValue corDebugValue, string friendlyTypeName, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? debuggerProxyInstance)
	{
		var unwrappedDebugValue = corDebugValue.UnwrapDebugValue();
		if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
		{
			if (arrayValue.Count is 0) return 0;
			return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance);
		}
		else if (unwrappedDebugValue is ICorDebugObjectValue objectValue)
		{
			var isNullableStruct = friendlyTypeName.EndsWith('?');
			if (isNullableStruct)
			{
				var underlyingValueOrNull = GetUnderlyingValueOrNullFromNullableStruct(objectValue);
				if (underlyingValueOrNull is null) return 0;
				if (underlyingValueOrNull is not ICorDebugObjectValue objValue) return 0; // underlying value is primitive
				objectValue = objValue;
			}

			var type = objectValue.Type;
			// Strings are objects but typically displayed as primitives
			if (type is CorElementType.STRING) return 0;
			// Decimal is a struct but should be treated as a primitive
			if (friendlyTypeName is "decimal" or "decimal?") return 0;
			// a boxed primitive is CorElementType.VALUETYPE but should be displayed as a primitive. They can never be nullable.
			if (friendlyTypeName is "bool" or "byte" or "sbyte" or "char" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "nint" or "nuint") return 0;
			if (type is CorElementType.CLASS or CorElementType.VALUETYPE or CorElementType.SZARRAY or CorElementType.ARRAY)
			{
				return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance);
			}
		}
		return 0;
	}

	private int GenerateUniqueVariableReference(ICorDebugValue value, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? debuggerProxyInstance)
	{
		var variablesReference = new VariablesReference(StoredReferenceKind.StackVariable, value, threadId, stackDepth, debuggerProxyInstance);
		var reference = _variableManager.CreateReference(variablesReference);
		return reference;
	}

	private async Task AddMembersAndStaticPseudoVariable(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers = true)
	{
		var requiresStaticPseudoVariable = await AddMembers(corDebugValue, corDebugType, threadId, stackDepth, result, includeNonPublicMembers);
		if (requiresStaticPseudoVariable)
		{
			var variableInfo = new VariableInfo
			{
				Name = "Static members",
				Value = "",
				Type = "",
				PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
				VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.StaticClassVariable, corDebugValue, threadId, stackDepth, null))
			};
			result.Add(variableInfo);
		}
	}

	private void AddEnumerablePseudoVariables(VariablesReference variablesReference, List<VariableInfo> result)
	{
		result.Add(new VariableInfo
		{
			Name = "Raw View",
			Value = "",
			Type = "",
			PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
			VariablesReference = _variableManager.CreateReference(variablesReference with { ReferenceKind = StoredReferenceKind.RawView })
		});
		result.Add(new VariableInfo
		{
			Name = "Results",
			Value = "Expanding will force enumeration of the object",
			Type = "",
			PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
			VariablesReference = _variableManager.CreateReference(variablesReference with { ReferenceKind = StoredReferenceKind.EnumerableResults })
		});
	}

	private bool IsEnumerable(ICorDebugType type)
	{
		for (var currentType = type; currentType is not null; currentType = currentType.Base)
		{
			var metadataReader = currentType.Class.Module.GetMetaDataInterface<IMetaDataImport>();
			foreach (var interfaceImpl in metadataReader.EnumInterfaceImpls(currentType.Class.Token))
			{
				var interfaceToken = metadataReader.GetInterfaceImplProps(interfaceImpl).ptkIface;
				var interfaceHandle = MetadataTokens.Handle(checked((int)interfaceToken.Value));
				var peMetadataReader = _modules[currentType.Class.Module.BaseAddress].MetadataReader.PeMetadataReader;
				var interfaceName = interfaceHandle.Kind switch
				{
					HandleKind.TypeDefinition => FunctionBreakpointSignatureTypeProvider.GetTypeName(peMetadataReader, (TypeDefinitionHandle)interfaceHandle),
					HandleKind.TypeReference => FunctionBreakpointSignatureTypeProvider.GetTypeName(peMetadataReader, (TypeReferenceHandle)interfaceHandle),
					HandleKind.TypeSpecification => peMetadataReader.GetTypeSpecification((TypeSpecificationHandle)interfaceHandle).DecodeSignature(new FunctionBreakpointSignatureTypeProvider(), null),
					_ => null
				};
				if (interfaceName is "System.Collections.IEnumerable" || interfaceName?.StartsWith("System.Collections.Generic.IEnumerable`1<", StringComparison.Ordinal) is true)
					return true;
			}
		}
		return false;
	}

	private async Task AddEnumerableResults(VariablesReference variablesReference, List<VariableInfo> result)
	{
		var linqModule = _modules.Values.FirstOrDefault(module => module.ModuleName is "System.Linq.dll");
		if (linqModule is null) throw new InvalidOperationException("System.Linq is not loaded");

		var proxyInstance = await CreateDebuggerProxyInstance(variablesReference.ObjectValue!, variablesReference.ThreadId, linqModule.Module, "System.Linq.SystemCore_EnumerableDebugView", []);
		try
		{
			var proxyObject = proxyInstance.UnwrapDebugValueToObject();
			await AddMembers(proxyInstance, proxyObject.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, false);
		}
		finally
		{
			if (proxyInstance is ICorDebugHandleValue handle) handle.TryDispose();
		}
	}

	/// Returns a bool indicating if a Static Members pseudo variable is required
	private async Task<bool> AddMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers = true)
	{
		var hasStaticMembers = false;
		var corDebugClass = corDebugType.Class;
		var module = corDebugClass.Module;
		var mdTypeDef = corDebugClass.Token;
		var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
		var mdFieldDefs = includeNonPublicMembers ? metadataImport.EnumFields(mdTypeDef) : metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
		var mdProperties = includeNonPublicMembers ? metadataImport.EnumProperties(mdTypeDef) : metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
		var staticFieldDefs = mdFieldDefs.AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
		var nonStaticFieldDefs = mdFieldDefs.AsValueEnumerable().Except(staticFieldDefs).ToArray();
		var staticProperties = mdProperties.AsValueEnumerable().Where(p => p.IsStatic(metadataImport)).ToArray();
		var nonStaticProperties = mdProperties.AsValueEnumerable().Except(staticProperties).ToArray();
		if (staticFieldDefs.Length > 0 || staticProperties.Length > 0)
		{
			hasStaticMembers = true;
		}

		await AddFields(nonStaticFieldDefs, metadataImport, corDebugType, corDebugValue, result, threadId, stackDepth);
		// We need to pass the un-unwrapped reference value here, as we need to invoke CallParameterizedFunction with the correct parameters
		await AddProperties(nonStaticProperties, metadataImport, corDebugClass, threadId, stackDepth, corDebugValue, result);

		// Handle members on base types recursively
		var baseType = corDebugType.Base;
		if (baseType is null) return hasStaticMembers;
		var baseTypeName = GetCorDebugTypeFriendlyName(baseType);
		if (baseTypeName is "System.Object" or "System.ValueType" or "System.Enum") return hasStaticMembers;
		return hasStaticMembers | await AddMembers(corDebugValue, baseType, threadId, stackDepth, result);
	}

	private async Task AddStaticMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
	{
		var corDebugClass = corDebugType.Class;
		var module = corDebugClass.Module;
		var mdTypeDef = corDebugClass.Token;
		var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
		var staticFieldDefs = metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
		var staticProperties = metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();

		await AddFields(staticFieldDefs, metadataImport, corDebugType, corDebugValue, result, threadId, stackDepth);
		// We need to pass the un-unwrapped reference value here, as we need to invoke CallParameterizedFunction with the correct parameters
		await AddProperties(staticProperties, metadataImport, corDebugClass, threadId, stackDepth, corDebugValue, result);

		// Handle members on base types recursively
		var baseType = corDebugType.Base;
		if (baseType is null) return;
		var baseTypeName = GetCorDebugTypeFriendlyName(baseType);
		if (baseTypeName is "System.Object" or "System.ValueType" or "System.Enum") return;
		await AddStaticMembers(corDebugValue, baseType, threadId, stackDepth, result);
	}

	private async Task AddFields(mdFieldDef[] mdFieldDefs, IMetaDataImport metadataImport, ICorDebugType corDebugType, ICorDebugValue corDebugValue, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var corDebugClass = corDebugType.Class;
		foreach (var mdFieldDef in mdFieldDefs)
		{
			var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
			var fieldName = fieldProps.szField;
			if (fieldName is null) continue;
			await WithFailureHandling(result, fieldName, async () =>
			{
				GeneratedNameParser.TryParseGeneratedName(fieldName, out var generatedNameKind, out var openBracketOffset, out var closeBracketOffset);
				if (generatedNameKind is GeneratedNameKind.HoistedLocalField)
				{
					// e.g. we are in an async method - local variables in the user's method are stored in fields on a generated class, e.g. "<intVar>5__1"
					// we want to extract "intVar"
					var originalLocalVariableName = fieldName.AsSpan()[(openBracketOffset + 1)..closeBracketOffset];
					fieldName = originalLocalVariableName.ToString();
				}
				else if (generatedNameKind is not GeneratedNameKind.None)
				{
					return;
				}
				var isStatic = fieldProps.pdwAttr.IsFdStatic();
				var isLiteral = fieldProps.pdwAttr.IsFdLiteral();
				var debuggerBrowsableRootHidden = false;
				var hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdFieldDef, "System.Diagnostics.DebuggerBrowsableAttribute", out var debuggerBrowsableAttributePointer, out var debuggerBrowsableAttributeSize) is Cor.S_OK;
				if (hasDebuggerBrowsableAttribute)
				{
					// https://github.com/Samsung/netcoredbg/blob/6476bc00c2beaab9255c750235a68de3a3d0cfae/src/debugger/evaluator.cpp#L913
					var debuggerBrowsableState = (DebuggerBrowsableState)GetDebuggerBrowsableCustomAttributeResultInt(debuggerBrowsableAttributePointer, debuggerBrowsableAttributeSize);
					if (debuggerBrowsableState == DebuggerBrowsableState.Never) return; // I may not end up doing this, as it would be ideal to still be able to hover the variable in the editor and see the value
					if (debuggerBrowsableState == DebuggerBrowsableState.RootHidden) debuggerBrowsableRootHidden = true;
				}
				if (isLiteral)
				{
					var literalValue = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag, fieldProps.pcchValue);
					var fieldType = GetFieldType(corDebugClass, mdFieldDef);
					var fieldTypeName = fieldType?.FriendlyName ?? GetFriendlyTypeName(fieldProps.pdwCPlusTypeFlag);
					var literalValueFormatted = literalValue switch
					{
						null => "null",
						bool b => b.ToString().ToLowerInvariant(),
						char c => $"{(int)c} '{c}'",
						string str => SymbolDisplay.FormatLiteral(str, quote: true),
						_ => literalValue.ToString()!
					};
					if (fieldType is { } declaredType && TryGetEnumDisplayValue(declaredType, literalValueFormatted, out var enumDisplayValue))
					{
						literalValueFormatted = enumDisplayValue;
					}
					var literalVariableInfo = new VariableInfo
					{
						Name = fieldName,
						Value = literalValueFormatted,
						Type = fieldTypeName,
						VariablesReference = 0
					};
					result.Add(literalVariableInfo);
					return;
				}

				var objectValue = corDebugValue.UnwrapDebugValueToObject();
				var fieldCorDebugValue = isStatic ? await GetStaticFieldValueAsync(corDebugType, mdFieldDef, threadId, stackDepth) : objectValue.GetFieldValue(corDebugClass, mdFieldDef);
				if (debuggerBrowsableRootHidden)
				{
					var unwrappedDebugValue = fieldCorDebugValue.UnwrapDebugValue();
					if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
					{
						await AddArrayElements(arrayValue, threadId, stackDepth, result);
						return;
					}
				}
				var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(fieldCorDebugValue, threadId, stackDepth, true);
				VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
				var variableInfo = new VariableInfo
				{
					Name = fieldName,
					Value = value,
					Type = friendlyTypeName,
					PresentationHint = variablePresentationHint,
					VariablesReference = GetVariablesReference(fieldCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
				};
				result.Add(variableInfo);
			});
		}
	}

	private (string FriendlyName, ModuleInfo Module, EntityHandle Handle)? GetFieldType(ICorDebugClass corDebugClass, mdFieldDef mdFieldDef)
	{
		if (_modules.TryGetValue(corDebugClass.Module.BaseAddress, out var moduleInfo) is false) return null;
		var handle = MetadataTokens.Handle(mdFieldDef);
		if (handle.Kind is not HandleKind.FieldDefinition) return null;
		var reader = moduleInfo.MetadataReader.PeMetadataReader;
		var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
		var typeName = field.DecodeSignature(new FunctionBreakpointSignatureTypeProvider(), null);
		var typeHandle = field.DecodeSignature(FieldTypeHandleProvider.Instance, null);
		return (ClassNameToMaybeLanguageAlias(typeName), moduleInfo, typeHandle);
	}

	internal class EvalException(string message) : Exception(message);
	private async Task AddProperties(mdProperty[] mdProperties, IMetaDataImport metadataImport, ICorDebugClass corDebugClass, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue corDebugValue, List<VariableInfo> result)
	{
		foreach (var mdProperty in mdProperties)
		{
			var propertyProps = metadataImport.GetPropertyProps(mdProperty);
			var propertyName = propertyProps.szProperty;
			if (propertyName is null) continue;
			await WithFailureHandling(result, propertyName, async () =>
			{
				var variablesReferenceIlFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);

				// Get the get method for the property
				var getMethodDef = propertyProps.pmdGetter;
				if (getMethodDef == 0) return; // No get method

				// Get method attributes to check if it's static
				var getterMethodProps = metadataImport.GetMethodProps(getMethodDef);
				var getterAttr = getterMethodProps.pdwAttr;

				var isStatic = getterAttr.IsMdStatic();

				var debuggerBrowsableRootHidden = false;
				var hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdProperty, "System.Diagnostics.DebuggerBrowsableAttribute", out var debuggerBrowsableAttributePointer, out var debuggerBrowsableAttributeSize) is Cor.S_OK;
				if (hasDebuggerBrowsableAttribute)
				{
					// https://github.com/Samsung/netcoredbg/blob/6476bc00c2beaab9255c750235a68de3a3d0cfae/src/debugger/evaluator.cpp#L913
					var debuggerBrowsableState = (DebuggerBrowsableState)GetDebuggerBrowsableCustomAttributeResultInt(debuggerBrowsableAttributePointer, debuggerBrowsableAttributeSize);
					if (debuggerBrowsableState == DebuggerBrowsableState.Never) return; // I may not end up doing this, as it would be ideal to still be able to hover the variable in the editor and see the value
					if (debuggerBrowsableState == DebuggerBrowsableState.RootHidden) debuggerBrowsableRootHidden = true;
				}

				var getMethod = corDebugClass.Module.GetFunctionFromToken(getMethodDef);
				var eval = variablesReferenceIlFrame.Chain.Thread.CreateEval();

				// May not be correct, will need further testing
				var parameterizedContainingType = corDebugValue.ExactType;

				var typeParameterTypes = parameterizedContainingType.TypeParameters;

				// For instance properties, pass the object; for static, pass nothing
				ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue];

				var returnValue = await eval.CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, getMethod, typeParameterTypes.Length, typeParameterTypes, corDebugValues.Length, corDebugValues);

				if (returnValue is null) return;
				var retainReturnValue = false;
				try
				{
					if (debuggerBrowsableRootHidden)
					{
						var unwrappedDebugValue = returnValue.UnwrapDebugValue();
						if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
						{
							await AddArrayElements(arrayValue, threadId, stackDepth, result);
							return;
						}
					}
					var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(returnValue, threadId, stackDepth, true);
					VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
					var variablesReference = GetVariablesReference(returnValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance);
					var variableInfo = new VariableInfo
					{
						Name = propertyName,
						Value = value,
						Type = friendlyTypeName,
						PresentationHint = variablePresentationHint,
						VariablesReference = variablesReference
					};
					retainReturnValue = variablesReference != 0;
					result.Add(variableInfo);
				}
				finally
				{
					if (!retainReturnValue && returnValue is ICorDebugHandleValue handle) handle.TryDispose();
				}
			});
		}
	}

	private async Task AddArrayElements(ICorDebugArrayValue arrayValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, uint[]? indexPrefix = null)
	{
		var rank = arrayValue.Rank;
		indexPrefix ??= [];
		var dimensions = arrayValue.GetDimensions(rank);
		var baseIndices = arrayValue.HasBaseIndicies() ? arrayValue.GetBaseIndicies(rank) : new uint[rank];
		var currentDimension = indexPrefix.Length;
		var currentDimensionStart = baseIndices[currentDimension];
		var currentDimensionLength = dimensions[currentDimension];

		if (currentDimension < rank - 1)
		{
			for (var offset = 0u; offset < currentDimensionLength; offset++)
			{
				uint[] indices = [.. indexPrefix, currentDimensionStart + offset];
				var name = $"[{string.Join(", ", indices)}, ...]";
				result.Add(new VariableInfo
				{
					Name = name,
					Value = "",
					Type = "",
					PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
					VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.ArrayRange, arrayValue, threadId, stackDepth, null, indices))
				});
			}
			return;
		}

		// Get the elements first, as the CorDebugArrayValue arrayValue may get neutered during 'await GetValueForCorDebugValueAsync' below, if any evals are required
		var elements = ValueEnumerable.Range(0, checked((int)currentDimensionLength))
			.Select(offset =>
			{
				uint[] indices = [.. indexPrefix, currentDimensionStart + checked((uint)offset)];
				return (Indices: indices, Element: arrayValue.GetElement(rank, indices));
			})
			.ToArray();
		foreach (var (indices, element) in elements)
		{
			var name = $"[{string.Join(", ", indices)}]";
			await WithFailureHandling(result, name, async () =>
			{
				var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(element, threadId, stackDepth, true);
				VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : new VariablePresentationHint { Kind = PresentationHintKind.Data };
				var variableReference = GetVariablesReference(element, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance);
				var variableInfo = new VariableInfo
				{
					Name = name,
					Type = friendlyTypeName,
					Value = value,
					PresentationHint = variablePresentationHint,
					VariablesReference = variableReference
				};
				result.Add(variableInfo);
			});
		}
	}

	private static async Task WithFailureHandling(List<VariableInfo> result, string fieldName, Func<Task> func)
	{
		try
		{
			await func();
		}
		catch (Exception ex)
		{
			result.Add(new VariableInfo
			{
				Name = fieldName,
				Value = ex.Message,
				Type = null,
				PresentationHint = new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation },
				VariablesReference = 0
			});
		}
	}
}

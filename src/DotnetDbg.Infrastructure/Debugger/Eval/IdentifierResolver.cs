using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public class IdentifierResolver
{
	private readonly EvalData _evalData;

	public IdentifierResolver(EvalData evalData)
	{
		_evalData = evalData;
	}

	public class ResolveResult
	{
		public CorDebugValue? Value { get; set; }
		public SetterData? SetterData { get; set; }
		public bool IsTypeResult { get; set; }
	}

	public async Task<ResolveResult> ResolveIdentifiersAsync(
		CorDebugValue? pInputValue,
		List<string> identifiers)
	{
		if (pInputValue != null && identifiers.Count == 0)
		{
			return new ResolveResult { Value = pInputValue, SetterData = null };
		}
		else if (pInputValue != null)
		{
			return await FollowFieldsAsync(pInputValue, ValueKind.ValueIsVariable, identifiers, 0);
		}

		int nextIdentifier = 0;
		CorDebugValue? pResolvedValue = null;
		CorDebugValue? pThisValue = null;

		if (identifiers[nextIdentifier] == "$exception")
		{
			var currentException = _evalData.Thread.CurrentException;
			if (currentException == null)
				throw new InvalidOperationException("No current exception");
			pResolvedValue = currentException;
		}
		else
		{
			await InternalWalkStackVarsAsync(async (name, getValue) =>
			{
				if (name == "this")
				{
					var value = await getValue();
					if (value == null)
						return;

					pThisValue = value;

					if (name == identifiers[nextIdentifier])
						throw new AbortWalkException();
				}
				else if (name == identifiers[nextIdentifier])
				{
					var value = await getValue();
					if (value == null)
						return;

					pResolvedValue = value;
					throw new AbortWalkException();
				}
			});
		}

		if (pResolvedValue == null && pThisValue != null)
		{
			if (identifiers[nextIdentifier] == "this")
				nextIdentifier++;

			var result = await FollowFieldsAsync(pThisValue, ValueKind.ValueIsVariable, identifiers, nextIdentifier);
			if (result.Value != null)
				return result;
		}

		if (pResolvedValue == null)
		{
			var pFrame = await GetFrameAtAsync(_evalData.FrameLevel);
			if (pFrame == null)
				throw new InvalidOperationException("Failed to get frame");

			var methodClass = await GetTypeAndMethodAsync(pFrame);
			if (methodClass == null)
				throw new InvalidOperationException("Failed to get type and method");

			var result = await FollowNestedFindValueAsync(methodClass, identifiers);
			if (result.Value != null)
				return result;

			var pResultType = await FollowNestedFindTypeAsync(methodClass, identifiers);
			if (pResultType != null)
				return new ResolveResult { IsTypeResult = true };
		}

		ValueKind valueKind;
		if (pResolvedValue != null)
		{
			nextIdentifier++;
			if (nextIdentifier == identifiers.Count)
				return new ResolveResult { Value = pResolvedValue };

			valueKind = ValueKind.ValueIsVariable;
		}
		else
		{
			var pType = await FindTypeAsync(identifiers, ref nextIdentifier);
			pResolvedValue = await CreateTypeObjectStaticConstructorAsync(pType);

			if (nextIdentifier == identifiers.Count)
				throw new ArgumentException("Type cannot be result");

			valueKind = ValueKind.ValueIsClass;
		}

		var finalResult = await FollowFieldsAsync(pResolvedValue, valueKind, identifiers, nextIdentifier);
		return finalResult;
	}

	private async Task<ResolveResult> FollowFieldsAsync(
		CorDebugValue pValue,
		ValueKind valueKind,
		List<string> identifiers,
		int nextIdentifier)
	{
		if (nextIdentifier > identifiers.Count)
			return new ResolveResult { Value = null };

		CorDebugValue? pResultValue = pValue;

		for (int i = nextIdentifier; i < identifiers.Count; i++)
		{
			if (string.IsNullOrEmpty(identifiers[i]))
				return new ResolveResult { Value = null };

			var pClassValue = pResultValue;
			CorDebugValue? tempResultValue = null;
			SetterData? tempSetterData = null;

			await InternalWalkMembersAsync(pClassValue, async (pType, isStatic, memberName, getValue, setterData) =>
			{
				if (isStatic && valueKind == ValueKind.ValueIsVariable)
					return;

				if (!isStatic && valueKind == ValueKind.ValueIsClass)
					return;

				if (memberName != identifiers[i])
					return;

				tempResultValue = await getValue();
				tempSetterData = setterData;
				throw new AbortWalkException();
			});

			if (tempResultValue == null)
				return new ResolveResult { Value = null };

			pResultValue = tempResultValue;
			valueKind = ValueKind.ValueIsVariable;
		}

		return new ResolveResult { Value = pResultValue, SetterData = null };
	}

	private async Task InternalWalkMembersAsync(
		CorDebugValue pInputValue,
		Func<CorDebugType?, bool, string, Func<Task<CorDebugValue?>>, SetterData?, Task> callback)
	{
		var pValue = await DereferenceAndUnboxValueAsync(pInputValue);
		if (pValue == null)
			return;

		var inputCorType = pValue.Type;
		if (inputCorType == CorElementType.Ptr)
		{
			await callback(null, false, "", async () => pValue, null);
			return;
		}

		if (pValue is CorDebugArrayValue pArrayValue)
		{
			var nRank = pArrayValue.GetRank();
			var cElements = pArrayValue.Count;
			var baseIndicies = new int[nRank];
			var ind = new int[nRank];
			var dims = new int[nRank];
			pArrayValue.GetDimensions(nRank, dims);

			for (int i = 0; i < cElements; i++)
			{
				await callback(null, false, $"[{IndicesToStr(ind, baseIndicies)}]", async () => pArrayValue.GetElementAtPosition(i), null);
				IncrementIndices(ind, dims);
			}
			return;
		}

		if (pValue is not CorDebugObjectValue pObjValue)
			return;

		var pType = pObjValue.ExactType;
		if (pType == null)
			throw new InvalidOperationException("Failed to get exact type");

		var className = await GetTypeNameAsync(pType);
		if (className == "decimal" || className.EndsWith("?"))
			return;

		var corElemType = pType.Type;
		if (corElemType == CorElementType.String)
			return;

		var pClass = pType.Class;
		if (pClass == null)
			throw new InvalidOperationException("Failed to get class");

		var pModule = pClass.Module;
		if (pModule == null)
			throw new InvalidOperationException("Failed to get module");

		var currentTypeDef = pClass.Token;

		var metaDataImport = pModule.GetMetaDataInterface().MetaDataImport;

		await WalkFieldsAsync(metaDataImport, currentTypeDef, pClass, pObjValue, pType, async (fieldDef, name, isStatic, getValue) =>
		{
			await callback(pType, isStatic, name, getValue, null);
		});

		await WalkPropertiesAsync(metaDataImport, currentTypeDef, pModule, pType, pObjValue, async (propertyDef, name, isStatic, getValue, setterData) =>
		{
			await callback(pType, isStatic, name, getValue, setterData);
		});

		var pBaseType = pType.Base;
		if (pBaseType != null)
		{
			var baseTypeName = await GetTypeNameAsync(pBaseType);
			if (baseTypeName != "System.Enum" && baseTypeName != "object" && baseTypeName != "System.Object" && baseTypeName != "System.ValueType")
			{
				await CreateTypeObjectStaticConstructorAsync(pBaseType);
				await InternalWalkMembersAsync(pInputValue, callback);
			}
		}
	}

	private async Task WalkFieldsAsync(
		MetaDataImport md,
		mdTypeDef currentTypeDef,
		CorDebugClass pClass,
		CorDebugObjectValue pObjectValue,
		CorDebugType pType,
		Func<mdFieldDef, string, bool, Func<Task<CorDebugValue?>>, Task> callback)
	{
		try
		{
			md.EnumFields(currentTypeDef).GetEnumerator();
		}
		catch { }

		var fieldDefs = md.EnumFields(currentTypeDef).ToArray();
		foreach (var fieldDef in fieldDefs)
		{
			var fieldProps = md.GetFieldProps(fieldDef);
			var fieldName = fieldProps.szField;
			if (!IsSynthesizedLocalName(fieldName))
			{
				bool isStatic = (fieldProps.dwAttr & CorFieldAttr.fdStatic) != 0;

				await callback(fieldDef, fieldName, isStatic, async () =>
				{
					if ((fieldProps.dwAttr & CorFieldAttr.fdLiteral) != 0)
						throw new NotImplementedException("Literal values not implemented");

					if (isStatic)
						return pType.GetStaticFieldValue(fieldDef, _evalData.ILFrame);

					return pObjectValue.GetFieldValue(pClass.Raw, fieldDef);
				});
			}
		}
	}

	private async Task WalkPropertiesAsync(
		MetaDataImport md,
		mdTypeDef currentTypeDef,
		CorDebugModule pModule,
		CorDebugType pType,
		CorDebugObjectValue pObjectValue,
		Func<mdProperty, string, bool, Func<Task<CorDebugValue?>>, SetterData?, Task> callback)
	{
		try
		{
			md.EnumProperties(currentTypeDef).GetEnumerator();
		}
		catch { }

		var propertyDefs = md.EnumProperties(currentTypeDef).ToArray();
		foreach (var propertyDef in propertyDefs)
		{
			var propertyProps = md.GetPropertyProps(propertyDef);
			var propertyName = propertyProps.szProperty;

			if (propertyProps.mdGetter.IsNil)
				continue;

			var getterProps = md.GetMethodProps(propertyProps.mdGetter);
			bool isStatic = (getterProps.dwAttr & CorMethodAttr.mdStatic) != 0;

			await callback(propertyDef, propertyName, isStatic, async () =>
			{
				var getterFunc = pModule.GetFunctionFromToken(propertyProps.mdGetter);
				var eval = _evalData.Thread.CreateEval();

				if (isStatic)
					return await eval.CallFunctionAsync(_evalData.ManagedCallback, getterFunc, pType, 0, null, _evalData.ILFrame);
				else
					return await eval.CallFunctionAsync(_evalData.ManagedCallback, getterFunc, pType, 1, new CorDebugValue[] { pObjectValue }, _evalData.ILFrame);
			}, null);
		}
	}

	private async Task InternalWalkStackVarsAsync(Func<string, Func<Task<CorDebugValue?>>, Task> callback)
	{
		var pFrame = await GetFrameAtAsync(_evalData.FrameLevel);
		if (pFrame == null)
			return;

		var pILFrame = pFrame as CorDebugILFrame;
		if (pILFrame == null)
			return;

		var pLocalsEnum = pILFrame.EnumerateLocalVariables();
		var cLocals = pLocalsEnum.GetCount();

		var pArgumentsEnum = pILFrame.EnumerateArguments();
		var cArguments = pArgumentsEnum.GetCount();

		for (uint i = 0; i < cLocals; i++)
		{
			var pLocal = await pILFrame.GetLocalVariableAsync(i);
			await callback($"local{i}", async () => pLocal);
		}

		for (uint i = 0; i < cArguments; i++)
		{
			var pArg = await pILFrame.GetArgumentAsync(i);
			await callback($"arg{i}", async () => pArg);
		}
	}

	private async Task<ResolveResult> FollowNestedFindValueAsync(
		string methodClass,
		List<string> identifiers)
	{
		return new ResolveResult { Value = null };
	}

	private async Task<CorDebugType?> FollowNestedFindTypeAsync(
		string methodClass,
		List<string> identifiers)
	{
		return null;
	}

	private async Task<CorDebugType> FindTypeAsync(List<string> identifiers, ref int nextIdentifier)
	{
		throw new NotImplementedException("Type lookup not implemented");
	}

	private async Task<CorDebugValue> CreateTypeObjectStaticConstructorAsync(CorDebugType pType)
	{
		var eval = _evalData.Thread.CreateEval();
		var pClass = pType.Class;
		return await eval.NewParameterizedObjectNoConstructorAsync(_evalData.ManagedCallback, pClass, 0, null, _evalData.ILFrame);
	}

	private async Task<CorDebugValue?> DereferenceAndUnboxValueAsync(CorDebugValue pValue)
	{
		if (pValue is CorDebugReferenceValue refValue)
		{
			if (!refValue.IsNull)
			{
				return refValue.Dereference();
			}
			else
			{
				return null;
			}
		}

		if (pValue is CorDebugBoxValue boxValue)
		{
			return boxValue.Object;
		}

		return pValue;
	}

	private async Task<string> GetTypeNameAsync(CorDebugType pType)
	{
		var pClass = pType.Class;
		if (pClass == null)
			return "";

		var pModule = pClass.Module;
		var md = pModule.GetMetaDataInterface().MetaDataImport;
		var typeDefProps = md.GetTypeDefProps(pClass.Token);
		return typeDefProps.szTypeDef;
	}

	private async Task<string?> GetTypeAndMethodAsync(CorDebugFrame pFrame)
	{
		var pFunction = pFrame.Function;
		if (pFunction == null)
			return null;

		var pClass = pFunction.Class;
		if (pClass == null)
			return null;

		var pModule = pClass.Module;
		var md = pModule.GetMetaDataInterface().MetaDataImport;
		var typeDefProps = md.GetTypeDefProps(pClass.Token);
		return typeDefProps.szTypeDef;
	}

	private async Task<CorDebugFrame?> GetFrameAtAsync(int frameLevel)
	{
		var pChain = _evalData.Thread.ActiveChain;
		if (pChain == null)
			return null;

		var pFramesEnum = pChain.EnumerateFrames();
		var currentLevel = 0;

		while (true)
		{
			var pFrame = pFramesEnum.Next();
			if (pFrame == null)
				break;

			if (currentLevel == frameLevel)
				return pFrame;

			currentLevel++;
		}

		return null;
	}

	private bool IsSynthesizedLocalName(string name)
	{
		if (name.Length > 1 && name.StartsWith("<"))
			return true;
		if (name.Length > 4 && name.StartsWith("CS$<"))
			return true;
		return false;
	}

	private void IncrementIndices(int[] ind, int[] dims)
	{
		int i = ind.Length - 1;
		while (i >= 0)
		{
			ind[i]++;
			if (ind[i] < dims[i])
				return;
			ind[i] = 0;
			i--;
		}
	}

	private string IndicesToStr(int[] ind, int[] @base)
	{
		if (ind.Length < 1 || @base.Length != ind.Length)
			return "";

		var parts = new List<string>();
		for (int i = 0; i < ind.Length; i++)
		{
			parts.Add((@base[i] + ind[i]).ToString());
		}
		return string.Join(", ", parts);
	}

	public enum ValueKind
	{
		ValueIsVariable,
		ValueIsClass
	}

	private class AbortWalkException : Exception { }
}

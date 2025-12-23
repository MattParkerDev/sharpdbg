using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public class IdentifierResolver
{
	private readonly EvalData _evalData;

	public IdentifierResolver(EvalData evalData)
	{
		_evalData = evalData;
	}

	public async Task<CorDebugValue?> ResolveIdentifiersAsync(
		CorDebugValue? pInputValue,
		List<string> identifiers,
		out SetterData? resultSetterData)
	{
		resultSetterData = null;
		SetterData? inputSetterData = null;
		CorDebugType? pResultType = null;

		if (pInputValue != null && identifiers.Count == 0)
		{
			resultSetterData = inputSetterData;
			return pInputValue;
		}
		else if (pInputValue != null)
		{
			return await FollowFieldsAsync(pInputValue, ValueKind.ValueIsVariable, identifiers, 0, out resultSetterData);
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

			var result = await FollowFieldsAsync(pThisValue, ValueKind.ValueIsVariable, identifiers, nextIdentifier, out resultSetterData);
			if (result != null)
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

			var result = await FollowNestedFindValueAsync(methodClass, identifiers, out resultSetterData);
			if (result != null)
				return result;

			pResultType = await FollowNestedFindTypeAsync(methodClass, identifiers);
			if (pResultType != null)
				throw new IdentifierResolvedToTypeException();
		}

		ValueKind valueKind;
		if (pResolvedValue != null)
		{
			nextIdentifier++;
			if (nextIdentifier == identifiers.Count)
				return pResolvedValue;

			valueKind = ValueKind.ValueIsVariable;
		}
		else
		{
			var pType = await FindTypeAsync(identifiers, ref nextIdentifier);
			pResolvedValue = await CreateTypeObjectStaticConstructorAsync(pType);

			if (pResultType != null && nextIdentifier == identifiers.Count)
				throw new IdentifierResolvedToTypeException();

			if (nextIdentifier == identifiers.Count)
				throw new ArgumentException("Type cannot be result");

			valueKind = ValueKind.ValueIsClass;
		}

		return await FollowFieldsAsync(pResolvedValue, valueKind, identifiers, nextIdentifier, out resultSetterData);
	}

	private async Task<CorDebugValue?> FollowFieldsAsync(
		CorDebugValue pValue,
		ValueKind valueKind,
		List<string> identifiers,
		int nextIdentifier,
		out SetterData? resultSetterData)
	{
		resultSetterData = null;

		if (nextIdentifier > identifiers.Count)
			return null;

		CorDebugValue? pResultValue = pValue;

		for (int i = nextIdentifier; i < identifiers.Count; i++)
		{
			if (string.IsNullOrEmpty(identifiers[i]))
				return null;

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
				return null;

			pResultValue = tempResultValue;
			resultSetterData = tempSetterData;
			valueKind = ValueKind.ValueIsVariable;
		}

		return pResultValue;
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
			var cElements = pArrayValue.GetCount();
			var dims = new uint[nRank];
			pArrayValue.GetDimensions(nRank, dims);

			var baseIndicies = new uint[nRank];
			var hasBaseIndicies = false;
			pArrayValue.HasBaseIndicies(out hasBaseIndicies);
			if (hasBaseIndicies)
				pArrayValue.GetBaseIndicies(nRank, baseIndicies);

			var ind = new uint[nRank];
			for (uint i = 0; i < cElements; i++)
			{
				uint index = i;
				await callback(null, false, $"[{IndicesToStr(ind, baseIndicies)}]", async () => pArrayValue.GetElementAtPosition(index), null);
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

		var pClass = pType.GetClass();
		if (pClass == null)
			throw new InvalidOperationException("Failed to get class");

		var pModule = pClass.GetModule();
		if (pModule == null)
			throw new InvalidOperationException("Failed to get module");

		var currentTypeDef = pClass.Token;

		var mdUnknown = pModule.GetMetaDataInterface(typeof(IMetaDataImport).GUID);
		var md = (IMetaDataImport)mdUnknown;

		await WalkFieldsAsync(md, currentTypeDef, pClass, pObjValue, async (fieldDef, name, isStatic, getValue) =>
		{
			await callback(pType, isStatic, name, getValue, null);
		});

		await WalkPropertiesAsync(md, currentTypeDef, pModule, pType, pObjValue, async (propertyDef, name, isStatic, getValue, setterData) =>
		{
			await callback(pType, isStatic, name, getValue, setterData);
		});

		var pBaseType = pType.GetBase();
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
		IMetaDataImport md,
		uint currentTypeDef,
		CorDebugClass pClass,
		CorDebugObjectValue pObjectValue,
		Func<uint, string, bool, Func<Task<CorDebugValue?>>, Task> callback)
	{
		IntPtr hEnum = IntPtr.Zero;
		try
		{
			md.EnumFields(ref hEnum, currentTypeDef, null, 0, out _);
			while (true)
			{
				uint[] fieldDefs = new uint[1];
				int fetched;
				md.EnumFields(ref hEnum, currentTypeDef, fieldDefs, 1, out fetched);
				if (fetched == 0)
					break;

				uint fieldDef = fieldDefs[0];
				uint pTypeDef;
				uint nameLen;
				char[] name = new char[1024];
				uint fieldAttr;
				uint[] pSigBlob = new uint[1];
				uint sigBlobLength;
				IntPtr pRawValue;
				uint rawValueLength;

				md.GetFieldProps(fieldDef, out pTypeDef, name, (uint)name.Length, out nameLen, out fieldAttr,
					pSigBlob, out sigBlobLength, null, out pRawValue, out rawValueLength);

				var fieldName = new string(name, 0, (int)nameLen);
				if (!IsSynthesizedLocalName(fieldName))
				{
					bool isStatic = (fieldAttr & 0x10) != 0;

					await callback(fieldDef, fieldName, isStatic, async () =>
					{
						if ((fieldAttr & 0x40) != 0)
							throw new NotImplementedException("Literal values not implemented");

						if (isStatic)
							return pType.GetStaticFieldValue(fieldDef, _evalData.ILFrame);

						return pObjectValue.GetFieldValue(pClass, fieldDef);
					});
				}
			}
		}
		finally
		{
			if (hEnum != IntPtr.Zero)
				md.CloseEnum(hEnum);
		}
	}

	private async Task WalkPropertiesAsync(
		IMetaDataImport md,
		uint currentTypeDef,
		CorDebugModule pModule,
		CorDebugType pType,
		CorDebugObjectValue pObjectValue,
		Func<uint, string, bool, Func<Task<CorDebugValue?>>, SetterData?, Task> callback)
	{
		IntPtr hEnum = IntPtr.Zero;
		try
		{
			md.EnumProperties(ref hEnum, currentTypeDef, null, 0, out _);
			while (true)
			{
				uint[] propertyDefs = new uint[1];
				int fetched;
				md.EnumProperties(ref hEnum, currentTypeDef, propertyDefs, 1, out fetched);
				if (fetched == 0)
					break;

				uint propertyDef = propertyDefs[0];
				uint pTypeDef;
				uint nameLen;
				char[] name = new char[1024];
				uint getterAttr;
				uint pSetter;
				uint pGetter;

				md.GetPropertyProps(propertyDef, out pTypeDef, name, (uint)name.Length, out nameLen,
					null, null, null, null, null, null, out pSetter, out pGetter, null, 0, null);

				md.GetMethodProps(pGetter, null, null, 0, null, out getterAttr, null, null, null, null);

				var propertyName = new string(name, 0, (int)nameLen);
				bool isStatic = (getterAttr & 0x10) != 0;

				await callback(propertyDef, propertyName, isStatic, async () =>
				{
					var getterFunc = pModule.GetFunctionFromToken(pGetter);
					var eval = _evalData.Thread.CreateEval();

					if (isStatic)
						return await eval.CallFunctionAsync(_evalData.ManagedCallback, getterFunc, pType, 0, null, _evalData.ILFrame);
					else
						return await eval.CallFunctionAsync(_evalData.ManagedCallback, getterFunc, pType, 1, new CorDebugValue[] { pObjectValue }, _evalData.ILFrame);
				}, null);
			}
		}
		finally
		{
			if (hEnum != IntPtr.Zero)
				md.CloseEnum(hEnum);
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

	private async Task<CorDebugValue?> FollowNestedFindValueAsync(
		string methodClass,
		List<string> identifiers,
		out SetterData? resultSetterData)
	{
		resultSetterData = null;
		return null;
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
		var pClass = pType.GetClass();
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
		var pClass = pType.GetClass();
		if (pClass == null)
			return "";

		var pModule = pClass.GetModule();
		var mdUnknown = pModule.GetMetaDataInterface(typeof(IMetaDataImport).GUID);
		var md = (IMetaDataImport)mdUnknown;

		var typeDef = pClass.Token;
		uint nameLen;
		char[] name = new char[1024];
		md.GetTypeDefProps(typeDef, name, (uint)name.Length, out nameLen, null, null);

		return new string(name, 0, (int)nameLen);
	}

	private async Task<string?> GetTypeAndMethodAsync(CorDebugFrame pFrame)
	{
		var pFunction = pFrame.GetFunction();
		if (pFunction == null)
			return null;

		var pClass = pFunction.GetClass();
		if (pClass == null)
			return null;

		var pModule = pClass.GetModule();
		var mdUnknown = pModule.GetMetaDataInterface(typeof(IMetaDataImport).GUID);
		var md = (IMetaDataImport)mdUnknown;

		var typeDef = pClass.Token;
		uint nameLen;
		char[] name = new char[1024];
		md.GetTypeDefProps(typeDef, name, (uint)name.Length, out nameLen, null, null);

		return new string(name, 0, (int)nameLen);
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

	private void IncrementIndices(uint[] ind, uint[] dims)
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

	private string IndicesToStr(uint[] ind, uint[] @base)
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
	public class IdentifierResolvedToTypeException : Exception { }
}

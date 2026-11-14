using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Roslyn writes no async stepping information for runtime-async methods (.NET 11), so we find their awaits from calls to
/// AsyncHelpers.Await in the IL
/// </summary>
public partial class ModuleMetadataReader
{
	/// <summary>
	/// An await of a runtime-async method: the instruction before it (the call it awaits, when it awaits a call), the call to the runtime's
	/// await helper, and the IL after it, where the method goes on once what it awaited is done
	/// </summary>
	public readonly record struct RuntimeAwait(int Before, int Call, int After);

	/// <summary>Distinguishes a return after the await from an unknown resume offset in <see cref="ResumeAfter"/></summary>
	public const int Returns = -1;

	/// <summary>MethodImplAttributes.Async, which the metadata reader's enum does not name</summary>
	private const int RuntimeAsyncFlag = 0x2000;

	private readonly Dictionary<int, IReadOnlyList<CilInstruction>?> _decodedMethods = new();

	public bool IsRuntimeAsync(int methodToken) =>
		MetadataTokens.EntityHandle(methodToken).Kind == HandleKind.MethodDefinition
		&& ((int)_peMetadataReader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(methodToken)).ImplAttributes & RuntimeAsyncFlag) != 0;

	/// <summary>A runtime-async method's awaits in IL order: its calls to AsyncHelpers' Await methods. None for any other method</summary>
	public IReadOnlyList<RuntimeAwait> RuntimeAwaits(int methodToken)
	{
		if (IsRuntimeAsync(methodToken) is false || Instructions(methodToken) is not { } instructions) return [];
		var awaits = new List<RuntimeAwait>();
		for (var i = 0; i + 1 < instructions.Count; i++)
		{
			if (IsRuntimeAwait(instructions[i]))
			{
				awaits.Add(new RuntimeAwait(i > 0 ? instructions[i - 1].Offset : -1, instructions[i].Offset, instructions[i + 1].Offset));
			}
		}
		return awaits;
	}

	/// <summary>
	/// A call awaited directly shares the caller's continuation chain. A call through a task-returning thunk starts its own chain,
	/// so an intervening call means the next await cannot identify the current callee's caller
	/// </summary>
	public RuntimeAwait? AwaitOfCallAt(int methodToken, int ip)
	{
		if (RuntimeAwaits(methodToken).FirstOrDefault(awaited => awaited.Call >= ip) is not { Call: > 0 } next || Instructions(methodToken) is not { } instructions) return null;
		var calledFirst = instructions.Any(instruction => instruction.Offset >= ip && instruction.Offset < next.Before && IsCall(instruction.OpCode));
		return calledFirst ? null : next;
	}

	/// <summary>
	/// A breakpoint needs a mapped IL offset, which may be past the await's statement in optimized code. Follow unconditional branches
	/// to find one; return <see cref="Returns"/> if the method ends first, or null if the resume location cannot be determined
	/// </summary>
	public int? ResumeAfter(int methodToken, RuntimeAwait awaited, IReadOnlySet<int> mapped)
	{
		if (Instructions(methodToken) is not { } instructions) return null;
		var index = instructions.Select((instruction, at) => (instruction.Offset, at)).ToDictionary();
		var passed = new HashSet<int>();
		var offset = awaited.After;
		while (passed.Add(offset) && index.TryGetValue(offset, out var at))
		{
			if (mapped.Contains(offset)) return offset;
			var instruction = instructions[at];
			switch (instruction.OpCode.FlowControl)
			{
				case FlowControl.Return when instruction.OpCode == OpCodes.Ret:
					return Returns;
				case FlowControl.Branch when instruction.Operand is int target:
					offset = target;
					break;
				case FlowControl.Next or FlowControl.Call or FlowControl.Meta when at + 1 < instructions.Count:
					offset = instructions[at + 1].Offset;
					break;
				default:
					return null;
			}
		}
		return null;
	}

	/// <summary>
	/// Both a resumed await and one that completed synchronously call GetResult, so it identifies where their paths rejoin
	/// </summary>
	public int? AwaiterGetResultCallAfter(int methodToken, int resumeOffset)
	{
		if (Instructions(methodToken) is not { } instructions) return null;
		return instructions.FirstOrDefault(instruction => instruction.Offset >= resumeOffset && IsCall(instruction.OpCode) && instruction.Operand is int token
			&& CalledMethodName(token) is "GetResult") is { } call ? call.Offset : null;
	}

	/// <summary>With no visible sequence point left, stepping over or into must be treated as stepping out</summary>
	public bool HasNoVisibleSequencePointFrom(int methodToken, int ilOffset)
	{
		if (_pdbMetadataReader is null) return false;
		var debugInfo = _pdbMetadataReader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(methodToken));
		if (debugInfo.SequencePointsBlob.IsNil) return false;
		return debugInfo.GetSequencePoints().Any(point => point.IsHidden is false && point.StartLine != 0 && point.Offset >= ilOffset) is false;
	}

	public bool IsVisibleSequencePointStart(int methodToken, int ilOffset)
	{
		if (_pdbMetadataReader is null) return false;
		var debugInfo = _pdbMetadataReader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(methodToken));
		if (debugInfo.SequencePointsBlob.IsNil) return false;
		return debugInfo.GetSequencePoints().Any(point => point.IsHidden is false && point.StartLine != 0 && point.Offset == ilOffset);
	}

	private IReadOnlyList<CilInstruction>? Instructions(int methodToken)
	{
		if (_decodedMethods.TryGetValue(methodToken, out var decoded)) return decoded;
		try
		{
			var method = _peMetadataReader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(methodToken));
			decoded = method.RelativeVirtualAddress is 0 ? null : CilInstructionDecoder.Decode(_peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []);
		}
		catch (Exception)
		{
			decoded = null;
		}
		_decodedMethods[methodToken] = decoded;
		return decoded;
	}

	private static bool IsCall(OpCode opCode) => opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Calli || opCode == OpCodes.Newobj;

	private bool IsRuntimeAwait(CilInstruction instruction) =>
		instruction.OpCode == OpCodes.Call && instruction.Operand is int token && DescribeCall(token) is ("AsyncHelpers", var name)
		&& (name.StartsWith("Await", StringComparison.Ordinal) || name.StartsWith("UnsafeAwait", StringComparison.Ordinal));

	/// <summary>GetResult may belong to a generic awaiter whose declaring type is a type specification; only its name is needed</summary>
	private string? CalledMethodName(int token)
	{
		var handle = MetadataTokens.EntityHandle(token);
		return handle.Kind switch
		{
			HandleKind.MethodSpecification => CalledMethodName(MetadataTokens.GetToken(_peMetadataReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
			HandleKind.MethodDefinition => _peMetadataReader.GetString(_peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
			HandleKind.MemberReference => _peMetadataReader.GetString(_peMetadataReader.GetMemberReference((MemberReferenceHandle)handle).Name),
			_ => null
		};
	}

	private (string DeclaringType, string Name)? DescribeCall(int token)
	{
		var handle = MetadataTokens.EntityHandle(token);
		switch (handle.Kind)
		{
			case HandleKind.MethodSpecification:
				return DescribeCall(MetadataTokens.GetToken(_peMetadataReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method));
			case HandleKind.MethodDefinition:
			{
				var method = _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
				return (GetTypeName(method.GetDeclaringType()).Name, _peMetadataReader.GetString(method.Name));
			}
			case HandleKind.MemberReference:
			{
				var member = _peMetadataReader.GetMemberReference((MemberReferenceHandle)handle);
				var name = _peMetadataReader.GetString(member.Name);
				return member.Parent.Kind switch
				{
					HandleKind.TypeReference => (GetTypeName((TypeReferenceHandle)member.Parent).Name, name),
					HandleKind.TypeDefinition => (GetTypeName((TypeDefinitionHandle)member.Parent).Name, name),
					_ => null
				};
			}
			default:
				return null;
		}
	}
}

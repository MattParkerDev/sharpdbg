using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	// e.g. localVar, or localVar.Field1.Field2, or ClassName.StaticField.SubField
	// optionalInputValue may be provided, e.g. in the case of where the value was created in the evaluation and does not exist
	// as a local in the stack frame.
	private CorDebugValue ResolveIdentifiers(List<string> identifiers, CorDebugThread thread, FrameStackDepth stackDepth, CorDebugValue? optionalInputValue)
	{
		if (identifiers.Count is 0) throw new ArgumentException("Identifiers list cannot be empty", nameof(identifiers));
		var rootValue = optionalInputValue;
		if (rootValue is null)
		{
			rootValue = ResolveIdentifier(identifiers[0], thread, stackDepth);
			if (rootValue is null) throw new InvalidOperationException("Identifier value is null. Even if the identifier could not be resolved, an exception should have been thrown, returned as the CorDebugValue");
		}
	}

	private CorDebugValue ResolveIdentifier(string identifier, CorDebugThread thread, FrameStackDepth stackDepth)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identifier, nameof(identifier));
		var frame = thread.ActiveChain.Frames[stackDepth.Value];
		// Try
		// 1. Stack variable, e.g. local variable or argument
		// 2. Field or property of 'this' if available (instance or static)
		// 3. Identifier as static class name
		var resolvedValue = ResolveIdentifierAsStackVariable(identifier, thread, stackDepth);
		if (resolvedValue is not null) return resolvedValue;

	}

	private CorDebugValue? ResolveIdentifierAsStackVariable(string identifier, CorDebugThread thread, FrameStackDepth stackDepth)
	{
		var frame = (CorDebugILFrame)thread.ActiveChain.Frames[stackDepth.Value];
		var corDebugFunction = frame.Function;
		var module = _modules[corDebugFunction.Module.BaseAddress];

		foreach (var (index, local) in frame.LocalVariables.Index())
		{
			var localVariableName = module.SymbolReader?.GetLocalVariableName(corDebugFunction.Token, index);
			if (localVariableName is null) continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
			if (localVariableName == identifier)
			{
				return local;
			}
		}
		
		foreach (var arg in frame.Arguments)
		{
			if (arg.Name == identifier)
			{
				return arg;
			}
		}
		return null;
	}
}

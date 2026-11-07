using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Breakpoints;

public record FunctionBreakpointBinding(
	ICorDebugFunctionBreakpoint CorBreakpoint,
	CORDB_ADDRESS ModuleBaseAddress,
	int MethodToken,
	ModuleMetadataReader.ResolvedBreakpoint Source);

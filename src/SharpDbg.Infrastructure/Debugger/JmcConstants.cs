namespace SharpDbg.Infrastructure.Debugger;

public static class JmcConstants
{
	public static readonly string[] JmcTypeAttributeNames =
	[
		"System.Diagnostics.DebuggerNonUserCodeAttribute",
		"System.Diagnostics.DebuggerStepThroughAttribute",
	];
	public static readonly string[] JmcMethodAttributeNames =
	[
		"System.Diagnostics.DebuggerNonUserCodeAttribute",
		"System.Diagnostics.DebuggerStepThroughAttribute",
		"System.Diagnostics.DebuggerHiddenAttribute"
	];
}

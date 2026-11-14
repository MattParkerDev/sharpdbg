namespace SharpDbg.Infrastructure.Debugger;

public static class AttributeConstants
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
	public const string ExtensionMethodAttributeName = "System.Runtime.CompilerServices.ExtensionAttribute";
	public const string DebuggerHiddenAttributeName = "System.Diagnostics.DebuggerHiddenAttribute";
	public const string StackTraceHiddenAttributeName = "System.Diagnostics.StackTraceHiddenAttribute";
}

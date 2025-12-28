using System.Diagnostics;

namespace DebuggableConsoleApp;

[DebuggerDisplay("{IntProperty}")]
public class ClassWithDebugDisplay
{
	public int IntProperty { get; set; } = 10;
}

using System.Diagnostics;

namespace DebuggableConsoleApp;

[DebuggerDisplay("IntProperty = {IntProperty}")]
public class ClassWithDebugDisplay
{
	public int IntProperty { get; set; } = 14;
}

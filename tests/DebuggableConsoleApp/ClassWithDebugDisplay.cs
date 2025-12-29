using System.Diagnostics;

namespace DebuggableConsoleApp;

[DebuggerDisplay("IntProperty = {IntProperty,nq}")]
[DebuggerTypeProxy(typeof(ClassWithDebugDisplayDebugView))]
public class ClassWithDebugDisplay
{
	public int IntProperty { get; set; } = 14;
}

public class ClassWithDebugDisplayDebugView
{
	private readonly ClassWithDebugDisplay _instance;

	public ClassWithDebugDisplayDebugView(ClassWithDebugDisplay instance)
	{
		_instance = instance ?? throw new ArgumentNullException(nameof(instance));
	}

	public int IntPropertyViaDebugView => _instance.IntProperty;

	//[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
}

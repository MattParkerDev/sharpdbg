using System.Diagnostics;

namespace DebuggableConsoleApp;

public class DebuggerBrowsableRootHidden
{
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public DebuggerBrowsableRootHiddenInner Field = new();
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public DebuggerBrowsableRootHiddenInner? NullField = null;
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public int PrimitiveField = 5;
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public List<int> ListField = [4, 5];
	public int Other = 7;
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public DebuggerBrowsableRootHiddenInner Property => new();

	public static void Test()
	{
		var rootHidden = new DebuggerBrowsableRootHidden();
		var rootHiddenCycle = new DebuggerBrowsableRootHiddenCycle();
		;
		;
	}
}

public class DebuggerBrowsableRootHiddenInner
{
	public int A = 1;
	public string B = "b";
}

public class DebuggerBrowsableRootHiddenCycle
{
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public DebuggerBrowsableRootHiddenCycle Self => this;
	public int Other = 7;
}

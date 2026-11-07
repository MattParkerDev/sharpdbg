using System.Diagnostics;

namespace DebuggableConsoleApp;

[DebuggerDisplay("Bool = {BoolField}, String = {StringField}, NoQuotes = {StringField,nq}, Null = {NullField}, NullableInt = {NullableIntField}, Enum = {EnumField}, Char = {CharField}, Add = {Add(1, 2)}, Nested = {NestedField}, Missing = {Missing}, Recursive = {RecursiveField}")]
public class DebuggerDisplayFormatting
{
	public bool BoolField = true;
	public string StringField = "text";
	public string? NullField = null;
	public int? NullableIntField = null;
	public DayOfWeek EnumField = DayOfWeek.Monday;
	public char CharField = 'x';
	public DebuggerDisplayFormattingNested NestedField = new();
	public DebuggerDisplayFormattingRecursive RecursiveField = new();
	public int Add(int a, int b) => a + b;

	public static void Test()
	{
		var debuggerDisplayFormatting = new DebuggerDisplayFormatting();
		var lazy = new Lazy<int>(() => 99);
		_ = lazy.Value;
		var braced = new DebuggerDisplayFormattingBraced();
		var linkTail = new DebuggerDisplayFormattingLink("tail", null);
		var derived = new DebuggerDisplayFormattingDerived();
		Console.WriteLine(debuggerDisplayFormatting);
	}
}

[DebuggerDisplay("Value = {Value}")]
public class DebuggerDisplayFormattingNested
{
	public string Value = "nested";
}

[DebuggerDisplay("{this}")]
public class DebuggerDisplayFormattingRecursive;

[DebuggerDisplay(@"\{{Value}\}")]
public class DebuggerDisplayFormattingBraced
{
	public int Value = 3;
}

[DebuggerDisplay("{_name,nq} -> {Next._name,nq}")]
public class DebuggerDisplayFormattingLink(string name, DebuggerDisplayFormattingLink? next)
{
	private string _name = name;
	public DebuggerDisplayFormattingLink? Next = next;
}

[DebuggerDisplay("base {Tag,nq}")]
public class DebuggerDisplayFormattingBase
{
	public string Tag = "b";
}

public class DebuggerDisplayFormattingDerived : DebuggerDisplayFormattingBase
{
	public int Extra = 9;
}

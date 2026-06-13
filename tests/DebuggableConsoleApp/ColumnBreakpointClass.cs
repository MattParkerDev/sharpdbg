namespace DebuggableConsoleApp;

public class ColumnBreakpointClass
{
	private int _sink;

	public void Test()
	{
		var first = 1; var second = first + 1; var third = second + 1; // column-breakpoint-line
		_sink = third;

		var multi =
			1 + 25 +
			12 + 5; var after = multi + 1; // column-breakpoint-multiline
		_sink += after;
	}
}

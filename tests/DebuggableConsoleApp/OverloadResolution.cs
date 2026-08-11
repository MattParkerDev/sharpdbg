namespace DebuggableConsoleApp;

public class OverloadResolutionGeneric<T>
{
	public int Pick(T value) => -100;
	public int Pick(string value) => value.Length;
}

public static class OverloadResolutionByRef
{
	public static int Pick(ref string value) => -100;
	public static int Pick(ref int value) => value;
}

namespace DebuggableConsoleApp;

public class MyClassWithGenericMethod
{
	public T Test<T>(T arg)
	{
		return arg;
	}
}

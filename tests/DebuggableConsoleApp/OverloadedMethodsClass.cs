namespace DebuggableConsoleApp;

public static class OverloadedMethodsClass
{
	public static void CallAllMethods()
	{
		OverloadedMethod();
		OverloadedMethod(42);
		OverloadedMethod(4.0f);
		OverloadedMethod(null);
		OverloadedMethod(42, null);
	}
	public static void OverloadedMethod()
	{
		;
	}
	public static void OverloadedMethod(int intParam)
	{
		;
	}
	public static void OverloadedMethod(double doubleParam)
	{
		;
	}
	public static void OverloadedMethod(MyClass? myClass)
	{
		;
	}
	public static void OverloadedMethod(int intParam, MyClass? myClass)
	{
		;
	}
}

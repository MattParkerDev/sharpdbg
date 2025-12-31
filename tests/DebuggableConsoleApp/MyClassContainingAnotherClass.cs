namespace DebuggableConsoleApp;

public class MyClassContainingAnotherClass
{
	public class MyNestedClass
	{
		public int NestedValue { get; set; }
	}
}


public class MyGenericClassContainingAnotherGenericClass<T, U>
{
	public class MyNestedGenericClass<TInner, UInner>
	{
		public int NestedValue { get; set; }
	}
}

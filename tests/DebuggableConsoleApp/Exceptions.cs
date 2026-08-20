using System.Net.Sockets;

namespace DebuggableConsoleApp;

public static class Exceptions
{
	public static void Test(ExceptionToThrow exceptionToThrow)
	{
		if (exceptionToThrow is ExceptionToThrow.None) return;
		try
		{
			if (exceptionToThrow is ExceptionToThrow.Normal)
			{
				throw new InvalidOperationException("Test exception");
			}
			else if (exceptionToThrow is ExceptionToThrow.ExternalCode)
			{
				var myInt = int.Parse("x");
			}
			else if (exceptionToThrow is ExceptionToThrow.HandledWithinExternalCode)
			{
				// Path.GetFullPath throws for the null character, which File.Exists catches internally.
				File.Exists("\0");
			}
			else if (exceptionToThrow is ExceptionToThrow.UserUnhandled)
			{
				var numbers = new List<int> { 3, 1, 2 };
				// List.Sort catches the comparer exception in library code before propagating a wrapper to user code.
				numbers.Sort((_, _) => throw new InvalidOperationException("User-unhandled test exception"));
			}
		}
		catch (Exception e)
		{
			;
		}
	}
}

public enum ExceptionToThrow
{
	None = 0,
	Normal,
	ExternalCode,
	HandledWithinExternalCode,
	UserUnhandled,
}

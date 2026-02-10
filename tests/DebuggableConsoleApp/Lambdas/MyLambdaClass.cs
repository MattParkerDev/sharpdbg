namespace DebuggableConsoleApp.Lambdas;

public class MyLambdaClass
{
    public int Test()
    {
	    var test = "asdf";
	    Func<int, int> square = x =>
		{
			int result = x * x; // set breakpoint here
			return result;
		};
		int value = 5;
		int squaredValue = square(value);
		return value;
    }
}

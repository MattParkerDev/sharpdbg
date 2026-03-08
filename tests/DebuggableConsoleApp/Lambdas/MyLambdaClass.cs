namespace DebuggableConsoleApp.Lambdas;

public class MyLambdaClass
{
    public int Test()
    {
	    var uncapturedString = "uncaptured";
	    var test = "asdf";
	    Func<int, int> square = x =>
		{
			var capturedString = test;
			var capturedIntField = _capturedIntField;
			int result = x * x; // set breakpoint here
			return result;
		};
		int value = 5;
		int squaredValue = square(value);
		return value;
    }
    private int _capturedIntField = 4;
    private int _uncapturedIntField = 5;
}

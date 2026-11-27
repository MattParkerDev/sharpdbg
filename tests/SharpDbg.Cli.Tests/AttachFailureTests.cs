using AwesomeAssertions;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public class AttachFailureTests
{
	[Fact]
	public async Task ConfigurationDone_WhenAttachRegistrationFails_EndsTheSession()
	{
		var debugger = new ManagedDebugger();
		var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var output = new List<string>();
		debugger.OnTerminated += () => terminated.TrySetResult();
		debugger.OnOutput += (message, _) => output.Add(message);

		// A negative id makes the registration fail before dbgshim is listening, the same path as a
		// failed SIGCONT: the client must not be left with an "attaching" session that never ends.
		debugger.Attach(-1, justMyCode: true);
		await debugger.ConfigurationDone();

		await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
		output.Should().Contain(message => message.Contains("failed"));
	}
}

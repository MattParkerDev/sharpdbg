using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class LambdaVariablesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task SharpDbgCli_InLambda_VariablesRequest_Returns_InScopeVariables()
    {
	    var startSuspended = false;
	    var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
	    using var _ = adapter;
	    using var __ = new ProcessKiller(p2);

	    await debugProtocolHost
		    .WithInitializeRequest()
		    .WithAttachRequest(p2.Id)
		    .WaitForInitializedEvent(initializedEventTcs);
	    debugProtocolHost
		    .WithBreakpointsRequest([11], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
		    .WithConfigurationDoneRequest()
		    .WithOptionalResumeRuntime(p2.Id, startSuspended);

	    // stop inside the lambda
	    var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo = stoppedEvent.ReadStopInfo();
	    stopInfo.filePath.Should().EndWith("MyLambdaClass.cs");
	    stopInfo.line.Should().Be(11);
    }
}

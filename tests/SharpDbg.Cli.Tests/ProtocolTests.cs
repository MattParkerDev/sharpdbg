using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.InMemory;

namespace SharpDbg.Cli.Tests;

public class ProtocolTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public void UnsupportedRequests_ReturnErrorsWithoutStoppingDispatcher()
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();
		using var _ = adapter;
		using var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		host.Run();

		var unimplementedError = Assert.Throws<ProtocolException>(() =>
			host.SendRequestSync(new SetVariableRequest(0, "value", "1")));
		unimplementedError.Message.Should().Be("Request 'setVariable' is not supported.");

		var unknownError = Assert.Throws<ProtocolException>(() =>
			host.SendRequestSync(new UnknownRequest()));
		unknownError.Message.Should().Be("Request 'unknownRequest' is not supported.");

		var initializeResponse = host.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		initializeResponse.Should().NotBeNull();
	}

	private sealed class UnknownRequest() : DebugRequestWithResponse<UnknownArguments, UnknownResponse>("unknownRequest");
	private sealed class UnknownArguments;
	private sealed class UnknownResponse : ResponseBody;
}

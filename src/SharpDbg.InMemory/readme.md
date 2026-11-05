# SharpDbg

This package allows you to use SharpDbg in memory. Its primary purpose is for use with [SharpIDE](https://github.com/MattParkerDev/SharpIDE).

`NewDebugAdapterStreams` returns the debug adapter's input and output streams. Any Debug Adapter Protocol client can be used with them, for example `DapConnection` from SharpDbg.DapProtocol (included in this package):

```csharp
var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();

// The client reads from the adapter's output, and writes to the adapter's input
var client = new DapConnection(output, input);
client.OnEvent<StoppedEvent>(stopped => Console.WriteLine($"Stopped: {stopped.Reason}"));
client.Start();

var result = await client.SendRequestAsync(new InitializeArguments { AdapterID = "coreclr" });
if (result is DapError error) Console.WriteLine(error.Message);
```

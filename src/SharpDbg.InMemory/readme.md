# SharpDbg

This package allows you to use SharpDbg in memory. Its primary purpose is for use with [SharpIDE](https://github.com/MattParkerDev/SharpIDE).

To obtain input and output streams to create a new DebugProtocolHost:

```csharp
var (input, output) = SharpDbgInMemory.NewDebugAdapterStreams();
var debugProtocolHost = new DebugProtocolHost(inputStream, outputStream, false);
```

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpDbg.Benchmarks.Reporting;

/// <summary>
/// Why each failed benchmark failed. Running in process, BenchmarkDotNet prints a benchmark's exception to the console and keeps none of it
/// in its results or log, so the console is teed and each exception's message is kept under the benchmark header it follows.
/// </summary>
public static partial class FailureLog
{
	private static readonly ConcurrentDictionary<string, string> Failures = new();

	public static string Key(string type, string method, string adapter) => $"{type}.{method}|{adapter}";

	public static string? Get(string type, string method, string adapter) => Failures.GetValueOrDefault(Key(type, method, adapter));

	/// <summary>Tees stdout and stderr. Call before BenchmarkDotNet creates its console logger.</summary>
	public static void CaptureConsole()
	{
		var parser = new Parser();
		Console.SetOut(TextWriter.Synchronized(new TeeWriter(Console.Out, parser)));
		Console.SetError(TextWriter.Synchronized(new TeeWriter(Console.Error, parser)));
	}

	[GeneratedRegex(@"^// Benchmark: (\w+)\.(\w+): .*\[Adapter=([^\]]+)\]")]
	private static partial Regex BenchmarkHeader();

	private sealed class Parser
	{
		private readonly Lock _lock = new();
		private string? _current;
		private StringBuilder? _message;

		public void OnLine(string line)
		{
			lock (_lock)
			{
				if (BenchmarkHeader().Match(line) is { Success: true } header)
				{
					_current = Key(header.Groups[1].Value, header.Groups[2].Value, header.Groups[3].Value);
					_message = null;
					return;
				}
				if (_current is null || Failures.ContainsKey(_current) && _message is null) return;

				// The benchmark's own exception is the innermost one, inside BenchmarkDotNet's TargetInvocationException
				var trimmed = line.TrimStart();
				if (trimmed.StartsWith("---> ", StringComparison.Ordinal))
				{
					_message = new StringBuilder(MessageOf(trimmed[5..]));
					Failures[_current] = _message.ToString();
				}
				else if (_message is not null)
				{
					// A message can span lines, up to the stack trace
					if (trimmed.StartsWith("at ", StringComparison.Ordinal) || trimmed.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
					{
						_message = null;
						return;
					}
					_message.AppendLine().Append(line);
					Failures[_current] = _message.ToString();
				}
				else if (trimmed.Contains("Exception: ", StringComparison.Ordinal) && trimmed.Contains("TargetInvocationException", StringComparison.Ordinal) is false)
				{
					Failures.TryAdd(_current, MessageOf(trimmed));
				}
			}
		}

		private static string MessageOf(string exceptionLine)
		{
			var index = exceptionLine.IndexOf("Exception: ", StringComparison.Ordinal);
			return index >= 0 ? exceptionLine[(index + "Exception: ".Length)..] : exceptionLine;
		}
	}

	private sealed class TeeWriter(TextWriter inner, Parser parser) : TextWriter
	{
		private readonly StringBuilder _line = new();

		public override Encoding Encoding => inner.Encoding;

		public override void Write(char value)
		{
			inner.Write(value);
			Append(value);
		}

		public override void Write(string? value)
		{
			inner.Write(value);
			foreach (var c in value ?? "") Append(c);
		}

		public override void Write(char[] buffer, int index, int count)
		{
			inner.Write(buffer, index, count);
			for (var i = index; i < index + count; i++) Append(buffer[i]);
		}

		public override void Flush() => inner.Flush();

		private void Append(char c)
		{
			if (c is '\r') return;
			if (c is '\n')
			{
				parser.OnLine(_line.ToString());
				_line.Clear();
				return;
			}
			_line.Append(c);
		}
	}
}

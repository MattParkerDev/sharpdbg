namespace SharpDbg.Infrastructure.Debugger;

public class FrameReferenceManager
{
	private int _nextFrameId = 1;
	private readonly Dictionary<int, (ThreadId threadId, FrameStackDepth frameStackDepth)?> _references = [];
	private readonly Dictionary<(ThreadId threadId, FrameStackDepth frameStackDepth), int?> _referencesByThreadAndFrameStackDepth = [];
	private readonly Lock _lock = new();

	public int GetOrCreateFrameId(ThreadId threadId, FrameStackDepth frameStackDepth)
	{
		lock (_lock)
		{
			var frameId = _referencesByThreadAndFrameStackDepth.GetValueOrDefault((threadId, frameStackDepth));
			if (frameId is null)
			{
				frameId = _nextFrameId++;
				_references[frameId.Value] = (threadId, frameStackDepth);
				_referencesByThreadAndFrameStackDepth[(threadId, frameStackDepth)] = frameId;
			}
			return frameId.Value;
		}
	}

	public (ThreadId threadId, FrameStackDepth frameStackDepth)? GetFrameInfoById(int frameId)
	{
		lock (_lock)
		{
			return _references.GetValueOrDefault(frameId);
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_references.Clear();
			_referencesByThreadAndFrameStackDepth.Clear();
			_nextFrameId = 1;
		}
	}
}

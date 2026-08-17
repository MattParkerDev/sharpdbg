namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private readonly HashSet<Guid> _transientEvaluationModules = new();

	internal void RegisterTransientEvaluationModule(Guid moduleVersionId)
	{
		lock (_transientEvaluationModules) _transientEvaluationModules.Add(moduleVersionId);
	}

	private bool TryConsumeTransientEvaluationModule(Guid moduleVersionId)
	{
		lock (_transientEvaluationModules) return _transientEvaluationModules.Remove(moduleVersionId);
	}
}

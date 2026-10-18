using Microsoft.Extensions.DependencyInjection;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfRewind;

public interface IWorkflowRewinder
{
    Task<RewindPlan> Plan(string workflowId);
    Task<RewindResult> Rewind(string workflowId);
}

public class WorkflowRewinder : IWorkflowRewinder
{
    private readonly IPersistenceProvider _store;
    private readonly IDistributedLockProvider _locks;
    private readonly IQueueProvider _queue;
    private readonly IWorkflowRegistry _registry;

    public WorkflowRewinder(IPersistenceProvider store, IDistributedLockProvider locks, IQueueProvider queue, IWorkflowRegistry registry)
    {
        _store = store;
        _locks = locks;
        _queue = queue;
        _registry = registry;
    }

    public async Task<RewindPlan> Plan(string workflowId)
    {
        var workflow = await _store.GetWorkflowInstance(workflowId);
        return PlanFor(workflow, FailedPointers(workflow));
    }

    public async Task<RewindResult> Rewind(string workflowId)
    {
        if (!await _locks.AcquireLock(workflowId, CancellationToken.None))
            return new RewindResult(RewindVerdict.LockBusy, []);

        RewindPlan plan;
        try
        {
            var workflow = await _store.GetWorkflowInstance(workflowId);
            var failed = FailedPointers(workflow);
            plan = PlanFor(workflow, failed);
            if (plan.Verdict != RewindVerdict.Ready)
                return new RewindResult(plan.Verdict, []);

            foreach (var pointer in failed)
            {
                pointer.Active = true;
                pointer.Status = PointerStatus.Pending;
                pointer.EndTime = null;
                pointer.SleepUntil = null;
                pointer.RetryCount++;
            }
            workflow.Status = WorkflowStatus.Runnable;
            workflow.CompleteTime = null;
            await _store.PersistWorkflow(workflow);
        }
        finally
        {
            await _locks.ReleaseLock(workflowId);
        }

        await _queue.QueueWork(workflowId, QueueType.Workflow);
        return new RewindResult(RewindVerdict.Rewound, plan.FailedSteps);
    }

    private static List<ExecutionPointer> FailedPointers(WorkflowInstance workflow) =>
        workflow.ExecutionPointers.Where(p => p.Status == PointerStatus.Failed).ToList();

    private RewindPlan PlanFor(WorkflowInstance workflow, List<ExecutionPointer> failed)
    {
        var def = _registry.GetDefinition(workflow.WorkflowDefinitionId, workflow.Version);
        var steps = failed
            .Select(p => new FailedStep(p.Id, p.StepId, StepName(def, p.StepId)))
            .ToList();

        var verdict = workflow.Status != WorkflowStatus.Terminated ? RewindVerdict.NotTerminated
            : steps.Count == 0 ? RewindVerdict.NoFailedStep
            : RewindVerdict.Ready;
        return new RewindPlan(workflow.Id, workflow.Status, steps, verdict);
    }

    private static string StepName(WorkflowDefinition? def, int stepId)
    {
        var step = def?.Steps.FindById(stepId);
        return step?.Name ?? step?.BodyType?.Name ?? $"step-{stepId}";
    }
}

public static class WfRewindServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowRewind(this IServiceCollection services) =>
        services.AddTransient<IWorkflowRewinder, WorkflowRewinder>();
}

using WorkflowCore.Models;

namespace WfRewind;

public enum RewindVerdict
{
    Ready,
    Rewound,
    NotTerminated,
    NoFailedStep,
    LockBusy
}

public record FailedStep(string PointerId, int StepId, string StepName);

public record RewindPlan(string WorkflowId, WorkflowStatus Status, IReadOnlyList<FailedStep> FailedSteps, RewindVerdict Verdict);

public record RewindResult(RewindVerdict Verdict, IReadOnlyList<FailedStep> Rewound);

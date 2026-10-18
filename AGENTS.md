# AGENTS.md

Handoff for a coding agent with no prior context on this repo.

## What this proves

One claim: a Workflow Core instance that a step error terminated can be rewound later, from a different host process, so that only the failed step runs again and the run completes. Completed steps do not repeat their side effects. Instances that are not terminated, or that were terminated by hand with no failed step, are refused and left alone.

It runs on the real `WorkflowCore` 3.21.0 and `WorkflowCore.Persistence.Sqlite` 3.21.0 NuGet packages. Process A runs the workflow until it terminates and exits. Process B is a fresh host on the same SQLite file that rewinds it. No paid APIs, no network after the NuGet restore. The partner API outage is simulated by an environment variable.

Why it matters. Workflow Core users have asked for this since 2020 ([#558](https://github.com/danielgerlag/workflow-core/issues/558), [#829](https://github.com/danielgerlag/workflow-core/issues/829)). `IWorkflowController.ResumeWorkflow` only acts on `Suspended` instances. The usual workaround, starting a new instance with the same data, replays every completed step. Dapr 1.19 (rerun), Durable Task .NET 1.26 (rewind) and Temporal (reset) ship this for their engines.

## Status

| Piece | Real or stubbed |
|---|---|
| Workflow host, error handling, `Terminate`, `ResumeWorkflow`, `TerminateWorkflow`, queue, lock provider | Real. NuGet `WorkflowCore` 3.21.0. |
| Persistence across two OS processes | Real. NuGet `WorkflowCore.Persistence.Sqlite` 3.21.0, one `.demo/wf.db` file. |
| `IWorkflowRewinder` (`Plan`, `Rewind`) | Real, in `src/WfRewind`. About 100 lines. Uses only public Workflow Core interfaces. |
| Side effects ledger | Real file (`.demo/effects.jsonl`). Each step appends one line when its body runs. The checks read this file, not the rewinder's own output. |
| Partner API | Stub. `CallPartner` throws `HttpRequestException` when its order id is in `PARTNER_DOWN`. No network. |
| Order workflow | Toy. `Reserve`, `CallPartner`, `Ship`, each only writes a ledger line. |

`bash demo.sh` passes 10 of 10 checks on a fresh clone. `bash demo.sh --break` fails checks 2, 5, 6, 8 and 10 and exits 1.

## Data shape

```
RewindVerdict        enum
  Ready              Plan only: Rewind would re-arm the listed steps
  Rewound            Rewind only: done, instance is Runnable and queued
  NotTerminated      status is not Terminated (Complete, Runnable, Suspended). Nothing changed.
  NoFailedStep       Terminated, but no pointer has Status Failed (manual TerminateWorkflow). Nothing changed.
  LockBusy           could not take the instance lock. Nothing changed.

FailedStep           record (PointerId string, StepId int, StepName string)

RewindPlan           record (WorkflowId, Status WorkflowStatus, FailedSteps FailedStep[], Verdict)
RewindResult         record (Verdict, Rewound FailedStep[])

Rewind(id), under the instance lock:
  failed = ExecutionPointers where Status == Failed
  require Status == Terminated and failed not empty
  for each failed pointer: Active = true, Status = Pending, EndTime = null, SleepUntil = null, RetryCount++
  Status = Runnable, CompleteTime = null
  PersistWorkflow, release lock, QueueWork(id, Workflow)
```

Demo output records (`.demo/fail.jsonl`, `.demo/rewind.jsonl`), one JSON line per scenario:

```json
{"phase":"fail","scenario":"order-1","status":"Terminated","failedSteps":["CallPartner"],"resumeAccepted":false,"statusAfterResume":"Terminated"}
{"phase":"rewind","scenario":"order-1","planVerdict":"Ready","planSteps":["CallPartner"],"verdict":"Rewound","rewound":["CallPartner"],"status":"Complete"}
{"phase":"rewind","scenario":"order-2","verdict":"NotTerminated","status":"Complete"}
{"phase":"rewind","scenario":"hold-3","verdict":"NoFailedStep","status":"Terminated"}
{"phase":"rewind","scenario":"order-1-again","verdict":"NotTerminated","status":"Complete"}
```

Side effects ledger line: `{"orderId":"order-1","step":"Reserve","result":"ok"}`. `result` is `ok` or `error`.

## Repo map

```
demo.sh                         the proof. Builds, runs process A then process B, prints CHECK lines
src/WfRewind/
  Rewind.cs                     RewindVerdict, FailedStep, RewindPlan, RewindResult
  WorkflowRewinder.cs           IWorkflowRewinder, WorkflowRewinder, AddWorkflowRewind() DI
src/WfRewind.Demo/
  Workflows.cs                  OrderSyncWorkflow (Reserve, CallPartner, Ship; Terminate on error), HoldWorkflow (waits forever), Effects ledger
  Program.cs                    "fail" phase (process A) and "rewind" phase (process B). JSON to stdout, logs to stderr
```

## Setup, run, verify (fresh clone)

Prereqs: .NET 8 SDK (`dotnet --list-sdks` shows 8.x), `jq`, and `python3` (break mode only). If the SDK lives in `~/.dotnet`, `demo.sh` puts it on PATH. The first build needs NuGet access.

```bash
git clone https://github.com/ideator-labs/wf-rewind.git
cd wf-rewind
bash demo.sh
```

Expected: ten `CHECK PASS` lines, the result records, the side effects per order, then `RESULT: PASS`, exit 0. About 10 seconds after the first restore.

```
CHECK PASS  1 build: WfRewind + demo compile against WorkflowCore 3.21.0 and WorkflowCore.Persistence.Sqlite 3.21.0
CHECK PASS  2 process A: order-1 terminated by CallPartner error, plan shows exactly [CallPartner]
CHECK PASS  3 control: stock ResumeWorkflow on the terminated instance returns false, status stays Terminated
CHECK PASS  4 before rewind: order-1 side effects are Reserve once, one CallPartner error, no Ship
CHECK PASS  5 process B (fresh host, same SQLite db): Plan=Ready [CallPartner], Rewind=Rewound, instance reaches Complete
CHECK PASS  6 no replay: order-1 side effects are Reserve once, CallPartner error then ok, Ship once
CHECK PASS  7 refuse: rewinding a Complete instance returns NotTerminated and it stays Complete
CHECK PASS  8 refuse: a manually terminated instance has no failed step, returns NoFailedStep, stays Terminated
CHECK PASS  9 idempotent: a second rewind of order-1 after it completed returns NotTerminated
CHECK PASS  10 untouched: order-2 and hold-3 side effects did not change during process B
RESULT: PASS
```

Break it on purpose:

```bash
bash demo.sh --break
```

It copies `src/` to `.demo/src/`, deletes `.Where(p => p.Status == PointerStatus.Failed)` from the copy of `WorkflowRewinder.cs`, then builds and runs the copy. The rewinder now re-arms every pointer, including the completed `Reserve`. `Reserve` and `Ship` run twice (check 6), the plan lists `Reserve` (checks 2 and 5), and the manually terminated `hold-3` gets rewound (checks 8 and 10). Exit 1. The script exits 2 if the sabotage string is not found, so it can never silently do nothing. The real `src/` is never touched.

Raw output lands in `.demo/` (`fail.jsonl`, `rewind.jsonl`, `effects.jsonl`, `effects-after-fail.jsonl`, `fail.log`, `rewind.log`, `build.log`, `wf.db`).

## Design decisions

- **Re-arm the failed pointer, do not rebuild the run.** When a step throws and the error behavior is `Terminate`, `ExecutionResultProcessor.HandleStepException` sets the pointer to `Failed` and `TerminateHandler` sets the instance to `Terminated`. The pointer stays `Active`. Setting the instance back to `Runnable` and the pointer to `Pending` is enough for `WorkflowExecutor` to run that step again and then follow its normal outcomes. Completed pointers are not touched, so their steps never run again.
- **Refuse anything else.** Only `Terminated` instances with at least one `Failed` pointer are rewound. A manual `TerminateWorkflow` leaves no failed pointer, so it is refused with `NoFailedStep`. That keeps "an operator stopped this on purpose" distinct from "this broke".
- **Same lock and queue as the stock controller.** `Rewind` mirrors `WorkflowController.ResumeWorkflow`: take the instance lock from `IDistributedLockProvider`, persist, release, then `QueueWork(id, QueueType.Workflow)`. Works with any persistence, lock and queue provider in principle. Only SQLite is tested.
- **Plan is a dry run.** `Plan` returns the steps `Rewind` would re-arm and the verdict it would return now (`Ready` instead of `Rewound`). Operators can show it before acting.
- **Prove side effects with a ledger, not the rewinder's report.** Steps append to a file that the checks read. Check 6 would catch a replay even if the rewinder reported success.
- **Two processes.** The realistic case is "terminated yesterday, fixed today". Process A exits before process B starts, so the only shared state is the SQLite file.
- **RetryCount++.** Each rewind counts as a retry on the pointer, so a later history view can tell a rewound step from a first run.

## Limits

- Only the `Terminate` error behavior is covered. `Compensate` deactivates pointers and runs compensation steps; rewinding after compensation is not handled and would be wrong. `Retry` never terminates. `Suspend` already works with stock `ResumeWorkflow`.
- Only linear workflows are tested. Failed steps inside `Parallel`, `If`, `While` or `Saga` containers are untested. The parent container pointer should still be waiting on its children, but that is a guess.
- No "rewind from step N" for a step that did not fail (Dapr `--event-id`, Temporal `--event-id`). This only re-runs the failed step.
- `Plan` and `Rewind` assume the instance exists. A missing id surfaces the persistence provider's exception (EF throws on an unknown or non-GUID id).
- No lifecycle event is published on rewind. `WorkflowResumed` or a new `WorkflowRewound` event would let observers see it.
- Not a NuGet package yet.

## Next steps (in priority order)

1. **Containers.** Add a workflow where the failing step sits inside `Parallel` and inside `Saga` with `Terminate`. Done when a demo check shows the instance completes after rewind and the other parallel branch ran once.
2. **Rewind from a step.** Add `RewindFrom(id, stepId)` that re-arms a completed step and discards later pointers. Done when a check rewinds `order-2` from `CallPartner` and the ledger shows `Reserve` once, `CallPartner` twice, `Ship` twice.
3. **Lifecycle event.** Publish `WorkflowResumed` (or a new event) on rewind. Done when a check subscribes via `ILifeCycleEventHub` and sees exactly one event for `order-1`.
4. **Find what to rewind.** Add a query for Terminated instances with failed pointers (the obsolete `GetWorkflowInstances` gap, discussion #1442). Done when process B finds `order-1` without the ids file.
5. **Package or core PR.** Ship `src/WfRewind` as a NuGet, or propose `IWorkflowController.RewindWorkflow` upstream. Done when a fresh console app referencing only the package passes checks 2 to 10.

## Working rules

- `bash demo.sh` is the source of truth. Keep it passing and keep `--break` failing.
- Every new claim gets a CHECK line that reads real state (the SQLite store or the ledger file) and a sabotage that makes it fail.
- Demo stdout is one JSON line per scenario. Logs go to stderr.
- Keep `WorkflowCore` packages at 3.21.x unless a step needs a newer API.
- Commit as `danielgerlag <daniel@gerlag.ca>`.
- Plain prose. No em-dashes.

## Sources

- Workflow Core: https://github.com/danielgerlag/workflow-core
- Restart a failed workflow from a specific step (#558): https://github.com/danielgerlag/workflow-core/issues/558
- How to retry a workflow, of which one step failed (#829): https://github.com/danielgerlag/workflow-core/issues/829
- GetWorkflowInstances deprecation, what replaces it (discussion #1442): https://github.com/danielgerlag/workflow-core/discussions/1442
- WorkflowCore 3.21.0 on NuGet: https://www.nuget.org/packages/WorkflowCore/3.21.0
- Dapr: expose list, history, rewind in all SDKs (#9729): https://github.com/dapr/dapr/issues/9729
- Dapr 1.19 manage workflows (`dapr workflow rerun`): https://v1-19.docs.dapr.io/developing-applications/building-blocks/workflow/howto-manage-workflow/
- Durable Task .NET releases (v1.26.0, "Adding rewind to the sidecar"): https://github.com/microsoft/durabletask-dotnet/releases
- Temporal CLI `workflow reset`: https://docs.temporal.io/cli/workflow#reset
- Sibling POCs: https://github.com/ideator-labs/approval-deadline , https://github.com/ideator-labs/defhotsync

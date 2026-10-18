# wf-rewind

Rewind a Workflow Core instance that a step error terminated. Only the failed step runs again. Steps that already completed do not repeat their side effects.

```csharp
services.AddWorkflow(o => o.UseSqlite("Data Source=wf.db;", true));
services.AddWorkflowRewind();

var rewinder = provider.GetRequiredService<IWorkflowRewinder>();
var plan = await rewinder.Plan(workflowId);      // dry run: Ready, NotTerminated or NoFailedStep, plus the failed steps
var result = await rewinder.Rewind(workflowId);  // Rewound: the failed pointer is re-armed and the instance is queued
```

The use case is the one asked for in [#558](https://github.com/danielgerlag/workflow-core/issues/558) and [#829](https://github.com/danielgerlag/workflow-core/issues/829). Step 5 of 10 failed because a partner API was down. The workflow was terminated. The API is fixed two hours later. You want to finish the run from step 5, without starting a new instance that runs steps 1 to 4 again. Stock `ResumeWorkflow` only works on Suspended instances and returns `false` here.

Dapr 1.19 (rerun), Durable Task 1.26 (rewind) and Temporal (reset) all ship this for their engines. This POC shows the same thing for Workflow Core on the real `WorkflowCore` 3.21.0 and `WorkflowCore.Persistence.Sqlite` 3.21.0 packages.

```bash
bash demo.sh           # 10 checks across two host processes, RESULT: PASS
bash demo.sh --break   # rewind re-arms every pointer, completed steps replay, checks fail, exit 1
```

Needs the .NET 8 SDK and `jq`. It is a proof of concept. It handles linear workflows terminated by `WorkflowErrorHandling.Terminate`. See [AGENTS.md](AGENTS.md) for the data shape, what is real, the limits, and next steps.

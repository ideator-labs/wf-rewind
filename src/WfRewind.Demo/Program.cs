using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WfRewind;
using WfRewind.Demo;
using WorkflowCore.Interface;
using WorkflowCore.Models;

var phase = args.FirstOrDefault() ?? throw new ArgumentException("usage: WfRewind.Demo fail|rewind");
var db = Environment.GetEnvironmentVariable("WF_DB") ?? "wf.db";
var idsFile = Environment.GetEnvironmentVariable("IDS_FILE") ?? "ids.json";

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning));
services.AddWorkflow(o => o.UseSqlite($"Data Source={db};", true));
services.AddWorkflowRewind();
var sp = services.BuildServiceProvider();

var host = sp.GetRequiredService<IWorkflowHost>();
host.RegisterWorkflow<OrderSyncWorkflow, OrderData>();
host.RegisterWorkflow<HoldWorkflow, OrderData>();
await host.StartAsync(CancellationToken.None);

var store = sp.GetRequiredService<IPersistenceProvider>();
var rewinder = sp.GetRequiredService<IWorkflowRewinder>();

async Task<WorkflowInstance> WaitFor(string id, Func<WorkflowInstance, bool> done)
{
    var until = DateTime.UtcNow.AddSeconds(15);
    while (true)
    {
        var wf = await store.GetWorkflowInstance(id);
        if (done(wf) || DateTime.UtcNow > until) return wf;
        await Task.Delay(100);
    }
}

void Emit(object record) => Console.WriteLine(JsonSerializer.Serialize(record));

string[] Names(IEnumerable<FailedStep> steps) => steps.Select(s => s.StepName).ToArray();

if (phase == "fail")
{
    var ids = new Dictionary<string, string>
    {
        ["order-1"] = await host.StartWorkflow("OrderSync", 1, new OrderData { OrderId = "order-1" }),
        ["order-2"] = await host.StartWorkflow("OrderSync", 1, new OrderData { OrderId = "order-2" }),
        ["hold-3"] = await host.StartWorkflow("Hold", 1, new OrderData { OrderId = "hold-3" }),
    };

    var failed = await WaitFor(ids["order-1"], w => w.Status == WorkflowStatus.Terminated);
    var plan = await rewinder.Plan(ids["order-1"]);
    var resumeAccepted = await host.ResumeWorkflow(ids["order-1"]);
    var afterResume = await store.GetWorkflowInstance(ids["order-1"]);
    Emit(new { phase, scenario = "order-1", status = failed.Status.ToString(), failedSteps = Names(plan.FailedSteps), resumeAccepted, statusAfterResume = afterResume.Status.ToString() });

    var ok = await WaitFor(ids["order-2"], w => w.Status == WorkflowStatus.Complete);
    Emit(new { phase, scenario = "order-2", status = ok.Status.ToString() });

    await WaitFor(ids["hold-3"], w => w.ExecutionPointers.Any(p => p.Status == PointerStatus.WaitingForEvent));
    var terminated = await host.TerminateWorkflow(ids["hold-3"]);
    var hold = await WaitFor(ids["hold-3"], w => w.Status == WorkflowStatus.Terminated);
    Emit(new { phase, scenario = "hold-3", terminated, status = hold.Status.ToString() });

    File.WriteAllText(idsFile, JsonSerializer.Serialize(ids));
}
else if (phase == "rewind")
{
    var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(idsFile))!;

    var plan = await rewinder.Plan(ids["order-1"]);
    var result = await rewinder.Rewind(ids["order-1"]);
    var done = await WaitFor(ids["order-1"], w => w.Status == WorkflowStatus.Complete);
    Emit(new { phase, scenario = "order-1", planVerdict = plan.Verdict.ToString(), planSteps = Names(plan.FailedSteps), verdict = result.Verdict.ToString(), rewound = Names(result.Rewound), status = done.Status.ToString() });

    foreach (var name in new[] { "order-2", "hold-3" })
    {
        var r = await rewinder.Rewind(ids[name]);
        await Task.Delay(1000);
        var wf = await store.GetWorkflowInstance(ids[name]);
        Emit(new { phase, scenario = name, verdict = r.Verdict.ToString(), status = wf.Status.ToString() });
    }

    var again = await rewinder.Rewind(ids["order-1"]);
    await Task.Delay(1000);
    var final = await store.GetWorkflowInstance(ids["order-1"]);
    Emit(new { phase, scenario = "order-1-again", verdict = again.Verdict.ToString(), status = final.Status.ToString() });
}
else throw new ArgumentException($"unknown phase {phase}");

await host.StopAsync(CancellationToken.None);

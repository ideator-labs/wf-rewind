using System.Text.Json;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfRewind.Demo;

public class OrderData
{
    public string OrderId { get; set; } = "";
}

public static class Effects
{
    public static string File => Environment.GetEnvironmentVariable("EFFECTS_FILE") ?? "effects.jsonl";

    public static void Record(string orderId, string step, string result) =>
        System.IO.File.AppendAllText(File, JsonSerializer.Serialize(new { orderId, step, result }) + "\n");

    public static List<(string OrderId, string Step, string Result)> Read() =>
        System.IO.File.Exists(File)
            ? System.IO.File.ReadAllLines(File)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Select(e => (e.GetProperty("orderId").GetString()!, e.GetProperty("step").GetString()!, e.GetProperty("result").GetString()!))
                .ToList()
            : [];
}

public class Reserve : StepBody
{
    public string OrderId { get; set; } = "";

    public override ExecutionResult Run(IStepExecutionContext context)
    {
        Effects.Record(OrderId, nameof(Reserve), "ok");
        return ExecutionResult.Next();
    }
}

public class CallPartner : StepBody
{
    public string OrderId { get; set; } = "";

    public override ExecutionResult Run(IStepExecutionContext context)
    {
        var down = (Environment.GetEnvironmentVariable("PARTNER_DOWN") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (down.Contains(OrderId))
        {
            Effects.Record(OrderId, nameof(CallPartner), "error");
            throw new HttpRequestException($"partner API unavailable for {OrderId} (simulated outage)");
        }
        Effects.Record(OrderId, nameof(CallPartner), "ok");
        return ExecutionResult.Next();
    }
}

public class Ship : StepBody
{
    public string OrderId { get; set; } = "";

    public override ExecutionResult Run(IStepExecutionContext context)
    {
        Effects.Record(OrderId, nameof(Ship), "ok");
        return ExecutionResult.Next();
    }
}

public class OrderSyncWorkflow : IWorkflow<OrderData>
{
    public string Id => "OrderSync";
    public int Version => 1;

    public void Build(IWorkflowBuilder<OrderData> builder) => builder
        .UseDefaultErrorBehavior(WorkflowErrorHandling.Terminate)
        .StartWith<Reserve>().Input(s => s.OrderId, d => d.OrderId)
        .Then<CallPartner>().Input(s => s.OrderId, d => d.OrderId)
        .Then<Ship>().Input(s => s.OrderId, d => d.OrderId);
}

public class HoldWorkflow : IWorkflow<OrderData>
{
    public string Id => "Hold";
    public int Version => 1;

    public void Build(IWorkflowBuilder<OrderData> builder) => builder
        .StartWith<Reserve>().Input(s => s.OrderId, d => d.OrderId)
        .WaitFor("NeverArrives", d => d.OrderId);
}

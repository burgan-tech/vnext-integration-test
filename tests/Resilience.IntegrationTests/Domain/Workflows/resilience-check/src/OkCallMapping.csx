using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// onExecution HTTP mapping for ok-call (Task type 6) on the stable-step transition.
/// Sends the instance orderId to MockLab and writes the mock's result onto instance data.
/// </summary>
public class OkCallMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
            throw new InvalidOperationException("Task must be an HttpTask");

        var data = context.Instance?.Data;
        var orderId = data?.orderId?.ToString();

        httpTask.SetBody(new { orderId = orderId ?? string.Empty });
        httpTask.SetHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var payload = context.Body?.data ?? context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                checkResult = payload?.result?.ToString() ?? "unknown",
                checkReference = payload?.referenceId?.ToString(),
                checkCompletedAt = DateTime.UtcNow.ToString("o")
            },
            Tags = new[] { "resilience", "ok-call" }
        });
    }
}

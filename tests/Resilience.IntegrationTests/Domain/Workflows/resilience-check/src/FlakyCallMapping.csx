using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// onExecution HTTP mapping for flaky-call (Task type 6) on the flaky-step transition.
/// MockLab serves this endpoint sequentially (503, 503, 200) so the first attempts fail
/// with a transient status and the instance faults; the /retry endpoint drives re-runs.
/// </summary>
public class FlakyCallMapping : ScriptBase, IMapping
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
        var statusCode = 0;
        try { statusCode = (int)(context.Body?.statusCode ?? 0); } catch { statusCode = 0; }

        var payload = context.Body?.data ?? context.Body;
        var succeeded = statusCode == 0 || (statusCode >= 200 && statusCode < 300);

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                flakyResult = payload?.result?.ToString() ?? "unknown",
                flakyReference = payload?.referenceId?.ToString(),
                flakyCompletedAt = DateTime.UtcNow.ToString("o")
            },
            Tags = succeeded
                ? new[] { "resilience", "flaky-call", "success" }
                : new[] { "resilience", "flaky-call", "failure" }
        });
    }
}

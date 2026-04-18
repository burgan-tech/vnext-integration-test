namespace VNext.Testing.Sdk.Builders;

/// <summary>
/// Abstract base for domain-specific test data builder classes.
/// Provides shared utility helpers so teams don't duplicate boilerplate payload construction.
///
/// Usage: create a static or instance class in your test project that inherits this,
/// then add domain-specific factory methods on top.
///
/// Example:
/// <code>
/// public static class MyDomainTestDataBuilder : TestDataBuilderBase
/// {
///     public static object MyWorkflow(string userId) =>
///         BuildInstancePayload(
///             key: $"my-wf-{userId}",
///             tags: ["my-tag"],
///             attributes: new { userId });
/// }
/// </code>
/// </summary>
public abstract class TestDataBuilderBase
{
    // ========================================================================
    // Core payload factories
    // ========================================================================

    /// <summary>
    /// Builds a standard vNext workflow instance start/transition body.
    /// </summary>
    protected static object BuildInstancePayload(
        string key,
        string[]? tags = null,
        object? attributes = null)
    {
        return new
        {
            key,
            tags = tags ?? Array.Empty<string>(),
            attributes = attributes ?? new { }
        };
    }

    /// <summary>
    /// Builds an attributes-only update/transition body.
    /// </summary>
    protected static object BuildTransitionBody(object attributes)
    {
        return new { attributes };
    }

    // ========================================================================
    // Key generation helpers
    // ========================================================================

    /// <summary>Generates a unique key for a workflow instance.</summary>
    protected static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Generates a deterministic key based on domain identifiers.</summary>
    protected static string DeterministicKey(string prefix, params string[] parts)
    {
        var suffix = string.Join("-", parts)
            .Replace(":", "")
            .Replace(".", "-")
            .Replace(" ", "-");
        return $"{prefix}-{suffix}";
    }

    // ========================================================================
    // Working hours helpers
    // ========================================================================

    /// <summary>
    /// Converts a schedule dictionary into the customWorkingHours shape expected by vNext.
    /// </summary>
    protected static Dictionary<string, object[]> BuildCustomWorkingHours(
        Dictionary<string, List<(string Start, string End)>> schedule)
    {
        var result = new Dictionary<string, object[]>();
        foreach (var (day, slots) in schedule)
        {
            result[day] = slots
                .Select(s => (object)new { start = s.Start, end = s.End })
                .ToArray();
        }
        return result;
    }

    /// <summary>
    /// Returns a typical weekday schedule (Mon–Fri, two shifts; Sat–Sun empty).
    /// </summary>
    protected static Dictionary<string, List<(string Start, string End)>> DefaultWeekdaySchedule(
        string morningStart = "09:00",
        string morningEnd = "12:00",
        string afternoonStart = "13:30",
        string afternoonEnd = "18:00",
        string fridayAfternoonEnd = "17:00")
    {
        var weekday = new List<(string, string)>
        {
            (morningStart, morningEnd),
            (afternoonStart, afternoonEnd)
        };

        return new Dictionary<string, List<(string Start, string End)>>
        {
            ["monday"] = weekday,
            ["tuesday"] = weekday,
            ["wednesday"] = weekday,
            ["thursday"] = weekday,
            ["friday"] = [(morningStart, morningEnd), (afternoonStart, fridayAfternoonEnd)],
            ["saturday"] = [],
            ["sunday"] = []
        };
    }
}

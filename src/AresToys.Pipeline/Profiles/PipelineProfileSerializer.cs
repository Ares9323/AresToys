using System.Text.Json;
using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;

namespace AresToys.Pipeline.Profiles;

public static class PipelineProfileSerializer
{
    private static readonly JsonSerializerOptions WriterOptions = new() { WriteIndented = false };

    /// <summary>Serialise everything that lives outside the columns (steps, hotkey, builtin flag).
    /// Stored in the <c>tasks_json</c> column so we can extend the model without schema migrations.</summary>
    public static string SerializeBody(PipelineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var array = new JsonArray();
        foreach (var step in profile.Steps)
        {
            var obj = new JsonObject
            {
                ["task_id"] = step.TaskId,
                ["enabled"] = step.Enabled,
                ["abort_on_error"] = step.AbortOnError
            };
            if (!string.IsNullOrEmpty(step.Id)) obj["id"] = step.Id;
            if (step.Config is not null) obj["config"] = step.Config.DeepClone();
            array.Add(obj);
        }
        var root = new JsonObject { ["tasks"] = array };
        if (profile.Hotkey is not null)
        {
            root["hotkey"] = new JsonObject
            {
                ["modifiers"] = profile.Hotkey.Modifiers,
                ["vk"] = profile.Hotkey.VirtualKey,
            };
        }
        if (profile.IsBuiltIn) root["is_builtin"] = true;
        return root.ToJsonString(WriterOptions);
    }

    /// <summary>Read the steps + hotkey + builtin flag back from the persisted blob.</summary>
    public static (IReadOnlyList<PipelineStep> Steps, HotkeyBinding? Hotkey, bool IsBuiltIn) DeserializeBody(string tasksJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(tasksJson);
        var root = JsonNode.Parse(tasksJson) as JsonObject
            ?? throw new InvalidDataException("Expected JSON object at root.");
        var tasks = root["tasks"] as JsonArray
            ?? throw new InvalidDataException("Expected 'tasks' array.");

        var steps = new List<PipelineStep>(tasks.Count);
        foreach (var node in tasks)
        {
            if (node is not JsonObject obj) continue;
            var taskId = (string?)obj["task_id"]
                ?? throw new InvalidDataException("Task entry missing 'task_id'.");
            var enabled = (bool?)obj["enabled"] ?? true;
            var abortOnError = (bool?)obj["abort_on_error"] ?? false;
            var id = (string?)obj["id"];
            var config = obj["config"]?.DeepClone();
            steps.Add(new PipelineStep(taskId, config, enabled, abortOnError, id));
        }

        HotkeyBinding? hotkey = null;
        if (root["hotkey"] is JsonObject hkObj)
        {
            var mod = (int?)hkObj["modifiers"] ?? 0;
            var vk = (uint?)hkObj["vk"] ?? 0u;
            if (vk != 0) hotkey = new HotkeyBinding(mod, vk);
        }
        var isBuiltIn = (bool?)root["is_builtin"] ?? false;
        return (steps, hotkey, isBuiltIn);
    }

    // --- Legacy aliases (used by existing tests / callers that only care about steps). ---
    public static string SerializeSteps(IEnumerable<PipelineStep> steps)
        => SerializeBody(new PipelineProfile("legacy", "legacy", "legacy", steps.ToList()));

    public static IReadOnlyList<PipelineStep> DeserializeSteps(string tasksJson)
        => DeserializeBody(tasksJson).Steps;
}

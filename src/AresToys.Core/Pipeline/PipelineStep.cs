using System.Text.Json.Nodes;

namespace AresToys.Core.Pipeline;

public sealed record PipelineStep(
    string TaskId,
    JsonNode? Config = null,
    bool Enabled = true,
    bool AbortOnError = false,
    /// <summary>Stable id for the step within its profile (e.g. "save", "copy-image"). Used by
    /// the Settings UI to toggle individual steps on/off via per-step overrides. Null = the step
    /// is mandatory and not user-toggleable.</summary>
    string? Id = null);

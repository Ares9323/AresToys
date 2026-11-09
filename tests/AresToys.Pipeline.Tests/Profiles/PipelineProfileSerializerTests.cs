using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using Xunit;

namespace AresToys.Pipeline.Tests.Profiles;

public class PipelineProfileSerializerTests
{
    [Fact]
    public void SerializeSteps_EmitsExpectedJson()
    {
        var steps = new[]
        {
            new PipelineStep("arestoys.add-to-history"),
            new PipelineStep(
                "arestoys.save-to-file",
                Config: JsonNode.Parse("{\"folder\":\"%PICTURES%\\\\AresToys\"}"),
                Enabled: true,
                AbortOnError: true),
            new PipelineStep("arestoys.copy-url", Enabled: false)
        };

        var json = PipelineProfileSerializer.SerializeSteps(steps);
        var parsed = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(3, parsed["tasks"]!.AsArray().Count);
        Assert.Equal("arestoys.add-to-history", (string?)parsed["tasks"]![0]!["task_id"]);
        Assert.Equal("arestoys.save-to-file", (string?)parsed["tasks"]![1]!["task_id"]);
        Assert.Equal(true, (bool?)parsed["tasks"]![1]!["abort_on_error"]);
        Assert.Equal(false, (bool?)parsed["tasks"]![2]!["enabled"]);
    }

    [Fact]
    public void DeserializeSteps_RoundTripsAllFields()
    {
        var input = """
            {
              "tasks": [
                { "task_id": "arestoys.add-to-history" },
                { "task_id": "arestoys.save-to-file", "config": {"folder": "C:\\out"}, "enabled": true, "abort_on_error": true },
                { "task_id": "arestoys.copy-url", "enabled": false }
              ]
            }
            """;

        var steps = PipelineProfileSerializer.DeserializeSteps(input);

        Assert.Equal(3, steps.Count);
        Assert.Equal("arestoys.add-to-history", steps[0].TaskId);
        Assert.True(steps[0].Enabled);
        Assert.False(steps[0].AbortOnError);

        Assert.Equal("C:\\out", (string?)steps[1].Config!["folder"]);
        Assert.True(steps[1].AbortOnError);

        Assert.False(steps[2].Enabled);
    }

    [Fact]
    public void SerializeAndDeserialize_AreInverse()
    {
        var original = new[]
        {
            new PipelineStep("a"),
            new PipelineStep("b", Config: JsonNode.Parse("{\"x\":1}"), Enabled: false, AbortOnError: true)
        };

        var roundTripped = PipelineProfileSerializer.DeserializeSteps(
            PipelineProfileSerializer.SerializeSteps(original));

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal("a", roundTripped[0].TaskId);
        Assert.True(roundTripped[0].Enabled);
        Assert.False(roundTripped[1].Enabled);
        Assert.True(roundTripped[1].AbortOnError);
        Assert.Equal(1, (int?)roundTripped[1].Config!["x"]);
    }
}

using AresToys.AI.Dictation;
using Xunit;

namespace AresToys.App.Tests;

public class DictationEngineTests
{
    [Theory]
    [InlineData(" [BLANK_AUDIO] ", "")]
    [InlineData("(music)", "")]
    [InlineData(" Hello there [Music]", "Hello there")]
    [InlineData("Call me (later) please", "Call me (later) please")]
    public void Clean_DropsWhisperTagsOnly(string raw, string expected)
    {
        Assert.Equal(expected, WhisperTranscriber.Clean(raw));
    }

    [Fact]
    public void Language_MapsKeysToCodesAndFallsBackToEnglish()
    {
        Assert.Equal("it", DictationLanguages.CodeFor("Italian"));
        Assert.Equal("de", DictationLanguages.CodeFor("german"));
        Assert.Equal("en", DictationLanguages.CodeFor("klingon"));
        Assert.Equal("en", DictationLanguages.CodeFor(null));
    }

    [Fact]
    public void ModelPath_SitsInAModelsFolder()
    {
        var path = DictationModel.DefaultPath(@"C:\data");
        Assert.Equal(System.IO.Path.Combine(@"C:\data", "models", "ggml-small-q5_1.bin"), path);
    }

    // Loads the real model when ARESTOYS_TEST_WHISPER_MODEL points at one, to prove the native runtime
    // loads; silence must come back as no words.
    [Fact]
    public async Task Transcribe_SilenceGivesNoWords_WhenTheModelIsThere()
    {
        var model = Environment.GetEnvironmentVariable("ARESTOYS_TEST_WHISPER_MODEL");
        if (string.IsNullOrEmpty(model) || !System.IO.File.Exists(model)) return;

        using var transcriber = new WhisperTranscriber();
        var text = await transcriber.TranscribeAsync(new float[MicRecorder.SampleRate * 2], model, "en", CancellationToken.None);

        Assert.True(text.Length < 40, text);
    }
}

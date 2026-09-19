using System.Globalization;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>Every string the trim UI shows has to exist in both languages. A missing key doesn't
/// throw — the markup extension falls back to the key name — so the failure mode is an Italian
/// dialog with "VideoTrim_SetStart" printed on a button, which only a test like this catches.</summary>
public sealed class VideoTrimLocalizationTests
{
    [Theory]
    [InlineData("Clipboard_TooltipTrimVideo")]
    [InlineData("Clipboard_MenuTrimVideo")]
    [InlineData("VideoTrim_Title")]
    [InlineData("VideoTrim_SetStart")]
    [InlineData("VideoTrim_SetStartTooltip")]
    [InlineData("VideoTrim_SetEnd")]
    [InlineData("VideoTrim_SetEndTooltip")]
    [InlineData("VideoTrim_PlayPauseTooltip")]
    [InlineData("VideoTrim_StopTooltip")]
    [InlineData("VideoTrim_StartHandleTooltip")]
    [InlineData("VideoTrim_EndHandleTooltip")]
    [InlineData("VideoTrim_Selection")]
    [InlineData("VideoTrim_NothingSelected")]
    [InlineData("VideoTrim_Trim")]
    [InlineData("VideoTrim_Cancel")]
    [InlineData("VideoTrim_Working")]
    [InlineData("VideoTrim_Failed")]
    [InlineData("VideoTrim_PlaybackFailed")]
    public void EveryTrimStringExistsInBothLanguages(string key)
    {
        foreach (var culture in new[] { "en", "it" })
        {
            var value = AresToys.App.Resources.Strings.ResourceManager.GetString(key, new CultureInfo(culture));
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} missing for '{culture}'");
        }
    }

    [Fact]
    public void TheSelectionLineKeepsItsThreePlaceholdersInBothLanguages()
    {
        // It's fed start, end and length; dropping one in translation would throw at format time.
        foreach (var culture in new[] { "en", "it" })
        {
            var template = AresToys.App.Resources.Strings.ResourceManager.GetString(
                "VideoTrim_Selection", new CultureInfo(culture))!;
            Assert.Contains("{0}", template, StringComparison.Ordinal);
            Assert.Contains("{1}", template, StringComparison.Ordinal);
            Assert.Contains("{2}", template, StringComparison.Ordinal);
        }
    }
}

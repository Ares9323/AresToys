using AresToys.ImageEffects.Adjustments;
using AresToys.ImageEffects.Serialization;
using Xunit;

namespace AresToys.ImageEffects.Tests;

public sealed class SxieImporterTests
{
    [Theory]
    [InlineData("ShareX.ImageEffectsLib.Brightness, ShareX.ImageEffectsLib", "brightness")]
    [InlineData("ShareX.ImageEffectsLib.Contrast, ShareX.ImageEffectsLib", "contrast")]
    [InlineData("ShareX.ImageEffectsLib.Saturation", "saturation")]
    [InlineData("ShareX.ImageEditor.Core.ImageEffects.Adjustments.BrightnessImageEffect, ShareX.ImageEditor", "brightness")]
    [InlineData("ShareX.ImageEditor.Core.ImageEffects.Adjustments.GrayscaleImageEffect, ShareX.ImageEditor", "grayscale")]
    public void ResolveId_KnownTypeNames(string typeName, string expectedId)
    {
        var resolved = SxiePresetImporter.ResolveId(typeName);
        Assert.Equal(expectedId, resolved);
    }

    [Fact]
    public void Import_LegacyPreset_ProducesEntries()
    {
        // Hand-rolled JSON shaped exactly like a ShareX 17.x .sxie payload.
        const string json = """
        {
          "Name": "PolaroidLite",
          "Effects": [
            {
              "$type": "ShareX.ImageEffectsLib.Brightness, ShareX.ImageEffectsLib",
              "Amount": 12,
              "Enabled": true
            },
            {
              "$type": "ShareX.ImageEffectsLib.Saturation, ShareX.ImageEffectsLib",
              "Amount": -25,
              "Enabled": false
            },
            {
              "$type": "ShareX.ImageEffectsLib.Grayscale, ShareX.ImageEffectsLib",
              "Strength": 40,
              "Enabled": true
            }
          ]
        }
        """;

        var preset = SxiePresetImporter.Import(json);

        Assert.Equal("PolaroidLite", preset.Name);
        Assert.Equal(3, preset.Effects.Count);

        var brightness = Assert.IsType<BrightnessImageEffect>(preset.Effects[0].Effect);
        Assert.Equal(12, brightness.Amount);
        Assert.True(preset.Effects[0].Enabled);

        var saturation = Assert.IsType<SaturationImageEffect>(preset.Effects[1].Effect);
        Assert.Equal(-25, saturation.Amount);
        Assert.False(preset.Effects[1].Enabled);

        var gray = Assert.IsType<GrayscaleImageEffect>(preset.Effects[2].Effect);
        Assert.Equal(40, gray.Strength);
    }

    [Fact]
    public void Import_ModernNamespace_ProducesEntries()
    {
        const string json = """
        {
          "Name": "Modern",
          "Effects": [
            {
              "$type": "ShareX.ImageEditor.Core.ImageEffects.Adjustments.BrightnessImageEffect, ShareX.ImageEditor",
              "Amount": 50
            }
          ]
        }
        """;

        var preset = SxiePresetImporter.Import(json);
        Assert.Single(preset.Effects);
        var brightness = Assert.IsType<BrightnessImageEffect>(preset.Effects[0].Effect);
        Assert.Equal(50, brightness.Amount);
    }

    [Fact]
    public void Import_UnknownType_SkipsEntry()
    {
        const string json = """
        {
          "Name": "Mixed",
          "Effects": [
            { "$type": "ShareX.ImageEffectsLib.Brightness, ShareX.ImageEffectsLib", "Amount": 5 },
            { "$type": "ShareX.ImageEffectsLib.WeirdUnportedThing, ShareX.ImageEffectsLib", "Foo": 1 },
            { "$type": "ShareX.ImageEffectsLib.Hue, ShareX.ImageEffectsLib", "Amount": 30 }
          ]
        }
        """;

        var preset = SxiePresetImporter.Import(json);
        Assert.Equal(2, preset.Effects.Count);
        Assert.IsType<BrightnessImageEffect>(preset.Effects[0].Effect);
        Assert.IsType<HueImageEffect>(preset.Effects[1].Effect);
    }
}

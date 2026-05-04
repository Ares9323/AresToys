using ShareQ.ImageEffects.Adjustments;
using ShareQ.ImageEffects.Serialization;
using Xunit;

namespace ShareQ.ImageEffects.Tests;

public sealed class PresetSerializerTests
{
    [Fact]
    public void Roundtrip_PreservesEffectsAndParameters()
    {
        var serializer = new EffectPresetSerializer();
        var preset = new EffectPreset { Name = "Vintage" };
        preset.Effects.Add(new EffectPresetEntry(new BrightnessImageEffect { Amount = 15 }));
        preset.Effects.Add(new EffectPresetEntry(new SaturationImageEffect { Amount = -20 }, enabled: false));
        preset.Effects.Add(new EffectPresetEntry(new GrayscaleImageEffect { Strength = 60 }));

        var json = serializer.Serialize(preset);
        var loaded = serializer.Deserialize(json);

        Assert.NotNull(loaded);
        Assert.Equal("Vintage", loaded!.Name);
        Assert.Equal(3, loaded.Effects.Count);

        Assert.IsType<BrightnessImageEffect>(loaded.Effects[0].Effect);
        Assert.True(loaded.Effects[0].Enabled);
        Assert.Equal(15, ((BrightnessImageEffect)loaded.Effects[0].Effect!).Amount);

        Assert.IsType<SaturationImageEffect>(loaded.Effects[1].Effect);
        Assert.False(loaded.Effects[1].Enabled);
        Assert.Equal(-20, ((SaturationImageEffect)loaded.Effects[1].Effect!).Amount);

        Assert.IsType<GrayscaleImageEffect>(loaded.Effects[2].Effect);
        Assert.Equal(60, ((GrayscaleImageEffect)loaded.Effects[2].Effect!).Strength);
    }

    [Fact]
    public void Deserialize_UnknownEffectId_SkipsEntry()
    {
        const string json = """
        {
          "id": "test", "name": "Mixed",
          "effects": [
            { "id": "brightness", "enabled": true, "amount": 10 },
            { "id": "imaginary-future-effect", "enabled": true, "magic": 99 },
            { "id": "contrast", "enabled": true, "amount": -5 }
          ]
        }
        """;

        var loaded = new EffectPresetSerializer().Deserialize(json);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Effects.Count);
        Assert.IsType<BrightnessImageEffect>(loaded.Effects[0].Effect);
        Assert.IsType<ContrastImageEffect>(loaded.Effects[1].Effect);
    }

    [Fact]
    public void Deserialize_GarbledParameter_KeepsDefault()
    {
        const string json = """
        {
          "id": "test", "name": "Bad",
          "effects": [
            { "id": "brightness", "enabled": true, "amount": "not a number" }
          ]
        }
        """;

        var loaded = new EffectPresetSerializer().Deserialize(json);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Effects);
        var brightness = (BrightnessImageEffect)loaded.Effects[0].Effect!;
        Assert.Equal(0, brightness.Amount); // unchanged default
    }
}

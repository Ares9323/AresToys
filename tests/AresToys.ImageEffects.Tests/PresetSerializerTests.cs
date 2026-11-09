using AresToys.ImageEffects.Adjustments;
using AresToys.ImageEffects.Serialization;
using Xunit;

namespace AresToys.ImageEffects.Tests;

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
        // ShareX format: PascalCase + bare-class-name "$type" discriminator. Unknown $types
        // are silently skipped so a preset that mixes ported and not-yet-ported effects still
        // lands the parts we understand.
        const string json = """
        {
          "Name": "Mixed",
          "Effects": [
            { "$type": "Brightness", "Amount": 10, "Enabled": true },
            { "$type": "ImaginaryFutureEffect", "Magic": 99, "Enabled": true },
            { "$type": "Contrast", "Amount": -5, "Enabled": true }
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
        // A non-numeric value where a number is expected gets caught by the property binder's
        // try/catch and the effect keeps its default — one bad slider doesn't poison the
        // whole preset.
        const string json = """
        {
          "Name": "Bad",
          "Effects": [
            { "$type": "Brightness", "Amount": "not a number", "Enabled": true }
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

using AresToys.ImageEffects.Adjustments;
using Xunit;

namespace AresToys.ImageEffects.Tests;

public sealed class RegistryTests
{
    [Theory]
    [InlineData("brightness", typeof(BrightnessImageEffect))]
    [InlineData("contrast", typeof(ContrastImageEffect))]
    [InlineData("saturation", typeof(SaturationImageEffect))]
    [InlineData("hue", typeof(HueImageEffect))]
    [InlineData("gamma", typeof(GammaImageEffect))]
    [InlineData("grayscale", typeof(GrayscaleImageEffect))]
    public void Create_ReturnsConcreteType(string id, Type expectedType)
    {
        var effect = ImageEffectRegistry.Default.Create(id);
        Assert.NotNull(effect);
        Assert.IsType(expectedType, effect);
    }

    [Fact]
    public void Create_UnknownId_ReturnsNull()
    {
        var effect = ImageEffectRegistry.Default.Create("does-not-exist");
        Assert.Null(effect);
    }

    [Fact]
    public void Create_ReturnsFreshInstance()
    {
        var a = (BrightnessImageEffect)ImageEffectRegistry.Default.Create("brightness")!;
        var b = (BrightnessImageEffect)ImageEffectRegistry.Default.Create("brightness")!;
        a.Amount = 42;
        Assert.NotEqual(a.Amount, b.Amount);
    }

    [Fact]
    public void All_HasAtLeastSpikeAdjustments()
    {
        var ids = ImageEffectRegistry.Default.All.Select(d => d.Id).ToHashSet();
        Assert.Contains("brightness", ids);
        Assert.Contains("contrast", ids);
        Assert.Contains("saturation", ids);
        Assert.Contains("hue", ids);
        Assert.Contains("gamma", ids);
        Assert.Contains("grayscale", ids);
    }

    [Fact]
    public void ByCategory_FiltersCorrectly()
    {
        var adjustments = ImageEffectRegistry.Default.ByCategory(ImageEffectCategory.Adjustments).ToList();
        Assert.NotEmpty(adjustments);
        Assert.All(adjustments, d => Assert.Equal(ImageEffectCategory.Adjustments, d.Category));
    }
}

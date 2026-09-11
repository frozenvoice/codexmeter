using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class FlyoutZoomTests
{
    [Theory]
    [InlineData(int.MinValue, 80)]
    [InlineData(80, 80)]
    [InlineData(100, 100)]
    [InlineData(114, 110)]
    [InlineData(115, 120)]
    [InlineData(150, 150)]
    [InlineData(int.MaxValue, 150)]
    public void StoredZoomIsBoundedAndAlignedToSteps(int input, int expected) =>
        Assert.Equal(expected, FlyoutZoom.Normalize(input));

    [Fact]
    public void RepeatedKeysStayWithinBoundsAndCanReturnToDefault()
    {
        var zoom = 100;
        for (var i = 0; i < 50; i++) zoom = FlyoutZoom.Adjust(zoom, true);
        Assert.Equal(150, zoom);
        for (var i = 0; i < 50; i++) zoom = FlyoutZoom.Adjust(zoom, false);
        Assert.Equal(80, zoom);
        Assert.Equal(100, FlyoutZoom.Adjust(FlyoutZoom.Adjust(zoom, true), true));
    }

    [Fact]
    public void ExistingSettingsDefaultToOriginalSizeAndSavedZoomSurvivesReload()
    {
        Assert.Equal(100, SettingsMigration.FromJson("{}").FlyoutZoomPercent);
        var original = new AppSettings { FlyoutZoomPercent = 130, FlyoutPinned = true, WidgetOpacity = 0.7 };
        var loaded = SettingsMigration.FromJson(JsonSerializer.Serialize(original));
        Assert.Equal(130, loaded.FlyoutZoomPercent);
        Assert.True(loaded.FlyoutPinned);
        Assert.Equal(0.7, loaded.WidgetOpacity);
        Assert.Equal(150, SettingsMigration.FromJson("{\"FlyoutZoomPercent\":200}").FlyoutZoomPercent);
    }
}

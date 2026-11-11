using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// A driver built against the SDK's first increment keeps running: <see cref="PanelCapabilities"/> keeps its 11-parameter
/// constructor and its 11-output <c>Deconstruct</c> (a driver compiled against them calls them by their exact
/// signature), and what came after them are <c>init</c> properties with their defaults.
/// </summary>
public sealed class PanelCapabilitiesCompatibilityTests
{
    // The first increment's positional parameters, in order.
    private static readonly Type[] First =
    [
        typeof(int), typeof(int), typeof(IReadOnlyList<PanelFrameFormat>), typeof(int), typeof(int?), typeof(int), typeof(int),
        typeof(bool), typeof(PanelRange), typeof(TimeSpan?), typeof(bool),
    ];

    [Fact]
    public void The_first_increments_constructor_and_deconstruct_are_kept_and_the_later_capabilities_are_init_properties()
    {
        var type = typeof(PanelCapabilities);

        var constructor = Assert.Single(type.GetConstructors()); // public ones
        Assert.Equal(First, constructor.GetParameters().Select(p => p.ParameterType));
        var deconstruct = Assert.Single(type.GetMethods(), m => m.Name == "Deconstruct");
        Assert.Equal(First.Select(t => t.MakeByRefType()), deconstruct.GetParameters().Select(p => p.ParameterType));

        // The later capabilities: init properties, with their defaults.
        var capabilities = new PanelCapabilities(480, 320, [PanelFrameFormat.Rgb565Le], 30);

        Assert.False(capabilities.DimInSoftware);
        Assert.Null(capabilities.Overlays);
        Assert.Null(capabilities.Crop);
        Assert.Equal(5, capabilities.JpegQualityStep);
        foreach (var name in new[] { nameof(PanelCapabilities.DimInSoftware), nameof(PanelCapabilities.Overlays), nameof(PanelCapabilities.Crop), nameof(PanelCapabilities.JpegQualityStep) })
        {
            var setter = typeof(PanelCapabilities).GetProperty(name)!.SetMethod!;
            Assert.Contains(setter.ReturnParameter.GetRequiredCustomModifiers(), m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }

        var cropped = capabilities with { Crop = new PanelCrop(0, 0, 480, 302), JpegQualityStep = 10, DimInSoftware = true };
        Assert.Equal((new PanelCrop(0, 0, 480, 302), 10, true), (cropped.Crop, cropped.JpegQualityStep, cropped.DimInSoftware));
    }
}

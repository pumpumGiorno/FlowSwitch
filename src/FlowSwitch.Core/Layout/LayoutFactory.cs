using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

public static class LayoutFactory
{
    public static ILayoutEngine Create(SwitcherMode mode) => mode switch
    {
        SwitcherMode.OrbitMinimal => new SolarSystemLayout(minimal: true),
        SwitcherMode.Carousel => new CarouselLayout(),
        SwitcherMode.Grid => new GridLayout(),
        SwitcherMode.CoverFlow => new CoverFlowLayout(),
        _ => new SolarSystemLayout(),
    };

    public static bool IsOrbital(SwitcherMode mode) => mode is SwitcherMode.SolarSystem or SwitcherMode.OrbitMinimal;
}

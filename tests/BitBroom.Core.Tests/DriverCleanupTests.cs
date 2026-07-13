using BitBroom.Core.Special;
using Xunit;

namespace BitBroom.Core.Tests;

public class DriverCleanupTests
{
    // Real pnputil /enum-drivers layout (values are locale-stable; labels may localize).
    private const string SampleOutput = """
        Microsoft PnP Utility

        Published Name:     oem53.inf
        Original Name:      alderlakesystem.inf
        Provider Name:      INTEL
        Class Name:         System
        Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
        Driver Version:     07/18/2025 10.1.45.9
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem17.inf
        Original Name:      alderlakesystem.inf
        Provider Name:      INTEL
        Class Name:         System
        Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
        Driver Version:     07/18/2024 10.1.45.4
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem22.inf
        Original Name:      nv_dispi.inf
        Provider Name:      NVIDIA
        Class Name:         Display
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     09/12/2025 32.0.15.7652
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem9.inf
        Original Name:      nv_dispi.inf
        Provider Name:      NVIDIA
        Class Name:         Display
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     05/01/2025 32.0.15.7602
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem91.inf
        Original Name:      nv_dispi.inf
        Provider Name:      NVIDIA
        Class Name:         Display
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     01/01/2025 31.0.15.5000

        Published Name:     oem1.inf
        Original Name:      appleusb.inf
        Provider Name:      Apple, Inc.
        Class Name:         USBDevice
        Driver Version:     06/14/2023 538.0.0.0
        """;

    [Fact]
    public void Parses_all_driver_blocks()
    {
        var drivers = DriverCleanup.ParseEnumOutput(SampleOutput);
        Assert.Equal(6, drivers.Count);
        Assert.All(drivers, d => Assert.Matches(@"^oem\w+\.inf$", d.PublishedName));
        Assert.Contains(drivers, d => d.Provider == "NVIDIA" && d.OriginalName == "nv_dispi.inf");
    }

    [Fact]
    public void Keeps_the_newest_of_each_family_and_supersedes_the_rest()
    {
        var drivers = DriverCleanup.ParseEnumOutput(SampleOutput);
        var superseded = DriverCleanup.FindSuperseded(drivers);

        // NVIDIA display: 3 versions → 2 superseded (newest 32.0.15.7652 kept).
        // Intel alderlakesystem: 2 versions → 1 superseded (10.1.45.9 kept).
        // Apple USB: unique → never touched.
        Assert.Equal(3, superseded.Count);

        // The newest NVIDIA is never in the removal set; the two older ones are.
        Assert.DoesNotContain(superseded, s => s.Driver.PublishedName == "oem22.inf");
        Assert.Contains(superseded, s => s.Driver.PublishedName == "oem9.inf");
        Assert.Contains(superseded, s => s.Driver.PublishedName == "oem91.inf");

        // Everything removed keeps a strictly-newer sibling.
        Assert.All(superseded, s => Assert.True(s.KeptInstead.Version > s.Driver.Version));

        // The unique Apple driver is safe.
        Assert.DoesNotContain(superseded, s => s.Driver.OriginalName == "appleusb.inf");
    }

    [Fact]
    public void Empty_or_single_version_families_yield_nothing()
    {
        var drivers = DriverCleanup.ParseEnumOutput("Microsoft PnP Utility\n");
        Assert.Empty(drivers);
        Assert.Empty(DriverCleanup.FindSuperseded(drivers));
    }

    [Fact]
    public void Same_version_duplicates_are_never_offered_for_removal()
    {
        // Two staged copies of the SAME version (common for USB drivers serving multiple
        // hardware IDs) — neither is provably superseded, so neither may be removed.
        const string output = """
            Published Name:     oem23.inf
            Original Name:      ibtusb.inf
            Provider Name:      Intel Corporation
            Class Name:         Bluetooth
            Driver Version:     03/20/2025 24.10.0.4

            Published Name:     oem24.inf
            Original Name:      ibtusb.inf
            Provider Name:      Intel Corporation
            Class Name:         Bluetooth
            Driver Version:     03/20/2025 24.10.0.4

            Published Name:     oem3.inf
            Original Name:      ibtusb.inf
            Provider Name:      Intel Corporation
            Class Name:         Bluetooth
            Driver Version:     01/10/2024 23.90.0.8
            """;

        var drivers = DriverCleanup.ParseEnumOutput(output);
        Assert.Equal(3, drivers.Count);

        var superseded = DriverCleanup.FindSuperseded(drivers);
        SupersededDriver only = Assert.Single(superseded);
        Assert.Equal("oem3.inf", only.Driver.PublishedName);
    }
}

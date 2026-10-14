using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>The simulators stand in for payment and delivery, so where they may be on is a rule, not a convention.</summary>
public sealed class SimulatorOptionsTests
{
    private static List<string> Problems(bool enabled, string stage, bool allowLocal)
    {
        var problems = new List<string>();
        new SimulatorOptions { Enabled = enabled }.Validate(problems, stage, allowLocal);
        return problems;
    }

    [Theory]
    [InlineData("dev", false)]
    [InlineData("", true)]
    [InlineData("staging", true)]
    [InlineData("prod", true)]
    public void Switched_off_is_always_fine(string stage, bool allowLocal) =>
        Problems(enabled: false, stage, allowLocal).ShouldBeEmpty();

    [Fact]
    public void Switched_on_is_allowed_on_the_dev_stage() =>
        Problems(enabled: true, "dev", allowLocal: false).ShouldBeEmpty();

    [Fact]
    public void Switched_on_is_allowed_on_a_developers_machine_whatever_the_stage_says() =>
        Problems(enabled: true, "", allowLocal: true).ShouldBeEmpty();

    [Theory]
    [InlineData("staging")]
    [InlineData("prod")]
    [InlineData("")]
    [InlineData("Dev")]
    [InlineData("production")]
    public void Switched_on_anywhere_else_is_a_configuration_error(string stage) =>
        Problems(enabled: true, stage, allowLocal: false).ShouldHaveSingleItem().ShouldContain("Simulators:Enabled");
}

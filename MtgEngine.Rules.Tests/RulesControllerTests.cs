using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Controllers;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class RulesControllerTests
{
    private static KbDto GetPayload(ActionResult<KbDto> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<KbDto>().Subject;
    }

    private static T[] GetArray<T>(ActionResult<T[]> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<T[]>().Subject;
    }

    // =========================================================
    // GET /api/rules — full payload
    // =========================================================

    [Fact]
    public void Get_returns_200()
    {
        var dto = GetPayload(new RulesController().Get());
        dto.Should().NotBeNull();
    }

    [Fact]
    public void Get_includes_keywords_mechanics_and_sbas()
    {
        var dto = GetPayload(new RulesController().Get());

        dto.Keywords.Should().NotBeEmpty();
        dto.Mechanics.Should().NotBeEmpty();
        dto.StateBasedActions.Should().NotBeEmpty();
    }

    // =========================================================
    // GET /api/rules/keywords
    // =========================================================

    [Fact]
    public void GetKeywords_returns_same_count_as_full_payload()
    {
        var full = GetPayload(new RulesController().Get());
        var kw   = GetArray(new RulesController().GetKeywords());

        kw.Should().HaveCount(full.Keywords.Length);
    }

    [Fact]
    public void GetKeywords_every_entry_has_name_description_and_rulesRef()
    {
        var kw = GetArray(new RulesController().GetKeywords());

        foreach (var k in kw)
        {
            k.Name.Should().NotBeNullOrWhiteSpace(because: $"keyword '{k.Name}' must have a name");
            k.Description.Should().NotBeNullOrWhiteSpace(because: $"keyword '{k.Name}' must have a description");
            k.RulesRef.Should().NotBeNullOrWhiteSpace(because: $"keyword '{k.Name}' must have a rules reference");
        }
    }

    [Fact]
    public void GetKeywords_statuses_are_only_valid_values()
    {
        var valid = new[] { "implemented", "partial", "stub" };
        var kw = GetArray(new RulesController().GetKeywords());

        foreach (var k in kw)
            k.Status.Should().BeOneOf(valid, because: $"'{k.Name}' status must be a recognised value");
    }

    [Fact]
    public void GetKeywords_flying_is_implemented()
    {
        var kw = GetArray(new RulesController().GetKeywords());
        var flying = kw.Should().ContainSingle(k => k.Name == "Flying").Subject;
        flying.Status.Should().Be("implemented");
    }

    [Fact]
    public void GetKeywords_ward_is_stub()
    {
        var kw = GetArray(new RulesController().GetKeywords());
        var ward = kw.Should().ContainSingle(k => k.Name == "Ward").Subject;
        ward.Status.Should().Be("stub");
    }

    [Fact]
    public void GetKeywords_menace_is_partial()
    {
        var kw = GetArray(new RulesController().GetKeywords());
        var menace = kw.Should().ContainSingle(k => k.Name == "Menace").Subject;
        menace.Status.Should().Be("partial");
    }

    [Fact]
    public void GetKeywords_rules_refs_start_with_CR()
    {
        var kw = GetArray(new RulesController().GetKeywords());
        foreach (var k in kw)
            k.RulesRef.Should().StartWith("CR", because: $"'{k.Name}' rulesRef should be a CR citation");
    }

    // =========================================================
    // GET /api/rules/mechanics
    // =========================================================

    [Fact]
    public void GetMechanics_returns_same_count_as_full_payload()
    {
        var full = GetPayload(new RulesController().Get());
        var m    = GetArray(new RulesController().GetMechanics());

        m.Should().HaveCount(full.Mechanics.Length);
    }

    [Fact]
    public void GetMechanics_every_entry_has_name_and_description()
    {
        var mechanics = GetArray(new RulesController().GetMechanics());

        foreach (var m in mechanics)
        {
            m.Name.Should().NotBeNullOrWhiteSpace();
            m.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetMechanics_turn_structure_has_twelve_steps()
    {
        var mechanics = GetArray(new RulesController().GetMechanics());
        var ts = mechanics.Should().ContainSingle(m => m.Name == "Turn Structure").Subject;
        ts.Steps.Should().HaveCount(12);
    }

    [Fact]
    public void GetMechanics_zones_has_seven_entries()
    {
        var mechanics = GetArray(new RulesController().GetMechanics());
        var zones = mechanics.Should().ContainSingle(m => m.Name == "Zones").Subject;
        zones.Steps.Should().HaveCount(7);
    }

    [Fact]
    public void GetMechanics_step_entries_all_have_name_and_description()
    {
        var mechanics = GetArray(new RulesController().GetMechanics());

        foreach (var m in mechanics)
        foreach (var s in m.Steps ?? [])
        {
            s.Name.Should().NotBeNullOrWhiteSpace(because: $"mechanic '{m.Name}' step must have a name");
            s.Description.Should().NotBeNullOrWhiteSpace(because: $"mechanic '{m.Name}' step '{s.Name}' must have a description");
        }
    }

    // =========================================================
    // GET /api/rules/sba
    // =========================================================

    [Fact]
    public void GetSba_returns_same_count_as_full_payload()
    {
        var full = GetPayload(new RulesController().Get());
        var sba  = GetArray(new RulesController().GetSba());

        sba.Should().HaveCount(full.StateBasedActions.Length);
    }

    [Fact]
    public void GetSba_every_entry_has_rulesRef_and_description()
    {
        var sba = GetArray(new RulesController().GetSba());

        foreach (var s in sba)
        {
            s.RulesRef.Should().NotBeNullOrWhiteSpace();
            s.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetSba_all_refs_start_with_CR_704()
    {
        var sba = GetArray(new RulesController().GetSba());
        foreach (var s in sba)
            s.RulesRef.Should().StartWith("CR 704", because: "all SBAs live in CR 704");
    }

    [Fact]
    public void GetSba_statuses_are_only_valid_values()
    {
        var valid = new[] { "implemented", "stub" };
        var sba = GetArray(new RulesController().GetSba());

        foreach (var s in sba)
            s.Status.Should().BeOneOf(valid);
    }

    [Fact]
    public void GetSba_life_loss_rule_is_implemented()
    {
        var sba = GetArray(new RulesController().GetSba());
        var rule = sba.Should().ContainSingle(s => s.RulesRef == "CR 704.5a").Subject;
        rule.Status.Should().Be("implemented");
    }

    [Fact]
    public void GetSba_legend_rule_is_implemented()
    {
        var sba = GetArray(new RulesController().GetSba());
        var rule = sba.Should().ContainSingle(s => s.RulesRef == "CR 704.5j").Subject;
        rule.Status.Should().Be("implemented");
    }
}

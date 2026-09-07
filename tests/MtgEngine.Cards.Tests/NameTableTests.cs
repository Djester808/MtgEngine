using System.Text.Json.Serialization;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The name table is the set of [JsonDerivedType] attributes on each polymorphic base. A
/// record that is not registered would serialise as its base and lose everything, silently.
/// These tests make that a red build instead.
/// </summary>
public sealed class NameTableTests
{
    public static TheoryData<Type> Bases => [.. CardFileSerializer.PolymorphicBases];

    [Theory]
    [MemberData(nameof(Bases))]
    public void Every_concrete_record_under_a_base_is_registered(Type polymorphicBase)
    {
        var concrete = typeof(CardFileSerializer).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && polymorphicBase.IsAssignableFrom(t) && t != polymorphicBase)
            .ToHashSet();
        var registered = CardFileSerializer.RegisteredNames(polymorphicBase);

        var missing = concrete.Where(t => !registered.ContainsKey(t)).Select(t => t.Name).Order().ToList();
        Assert.True(missing.Count == 0, $"{polymorphicBase.Name} is missing [JsonDerivedType] for: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public void Every_registered_type_is_named_after_itself_and_belongs_to_the_base(Type polymorphicBase)
    {
        foreach (var (type, name) in CardFileSerializer.RegisteredNames(polymorphicBase))
        {
            Assert.True(polymorphicBase.IsAssignableFrom(type), $"{type.Name} is registered on {polymorphicBase.Name} but does not derive from it.");
            Assert.Equal(type.Name, name);
        }
    }

    [Fact]
    public void Every_polymorphic_base_uses_the_same_discriminator()
    {
        foreach (var polymorphicBase in CardFileSerializer.PolymorphicBases)
        {
            var attribute = polymorphicBase.GetCustomAttributes(typeof(JsonPolymorphicAttribute), false).OfType<JsonPolymorphicAttribute>().Single();
            Assert.Equal("kind", attribute.TypeDiscriminatorPropertyName);
        }
    }

    [Fact]
    public void No_discriminator_is_used_twice_across_bases()
    {
        // A translator emits {"kind": "Exile"} inside an Effect and inside a Cost would be two
        // different records; that is fine. What is not fine is the same name under one base.
        foreach (var polymorphicBase in CardFileSerializer.PolymorphicBases)
        {
            var names = CardFileSerializer.RegisteredNames(polymorphicBase).Values.ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }
}

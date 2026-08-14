using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Every domain CardType flag must survive the trip to the DTO enum — a missing
/// arm silently erased Battles (et al.) from API responses.
/// </summary>
public class DomainMapperCardTypeTests
{
    [Theory]
    [InlineData(CardType.Battle, CardTypeDto.Battle)]
    [InlineData(CardType.Tribal, CardTypeDto.Tribal)]
    [InlineData(CardType.Token, CardTypeDto.Token)]
    [InlineData(CardType.Other, CardTypeDto.Other)]
    [InlineData(CardType.Creature, CardTypeDto.Creature)]
    public void SingleFlag_MapsToItsDto(CardType flag, CardTypeDto expected) =>
        Assert.Equal([expected], DomainMapper.ToCardTypeDto(flag));

    [Fact]
    public void EveryDefinedFlag_ProducesAtLeastOneDto()
    {
        foreach (CardType flag in Enum.GetValues<CardType>())
        {
            if (flag == CardType.None)
                continue;
            Assert.NotEmpty(DomainMapper.ToCardTypeDto(flag));
        }
    }
}

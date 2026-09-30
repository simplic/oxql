using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.Core;

/// <summary>The former cache key still binds for one release; where both are set, the new key wins.</summary>
public class CacheOptionsAliasTests
{
    private static OxQLOptions Bound(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => "OxQL:Cache:" + setting.Key, setting => (string?)setting.Value))
            .Build();

        return configuration.GetSection("OxQL").Get<OxQLOptions>()!;
    }

    [Fact]
    public void The_former_key_alone_still_binds() =>
        Bound(("ResolveCacheMaxEntries", "111")).Cache.OwnerFetchCacheMaxEntries.Should().Be(111);

    [Fact]
    public void The_new_key_wins_over_the_former_one() =>
        Bound(("OwnerFetchCacheMaxEntries", "222"), ("ResolveCacheMaxEntries", "111")).Cache.OwnerFetchCacheMaxEntries.Should().Be(222);
}

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NSubstitute;
using OxQL.Core;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Core;

/// <summary>The limits a host configures are brought into the range the engine can work with, and every adjustment is said once.</summary>
public class CoreHardeningOptionsTests
{
    [Fact]
    public void The_shipped_defaults_need_no_adjustment()
    {
        var options = new OxQLOptions();

        options.Normalise().Should().BeEmpty();
        options.Limits.Should().BeEquivalentTo(new LimitOptions());
    }

    [Fact]
    public void A_limit_at_its_cap_is_left_alone()
    {
        var options = new OxQLOptions
        {
            Limits = { MaxPageSize = 500, DefaultPageSize = 500, ResolveKeyChunk = 500, MaxOffset = 5_000, MaxSemiJoinIds = 5_000 },
        };

        options.Normalise().Should().BeEmpty();
        options.Limits.DefaultPageSize.Should().Be(500);
        options.Limits.ResolveKeyChunk.Should().Be(500);
        options.Limits.MaxSemiJoinIds.Should().Be(5_000);
    }

    [Fact]
    public void A_limit_above_its_cap_is_clamped_to_it_and_named()
    {
        var options = new OxQLOptions
        {
            Limits = { MaxPageSize = 50, DefaultPageSize = 100, ResolveKeyChunk = 51, MaxOffset = 1_000, MaxSemiJoinIds = 1_001 },
        };

        var adjustments = options.Normalise();

        options.Limits.DefaultPageSize.Should().Be(50);
        options.Limits.ResolveKeyChunk.Should().Be(50);
        options.Limits.MaxSemiJoinIds.Should().Be(1_000);
        adjustments.Should().HaveCount(3);
        adjustments.Should().Contain(line => line.Contains("DefaultPageSize"))
            .And.Contain(line => line.Contains("ResolveKeyChunk"))
            .And.Contain(line => line.Contains("MaxSemiJoinIds"));
    }

    public static TheoryData<string> Limits => new(typeof(LimitOptions).GetProperties().Select(property => property.Name));

    [Theory]
    [MemberData(nameof(Limits))]
    public void A_limit_below_the_least_the_engine_can_work_with_is_raised_to_it(string limit)
    {
        var property = typeof(LimitOptions).GetProperty(limit)!;
        var least = limit == nameof(LimitOptions.MaxOffset) ? 0 : 1;

        foreach (var configured in new[] { least - 1, -5, int.MinValue })
        {
            var options = new OxQLOptions();

            property.SetValue(options.Limits, configured);

            var adjustments = options.Normalise();

            ((int)property.GetValue(options.Limits)!).Should().Be(least, $"{limit} was configured as {configured}");
            adjustments.Should().Contain(line => line.Contains($"OxQL:Limits:{limit} was {configured}"));
        }
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void A_limit_at_the_least_the_engine_can_work_with_is_left_alone(string limit)
    {
        var property = typeof(LimitOptions).GetProperty(limit)!;
        var least = limit == nameof(LimitOptions.MaxOffset) ? 0 : 1;
        var options = new OxQLOptions();

        property.SetValue(options.Limits, least);

        options.Normalise().Should().NotContain(line => line.Contains($"OxQL:Limits:{limit} was"));
        ((int)property.GetValue(options.Limits)!).Should().Be(least);
    }

    [Fact]
    public void A_count_cap_below_one_never_reaches_the_database_as_a_limit_it_rejects()
    {
        var options = new OxQLOptions { Limits = { CountCap = -1 } };

        options.Normalise();

        (options.Limits.CountCap + 1).Should().BePositive("the count pipeline ends in $limit: CountCap + 1");
    }

    [Fact]
    public void The_registration_binds_the_section_and_normalises_what_it_bound()
    {
        var section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OxQL:Limits:MaxPageSize"] = "0",
            ["OxQL:Limits:CountCap"] = "-10",
            ["OxQL:Limits:MaxLookupLimit"] = "7",
            ["OxQL:Cursor:SigningKey"] = BindHost.SigningKey,
        }).Build().GetSection("OxQL");

        using var provider = new ServiceCollection().AddOxQLCore(section).BuildServiceProvider();
        var options = provider.GetRequiredService<OxQLOptions>();

        options.Limits.MaxPageSize.Should().Be(1);
        options.Limits.DefaultPageSize.Should().Be(1, "the default page cannot exceed the largest one");
        options.Limits.CountCap.Should().Be(1);
        options.Limits.MaxLookupLimit.Should().Be(7);
        provider.GetServices<OxQLOptionsAdjustment>().Select(adjustment => adjustment.Message).Should()
            .Contain(line => line.Contains("MaxPageSize was 0"))
            .And.Contain(line => line.Contains("CountCap was -10"))
            .And.Contain(line => line.Contains("DefaultPageSize was 100"));
    }

    [Fact]
    public void The_engine_logs_every_adjustment_once_when_it_is_built()
    {
        var log = new RecordingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(log))
            .AddOxQLCore(options =>
            {
                options.Cursor.SigningKey = BindHost.SigningKey;
                options.Limits.MaxPageSize = 0;
            })
            .AddSingleton<IEntityModelProvider>(new StaticEntityModelProvider(BindHost.Probe))
            .AddSingleton<IAggregateRunner>(new FakeAggregateRunner())
            .AddSingleton(Substitute.For<IMongoClient>())
            .AddOxQLMongo(_ => { });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IQueryEngine>();
        provider.GetRequiredService<IQueryEngine>();

        // The page size itself, and the two limits that may not exceed it.
        log.Warnings.Should().HaveCount(3)
            .And.Contain(line => line.Contains("MaxPageSize was 0"))
            .And.Contain(line => line.Contains("ResolveKeyChunk was 500"))
            .And.Contain(line => line.Contains("DefaultPageSize was 100"));
    }

    [Fact]
    public void The_engine_logs_nothing_about_a_configuration_it_did_not_adjust()
    {
        var log = new RecordingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(log))
            .AddOxQLCore(options => options.Cursor.SigningKey = BindHost.SigningKey)
            .AddSingleton<IEntityModelProvider>(new StaticEntityModelProvider(BindHost.Probe))
            .AddSingleton<IAggregateRunner>(new FakeAggregateRunner())
            .AddSingleton(Substitute.For<IMongoClient>())
            .AddOxQLMongo(_ => { });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IQueryEngine>();

        log.Warnings.Should().BeEmpty();
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Warnings);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    lock (warnings)
                        warnings.Add(formatter(state, exception));
            }
        }
    }
}

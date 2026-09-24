using System.Collections.Concurrent;
using EphemeralMongo;
using MongoDB.Driver;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// The one MongoDB server of a test run, started on first use and shared by every test class.
/// By default EphemeralMongo starts a MongoDB 8 single-node replica set on a random port and the
/// process exit stops it. Overrides, in order of precedence:
/// <list type="bullet">
/// <item><c>OXQL_TEST_MONGO=&lt;uri&gt;</c> uses an existing server and starts nothing; the databases this run created are dropped at exit.</item>
/// <item><c>OXQL_TEST_MONGO_VERSION=6|7|8</c> picks the major version EphemeralMongo starts.</item>
/// <item><c>OXQL_TEST_MONGO_BIN=&lt;dir&gt;</c> starts the <c>mongod</c> in that directory instead of a downloaded one.</item>
/// </list>
/// When no server can be provided every test that needs one fails with the same message naming
/// the overrides; nothing is skipped.
/// </summary>
public static class MongoFixture
{
    public const string ConnectionVariable = "OXQL_TEST_MONGO";

    public const string VersionVariable = "OXQL_TEST_MONGO_VERSION";

    public const string BinaryVariable = "OXQL_TEST_MONGO_BIN";

    /// <summary>MongoDB caps a database name at 63 bytes.</summary>
    private const int MaxDatabaseName = 63;

    private static readonly Lazy<Task<Server>> Shared = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, byte> Created = new(StringComparer.Ordinal);

    private static int sequence;

    /// <summary>Distinguishes this run's databases from another run's on a shared server.</summary>
    public static string RunId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The run's client, connected to the shared server; starts the server on first use.</summary>
    public static async Task<IMongoClient> ClientAsync() => (await Shared.Value).Client;

    /// <summary>A database under a name no other test or run uses; dropped again when the result is disposed.</summary>
    public static async Task<TestDatabase> CreateDatabaseAsync(string purpose)
    {
        var client = await ClientAsync();

        return new TestDatabase(client, DatabaseName(purpose));
    }

    /// <summary>
    /// A database name unique to this run: <c>oxql_it_&lt;run&gt;_&lt;purpose&gt;_&lt;n&gt;</c>. Recorded, so
    /// a database a test forgot to drop does not outlive the run on a shared server.
    /// </summary>
    public static string DatabaseName(string purpose)
    {
        var clean = new string(purpose.Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray());
        var suffix = $"_{Interlocked.Increment(ref sequence)}";
        var name = $"oxql_it_{RunId}_{clean}";

        if (name.Length + suffix.Length > MaxDatabaseName)
            name = name[..(MaxDatabaseName - suffix.Length)];

        name += suffix;
        Created.TryAdd(name, 0);

        return name;
    }

    private static async Task<Server> StartAsync()
    {
        var uri = Environment.GetEnvironmentVariable(ConnectionVariable);

        if (!string.IsNullOrWhiteSpace(uri))
            return await ConnectAsync(new MongoClient(uri), runner: null, $"the server named by {ConnectionVariable}");

        IMongoRunner runner;
        var options = RunnerOptions();

        try
        {
            runner = await MongoRunner.RunAsync(options);
        }
        catch (Exception exception)
        {
            throw Unavailable($"EphemeralMongo could not start MongoDB {(int)options.Version}: {exception.Message.TrimEnd('.')}", exception);
        }

        return await ConnectAsync(new MongoClient(runner.ConnectionString), runner, $"MongoDB {(int)options.Version} started by EphemeralMongo");
    }

    private static MongoRunnerOptions RunnerOptions()
    {
        var options = new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
        };

        if (Environment.GetEnvironmentVariable(VersionVariable) is { Length: > 0 } version)
        {
            options.Version = version.Trim() switch
            {
                "6" => MongoVersion.V6,
                "7" => MongoVersion.V7,
                "8" => MongoVersion.V8,
                _ => throw Unavailable($"{VersionVariable} is '{version}'; it takes 6, 7 or 8."),
            };
        }

        if (Environment.GetEnvironmentVariable(BinaryVariable) is { Length: > 0 } binaries)
            options.BinaryDirectory = binaries;

        return options;
    }

    /// <summary>Proves the server answers before any test relies on it, and arranges the cleanup at process exit.</summary>
    private static async Task<Server> ConnectAsync(MongoClient client, IMongoRunner? runner, string origin)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.GetDatabase("admin").RunCommandAsync<MongoDB.Bson.BsonDocument>("{ ping: 1 }", cancellationToken: timeout.Token);
        }
        catch (Exception exception)
        {
            runner?.Dispose();
            throw Unavailable($"{origin} did not answer a ping: {exception.Message.TrimEnd('.')}", exception);
        }

        var server = new Server(client, runner);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Stop();

        return server;
    }

    private static InvalidOperationException Unavailable(string reason, Exception? inner = null) => new(
        $"The OxQL integration tests need a MongoDB server and none could be provided: {reason}. " +
        $"Set {ConnectionVariable}=<connection string> to use an existing server, {VersionVariable}=6|7|8 to pick the version " +
        $"EphemeralMongo downloads, or {BinaryVariable}=<directory> to use local mongod binaries. " +
        "The first run downloads MongoDB once and needs network access for that.",
        inner);

    private sealed class Server(MongoClient client, IMongoRunner? runner)
    {
        public IMongoClient Client => client;

        public void Stop()
        {
            if (runner is not null)
            {
                // The process and its data directory go together; nothing is left to drop.
                runner.Dispose();
                return;
            }

            foreach (var name in Created.Keys)
            {
                try
                {
                    client.DropDatabase(name);
                }
                catch (MongoException)
                {
                    // A server that went away first has nothing left to clean.
                }
            }
        }
    }
}

/// <summary>A database of one test or class, dropped on disposal.</summary>
public sealed class TestDatabase(IMongoClient client, string name) : IAsyncDisposable
{
    public string Name { get; } = name;

    public IMongoDatabase Database { get; } = client.GetDatabase(name);

    public async ValueTask DisposeAsync() => await client.DropDatabaseAsync(Name);
}

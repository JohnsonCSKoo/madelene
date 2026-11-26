using Madelene.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Madelene.Tests;

/// <summary>
/// End-to-end test against a real MongoDB replica set (change streams need one).
/// Opt-in: set MADELENE_MONGO_URI, e.g. mongodb://localhost:27017/?directConnection=true.
/// Uses a throwaway database that is dropped afterwards.
/// </summary>
public sealed class MongoSyncIntegrationTests
{
    private const int Dims = 32;

    [Fact]
    public async Task BackfillTailUpdatesDeletesAndResume()
    {
        var uri = Environment.GetEnvironmentVariable("MADELENE_MONGO_URI");
        if (string.IsNullOrEmpty(uri))
            return; // not configured: skip

        var client = new MongoClient(uri);
        var dbName = "madelene_it_" + Guid.NewGuid().ToString("N")[..8];
        var db = client.GetDatabase(dbName);
        var items = db.GetCollection<BsonDocument>("items");
        using var tmp = new TempDirectory();
        try
        {
            var rng = new Random(1);
            var centres = TestData.Centres(10, Dims, 2);
            BsonDocument Item(int i, string tenant) => new()
            {
                { "_id", ObjectId.GenerateNewId() },
                { "tenantId", tenant },
                { "sourceId", "src" + (i % 7) },
                { "tags", new BsonArray { "tok" + (i % 5), "tok" + (i % 11) } },
                { "timestamp", DateTime.UtcNow.AddMinutes(-i) },
                { "data", new BsonDocument("vector", new BsonArray(TestData.Near(centres[i % 10], rng, 0.5f).Select(f => (double)f))) },
            };

            // Seeded before the sync starts: picked up by the backfill scan.
            var initial = Enumerable.Range(0, 1_500).Select(i => Item(i, i % 3 == 0 ? "other" : "t1")).ToList();
            await items.InsertManyAsync(initial);

            var definition = new VectorIndexDefinition
            {
                Name = "items.t1",
                VectorPath = "data.vector",
                Dimensions = Dims,
                FilterPaths = ["tenantId", "sourceId", "tags"],
                TimePath = "timestamp",
                Options = new MadeleneIndexOptions { SealRows = 400 },
            };
            var options = new MongoSyncOptions
            {
                Match = new BsonDocument("tenantId", "t1"),
                CheckpointInterval = TimeSpan.FromMilliseconds(200),
                MaxAwaitTime = TimeSpan.FromMilliseconds(100),
            };

            using (var engine = new MadeleneEngine(tmp.Path))
            {
                var sync = new MongoIndexSync(engine, items, definition, options);
                using var cts = new CancellationTokenSource();
                var run = sync.RunAsync(cts.Token);
                var t1Count = initial.Count(d => d["tenantId"] == "t1");
                await WaitFor(() => Live(sync.Index) == t1Count, "backfill", run);

                // Changes after the backfill flow through the change stream.
                var added = Item(5_000, "t1");
                await items.InsertOneAsync(added);
                await items.InsertOneAsync(Item(5_001, "other")); // filtered out by Match
                var moved = initial.First(d => d["tenantId"] == "t1");
                await items.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", moved["_id"]), Builders<BsonDocument>.Update.Set("tenantId", "other"));
                var removed = initial.Last(d => d["tenantId"] == "t1");
                await items.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", removed["_id"]));
                await WaitFor(() => Live(sync.Index) == t1Count + 1 - 2, "tail", run, () => Live(sync.Index) + " live vs expected " + (t1Count - 1));

                var stage = new BsonDocument("$vectorSearch", new BsonDocument
                {
                    { "index", definition.Name },
                    { "path", "data.vector" },
                    { "queryVector", added["data"]["vector"] },
                    { "numCandidates", 100 },
                    { "limit", 5 },
                    { "filter", new BsonDocument { { "tenantId", "t1" }, { "tags", new BsonDocument("$in", new BsonArray { "tok0" }) } } },
                });
                var hits = MongoVectorSearch.Search(engine, stage, out _);
                Assert.Equal(added["_id"], hits[0].Id);

                // Hydrate through MongoDB with the rewritten pipeline.
                var pipeline = MongoVectorSearch.RewritePipeline(engine, [stage, new BsonDocument("$project", new BsonDocument { { "sourceId", 1 }, { "score", new BsonDocument("$meta", "vectorSearchScore") } })], out _);
                var docs = await items.Aggregate<BsonDocument>(pipeline).ToListAsync();
                Assert.Equal(added["_id"], docs[0]["_id"]);
                Assert.True(docs[0]["score"].AsDouble > 0.99);

                await WaitFor(() => sync.Index.Checkpoint?.StartsWith("rt:") == true, "checkpoint", run);
                await cts.CancelAsync();
                await run;
            }

            // Writes while the sync is down are replayed from the stored resume token.
            var whileDown = Item(6_000, "t1");
            await items.InsertOneAsync(whileDown);
            using (var engine = new MadeleneEngine(tmp.Path))
            {
                var sync = new MongoIndexSync(engine, items, definition, options);
                var before = Live(sync.Index);
                using var cts = new CancellationTokenSource();
                var run = sync.RunAsync(cts.Token);
                await WaitFor(() => Live(sync.Index) >= before + 1 && Contains(engine, definition, whileDown), "resume", run);
                await cts.CancelAsync();
                await run;
            }
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    private static long Live(VectorIndex index)
    {
        var stats = index.GetStatistics();
        return stats.LiveSegmentRows + stats.BufferedRows;
    }

    private static bool Contains(MadeleneEngine engine, VectorIndexDefinition definition, BsonDocument doc)
    {
        var stage = new BsonDocument("$vectorSearch", new BsonDocument
        {
            { "index", definition.Name }, { "path", definition.VectorPath },
            { "queryVector", doc["data"]["vector"] }, { "exact", true }, { "limit", 1 },
        });
        var hits = MongoVectorSearch.Search(engine, stage, out _);
        return hits.Count > 0 && hits[0].Id == doc["_id"];
    }

    private static async Task WaitFor(Func<bool> condition, string what, Task background, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            if (background.IsFaulted)
                throw new InvalidOperationException($"Sync failed while waiting for {what}.", background.Exception);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}. {detail?.Invoke()}");
            await Task.Delay(100);
        }
    }
}

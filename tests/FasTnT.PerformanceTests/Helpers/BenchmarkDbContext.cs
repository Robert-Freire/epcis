using FasTnT.Application.Database;
using FasTnT.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FasTnT.PerformanceTests.Helpers;

public static class BenchmarkDbContext
{
    /// <summary>
    /// Creates an EpcisContext configured with SQLite for benchmarking.
    /// </summary>
    /// <param name="databaseName">Name of the database. Use ":memory:" for in-memory database.</param>
    /// <param name="useInMemory">If true, uses in-memory database. Otherwise, creates a file-based database.</param>
    /// <returns>Configured EpcisContext ready for benchmarking.</returns>
    public static EpcisContext CreateContext(string databaseName, bool useInMemory = false)
    {
        var connectionString = useInMemory
            ? "DataSource=:memory:"
            : $"DataSource={databaseName}.db";

        var connection = new SqliteConnection(connectionString);

        // For in-memory databases, keep connection open
        if (useInMemory)
        {
            connection.Open();
        }

        var options = new DbContextOptionsBuilder<EpcisContext>()
            .UseSqlite(connection, x =>
            {
                x.MigrationsAssembly(typeof(SqliteProvider).Assembly.FullName);
                x.CommandTimeout(300); // 5 minutes timeout for large operations
            })
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        var context = new EpcisContext(options);

        // Apply migrations
        context.Database.Migrate();

        return context;
    }

    /// <summary>
    /// Creates an in-memory EpcisContext for benchmarking.
    /// </summary>
    /// <param name="databaseName">Unique name for the in-memory database.</param>
    /// <returns>Configured EpcisContext with in-memory database.</returns>
    public static EpcisContext CreateInMemoryContext(string databaseName)
    {
        return CreateContext(databaseName, useInMemory: true);
    }

    /// <summary>
    /// Creates a file-based EpcisContext for benchmarking.
    /// </summary>
    /// <param name="databaseName">Name of the database file (without extension).</param>
    /// <returns>Configured EpcisContext with file-based database.</returns>
    public static EpcisContext CreateFileContext(string databaseName)
    {
        return CreateContext(databaseName, useInMemory: false);
    }

    /// <summary>
    /// Cleans up database resources.
    /// </summary>
    /// <param name="context">The context to clean up.</param>
    /// <param name="deleteFile">If true, deletes the database file.</param>
    public static void Cleanup(EpcisContext context, bool deleteFile = false)
    {
        var connection = context.Database.GetDbConnection();
        var filePath = connection.DataSource;

        context.Dispose();
        connection.Dispose();

        if (deleteFile && filePath != ":memory:" && File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Clears all data from the database without dropping the schema.
    /// </summary>
    /// <param name="context">The context to clear.</param>
    public static async Task ClearDataAsync(EpcisContext context)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            // Disable foreign key checks during cleanup
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF");

            // Delete all tables in dependency-safe order
            // Child tables of Event (owned entities)
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Epc");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Source");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Destination");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM BusinessTransaction");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM PersistentDisposition");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM SensorElement");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM SensorReport");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Field");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM CorrectiveEventId");

            // Child tables of MasterData (owned entities)
            await context.Database.ExecuteSqlRawAsync("DELETE FROM MasterDataField");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM MasterDataAttribute");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM MasterDataChildren");

            // Child tables of StandardBusinessHeader (owned entities)
            await context.Database.ExecuteSqlRawAsync("DELETE FROM ContactInformation");

            // Child tables of Subscription (owned entities)
            await context.Database.ExecuteSqlRawAsync("DELETE FROM SubscriptionParameter");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM SubscriptionSchedule");

            // Child tables of StoredQuery (owned entities)
            await context.Database.ExecuteSqlRawAsync("DELETE FROM StoredQueryParameter");

            // Parent tables - delete in reverse dependency order
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Event");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM MasterData");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM StandardBusinessHeader");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM SubscriptionCallback");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Subscription");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM StoredQuery");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Request");

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            // Re-enable foreign key checks
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON");
        }
    }
}

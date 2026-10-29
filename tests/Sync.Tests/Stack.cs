namespace Sync.Tests;

/// <summary>One PostgreSQL, one SQL Server and one RabbitMQ shared by every test class (started once per run).
/// Tests in a collection run one after another, and each resets the parts it uses.</summary>
[CollectionDefinition("stack")]
public class StackCollection :
    ICollectionFixture<PostgresFixture>, ICollectionFixture<SqlServerFixture>, ICollectionFixture<RabbitFixture>;

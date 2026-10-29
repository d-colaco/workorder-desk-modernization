using Microsoft.EntityFrameworkCore;

namespace WorkOrders.Api.Data;

public class LabDbContext(DbContextOptions<LabDbContext> options) : DbContext(options)
{
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderOperation> Operations => Set<WorkOrderOperation>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<ConflictLogEntry> Conflicts => Set<ConflictLogEntry>();
    public DbSet<Planner> Planners => Set<Planner>();
    public DbSet<ErasureLogEntry> ErasureLog => Set<ErasureLogEntry>();
    public DbSet<ScrapReason> ScrapReasons => Set<ScrapReason>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    // The legacy side stores plant wall-clock datetimes with no zone; keep them as such rather than inventing UTC.
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveColumnType("timestamp without time zone");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<WorkOrder>(e =>
        {
            e.ToTable("work_order");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.LegacyWorkOrderId).IsUnique();
            e.Property(x => x.ProductName).HasMaxLength(50);
            e.Ignore(x => x.Status);
            e.HasMany(x => x.Operations).WithOne().HasForeignKey(o => o.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.PublishedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.ConfirmedAt).HasColumnType("timestamp with time zone");
            e.HasIndex(x => x.Id).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_unpublished");
        });
        b.Entity<ConflictLogEntry>(e =>
        {
            e.ToTable("conflict_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.HasIndex(x => x.LegacyWorkOrderId);
        });
        b.Entity<Planner>(e =>
        {
            e.ToTable("planner");
            e.HasKey(x => x.BusinessEntityId);
            e.Property(x => x.BusinessEntityId).ValueGeneratedNever();
            e.Property(x => x.ErasedAt).HasColumnType("timestamp with time zone");
        });
        b.Entity<ErasureLogEntry>(e =>
        {
            e.ToTable("erasure_log");
            e.HasKey(x => x.RequestId);
            e.Property(x => x.RequestedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.LegacyConfirmedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.NewPurgedAt).HasColumnType("timestamp with time zone");
        });
        b.Entity<ScrapReason>(e =>
        {
            e.ToTable("scrap_reason");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });
        b.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("processed_message");
            e.HasKey(x => x.MessageId);
            e.Property(x => x.ProcessedAt).HasColumnType("timestamp with time zone");
        });
        b.Entity<WorkOrderOperation>(e =>
        {
            e.ToTable("work_order_operation");
            e.HasKey(x => new { x.WorkOrderId, x.OperationSequence });
            e.Property(x => x.LocationName).HasMaxLength(50);
            e.Property(x => x.ActualResourceHrs).HasPrecision(9, 4);
            e.Property(x => x.PlannedCost).HasPrecision(19, 4);
            e.Property(x => x.ActualCost).HasPrecision(19, 4);
        });
    }
}

-- V004: legacy-side outbox. Triggers on the work-order tables write sync.Outbox in the caller's
-- transaction, so every writer is captured (the WPF client, scripts, other screens), not only our procs.
-- Origin comes from SESSION_CONTEXT 'sync.origin' (default 'legacy'); L4's consumer sets 'new'.
CREATE SCHEMA sync AUTHORIZATION dbo;
GO
CREATE TABLE sync.AggregateVersion (
    AggregateId int    NOT NULL CONSTRAINT PK_AggregateVersion PRIMARY KEY,   -- WorkOrderID
    Version     bigint NOT NULL
);
CREATE TABLE sync.Outbox (
    OutboxId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
    AggregateType nvarchar(30)  NOT NULL,
    AggregateId   int           NOT NULL,
    Version       bigint        NOT NULL,
    Origin        nvarchar(10)  NOT NULL,
    TraceParent   nvarchar(60)  NULL,
    Payload       nvarchar(max) NOT NULL,
    CreatedAt     datetime2(3)  NOT NULL CONSTRAINT DF_Outbox_CreatedAt DEFAULT sysutcdatetime(),
    PublishedAt   datetime2(3)  NULL
);
CREATE INDEX IX_Outbox_Unpublished ON sync.Outbox (OutboxId) WHERE PublishedAt IS NULL;
GO
-- Shared body: bump the per-order version and write one snapshot row per affected order.
-- Called by both triggers with the affected ids in #sync_ids.
CREATE PROCEDURE sync.uspEnqueueWorkOrders
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @origin nvarchar(10) = ISNULL(CONVERT(nvarchar(10), SESSION_CONTEXT(N'sync.origin')), N'legacy');
    DECLARE @trace nvarchar(60) = CONVERT(nvarchar(60), SESSION_CONTEXT(N'traceparent'));
    DECLARE @v TABLE (AggregateId int PRIMARY KEY, Version bigint);

    INSERT sync.AggregateVersion (AggregateId, Version)
    SELECT i.WorkOrderID, 0 FROM #sync_ids i
    WHERE NOT EXISTS (SELECT 1 FROM sync.AggregateVersion a WHERE a.AggregateId = i.WorkOrderID);

    UPDATE a SET Version = a.Version + 1
    OUTPUT inserted.AggregateId, inserted.Version INTO @v
    FROM sync.AggregateVersion a JOIN #sync_ids i ON i.WorkOrderID = a.AggregateId;

    INSERT sync.Outbox (AggregateType, AggregateId, Version, Origin, TraceParent, Payload)
    SELECT N'WorkOrder', v.AggregateId, v.Version, @origin, @trace,
        ISNULL((
            SELECT wo.WorkOrderID AS workOrderId, wo.ProductID AS productId, p.Name AS productName,
                   wo.OrderQty AS orderQty, wo.ScrappedQty AS scrappedQty, wo.StartDate AS startDate,
                   wo.EndDate AS endDate, wo.DueDate AS dueDate, wo.ScrapReasonID AS scrapReasonId,
                   JSON_QUERY(ISNULL((
                       SELECT r.OperationSequence AS operationSequence, r.LocationID AS locationId, l.Name AS locationName,
                              r.ScheduledStartDate AS scheduledStartDate, r.ScheduledEndDate AS scheduledEndDate,
                              r.ActualStartDate AS actualStartDate, r.ActualEndDate AS actualEndDate,
                              r.ActualResourceHrs AS actualResourceHrs, r.PlannedCost AS plannedCost, r.ActualCost AS actualCost
                       FROM Production.WorkOrderRouting r JOIN Production.Location l ON l.LocationID = r.LocationID
                       WHERE r.WorkOrderID = wo.WorkOrderID ORDER BY r.OperationSequence
                       FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')) AS operations
            FROM Production.WorkOrder wo JOIN Production.Product p ON p.ProductID = wo.ProductID
            WHERE wo.WorkOrderID = v.AggregateId
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES), N'{"deleted":true}')
    FROM @v v;
END;
GO
CREATE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    CREATE TABLE #sync_ids (WorkOrderID int PRIMARY KEY);
    INSERT #sync_ids SELECT WorkOrderID FROM inserted UNION SELECT WorkOrderID FROM deleted;
    EXEC sync.uspEnqueueWorkOrders;
END;
GO
CREATE TRIGGER Production.trWorkOrderRoutingSync ON Production.WorkOrderRouting AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    CREATE TABLE #sync_ids (WorkOrderID int PRIMARY KEY);
    INSERT #sync_ids SELECT WorkOrderID FROM inserted UNION SELECT WorkOrderID FROM deleted;
    EXEC sync.uspEnqueueWorkOrders;
END;
GO
-- The relay logs in as lab_app: it reads the outbox and marks rows published.
GRANT SELECT, UPDATE ON sync.Outbox TO lab_app;
GO

-- V007: conflict handling and reconciliation support (L6).
--  * sync.Outbox.CommandId: the id of the new-side command that caused this change (SESSION_CONTEXT
--    'sync.command_id'), so the new side can mark that command confirmed.
--  * sync.ConflictLog: every conflict the legacy side resolved, with both values and the winner.
--  * sync.uspResync: re-emit an order's current state as an origin-'new' confirmation, used when a command
--    is refused (order already closed) so the new side drops its optimistic state.
ALTER TABLE sync.Outbox ADD CommandId nvarchar(100) NULL;
GO
CREATE TABLE sync.ConflictLog (
    ConflictId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConflictLog PRIMARY KEY,
    WorkOrderID int           NOT NULL,
    Field       nvarchar(30)  NOT NULL,
    BaseValue   nvarchar(50)  NULL,     -- what the planner saw when editing
    LegacyValue nvarchar(50)  NULL,     -- what the legacy side held
    NewValue    nvarchar(50)  NULL,     -- what the planner asked for
    Winner      nvarchar(10)  NOT NULL, -- 'new' or 'legacy'
    MessageId   nvarchar(100) NULL,
    CreatedAt   datetime2(3)  NOT NULL CONSTRAINT DF_ConflictLog_CreatedAt DEFAULT sysutcdatetime()
);
CREATE INDEX IX_ConflictLog_WorkOrderID ON sync.ConflictLog (WorkOrderID);
GRANT INSERT, SELECT ON sync.ConflictLog TO lab_app;
GO
ALTER PROCEDURE sync.uspEnqueueWorkOrders
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @origin nvarchar(10) = ISNULL(CONVERT(nvarchar(10), SESSION_CONTEXT(N'sync.origin')), N'legacy');
    DECLARE @trace nvarchar(60) = CONVERT(nvarchar(60), SESSION_CONTEXT(N'traceparent'));
    DECLARE @commandId nvarchar(100) = CONVERT(nvarchar(100), SESSION_CONTEXT(N'sync.command_id'));
    DECLARE @newId uniqueidentifier = TRY_CONVERT(uniqueidentifier, CONVERT(nvarchar(36), SESSION_CONTEXT(N'sync.new_id')));
    DECLARE @v TABLE (AggregateId int PRIMARY KEY, Version bigint);

    INSERT sync.AggregateVersion (AggregateId, Version)
    SELECT i.WorkOrderID, 0 FROM #sync_ids i
    WHERE NOT EXISTS (SELECT 1 FROM sync.AggregateVersion a WHERE a.AggregateId = i.WorkOrderID);

    UPDATE a SET Version = a.Version + 1
    OUTPUT inserted.AggregateId, inserted.Version INTO @v
    FROM sync.AggregateVersion a JOIN #sync_ids i ON i.WorkOrderID = a.AggregateId;

    INSERT sync.Outbox (AggregateType, AggregateId, Version, Origin, TraceParent, NewId, CommandId, Payload)
    SELECT N'WorkOrder', v.AggregateId, v.Version, @origin, @trace, @newId, @commandId,
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
CREATE PROCEDURE sync.uspResync @WorkOrderID int
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #sync_ids (WorkOrderID int PRIMARY KEY);
    INSERT #sync_ids VALUES (@WorkOrderID);
    EXEC sync.uspEnqueueWorkOrders;
END;
GO
GRANT EXECUTE ON sync.uspResync TO lab_app;
GO

-- V005: new -> legacy support.
--  * sync.Outbox.NewId: the new side's id for an order it created, read from SESSION_CONTEXT 'sync.new_id'.
--    The first snapshot of such an order carries it, which is how the new side learns the legacy id.
--  * sync.Inbox: commands from the new side already applied (committed with their effect).
--  * dbo.uspWorkOrderChangePlan: the planner's edit of quantity and due date, through a procedure so the
--    AdventureWorks triggers (uWorkOrder -> TransactionHistory) still fire.
ALTER TABLE sync.Outbox ADD NewId uniqueidentifier NULL;
GO
CREATE TABLE sync.Inbox (
    MessageId   nvarchar(100) NOT NULL CONSTRAINT PK_Inbox PRIMARY KEY,
    ProcessedAt datetime2(3)  NOT NULL CONSTRAINT DF_Inbox_ProcessedAt DEFAULT sysutcdatetime()
);
GRANT INSERT, SELECT ON sync.Inbox TO lab_app;
GO
ALTER PROCEDURE sync.uspEnqueueWorkOrders
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @origin nvarchar(10) = ISNULL(CONVERT(nvarchar(10), SESSION_CONTEXT(N'sync.origin')), N'legacy');
    DECLARE @trace nvarchar(60) = CONVERT(nvarchar(60), SESSION_CONTEXT(N'traceparent'));
    DECLARE @newId uniqueidentifier = TRY_CONVERT(uniqueidentifier, CONVERT(nvarchar(36), SESSION_CONTEXT(N'sync.new_id')));
    DECLARE @v TABLE (AggregateId int PRIMARY KEY, Version bigint);

    INSERT sync.AggregateVersion (AggregateId, Version)
    SELECT i.WorkOrderID, 0 FROM #sync_ids i
    WHERE NOT EXISTS (SELECT 1 FROM sync.AggregateVersion a WHERE a.AggregateId = i.WorkOrderID);

    UPDATE a SET Version = a.Version + 1
    OUTPUT inserted.AggregateId, inserted.Version INTO @v
    FROM sync.AggregateVersion a JOIN #sync_ids i ON i.WorkOrderID = a.AggregateId;

    INSERT sync.Outbox (AggregateType, AggregateId, Version, Origin, TraceParent, NewId, Payload)
    SELECT N'WorkOrder', v.AggregateId, v.Version, @origin, @trace, @newId,
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
CREATE PROCEDURE dbo.uspWorkOrderChangePlan
    @WorkOrderID int, @OrderQty int = NULL, @DueDate datetime = NULL, @ActorBusinessEntityID int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        DECLARE @Scrapped int, @End datetime, @Found int;
        SELECT @Found = 1, @Scrapped = ScrappedQty, @End = EndDate FROM Production.WorkOrder WHERE WorkOrderID = @WorkOrderID;
        IF @Found IS NULL THROW 50050, 'Work order not found.', 1;
        IF @End IS NOT NULL THROW 50051, 'Work order is closed.', 1;
        IF @OrderQty IS NOT NULL AND @OrderQty < @Scrapped THROW 50052, 'Quantity is below what is already scrapped.', 1;
        BEGIN TRAN;
        UPDATE Production.WorkOrder
        SET OrderQty = ISNULL(@OrderQty, OrderQty), DueDate = ISNULL(@DueDate, DueDate), ModifiedDate = GETDATE()
        WHERE WorkOrderID = @WorkOrderID;
        INSERT dbo.WorkOrderAudit (WorkOrderID, Action, ActorBusinessEntityID, Detail)
        VALUES (@WorkOrderID, N'ChangePlan', @ActorBusinessEntityID, CONCAT(N'qty ', @OrderQty, N' due ', CONVERT(nvarchar(10), @DueDate, 23)));
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO

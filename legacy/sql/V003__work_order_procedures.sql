-- V003: the work-order desk procedures (AdventureWorks naming, dbo.usp...). Each takes the acting
-- employee and writes dbo.WorkOrderAudit. A failing AdventureWorks trigger rolls back the caller's
-- transaction itself (error 3609 reaches the caller); the CATCH blocks cope with that.
CREATE PROCEDURE dbo.uspWorkOrderCreate
    @ProductID int, @OrderQty int, @DueDate datetime, @ActorBusinessEntityID int, @WorkOrderID int OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM HumanResources.Employee WHERE BusinessEntityID = @ActorBusinessEntityID)
            THROW 50001, 'Unknown actor.', 1;
        IF NOT EXISTS (SELECT 1 FROM Production.Product WHERE ProductID = @ProductID AND MakeFlag = 1)
            THROW 50002, 'Product does not exist or is not manufactured.', 1;
        BEGIN TRAN;
        INSERT Production.WorkOrder (ProductID, OrderQty, ScrappedQty, StartDate, EndDate, DueDate, ScrapReasonID, ModifiedDate)
        VALUES (@ProductID, @OrderQty, 0, GETDATE(), NULL, @DueDate, NULL, GETDATE());
        SET @WorkOrderID = SCOPE_IDENTITY();
        INSERT dbo.WorkOrderAudit (WorkOrderID, Action, ActorBusinessEntityID, Detail)
        VALUES (@WorkOrderID, N'Create', @ActorBusinessEntityID, CONCAT(N'qty ', @OrderQty));
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO
CREATE PROCEDURE dbo.uspWorkOrderRelease
    @WorkOrderID int, @ActorBusinessEntityID int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        DECLARE @ProductID int, @Start datetime, @End datetime, @Template int, @Shift int, @Rows int;
        SELECT @ProductID = ProductID, @Start = StartDate, @End = EndDate
        FROM Production.WorkOrder WHERE WorkOrderID = @WorkOrderID;
        IF @ProductID IS NULL THROW 50010, 'Work order not found.', 1;
        IF @End IS NOT NULL THROW 50011, 'Work order is closed.', 1;
        IF EXISTS (SELECT 1 FROM Production.WorkOrderRouting WHERE WorkOrderID = @WorkOrderID)
            THROW 50012, 'Work order is already released.', 1;
        -- No routing template exists: copy the operations of the product's most recent earlier order.
        SELECT TOP (1) @Template = r.WorkOrderID
        FROM Production.WorkOrderRouting r
        WHERE r.ProductID = @ProductID AND r.WorkOrderID <> @WorkOrderID
        ORDER BY r.WorkOrderID DESC;
        IF @Template IS NULL THROW 50013, 'No earlier work order of this product to copy operations from.', 1;
        SELECT @Shift = DATEDIFF(DAY, StartDate, @Start) FROM Production.WorkOrder WHERE WorkOrderID = @Template;
        BEGIN TRAN;
        INSERT Production.WorkOrderRouting
            (WorkOrderID, ProductID, OperationSequence, LocationID, ScheduledStartDate, ScheduledEndDate,
             ActualStartDate, ActualEndDate, ActualResourceHrs, PlannedCost, ActualCost, ModifiedDate)
        SELECT @WorkOrderID, ProductID, OperationSequence, LocationID,
               DATEADD(DAY, @Shift, ScheduledStartDate), DATEADD(DAY, @Shift, ScheduledEndDate),
               NULL, NULL, NULL, PlannedCost, NULL, GETDATE()
        FROM Production.WorkOrderRouting WHERE WorkOrderID = @Template;
        SET @Rows = @@ROWCOUNT;
        INSERT dbo.WorkOrderAudit (WorkOrderID, Action, ActorBusinessEntityID, Detail)
        VALUES (@WorkOrderID, N'Release', @ActorBusinessEntityID, CONCAT(@Rows, N' operations from order ', @Template));
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO
CREATE PROCEDURE dbo.uspWorkOrderRoutingRecord
    @WorkOrderID int, @OperationSequence smallint, @ActualStartDate datetime, @ActualEndDate datetime = NULL,
    @ActualResourceHrs decimal(9,4) = NULL, @ActualCost money = NULL, @ActorBusinessEntityID int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM Production.WorkOrder WHERE WorkOrderID = @WorkOrderID)
            THROW 50020, 'Work order not found.', 1;
        IF EXISTS (SELECT 1 FROM Production.WorkOrder WHERE WorkOrderID = @WorkOrderID AND EndDate IS NOT NULL)
            THROW 50021, 'Work order is closed.', 1;
        IF NOT EXISTS (SELECT 1 FROM Production.WorkOrderRouting WHERE WorkOrderID = @WorkOrderID AND OperationSequence = @OperationSequence)
            THROW 50022, 'Operation not found (is the order released?).', 1;
        BEGIN TRAN;
        UPDATE Production.WorkOrderRouting
        SET ActualStartDate = @ActualStartDate, ActualEndDate = @ActualEndDate,
            ActualResourceHrs = @ActualResourceHrs, ActualCost = @ActualCost, ModifiedDate = GETDATE()
        WHERE WorkOrderID = @WorkOrderID AND OperationSequence = @OperationSequence;
        INSERT dbo.WorkOrderAudit (WorkOrderID, Action, ActorBusinessEntityID, Detail)
        VALUES (@WorkOrderID, N'Routing', @ActorBusinessEntityID, CONCAT(N'operation ', @OperationSequence));
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO
CREATE PROCEDURE dbo.uspWorkOrderScrap
    @WorkOrderID int, @ScrapQty smallint, @ScrapReasonID smallint, @ActorBusinessEntityID int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        DECLARE @Qty int, @Scrapped int, @End datetime;
        SELECT @Qty = OrderQty, @Scrapped = ScrappedQty, @End = EndDate
        FROM Production.WorkOrder WHERE WorkOrderID = @WorkOrderID;
        IF @Qty IS NULL THROW 50030, 'Work order not found.', 1;
        IF @End IS NOT NULL THROW 50031, 'Work order is closed.', 1;
        IF @ScrapQty <= 0 THROW 50032, 'Scrap quantity must be positive.', 1;
        IF @Scrapped + @ScrapQty > @Qty THROW 50033, 'Scrap would exceed the order quantity.', 1;
        IF NOT EXISTS (SELECT 1 FROM Production.ScrapReason WHERE ScrapReasonID = @ScrapReasonID)
            THROW 50034, 'Unknown scrap reason.', 1;
        BEGIN TRAN;
        UPDATE Production.WorkOrder
        SET ScrappedQty = ScrappedQty + @ScrapQty, ScrapReasonID = @ScrapReasonID, ModifiedDate = GETDATE()
        WHERE WorkOrderID = @WorkOrderID;
        INSERT dbo.WorkOrderAudit (WorkOrderID, Action, ActorBusinessEntityID, Detail)
        VALUES (@WorkOrderID, N'Scrap', @ActorBusinessEntityID, CONCAT(N'+', @ScrapQty, N' reason ', @ScrapReasonID));
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO
CREATE PROCEDURE dbo.uspWorkOrderClose
    @WorkOrderID int, @ActorBusinessEntityID int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        DECLARE @End datetime, @Found int;
        SELECT @Found = 1, @End = EndDate FROM Production.WorkOrder WHERE WorkOrderID = @WorkOrderID;
        IF @Found IS NULL THROW 50040, 'Work order not found.', 1;
        IF @End IS NOT NULL THROW 50041, 'Work order is already closed.', 1;
        IF NOT EXISTS (SELECT 1 FROM Production.WorkOrderRouting WHERE WorkOrderID = @WorkOrderID)
            THROW 50042, 'Work order is not released.', 1;
        IF EXISTS (SELECT 1 FROM Production.WorkOrderRouting WHERE WorkOrderID = @WorkOrderID AND ActualEndDate IS NULL)
            THROW 50043, 'Not every operation has an actual end date.', 1;
        BEGIN TRAN;
        UPDATE Production.WorkOrder
        SET EndDate = (SELECT CASE WHEN MAX(ActualEndDate) > StartDate THEN MAX(ActualEndDate) ELSE StartDate END
                       FROM Production.WorkOrderRouting r WHERE r.WorkOrderID = Production.WorkOrder.WorkOrderID),
            ModifiedDate = GETDATE()
        WHERE WorkOrderID = @WorkOrderID;
        INSERT dbo.WorkOrderAudit (WorkOrderID, Action, ActorBusinessEntityID, Detail)
        VALUES (@WorkOrderID, N'Close', @ActorBusinessEntityID, NULL);
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO
GRANT EXECUTE ON SCHEMA::dbo TO lab_app;
GO

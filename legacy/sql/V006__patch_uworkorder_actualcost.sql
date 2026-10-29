-- V006: LAB PATCH to Microsoft's sample, not our design. In the unmodified AdventureWorks2022 backup,
-- Production.uWorkOrder inserts into TransactionHistory without the NOT NULL column ActualCost, so any UPDATE
-- that touches OrderQty or ProductID fails (error 515, then 3609 "transaction ended in the trigger").
-- iWorkOrder supplies ActualCost = 0; this makes uWorkOrder do the same and changes nothing else.
-- Without it no quantity change is possible on the legacy side, from the desktop app or from the sync.
ALTER TRIGGER [Production].[uWorkOrder] ON [Production].[WorkOrder] 
AFTER UPDATE AS 
BEGIN
    DECLARE @Count int;

    SET @Count = @@ROWCOUNT;
    IF @Count = 0 
        RETURN;

    SET NOCOUNT ON;

    BEGIN TRY
        IF UPDATE([ProductID]) OR UPDATE([OrderQty])
        BEGIN
            INSERT INTO [Production].[TransactionHistory](
                [ProductID]
                ,[ReferenceOrderID]
                ,[TransactionType]
                ,[TransactionDate]
                ,[Quantity]
                ,[ActualCost])
            SELECT 
                inserted.[ProductID]
                ,inserted.[WorkOrderID]
                ,'W'
                ,GETDATE()
                ,inserted.[OrderQty]
                ,0
            FROM inserted;
        END;
    END TRY
    BEGIN CATCH
        EXECUTE [dbo].[uspPrintError];

        -- Rollback any active or uncommittable transactions before
        -- inserting information in the ErrorLog
        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK TRANSACTION;
        END

        EXECUTE [dbo].[uspLogError];
    END CATCH;
END;
GO

-- V002: derived lifecycle. AdventureWorks has no status column and we do not add one to its tables.
-- created = no routing rows; released = routing rows; in progress = any actual start; closed = EndDate set.
CREATE VIEW dbo.vWorkOrderStatus
AS
SELECT  wo.WorkOrderID, wo.ProductID, p.Name AS ProductName, wo.OrderQty, wo.ScrappedQty,
        wo.StockedQty, wo.StartDate, wo.EndDate, wo.DueDate, wo.ScrapReasonID, wo.ModifiedDate,
        CASE WHEN wo.EndDate IS NOT NULL THEN N'Closed'
             WHEN EXISTS (SELECT 1 FROM Production.WorkOrderRouting r
                          WHERE r.WorkOrderID = wo.WorkOrderID AND r.ActualStartDate IS NOT NULL) THEN N'In progress'
             WHEN EXISTS (SELECT 1 FROM Production.WorkOrderRouting r
                          WHERE r.WorkOrderID = wo.WorkOrderID) THEN N'Released'
             ELSE N'Created' END AS Status
FROM    Production.WorkOrder wo
JOIN    Production.Product p ON p.ProductID = wo.ProductID;
GO

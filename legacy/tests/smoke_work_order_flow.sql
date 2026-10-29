-- Drives the desk procedures end to end, as the desktop driver does. Rolls nothing back: it creates one work order.
SET NOCOUNT ON;
DECLARE @actor int = (SELECT TOP (1) e.BusinessEntityID FROM HumanResources.Employee e
    JOIN HumanResources.EmployeeDepartmentHistory h ON h.BusinessEntityID = e.BusinessEntityID AND h.EndDate IS NULL
    JOIN HumanResources.Department d ON d.DepartmentID = h.DepartmentID WHERE d.Name = 'Production' ORDER BY e.BusinessEntityID);
DECLARE @product int = (SELECT TOP (1) ProductID FROM Production.WorkOrderRouting GROUP BY ProductID ORDER BY ProductID);
DECLARE @id int, @due datetime = DATEADD(DAY, 14, GETDATE());
EXEC dbo.uspWorkOrderCreate @product, 10, @due, @actor, @id OUTPUT;
SELECT @id AS WorkOrderID, Status FROM dbo.vWorkOrderStatus WHERE WorkOrderID = @id;
EXEC dbo.uspWorkOrderRelease @id, @actor;
SELECT Status FROM dbo.vWorkOrderStatus WHERE WorkOrderID = @id;
DECLARE @seq smallint, @now datetime = GETDATE();
DECLARE c CURSOR FOR SELECT OperationSequence FROM Production.WorkOrderRouting WHERE WorkOrderID = @id ORDER BY OperationSequence;
OPEN c; FETCH NEXT FROM c INTO @seq;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC dbo.uspWorkOrderRoutingRecord @id, @seq, @now, @now, 1.5, 20.0, @actor;
    FETCH NEXT FROM c INTO @seq;
END
CLOSE c; DEALLOCATE c;
EXEC dbo.uspWorkOrderScrap @id, 2, 1, @actor;
EXEC dbo.uspWorkOrderClose @id, @actor;
SELECT Status, OrderQty, ScrappedQty, StockedQty FROM dbo.vWorkOrderStatus WHERE WorkOrderID = @id;
SELECT Action, Detail FROM dbo.WorkOrderAudit WHERE WorkOrderID = @id ORDER BY AuditID;
SELECT COUNT(*) AS TransactionHistoryRows FROM Production.TransactionHistory WHERE ReferenceOrderID = @id AND TransactionType = 'W';

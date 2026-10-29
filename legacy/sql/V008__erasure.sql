-- V008: erasure of a data subject (an employee) on the owning side (L8).
--  * dbo.ErasedSubject: who has been erased. A later write that names them is stored as the tombstone -1
--    (a late command still in flight, or anything else that records the actor).
--  * dbo.ErasureLog: minimal evidence that an erasure happened. It holds the request id, the (now meaningless)
--    BusinessEntityID, a timestamp and row counts, and no personal data.
--  * dbo.uspEraseSubject: anonymises the person and removes contact data; records the audit trail without the actor;
--    queues a SubjectErased event so the new side purges its copies.
-- What stays, and why: the Person and Employee rows (payroll, purchasing and sales rows point at them), pay history
-- and hire date (retention duties are a legal question, not a technical one). They no longer identify anyone by
-- name, login, national id, contact data or address. They are placeholders, not true values.
CREATE TABLE dbo.ErasedSubject (
    BusinessEntityID int          NOT NULL CONSTRAINT PK_ErasedSubject PRIMARY KEY,
    ErasedAt         datetime2(3) NOT NULL CONSTRAINT DF_ErasedSubject_At DEFAULT sysutcdatetime()
);
CREATE TABLE dbo.ErasureLog (
    RequestId                uniqueidentifier NOT NULL CONSTRAINT PK_ErasureLog PRIMARY KEY,
    SubjectBusinessEntityID  int              NOT NULL,
    ErasedAt                 datetime2(3)     NOT NULL CONSTRAINT DF_ErasureLog_At DEFAULT sysutcdatetime(),
    Detail                   nvarchar(400)    NULL
);
GO
CREATE TRIGGER dbo.trWorkOrderAuditErased ON dbo.WorkOrderAudit AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE a SET ActorBusinessEntityID = -1
    FROM dbo.WorkOrderAudit a
    JOIN inserted i ON i.AuditID = a.AuditID
    WHERE EXISTS (SELECT 1 FROM dbo.ErasedSubject s WHERE s.BusinessEntityID = i.ActorBusinessEntityID);
END;
GO
CREATE PROCEDURE dbo.uspEraseSubject
    @SubjectBusinessEntityID int, @RequestId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        IF EXISTS (SELECT 1 FROM dbo.ErasureLog WHERE RequestId = @RequestId) RETURN;   -- idempotent
        IF NOT EXISTS (SELECT 1 FROM Person.Person WHERE BusinessEntityID = @SubjectBusinessEntityID)
            THROW 50060, 'Unknown data subject.', 1;
        DECLARE @email int, @phone int, @address int, @password int, @audit int;
        DECLARE @addr TABLE (AddressID int PRIMARY KEY);

        BEGIN TRAN;
        UPDATE Person.Person
        SET Title = NULL, FirstName = N'Erased', MiddleName = NULL, LastName = N'Erased', Suffix = NULL,
            AdditionalContactInfo = NULL, Demographics = NULL, EmailPromotion = 0, ModifiedDate = GETDATE()
        WHERE BusinessEntityID = @SubjectBusinessEntityID;

        DELETE Person.EmailAddress WHERE BusinessEntityID = @SubjectBusinessEntityID;  SET @email = @@ROWCOUNT;
        DELETE Person.PersonPhone  WHERE BusinessEntityID = @SubjectBusinessEntityID;  SET @phone = @@ROWCOUNT;
        DELETE Person.[Password]   WHERE BusinessEntityID = @SubjectBusinessEntityID;  SET @password = @@ROWCOUNT;

        INSERT @addr SELECT AddressID FROM Person.BusinessEntityAddress WHERE BusinessEntityID = @SubjectBusinessEntityID;
        DELETE Person.BusinessEntityAddress WHERE BusinessEntityID = @SubjectBusinessEntityID;
        -- An address row goes only if nothing else uses it (shared addresses belong to other parties).
        DELETE a FROM Person.Address a JOIN @addr x ON x.AddressID = a.AddressID
        WHERE NOT EXISTS (SELECT 1 FROM Person.BusinessEntityAddress b WHERE b.AddressID = a.AddressID)
          AND NOT EXISTS (SELECT 1 FROM Sales.SalesOrderHeader s WHERE s.BillToAddressID = a.AddressID OR s.ShipToAddressID = a.AddressID);
        SET @address = @@ROWCOUNT;

        UPDATE HumanResources.Employee
        SET NationalIDNumber = CONCAT(N'ERASED-', @SubjectBusinessEntityID), LoginID = CONCAT(N'erased\', @SubjectBusinessEntityID),
            JobTitle = N'Erased', BirthDate = '1930-01-01', MaritalStatus = N'S', Gender = N'M', ModifiedDate = GETDATE()
        WHERE BusinessEntityID = @SubjectBusinessEntityID;
        UPDATE HumanResources.JobCandidate SET Resume = NULL WHERE BusinessEntityID = @SubjectBusinessEntityID;

        -- The audit trail keeps what happened and when, not who.
        IF NOT EXISTS (SELECT 1 FROM dbo.ErasedSubject WHERE BusinessEntityID = @SubjectBusinessEntityID)
            INSERT dbo.ErasedSubject (BusinessEntityID) VALUES (@SubjectBusinessEntityID);
        UPDATE dbo.WorkOrderAudit SET ActorBusinessEntityID = -1 WHERE ActorBusinessEntityID = @SubjectBusinessEntityID;
        SET @audit = @@ROWCOUNT;

        DECLARE @detail nvarchar(400) = CONCAT(N'email ', @email, N', phone ', @phone, N', password ', @password, N', address ', @address, N', audit rows ', @audit);
        INSERT dbo.ErasureLog (RequestId, SubjectBusinessEntityID, Detail) VALUES (@RequestId, @SubjectBusinessEntityID, @detail);

        -- Tell the new side. It uses the same outbox and relay as everything else; the origin marks it as the answer
        -- to a command from the new side.
        INSERT sync.Outbox (AggregateType, AggregateId, Version, Origin, TraceParent, CommandId, Payload)
        VALUES (N'Erasure', @SubjectBusinessEntityID, 1, N'new', CONVERT(nvarchar(60), SESSION_CONTEXT(N'traceparent')),
                CONVERT(nvarchar(100), SESSION_CONTEXT(N'sync.command_id')),
                (SELECT @RequestId AS requestId, @SubjectBusinessEntityID AS subject, @detail AS detail FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO

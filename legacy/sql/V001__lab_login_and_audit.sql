-- V001: the lab's SQL login and the audit table (AdventureWorks has no person column on work orders).
-- sqlcmd variable: LabAppPassword
SET NOCOUNT ON;
GO
USE master;
IF SUSER_ID('lab_app') IS NULL
    EXEC('CREATE LOGIN lab_app WITH PASSWORD = ''$(LabAppPassword)'', CHECK_POLICY = OFF');
GO
USE AdventureWorks2022;
IF USER_ID('lab_app') IS NULL CREATE USER lab_app FOR LOGIN lab_app;
ALTER ROLE db_datareader ADD MEMBER lab_app;
GO
CREATE TABLE dbo.WorkOrderAudit (
    AuditID               bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_WorkOrderAudit PRIMARY KEY,
    WorkOrderID           int           NOT NULL,
    Action                nvarchar(30)  NOT NULL,
    ActorBusinessEntityID int           NOT NULL,
    Detail                nvarchar(400) NULL,
    CreatedAt             datetime2(3)  NOT NULL CONSTRAINT DF_WorkOrderAudit_CreatedAt DEFAULT sysutcdatetime()
);
CREATE INDEX IX_WorkOrderAudit_WorkOrderID ON dbo.WorkOrderAudit (WorkOrderID);
GO

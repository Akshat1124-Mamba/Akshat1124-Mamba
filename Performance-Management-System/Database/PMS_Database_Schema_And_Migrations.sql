-- ============================================================
-- SAFE MIGRATION: Add audit columns only if they don't exist
-- Run this on existing database
-- ============================================================

-- ============================================================
-- TABLE 1: PAS_BasePercentageMaster
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='CreatedBy')
    ALTER TABLE PAS_BasePercentageMaster ADD CreatedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='CreatedAt')
    ALTER TABLE PAS_BasePercentageMaster ADD CreatedAt DATETIME NULL DEFAULT GETDATE();

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='ModifiedBy')
    ALTER TABLE PAS_BasePercentageMaster ADD ModifiedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='ModifiedOn')
    ALTER TABLE PAS_BasePercentageMaster ADD ModifiedOn DATETIME NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='IsDeleted')
    ALTER TABLE PAS_BasePercentageMaster ADD IsDeleted BIT NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='DeletedBy')
    ALTER TABLE PAS_BasePercentageMaster ADD DeletedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_BasePercentageMaster' AND COLUMN_NAME='DeletedOn')
    ALTER TABLE PAS_BasePercentageMaster ADD DeletedOn DATETIME NULL;
GO

-- ============================================================
-- TABLE 2: PAS_RatingMasterVersion
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='CreatedBy')
    ALTER TABLE PAS_RatingMasterVersion ADD CreatedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='CreatedAt')
    ALTER TABLE PAS_RatingMasterVersion ADD CreatedAt DATETIME NULL DEFAULT GETDATE();

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='ModifiedBy')
    ALTER TABLE PAS_RatingMasterVersion ADD ModifiedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='ModifiedOn')
    ALTER TABLE PAS_RatingMasterVersion ADD ModifiedOn DATETIME NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='IsDeleted')
    ALTER TABLE PAS_RatingMasterVersion ADD IsDeleted BIT NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='DeletedBy')
    ALTER TABLE PAS_RatingMasterVersion ADD DeletedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingMasterVersion' AND COLUMN_NAME='DeletedOn')
    ALTER TABLE PAS_RatingMasterVersion ADD DeletedOn DATETIME NULL;
GO

-- ============================================================
-- TABLE 3: PAS_RatingHikeDetail
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='CreatedBy')
    ALTER TABLE PAS_RatingHikeDetail ADD CreatedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='CreatedAt')
    ALTER TABLE PAS_RatingHikeDetail ADD CreatedAt DATETIME NULL DEFAULT GETDATE();

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='ModifiedBy')
    ALTER TABLE PAS_RatingHikeDetail ADD ModifiedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='ModifiedOn')
    ALTER TABLE PAS_RatingHikeDetail ADD ModifiedOn DATETIME NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='IsDeleted')
    ALTER TABLE PAS_RatingHikeDetail ADD IsDeleted BIT NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='DeletedBy')
    ALTER TABLE PAS_RatingHikeDetail ADD DeletedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_RatingHikeDetail' AND COLUMN_NAME='DeletedOn')
    ALTER TABLE PAS_RatingHikeDetail ADD DeletedOn DATETIME NULL;
GO

-- ============================================================
-- TABLE 4: PAS_YearlyPATEntry
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='CreatedBy')
    ALTER TABLE PAS_YearlyPATEntry ADD CreatedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='CreatedAt')
    ALTER TABLE PAS_YearlyPATEntry ADD CreatedAt DATETIME NULL DEFAULT GETDATE();

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='ModifiedBy')
    ALTER TABLE PAS_YearlyPATEntry ADD ModifiedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='ModifiedOn')
    ALTER TABLE PAS_YearlyPATEntry ADD ModifiedOn DATETIME NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='IsDeleted')
    ALTER TABLE PAS_YearlyPATEntry ADD IsDeleted BIT NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='DeletedBy')
    ALTER TABLE PAS_YearlyPATEntry ADD DeletedBy NVARCHAR(450) NULL;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='DeletedOn')
    ALTER TABLE PAS_YearlyPATEntry ADD DeletedOn DATETIME NULL;

-- Also add PATRevenuePercentDirect if missing (new column from controller)
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME='PAS_YearlyPATEntry' AND COLUMN_NAME='PATRevenuePercentDirect')
    ALTER TABLE PAS_YearlyPATEntry ADD PATRevenuePercentDirect DECIMAL(10,4) NULL;
GO

-- ============================================================
-- TABLE 5: PAS_ChangeLog (CREATE only if not exists)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES 
    WHERE TABLE_NAME='PAS_ChangeLog')
BEGIN
    CREATE TABLE PAS_ChangeLog (
        LogId       INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TableName   NVARCHAR(100)     NOT NULL,
        RecordId    INT               NOT NULL,
        ActionType  NVARCHAR(20)      NOT NULL,  -- INSERT / UPDATE / DELETE
        FieldName   NVARCHAR(200)     NULL,
        OldValue    NVARCHAR(1000)    NULL,
        NewValue    NVARCHAR(1000)    NULL,
        ChangedBy   NVARCHAR(450)     NOT NULL,
        ChangedOn   DATETIME          NOT NULL DEFAULT GETDATE(),
        SessionJson NVARCHAR(MAX)     NULL,
        IPAddress   NVARCHAR(50)      NULL,
        Remarks     NVARCHAR(500)     NULL
    );

    CREATE NONCLUSTERED INDEX IX_PAS_ChangeLog_Table_Record
        ON PAS_ChangeLog (TableName, RecordId);
END
GO

-- ============================================================
-- SAFE INDEXES (only if not exists)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes 
    WHERE name='IX_PAS_Base_SortOrder' 
    AND object_id = OBJECT_ID('PAS_BasePercentageMaster'))
    CREATE NONCLUSTERED INDEX IX_PAS_Base_SortOrder
        ON PAS_BasePercentageMaster (SortOrder);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes 
    WHERE name='IX_PAS_RMV_IsActive' 
    AND object_id = OBJECT_ID('PAS_RatingMasterVersion'))
    CREATE NONCLUSTERED INDEX IX_PAS_RMV_IsActive
        ON PAS_RatingMasterVersion (IsActive);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes 
    WHERE name='IX_PAS_RMV_EffDate' 
    AND object_id = OBJECT_ID('PAS_RatingMasterVersion'))
    CREATE NONCLUSTERED INDEX IX_PAS_RMV_EffDate
        ON PAS_RatingMasterVersion (EffectiveDate DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes 
    WHERE name='IX_PAS_Detail_VersionSort' 
    AND object_id = OBJECT_ID('PAS_RatingHikeDetail'))
    CREATE NONCLUSTERED INDEX IX_PAS_Detail_VersionSort
        ON PAS_RatingHikeDetail (VersionId, SortOrder);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes 
    WHERE name='IX_PAS_Yearly_FY' 
    AND object_id = OBJECT_ID('PAS_YearlyPATEntry'))
    CREATE NONCLUSTERED INDEX IX_PAS_Yearly_FY
        ON PAS_YearlyPATEntry (FinancialYear DESC);
GO

-- ============================================================
-- VERIFY: Check all audit columns were added
-- ============================================================
SELECT 
    TABLE_NAME,
    COLUMN_NAME,
    DATA_TYPE,
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME IN (
    'PAS_BasePercentageMaster',
    'PAS_RatingMasterVersion',
    'PAS_RatingHikeDetail',
    'PAS_YearlyPATEntry'
)
AND COLUMN_NAME IN (
    'CreatedBy','CreatedAt',
    'ModifiedBy','ModifiedOn',
    'IsDeleted','DeletedBy','DeletedOn',
    'PATRevenuePercentDirect'
)
ORDER BY TABLE_NAME, COLUMN_NAME;
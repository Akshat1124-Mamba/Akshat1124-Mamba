// ====================================================================
// FILE: Services/PmsBaSyncService.cs
// ====================================================================
using Microsoft.Data.SqlClient;
using System.Data;
using vgl.Models;

namespace vgl.Services
{
    public interface IPmsBaSyncService
    {
        Task<PmsBaSyncResult> SyncReviewersAsync(EmpKpiMappingModel mapping, string createdByUserId);
    }

    public class PmsBaSyncResult
    {
        public int AuditId { get; set; }
        public bool WasCreated { get; set; }   // true = INSERT, false = UPDATE
        public bool Success { get; set; }
        public string ErrorMsg { get; set; }
    }

    public class PmsBaSyncService : IPmsBaSyncService
    {
        private readonly string _conn;
        private readonly string _orgConn;
        private readonly ILogger<PmsBaSyncService> _log;

        public PmsBaSyncService(IConfiguration config, ILogger<PmsBaSyncService> log)
        {
            _conn = config.GetConnectionString("DbConStr");
            _orgConn = config.GetConnectionString("OrgConStr");
            _log = log;
        }

        public async Task<PmsBaSyncResult> SyncReviewersAsync(
            EmpKpiMappingModel mapping, string createdByUserId)
        {
            if (mapping == null || mapping.EmpId <= 0)
                return new PmsBaSyncResult { Success = false, ErrorMsg = "Invalid mapping." };

            try
            {
                var reviewerIds = await ResolveReviewerIdsAsync(mapping);

                using var con = new SqlConnection(_conn);
                await con.OpenAsync();

                bool tableExists = await TableExistsAsync(con, "Behaviour_Audit_Main");
                if (!tableExists)
                {
                    _log.LogWarning("[PMS-BA Sync] Behaviour_Audit_Main not found for EmpId={EmpId}", mapping.EmpId);
                    await WriteSyncLogAsync(con, mapping.MappingId, mapping.EmpId, mapping.EmpName,
                        0, "ERROR", null,
                        "Behaviour_Audit_Main table not found", null, null,
                        createdByUserId, false, "Table missing");
                    return new PmsBaSyncResult { Success = false, ErrorMsg = "BA table not found." };
                }
                // ── Cycle-scoped BA lookup ──────────────────────────────────────────
                int auditId = 0;
                bool wasCreated = false;

                // Step 1: check if this specific PMS mapping already has a linked BA record
                if (mapping.MappingId > 0)
                {
                    using var baChk = new SqlCommand(
                        "SELECT ISNULL(BehaviourAuditId, 0) FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    baChk.Parameters.AddWithValue("@Mid", mapping.MappingId);
                    var bv = await baChk.ExecuteScalarAsync();
                    if (bv != null && bv != DBNull.Value)
                        auditId = Convert.ToInt32(bv);
                }

                // Step 2: if no direct link, try to find a BA record scoped
                //         to this cycle's date window only — never reuse old cycles
                if (auditId == 0 && mapping.CycleFromDate.HasValue && mapping.CycleToDate.HasValue)
                {
                    // Search by OrgMaster EmpID first, then fallback to EmpCode
                    // so both old and new records are found correctly
                    using var cycleChk = new SqlCommand(@"
        SELECT TOP 1 AuditID
        FROM   Behaviour_Audit_Main
        WHERE  (EmpID   = @EmpId OR LTRIM(RTRIM(EmpCode)) = @EmpCode)
          AND  CreatedOn >= @From
          AND  CreatedOn <= DATEADD(day, 1, @To)
        ORDER BY AuditID DESC", con);
                    cycleChk.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                    cycleChk.Parameters.AddWithValue("@EmpCode", mapping.EmpCode?.Trim() ?? "");
                    cycleChk.Parameters.AddWithValue("@From", mapping.CycleFromDate.Value);
                    cycleChk.Parameters.AddWithValue("@To", mapping.CycleToDate.Value);
                    var cv = await cycleChk.ExecuteScalarAsync();
                    if (cv != null && cv != DBNull.Value)
                        auditId = Convert.ToInt32(cv);
                }

                // Step 3: if still 0 and no cycle dates available,
                //         check if any existing audit is NOT completed —
                //         only reuse it if it's still in progress (same cycle implied)
                //         NEVER reuse a completed audit — always create fresh for new cycle
                if (auditId == 0 && (!mapping.CycleFromDate.HasValue || !mapping.CycleToDate.HasValue))
                {
                    using var pendingChk = new SqlCommand(@"
        SELECT TOP 1 AuditID
        FROM   Behaviour_Audit_Main
        WHERE  (EmpID = @EmpId OR LTRIM(RTRIM(EmpCode)) = @EmpCode)
          AND  ISNULL(FinalStatus, '') NOT IN ('Completed', 'Closed')
          AND  ISNULL(AuditStatus, '') NOT IN ('Completed', 'Closed')
        ORDER BY AuditID DESC", con);
                    pendingChk.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                    pendingChk.Parameters.AddWithValue("@EmpCode", mapping.EmpCode?.Trim() ?? "");
                    var pv = await pendingChk.ExecuteScalarAsync();
                    if (pv != null && pv != DBNull.Value)
                        auditId = Convert.ToInt32(pv);
                }

                // Step 4: auditId still 0 → no matching record found → INSERT new one
                // ── DO NOT fall back to GetLatestAuditIdAsync here ──
                // That would pick up old completed cycles and override them.
                // ───────────────────────────────────────────────────────────────────
                if (auditId > 0)
                {
                    // Verify the found audit is not already completed before updating
                    // If it IS completed, treat as a new cycle → INSERT fresh record
                    bool existingIsCompleted = false;
                    using (var compChk = new SqlCommand(
                        "SELECT ISNULL(FinalStatus,'') FROM Behaviour_Audit_Main WHERE AuditID=@Aid", con))
                    {
                        compChk.Parameters.AddWithValue("@Aid", auditId);
                        var fs = await compChk.ExecuteScalarAsync();
                        existingIsCompleted = fs?.ToString()
                            .Equals("Completed", StringComparison.OrdinalIgnoreCase) == true;
                    }

                    // If the linked audit (from BehaviourAuditId column) is completed
                    // but there are no new cycle dates to scope — this is a NEW cycle,
                    // so clear auditId to force INSERT
                    if (existingIsCompleted && mapping.MappingId > 0)
                    {
                        // Only force new INSERT if this mapping doesn't already own this audit
                        // (i.e. BehaviourAuditId was set in a previous cycle and never cleared)
                        bool mappingOwnsThisAudit = false;
                        using (var ownChk = new SqlCommand(
                            "SELECT ISNULL(BehaviourAuditId,0) FROM EmpAppraisalMapping " +
                            "WHERE MappingId=@Mid AND IsDeleted=0", con))
                        {
                            ownChk.Parameters.AddWithValue("@Mid", mapping.MappingId);
                            var ov = await ownChk.ExecuteScalarAsync();
                            mappingOwnsThisAudit = ov != null
                                && ov != DBNull.Value
                                && Convert.ToInt32(ov) == auditId;
                        }

                        if (!mappingOwnsThisAudit)
                        {
                            _log.LogInformation(
                                "[PMS-BA Sync] AuditID={AuditId} is Completed and not owned by " +
                                "MappingId={MappingId} — forcing new INSERT for new cycle.",
                                auditId, mapping.MappingId);
                            auditId = 0; // force INSERT
                        }
                    }
                }

                if (auditId > 0)
                {
                    await UpdateAuditReviewersAsync(con, auditId, mapping, reviewerIds);
                    await WriteSyncLogAsync(con, mapping.MappingId, mapping.EmpId, mapping.EmpName,
                        auditId, "BA_UPDATED", null,
                        $"Reviewers synced from PMS MappingId={mapping.MappingId}",
                        null,
                        $"RM={mapping.RmName}, HOD={mapping.HodName}, UH={mapping.UnitHeadName}, " +
                        $"MC={mapping.ModCommitteeNames}, JMD={mapping.JmdName}",
                        createdByUserId, true, null);
                    _log.LogInformation(
                        "[PMS-BA Sync] Updated AuditID={AuditId} for EmpId={EmpId}",
                        auditId, mapping.EmpId);
                }
                else
                {
                    auditId = await InsertAuditRecordAsync(con, mapping, reviewerIds, createdByUserId);
                    wasCreated = true;
                    await WriteSyncLogAsync(con, mapping.MappingId, mapping.EmpId, mapping.EmpName,
                        auditId, "BA_CREATED", null,
                        $"New BA record created from PMS MappingId={mapping.MappingId}",
                        null,
                        $"RM={mapping.RmName}, HOD={mapping.HodName}, UH={mapping.UnitHeadName}, " +
                        $"MC={mapping.ModCommitteeNames}, JMD={mapping.JmdName}",
                        createdByUserId, true, null);
                    _log.LogInformation(
                        "[PMS-BA Sync] Created AuditID={AuditId} for EmpId={EmpId}",
                        auditId, mapping.EmpId);
                }

                // ── Write BehaviourAuditId back to EmpAppraisalMapping ──────
                await SaveAuditIdToMappingAsync(con, mapping.MappingId, auditId);
                await WriteSyncLogAsync(con, mapping.MappingId, mapping.EmpId, mapping.EmpName,
                    auditId, "PMS_UPDATED", null,
                    $"BehaviourAuditId={auditId} saved to EmpAppraisalMapping",
                    null, auditId.ToString(),
                    createdByUserId, true, null);

                return new PmsBaSyncResult
                {
                    AuditId = auditId,
                    WasCreated = wasCreated,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[PMS-BA Sync] Error for EmpId={EmpId}",
                    mapping.EmpId);

                return new PmsBaSyncResult
                {
                    Success = false,
                    ErrorMsg = ex.Message
                };
            }
        }
        // ============================================================
        // Write BehaviourAuditId back to EmpAppraisalMapping
        // ============================================================
        private static async Task SaveAuditIdToMappingAsync(
            SqlConnection con, int mappingId, int auditId)
        {
            if (mappingId <= 0 || auditId <= 0) return;
            try
            {
                // Ensure column exists (added by migration script — defensive check)
                using var ensure = new SqlCommand(@"
                    IF NOT EXISTS (
                        SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='BehaviourAuditId'
                    )
                        ALTER TABLE EmpAppraisalMapping ADD BehaviourAuditId INT NULL;", con);
                await ensure.ExecuteNonQueryAsync();

                using var cmd = new SqlCommand(@"
                    UPDATE EmpAppraisalMapping
                    SET    BehaviourAuditId = @AuditId,
                           UpdatedAt        = GETDATE()
                    WHERE  MappingId = @MappingId
                      AND  IsDeleted = 0", con);
                cmd.Parameters.AddWithValue("@AuditId", auditId);
                cmd.Parameters.AddWithValue("@MappingId", mappingId);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* non-fatal */ }
        }

        // ============================================================
        // Write a row to PmsBaSyncLog
        // ============================================================
        public static async Task WriteSyncLogAsync(
    SqlConnection con,
    int mappingId, int empId, string empName,
    int auditId, string actionType, string stage,
    string details, string oldValue, string newValue,
    string triggeredBy, bool isSuccess, string errorMessage)
        {
            try
            {
                // Ensure table exists with all columns
                using (var ensure = new SqlCommand(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PmsBaSyncLog')
            BEGIN
                CREATE TABLE PmsBaSyncLog (
                    LogId        INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    MappingId    INT           NOT NULL,
                    EmpId        INT           NOT NULL,
                    EmpName      NVARCHAR(200) NULL,
                    AuditId      INT           NULL,
                    ActionType   NVARCHAR(50)  NOT NULL,
                    Stage        NVARCHAR(50)  NULL,
                    Details      NVARCHAR(MAX) NULL,
                    OldValue     NVARCHAR(500) NULL,
                    NewValue     NVARCHAR(500) NULL,
                    TriggeredBy  NVARCHAR(450) NULL,
                    TriggeredOn  DATETIME      NOT NULL DEFAULT GETDATE(),
                    IsSuccess    BIT           NOT NULL DEFAULT 1,
                    ErrorMessage NVARCHAR(MAX) NULL,
                    CreatedBy    NVARCHAR(450) NULL,
                    CreatedOn    DATETIME      NOT NULL DEFAULT GETDATE(),
                    ModifiedBy   NVARCHAR(450) NULL,
                    ModifiedOn   DATETIME      NULL,
                    IsDeleted    BIT           NOT NULL DEFAULT 0,
                    DeletedBy    NVARCHAR(450) NULL,
                    DeletedOn    DATETIME      NULL
                );
                CREATE NONCLUSTERED INDEX IX_PmsBaSyncLog_MappingId ON PmsBaSyncLog(MappingId);
                CREATE NONCLUSTERED INDEX IX_PmsBaSyncLog_AuditId   ON PmsBaSyncLog(AuditId);
                CREATE NONCLUSTERED INDEX IX_PmsBaSyncLog_EmpId     ON PmsBaSyncLog(EmpId);
            END", con))
                    await ensure.ExecuteNonQueryAsync();

                using var ins = new SqlCommand(@"
            INSERT INTO PmsBaSyncLog
                (MappingId, EmpId, EmpName, AuditId, ActionType, Stage,
                 Details, OldValue, NewValue, TriggeredBy, TriggeredOn,
                 IsSuccess, ErrorMessage,
                 CreatedBy, CreatedOn,
                 ModifiedBy, ModifiedOn,
                 IsDeleted, DeletedBy, DeletedOn)
            VALUES
                (@MappingId, @EmpId, @EmpName, @AuditId, @ActionType, @Stage,
                 @Details, @OldValue, @NewValue, @TriggeredBy, GETDATE(),
                 @IsSuccess, @ErrorMessage,
                 @CreatedBy, GETDATE(),
                 NULL, NULL,
                 0, NULL, NULL)", con);

                ins.Parameters.AddWithValue("@MappingId", mappingId);
                ins.Parameters.AddWithValue("@EmpId", empId);
                ins.Parameters.AddWithValue("@EmpName", (object?)empName ?? DBNull.Value);
                ins.Parameters.AddWithValue("@AuditId", auditId > 0 ? (object)auditId : DBNull.Value);
                ins.Parameters.AddWithValue("@ActionType", actionType);
                ins.Parameters.AddWithValue("@Stage", (object?)stage ?? DBNull.Value);
                ins.Parameters.AddWithValue("@Details", (object?)details ?? DBNull.Value);
                ins.Parameters.AddWithValue("@OldValue", (object?)oldValue ?? DBNull.Value);
                ins.Parameters.AddWithValue("@NewValue", (object?)newValue ?? DBNull.Value);
                ins.Parameters.AddWithValue("@TriggeredBy", (object?)triggeredBy ?? DBNull.Value);
                ins.Parameters.AddWithValue("@IsSuccess", isSuccess);
                ins.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
                ins.Parameters.AddWithValue("@CreatedBy", (object?)triggeredBy ?? DBNull.Value); // same as TriggeredBy on insert

                await ins.ExecuteNonQueryAsync();
            }
            catch { /* never throw from logging */ }
        }
        // ============================================================
        // RESOLVE reviewer Emp_IDs from OrgMaster by full name
        // ============================================================
        private async Task<ReviewerIds> ResolveReviewerIdsAsync(EmpKpiMappingModel mapping)
        {
            var ids = new ReviewerIds();
            var namesToResolve = new[]
            {
                (mapping.RmName,            (Action<string>)(v => ids.RmId  = v), (Action<string>)(v => ids.RmName  = v)),
                (mapping.HodName,           (Action<string>)(v => ids.HodId = v), (Action<string>)(v => ids.HodName = v)),
                (mapping.UnitHeadName,      (Action<string>)(v => ids.UhId  = v), (Action<string>)(v => ids.UhName  = v)),
                (mapping.ModCommitteeNames, (Action<string>)(v => ids.McId  = v), (Action<string>)(v => ids.McName  = v)),
                (mapping.JmdName,           (Action<string>)(v => ids.JmdId = v), (Action<string>)(v => ids.JmdName = v)),
            };

            var distinctNames = namesToResolve
                .Where(x => !string.IsNullOrWhiteSpace(x.Item1))
                .Select(x => x.Item1.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!distinctNames.Any()) return ids;

            var nameToEmpId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var con = new SqlConnection(_orgConn);
                await con.OpenAsync();
                var paramNames = distinctNames.Select((_, i) => "@n" + i).ToList();
                string inClause = string.Join(",", paramNames);
                using var cmd = new SqlCommand($@"
   SELECT
    e.Emp_ID,
    LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'') + ' ' + ISNULL(e.Emp_Last_Name,''))) AS Emp_Full_Name
FROM T0080_EMP_MASTER e WITH (NOLOCK)
WHERE LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'') + ' ' + ISNULL(e.Emp_Last_Name,'')))
          IN ({inClause})
  AND ISNULL(e.Emp_Left, 'N') = 'N'
  AND e.Cmp_ID = 2", con);
                for (int i = 0; i < distinctNames.Count; i++)
                    cmd.Parameters.Add(paramNames[i], SqlDbType.NVarChar).Value = distinctNames[i];
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    string n = rdr["Emp_Full_Name"]?.ToString()?.Trim() ?? "";
                    string id = Convert.ToInt32(rdr["Emp_ID"]).ToString();
                    if (!string.IsNullOrWhiteSpace(n)) nameToEmpId[n] = id;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PMS-BA Sync] OrgMaster lookup failed");
            }

            foreach (var (name, setId, setName) in namesToResolve)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                string trimmed = name.Trim();
                setId(nameToEmpId.TryGetValue(trimmed, out var id) ? id : "");
                setName(trimmed);
            }
            return ids;
        }

        // ============================================================
        // UPDATE existing Behaviour_Audit_Main
        // ============================================================
        private async Task UpdateAuditReviewersAsync(
            SqlConnection con, int auditId,
            EmpKpiMappingModel mapping, ReviewerIds ids)
        {
            bool hasMc = await ColExistsAsync(con, "Behaviour_Audit_Main", "MC_ID");
            bool hasJmd = await ColExistsAsync(con, "Behaviour_Audit_Main", "JMD_ID");
            bool hasUh = await ColExistsAsync(con, "Behaviour_Audit_Main", "UH_ID");

            var sets = new System.Text.StringBuilder(
                "RM_ID=@RmId, RM_Name=@RmName, HOD_ID=@HodId, HOD_Name=@HodName, HOD_Required=@HodReq, UH_Required=@UhReq");
            if (hasUh) sets.Append(", UH_ID=@UhId, UH_Name=@UhName");
            if (hasMc) sets.Append(", MC_ID=@McId, MC_Name=@McName, MC_Required=@McReq");
            if (hasJmd) sets.Append(", JMD_ID=@JmdId, JMD_Name=@JmdName, JMD_Required=@JmdReq");

            using var cmd = new SqlCommand(
                $"UPDATE Behaviour_Audit_Main SET {sets} WHERE AuditID=@AuditId", con);
            AddReviewerParams(cmd, ids, auditId);
            await cmd.ExecuteNonQueryAsync();
        }

        // ============================================================
        // INSERT new Behaviour_Audit_Main
        // ============================================================
        private async Task<int> InsertAuditRecordAsync(
     SqlConnection con, EmpKpiMappingModel mapping,
     ReviewerIds ids, string createdByUserId)
        {
            bool hasMc = await ColExistsAsync(con, "Behaviour_Audit_Main", "MC_ID");
            bool hasJmd = await ColExistsAsync(con, "Behaviour_Audit_Main", "JMD_ID");
            bool hasUh = await ColExistsAsync(con, "Behaviour_Audit_Main", "UH_ID");

            var cols = new System.Text.StringBuilder(
                "EmpID,EmpCode,EmpName,DesigName,DeptName,RM_ID,RM_Name,HOD_ID,HOD_Name," +
                "HOD_Required,UH_Required,CurrentStage,AuditStatus,FinalStatus," +
                "DeptID,UnitID,CreatedBy,CreatedOn");
            var vals = new System.Text.StringBuilder(
                "@EmpId,@EmpCode,@EmpName,@DesigName,@DeptName,@RmId,@RmName,@HodId,@HodName," +
                "@HodReq,@UhReq,'RM','Pending at RM','In Process'," +
                "@DeptId,@UnitId,@CreatedBy,GETDATE()");

            if (hasUh) { cols.Append(",UH_ID,UH_Name"); vals.Append(",@UhId,@UhName"); }
            if (hasMc) { cols.Append(",MC_ID,MC_Name,MC_Required"); vals.Append(",@McId,@McName,@McReq"); }
            if (hasJmd) { cols.Append(",JMD_ID,JMD_Name,JMD_Required"); vals.Append(",@JmdId,@JmdName,@JmdReq"); }

            // ── Resolve the correct OrgMaster EmpID using EmpCode ──────────────
            // The BA module uses Emp_ID from T0080_EMP_MASTER (OrgMaster DB),
            // but PMS stores Emp_Id from UserDetails (vgl_test DB).
            // These are DIFFERENT IDs — we must look up the OrgMaster one by EmpCode.
            int baEmpId = mapping.EmpId; // safe fallback if OrgMaster lookup fails
            try
            {
                if (!string.IsNullOrWhiteSpace(mapping.EmpCode))
                {
                    using var orgCon = new SqlConnection(_orgConn);
                    await orgCon.OpenAsync();

                    // Try lookup by EmpCode first (most reliable)
                    using var orgCmd = new SqlCommand(@"
                SELECT TOP 1 Emp_ID
                FROM   T0080_EMP_MASTER WITH (NOLOCK)
                WHERE  LTRIM(RTRIM(Emp_Code)) = LTRIM(RTRIM(@EmpCode))
                  AND  Emp_Left = 'N'
                  AND  Cmp_ID  = 2", orgCon);
                    orgCmd.Parameters.AddWithValue("@EmpCode", mapping.EmpCode.Trim());

                    var orgResult = await orgCmd.ExecuteScalarAsync();
                    if (orgResult != null && orgResult != DBNull.Value)
                    {
                        baEmpId = Convert.ToInt32(orgResult);
                        _log.LogInformation(
                            "[PMS-BA Sync] Resolved OrgMaster EmpID={BaEmpId} for EmpCode={EmpCode}",
                            baEmpId, mapping.EmpCode);
                    }
                    else
                    {
                        // Fallback: try by full name if EmpCode didn't match
                        using var nameCmd = new SqlCommand(@"
                    SELECT TOP 1 Emp_ID
                    FROM   T0080_EMP_MASTER WITH (NOLOCK)
                    WHERE  LTRIM(RTRIM(Emp_Full_Name)) = LTRIM(RTRIM(@EmpName))
                      AND  Emp_Left = 'N'
                      AND  Cmp_ID  = 2", orgCon);
                        nameCmd.Parameters.AddWithValue("@EmpName", mapping.EmpName?.Trim() ?? "");

                        var nameResult = await nameCmd.ExecuteScalarAsync();
                        if (nameResult != null && nameResult != DBNull.Value)
                        {
                            baEmpId = Convert.ToInt32(nameResult);
                            _log.LogInformation(
                                "[PMS-BA Sync] Resolved OrgMaster EmpID={BaEmpId} by name for EmpCode={EmpCode}",
                                baEmpId, mapping.EmpCode);
                        }
                        else
                        {
                            _log.LogWarning(
                                "[PMS-BA Sync] Could not resolve OrgMaster EmpID for EmpCode={EmpCode}, EmpName={EmpName}. " +
                                "Falling back to PMS EmpId={EmpId}.",
                                mapping.EmpCode, mapping.EmpName, mapping.EmpId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[PMS-BA Sync] OrgMaster EmpID lookup failed for EmpCode={EmpCode}. " +
                    "Falling back to PMS EmpId={EmpId}.",
                    mapping.EmpCode, mapping.EmpId);
            }
            // ────────────────────────────────────────────────────────────────────

            using var cmd = new SqlCommand(
                $"INSERT INTO Behaviour_Audit_Main ({cols}) OUTPUT INSERTED.AuditID VALUES ({vals})", con);

            AddReviewerParams(cmd, ids, 0);

            cmd.Parameters.AddWithValue("@EmpId", baEmpId);                   // ← OrgMaster ID
            cmd.Parameters.AddWithValue("@EmpCode", mapping.EmpCode ?? "");
            cmd.Parameters.AddWithValue("@EmpName", mapping.EmpName ?? "");
            cmd.Parameters.AddWithValue("@DesigName", mapping.Designation ?? "");
            cmd.Parameters.AddWithValue("@DeptName", mapping.Department ?? "");
            cmd.Parameters.AddWithValue("@DeptId", 0);
            cmd.Parameters.AddWithValue("@UnitId", 0);
            cmd.Parameters.AddWithValue("@CreatedBy", createdByUserId ?? "System");

            var result = await cmd.ExecuteScalarAsync();
            return (result != null && result != DBNull.Value) ? Convert.ToInt32(result) : 0;
        }
        // ============================================================
        // SHARED parameter builder
        // ============================================================
        private static void AddReviewerParams(SqlCommand cmd, ReviewerIds ids, int auditId)
        {
            if (auditId > 0) cmd.Parameters.AddWithValue("@AuditId", auditId);
            cmd.Parameters.AddWithValue("@RmId", ids.RmId ?? "");
            cmd.Parameters.AddWithValue("@RmName", ids.RmName ?? "");
            cmd.Parameters.AddWithValue("@HodId", ids.HodId ?? "");
            cmd.Parameters.AddWithValue("@HodName", ids.HodName ?? "");
            cmd.Parameters.AddWithValue("@UhId", ids.UhId ?? "");
            cmd.Parameters.AddWithValue("@UhName", ids.UhName ?? "");
            cmd.Parameters.AddWithValue("@McId", ids.McId ?? "");
            cmd.Parameters.AddWithValue("@McName", ids.McName ?? "");
            cmd.Parameters.AddWithValue("@JmdId", ids.JmdId ?? "");
            cmd.Parameters.AddWithValue("@JmdName", ids.JmdName ?? "");
            cmd.Parameters.AddWithValue("@HodReq", !string.IsNullOrWhiteSpace(ids.HodId));
            cmd.Parameters.AddWithValue("@UhReq", !string.IsNullOrWhiteSpace(ids.UhId));
            cmd.Parameters.AddWithValue("@McReq", !string.IsNullOrWhiteSpace(ids.McId));
            cmd.Parameters.AddWithValue("@JmdReq", !string.IsNullOrWhiteSpace(ids.JmdId));
        }

        // ============================================================
        // HELPERS
        // ============================================================
        private static async Task<int> GetLatestAuditIdAsync(SqlConnection con, int empId)
        {
            // Primary: search by EmpID (works after OrgMaster EmpID fix)
            using var cmd = new SqlCommand(
                "SELECT TOP 1 AuditID FROM Behaviour_Audit_Main " +
                "WHERE EmpID=@EmpId ORDER BY AuditID DESC", con);
            cmd.Parameters.AddWithValue("@EmpId", empId);
            var v = await cmd.ExecuteScalarAsync();
            return (v != null && v != DBNull.Value) ? Convert.ToInt32(v) : 0;
        }

        private static async Task<bool> TableExistsAsync(SqlConnection con, string tableName)
        {
            using var cmd = new SqlCommand(
                "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME=@T", con);
            cmd.Parameters.AddWithValue("@T", tableName);
            return (int)(await cmd.ExecuteScalarAsync()) > 0;
        }

        private static async Task<bool> ColExistsAsync(SqlConnection con, string table, string col)
        {
            using var cmd = new SqlCommand(
                "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@T AND COLUMN_NAME=@C", con);
            cmd.Parameters.AddWithValue("@T", table);
            cmd.Parameters.AddWithValue("@C", col);
            return (int)(await cmd.ExecuteScalarAsync()) > 0;
        }

        private class ReviewerIds
        {
            public string RmId { get; set; } = ""; public string RmName { get; set; } = "";
            public string HodId { get; set; } = ""; public string HodName { get; set; } = "";
            public string UhId { get; set; } = ""; public string UhName { get; set; } = "";
            public string McId { get; set; } = ""; public string McName { get; set; } = "";
            public string JmdId { get; set; } = ""; public string JmdName { get; set; } = "";
        }
    }
}
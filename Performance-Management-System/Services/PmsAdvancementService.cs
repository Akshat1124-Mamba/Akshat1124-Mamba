using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using vgl.Models; // wherever EmpKpiMappingModel lives
namespace vgl.Services
{
    public interface IPmsAdvancementService
    {
        Task TryAdvancePmsAfterBaAsync(int auditId, string completedStage);
    }

    public class PmsAdvancementService : IPmsAdvancementService
    {
        private readonly string _conn;
        private readonly IEmailService _email;
        private readonly ILogger<PmsAdvancementService> _log;
        private readonly IAppBaseUrlProvider _baseUrlProvider;
        private string BaseUrl => _baseUrlProvider.GetBaseUrl();

        public PmsAdvancementService(
            IConfiguration config,
            IEmailService email,
            ILogger<PmsAdvancementService> log,
            IAppBaseUrlProvider baseUrlProvider)   // ← add
        {
            _conn = config.GetConnectionString("DbConStr");
            _email = email;
            _log = log;
            _baseUrlProvider = baseUrlProvider;
            // delete: _baseUrl = config["AppSettings:BaseUrl"] ?? "";
        }
        public async Task TryAdvancePmsAfterBaAsync(int auditId, string completedStage)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // Find mappings linked to this auditId that are blocked
                // Status = EmployeeFilled means Employee submitted PMS but 
                //          RM hasn't been notified yet (BA was blocking)
                // Status = RMReview means RM submitted PMS but 
                //          HOD hasn't been notified yet (BA was blocking)
                var stuck = new List<(int MappingId, string Status)>();
                // REPLACE the stuck mappings query:
                // REPLACE the query:
                using (var cmd = new SqlCommand(@"
    SELECT MappingId, [Status]
    FROM   EmpAppraisalMapping
    WHERE  BehaviourAuditId = @AuditId
      AND  IsDeleted = 0
      AND  [Status] IN ('EmployeeFilled', 'RMReview', 'HODReview')", con))
                // EmployeeFilled: RM submitted BA, waiting to notify HOD
                // RMReview:       HOD submitted BA, waiting to notify UH  
                // HODReview:      UH has no BA, but included for completeness
                // HODReview added — when HOD submits BA (audit completes), UH should be notified
                {
                    cmd.Parameters.AddWithValue("@AuditId", auditId);
                    using var dr = cmd.ExecuteReader();
                    while (dr.Read())
                        stuck.Add((Convert.ToInt32(dr["MappingId"]), dr["Status"].ToString()));
                }

                if (!stuck.Any())
                {
                    _log.LogInformation(
                        "[PmsAdvancement] No stuck PMS mappings found for AuditId={AuditId}",
                        auditId);
                    return;
                }

                foreach (var (mappingId, status) in stuck)
                {
                    await TryAdvanceOneMappingAsync(mappingId, status, completedStage);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[PmsAdvancement] Failed for AuditId={AuditId} Stage={Stage}",
                    auditId, completedStage);
            }
        }

        private async Task TryAdvanceOneMappingAsync(
       int mappingId, string currentStatus, string completedStage)
        {
            try
            {
                // Re-fetch mapping fresh from DB to get current HodName/UnitHeadName
                var mapping = GetMappingByIdInternal(_conn, mappingId);
                if (mapping == null)
                {
                    _log.LogWarning("[PmsAdvancement] Mapping not found MappingId={Id}", mappingId);
                    return;
                }

                _log.LogInformation(
                    "[PmsAdvancement] TryAdvanceOneMappingAsync: MappingId={Id} Status={Status} " +
                    "completedStage={Stage} RmName='{Rm}' HodName='{Hod}' UhName='{Uh}'",
                    mappingId, currentStatus, completedStage,
                    mapping.RmName, mapping.HodName, mapping.UnitHeadName);

                // ── Semantic table ──────────────────────────────────────────────
                // Status          | roleToCheck | justCompletedRole | Who gets notified
                // EmployeeFilled  | RM          | Employee          | RM (via Employee→RM chain)
                // RMReview        | RM          | RM                | HOD (via RM→HOD chain)  ← KEY FIX
                // HODReview       | HOD         | HOD               | UH (via HOD→UH chain)
                // ───────────────────────────────────────────────────────────────
                // "EmployeeFilled" = Employee submitted PMS, waiting for RM to do both PMS+BA
                //   → when RM submits BA, verify RM's BA, then notify HOD? NO.
                //   → Actually: when BA sync creates audit on cycle assign, status=Draft/EmployeeFilled
                //   → Employee submits PMS → status=EmployeeFilled
                //   → RM submits BA → TryAdvance fires → status=EmployeeFilled still
                //     (RM hasn't submitted PMS yet) → don't advance, let normal PMS flow handle it
                //
                // "RMReview" = RM submitted PMS already. BA was blocking HOD notification.
                //   → RM now submits BA → TryAdvance fires → verify RM's BA → notify HOD ✅

                string roleToCheck;
                string justCompletedRole;

                switch (currentStatus)
                {
                    case "EmployeeFilled":
                        // RM submitted BA but hasn't submitted PMS yet (or vice versa)
                        // Only advance if RM has ALSO submitted PMS (i.e. status would be RMReview)
                        // Since status is still EmployeeFilled, RM hasn't submitted PMS.
                        // The normal PMS submit flow handles notifying RM.
                        // TryAdvance shouldn't fire here unless RM submitted BA before PMS.
                        // In that case, when RM later submits PMS, SubmitAppraisalFill will
                        // check IsBaCompletedForAudit → true → SendToNextReviewer → HOD notified.
                        _log.LogInformation(
                            "[PmsAdvancement] Status=EmployeeFilled — RM hasn't submitted PMS yet. " +
                            "Skipping TryAdvance for MappingId={Id}", mappingId);
                        return;

                    case "RMReview":
                        // RM submitted PMS (status=RMReview). RM's BA just completed.
                        // Verify RM's BA is done, then notify HOD.
                        roleToCheck = "RM";
                        justCompletedRole = "RM"; // triggers ("RM", HodName, ...) → HOD email
                        break;

                    case "HODReview":
                        // HOD submitted PMS (status=HODReview). HOD's BA just completed.
                        // Verify HOD's BA is done, then notify UH.
                        roleToCheck = "HOD";
                        justCompletedRole = "HOD"; // triggers ("HOD", UhName, ...) → UH email
                        break;

                    default:
                        _log.LogInformation(
                            "[PmsAdvancement] Status={Status} not handled. Skipping MappingId={Id}",
                            currentStatus, mappingId);
                        return;
                }

                // Verify BA is complete for roleToCheck
                int baAuditId = GetBehaviouralAuditIdStatic(_conn, mapping.EmpId, mappingId);
                if (baAuditId == 0)
                {
                    _log.LogWarning(
                        "[PmsAdvancement] No BA audit found for MappingId={Id} EmpId={EmpId}",
                        mappingId, mapping.EmpId);
                    return;
                }

                bool baComplete;
                using (var baCon = new SqlConnection(_conn))
                {
                    baCon.Open();
                    baComplete = IsBaCompletedForAuditStatic(
                        baCon, baAuditId, roleToCheck, mappingId, _log);
                }

                _log.LogInformation(
                    "[PmsAdvancement] BA check: MappingId={Id} roleToCheck={Role} " +
                    "baAuditId={AuditId} baComplete={Complete}",
                    mappingId, roleToCheck, baAuditId, baComplete);

                if (!baComplete)
                {
                    _log.LogInformation(
                        "[PmsAdvancement] BA not complete for role={Role} — not advancing MappingId={Id}",
                        roleToCheck, mappingId);
                    return;
                }

                await SendToNextReviewerInternal(mapping, mappingId, justCompletedRole);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[PmsAdvancement] TryAdvanceOneMappingAsync failed for MappingId={Id}", mappingId);
            }
        }
        private async Task SendToNextReviewerInternal(
      EmpKpiMappingModel mapping, int mappingId, string justCompletedRole)
        {
            int auditId = GetBehaviouralAuditIdStatic(_conn, mapping.EmpId, mappingId);

            _log.LogInformation(
                "[PmsAdvancement] SendToNextReviewerInternal: mappingId={Id} justCompletedRole={JCR} " +
                "RmName='{Rm}' HodName='{Hod}' UhName='{Uh}' auditId={AuditId}",
                mappingId, justCompletedRole,
                mapping.RmName, mapping.HodName, mapping.UnitHeadName, auditId);

            // NEW
            var fullChain = new[]
            {
    ("Employee", mapping.RmName,
        $"{BaseUrl}/Pms/RmAppraisal?mappingId={mappingId}", "RM"),
    ("RM",       mapping.HodName,
        $"{BaseUrl}/Pms/HodAppraisal?mappingId={mappingId}", "HOD"),
    ("HOD",      mapping.UnitHeadName,
        $"{BaseUrl}/Pms/UnitHeadAppraisal?mappingId={mappingId}", "UnitHead"),
};
            foreach (var (triggerRole, name, pmsUrl, action) in fullChain)
            {
                if (!string.Equals(justCompletedRole, triggerRole, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(name))
                {
                    _log.LogWarning(
                        "[PmsAdvancement] Reviewer name is EMPTY for action={Action} " +
                        "mappingId={MappingId} — cannot send email.",
                        action, mappingId);
                    return;
                }

                // ── Guard: don't re-send if already sent recently ───────────────
                if (WasEmailAlreadySent(mappingId, action))
                {
                    _log.LogInformation(
                        "[PmsAdvancement] EMAIL_SENT already logged for mappingId={Id} stage={Stage} " +
                        "within last 4 hours — skipping duplicate send.",
                        mappingId, action);
                    return;
                }

                string baUrl = GetBaUrlForRoleStatic(BaseUrl, auditId, action);

                _log.LogInformation(
                    "[PmsAdvancement] Sending {Action} email to '{Name}' for mappingId={MappingId} " +
                    "pmsUrl={PmsUrl} baUrl={BaUrl}",
                    action, name, mappingId, pmsUrl, baUrl);

                switch (action)
                {
                    case "RM":
                        await _email.SendRmReviewNotificationAsync(mapping, pmsUrl, baUrl, BaseUrl);
                        break;
                    case "HOD":
                        await _email.SendHodReviewNotificationAsync(mapping, pmsUrl, baUrl, BaseUrl);
                        break;
                    case "UnitHead":
                        await _email.SendUnitHeadReviewNotificationAsync(mapping, pmsUrl, BaseUrl);
                        break;
                }

                // ── LOG the send ─────────────────────────────────────────────────
                try
                {
                    using var logCon = new SqlConnection(_conn);
                    await logCon.OpenAsync();
                    await PmsBaSyncService.WriteSyncLogAsync(
                        logCon,
                        mappingId: mappingId,
                        empId: mapping.EmpId,
                        empName: mapping.EmpName,
                        auditId: auditId,
                        actionType: "EMAIL_SENT",
                        stage: action,
                        details: $"[PmsAdvancement] {action} review email sent. PmsUrl={pmsUrl}",
                        oldValue: null,
                        newValue: null,
                        triggeredBy: "PmsAdvancementService",
                        isSuccess: true,
                        errorMessage: null);
                }
                catch { /* never throw from logging */ }

                _log.LogInformation(
                    "[PmsAdvancement] Email sent to {Action} ({Name}) for MappingId={MappingId}",
                    action, name, mappingId);
                return;
            }

            _log.LogWarning(
                "[PmsAdvancement] No chain entry matched justCompletedRole={JCR} for MappingId={MappingId}",
                justCompletedRole, mappingId);
        }

        // ── ADD this helper inside PmsAdvancementService ──────────────────────────
        private bool WasEmailAlreadySent(int mappingId, string stage)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PmsBaSyncLog'", con))
                    if ((int)chk.ExecuteScalar() == 0) return false;

                using var cmd = new SqlCommand(@"
            SELECT COUNT(1)
            FROM   PmsBaSyncLog
            WHERE  MappingId   = @MappingId
              AND  ActionType  = 'EMAIL_SENT'
              AND  Stage       = @Stage
              AND  TriggeredOn >= DATEADD(HOUR, -4, GETDATE())
              AND  IsDeleted   = 0", con);
                cmd.Parameters.AddWithValue("@MappingId", mappingId);
                cmd.Parameters.AddWithValue("@Stage", stage);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }
        // ── Static helpers (duplicated from PmsController to avoid coupling) ──

        private static EmpKpiMappingModel GetMappingByIdInternal(string connStr, int mappingId)
        {
            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();

                // Step 1: Load header from app DB
                int empId = 0;
                string userId = "", rmName = "", hodName = "", uhName = "",
                       modNames = "", jmdName = "", appraisalCycle = "", status = "";
                int appraisalYear = 0;
                DateTime? cycleFrom = null, cycleTo = null;

                using (var cmd = new SqlCommand(@"
            SELECT m.MappingId, m.EmpId, ISNULL(m.UserId,'') AS UserId,
                   m.AppraisalYear, m.AppraisalCycle,
                   m.CycleFromDate, m.CycleToDate, m.[Status],
                   ISNULL(m.RmName,'')            AS RmName,
                   ISNULL(m.HodName,'')           AS HodName,
                   ISNULL(m.UnitHeadName,'')      AS UnitHeadName,
                   ISNULL(m.ModCommitteeNames,'') AS ModCommitteeNames,
                   ISNULL(m.JmdName,'')           AS JmdName
            FROM   EmpAppraisalMapping m
            WHERE  m.MappingId = @Id AND m.IsDeleted = 0", con))
                {
                    cmd.Parameters.AddWithValue("@Id", mappingId);
                    using var dr = cmd.ExecuteReader();
                    if (!dr.Read()) return null;

                    empId = Convert.ToInt32(dr["EmpId"]);
                    userId = dr["UserId"].ToString();
                    appraisalYear = Convert.ToInt32(dr["AppraisalYear"]);
                    appraisalCycle = dr["AppraisalCycle"].ToString();
                    cycleFrom = dr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleFromDate"]);
                    cycleTo = dr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleToDate"]);
                    status = dr["Status"].ToString();
                    rmName = dr["RmName"].ToString();
                    hodName = dr["HodName"].ToString();
                    uhName = dr["UnitHeadName"].ToString();
                    modNames = dr["ModCommitteeNames"].ToString();
                    jmdName = dr["JmdName"].ToString();
                }

                // Step 2: Enrich with HRMS employee details
                string empCode = "", empName = "", email = "", dept = "", desig = "";
                if (empId > 0)
                {
                    try
                    {
                        // Use same connStr if single server, or swap to HrmsDbConStr
                        using var hrmsCon = new SqlConnection(connStr);
                        hrmsCon.Open();
                        using var hrmsCmd = new SqlCommand(@"
                    SELECT
                        ISNULL(e.Emp_code,'')  AS EMPCode,
                        ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
                        ISNULL(e.Work_Email,'') AS Email,
                        ISNULL(dept.Dept_Name,'')  AS DepartmentName,
                        ISNULL(des.Desig_Name,'')  AS DesignationName
                    FROM   [Orange_Hrms].[dbo].[T0080_EMP_MASTER] e
                    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER]  dept ON dept.Dept_Id = e.Dept_ID
                    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des  ON des.Desig_ID = e.Desig_Id
                    WHERE  e.Emp_ID = @EmpId", hrmsCon);
                        hrmsCmd.Parameters.AddWithValue("@EmpId", empId);
                        using var hrDr = hrmsCmd.ExecuteReader();
                        if (hrDr.Read())
                        {
                            empCode = hrDr["EMPCode"].ToString();
                            empName = hrDr["FullName"].ToString();
                            email = hrDr["Email"].ToString();
                            dept = hrDr["DepartmentName"].ToString();
                            desig = hrDr["DesignationName"].ToString();
                        }
                    }
                    catch { /* HRMS enrichment failed — proceed with blanks */ }
                }

                return new EmpKpiMappingModel
                {
                    MappingId = mappingId,
                    EmpId = empId,
                    UserId = userId,
                    EmpCode = empCode,
                    EmpName = empName,
                    Email = email,
                    Department = dept,
                    Designation = desig,
                    AppraisalYear = appraisalYear,
                    AppraisalCycle = appraisalCycle,
                    CycleFromDate = cycleFrom,
                    CycleToDate = cycleTo,
                    Status = status,
                    RmName = rmName,
                    HodName = hodName,
                    UnitHeadName = uhName,
                    ModCommitteeNames = modNames,
                    JmdName = jmdName,
                };
            }
            catch { return null; }
        }
        private static int GetBehaviouralAuditIdStatic(
            string connStr, int empId, int mappingId = 0)
        {
            if (empId <= 0) return 0;
            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();

                if (mappingId > 0)
                {
                    using var mc = new SqlCommand(
                        "SELECT ISNULL(BehaviourAuditId,0) FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    mc.Parameters.AddWithValue("@Mid", mappingId);
                    var linked = mc.ExecuteScalar();
                    if (linked != null && linked != DBNull.Value)
                    {
                        int id = Convert.ToInt32(linked);
                        if (id > 0) return id;
                    }
                }

                using var fallback = new SqlCommand(
                    "SELECT TOP 1 AuditID FROM Behaviour_Audit_Main " +
                    "WHERE EmpID=@EmpId ORDER BY AuditID DESC", con);
                fallback.Parameters.AddWithValue("@EmpId", empId);
                var fv = fallback.ExecuteScalar();
                return (fv != null && fv != DBNull.Value) ? Convert.ToInt32(fv) : 0;
            }
            catch { return 0; }
        }

        private static bool IsBaCompletedForAuditStatic(
            SqlConnection con, int auditId, string reviewerRole,
            int mappingId, ILogger log)
        {
            if (reviewerRole != "RM" && reviewerRole != "HOD") return true;

            string submitDateCol = reviewerRole == "RM" ? "RM_SubmitDate" : "HOD_SubmitDate";
            string totalScoreCol = reviewerRole == "RM" ? "RM_TotalScore" : "HOD_TotalScore";

            try
            {
                string auditStatus = "", finalStatus = "";
                bool hasSubmitCol = false, hasTotalCol = false;
                object submitDateVal = DBNull.Value;
                decimal totalScore = 0m;

                using (var cc = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_NAME='Behaviour_Audit_Main' AND COLUMN_NAME=@col", con))
                {
                    cc.Parameters.AddWithValue("@col", submitDateCol);
                    hasSubmitCol = (int)cc.ExecuteScalar() > 0;
                }
                using (var cc2 = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_NAME='Behaviour_Audit_Main' AND COLUMN_NAME=@col", con))
                {
                    cc2.Parameters.AddWithValue("@col", totalScoreCol);
                    hasTotalCol = (int)cc2.ExecuteScalar() > 0;
                }

                var selectParts = new List<string>
                {
                    "ISNULL(FinalStatus,'')  AS FinalStatus",
                    "ISNULL(AuditStatus,'') AS AuditStatus"
                };
                if (hasSubmitCol) selectParts.Add(submitDateCol);
                if (hasTotalCol) selectParts.Add($"ISNULL({totalScoreCol},0) AS TotalScore");

                using (var mainCmd = new SqlCommand(
                    $"SELECT {string.Join(", ", selectParts)} " +
                    "FROM Behaviour_Audit_Main WHERE AuditID=@Aid", con))
                {
                    mainCmd.Parameters.AddWithValue("@Aid", auditId);
                    using var dr = mainCmd.ExecuteReader();
                    if (!dr.Read()) return true;

                    finalStatus = dr["FinalStatus"].ToString();
                    auditStatus = dr["AuditStatus"].ToString();
                    if (hasSubmitCol) submitDateVal = dr[submitDateCol];
                    if (hasTotalCol) totalScore = Convert.ToDecimal(dr["TotalScore"]);
                }

                if (string.Equals(finalStatus, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(auditStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                    return true;

                bool isPast = reviewerRole switch
                {
                    "RM" => auditStatus is "Pending at HOD" or "Pending at UH"
                             || auditStatus.StartsWith("Completed",
                                 StringComparison.OrdinalIgnoreCase),
                    "HOD" => auditStatus is "Pending at UH"
                             || auditStatus.StartsWith("Completed",
                                 StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                if (isPast) return true;
                if (hasSubmitCol && submitDateVal != DBNull.Value) return true;
                if (hasTotalCol && totalScore > 0) return true;

                return false;
            }
            catch { return true; }
        }

        private static string GetBaUrlForRoleStatic(
            string baseUrl, int auditId, string role)
        {
            if (auditId <= 0) return string.Empty;
            string path = role switch
            {
                "RM" => $"/BehaviouralAudit/RMForm/{auditId}",
                "HOD" => $"/BehaviouralAudit/HODForm/{auditId}",
                _ => string.Empty
            };
            return string.IsNullOrEmpty(path) ? string.Empty : $"{baseUrl}{path}";
        }
    }
}
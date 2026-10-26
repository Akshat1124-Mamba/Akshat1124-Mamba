using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using vgl.Models;

namespace vgl.Services
{
    public interface IPmsReminderService
    {
        Task CheckAndSendPendingReminders();
    }

    public class PmsReminderService : IPmsReminderService
    {
        private readonly string _conn;
        private readonly IEmailService _email;
        private readonly ILogger<PmsReminderService> _log;
        private readonly IAppBaseUrlProvider _baseUrlProvider;
        private string BaseUrl => _baseUrlProvider.GetBaseUrl();

        // ── Grace period: do NOT remind until 1 hour after submission ────────
        private const int GraceMinutes = 60;

        // ── Anti-spam: once a reminder is sent, wait this long before re-sending
        private const int ReminderCooldownMinutes = 60;

        private static readonly Dictionary<string, string> StatusToCurrentReviewer = new()
        {
            { "EmployeeFilled",  "RM"           },
            { "RMReview",        "HOD"          },
            { "HODReview",       "UnitHead"     },
            { "UnitHeadReview",  "ModCommittee" },
        };

        public PmsReminderService(
             IConfiguration config,
             IEmailService email,
             ILogger<PmsReminderService> log,
             IAppBaseUrlProvider baseUrlProvider)   // ← add
        {
            _conn = config.GetConnectionString("DbConStr");
            _email = email;
            _log = log;
            _baseUrlProvider = baseUrlProvider;
            // delete: _baseUrl = (config["AppSettings:BaseUrl"] ??).TrimEnd('/');
        }

        // ====================================================
        // ENTRY POINT — called by Hangfire at 10:30 and 15:30
        // ====================================================
        public async Task CheckAndSendPendingReminders()
        {
            _log.LogInformation("[PMS Reminder] Starting check at {Time}", DateTime.Now);

            // Auto-create timestamp columns if they don't exist yet
            EnsureTimestampColumns();

            var pending = GetPendingReviewerMappings();
            _log.LogInformation("[PMS Reminder] {Count} mappings eligible", pending.Count);

            foreach (var item in pending)
            {
                try { await ProcessOneMapping(item); }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "[PMS Reminder] Failed for MappingId={Id}", item.MappingId);
                }
            }
        }

        // ====================================================
        // AUTO-CREATE TIMESTAMP COLUMNS ON FIRST RUN
        // ====================================================
        private void EnsureTimestampColumns()
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(@"
                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping'
                          AND COLUMN_NAME='RMSubmittedPMSAt')
                        ALTER TABLE EmpAppraisalMapping ADD RMSubmittedPMSAt DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping'
                          AND COLUMN_NAME='HODSubmittedPMSAt')
                        ALTER TABLE EmpAppraisalMapping ADD HODSubmittedPMSAt DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping'
                          AND COLUMN_NAME='RMSubmittedBAAt')
                        ALTER TABLE EmpAppraisalMapping ADD RMSubmittedBAAt DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping'
                          AND COLUMN_NAME='HODSubmittedBAAt')
                        ALTER TABLE EmpAppraisalMapping ADD HODSubmittedBAAt DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping'
                          AND COLUMN_NAME='RMLastReminderSentAt')
                        ALTER TABLE EmpAppraisalMapping ADD RMLastReminderSentAt DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_NAME='EmpAppraisalMapping'
                          AND COLUMN_NAME='HODLastReminderSentAt')
                        ALTER TABLE EmpAppraisalMapping ADD HODLastReminderSentAt DATETIME NULL;", con);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PMS Reminder] EnsureTimestampColumns failed (non-fatal)");
            }
        }

        // ====================================================
        // PROCESS ONE MAPPING — full guard chain
        // ====================================================
        private async Task ProcessOneMapping(PendingReminderItem item)
        {
            string currentRole = item.ReviewerRole;
            string currentName = item.ReviewerName;
            if (string.IsNullOrWhiteSpace(currentName)) return;

            bool hasBaGate = currentRole == "RM" || currentRole == "HOD";

            // ── Guard 1: Suppressed by recent BA submission ───────────────────
            if (IsReminderSuppressed(item.MappingId))
            {
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} suppressed — skipping.", item.MappingId);
                return;
            }

            // ── Guard 2: 1-HOUR GRACE WINDOW ─────────────────────────────────
            // Do not remind until at least 1 hour has passed since the FIRST
            // form (PMS or BA) was submitted by this reviewer.
            // If neither has been submitted yet, check how long the mapping
            // has been sitting at this status.
            var (graceBlocked, graceReason) = IsWithinGracePeriod(item, currentRole);
            if (graceBlocked)
            {
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} within 1hr grace — {Reason} — skipping.",
                    item.MappingId, graceReason);
                return;
            }

            // ── Guard 3: Anti-spam cooldown ───────────────────────────────────
            // Don't re-send if we already sent a reminder within the cooldown window
            if (WasReminderSentRecently(item.MappingId, currentRole))
            {
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} reminder sent recently for {Role} — skipping.",
                    item.MappingId, currentRole);
                return;
            }

            // ── Guard 4: Advancement email already sent for next stage ────────
            string nextStage = item.CurrentStatus switch
            {
                "EmployeeFilled" => "RM",
                "RMReview" => "HOD",
                "HODReview" => "UnitHead",
                _ => null
            };
            if (nextStage != null && WasAdvancementEmailSentRecently(item.MappingId, nextStage))
            {
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} advancement email sent for stage={Stage} — skipping.",
                    item.MappingId, nextStage);
                return;
            }

            // ── Guard 5: Sync BA flags before checks ──────────────────────────
            if (hasBaGate)
                await EnsureBaFlagsBeforeReminderCheck(item.MappingId, item.EmpId, currentRole);

            // ── Guard 6: PMS already submitted? ──────────────────────────────
            bool pmsSubmitted = IsPmsSubmittedByReviewer(item.MappingId, currentRole);
            if (pmsSubmitted)
            {
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} — {Role} PMS already submitted — skipping.",
                    item.MappingId, currentRole);
                return;
            }

            // ── Guard 7: BA submitted (via cross-table JOIN) ───────────────────
            if (hasBaGate && IsBaSubmittedViaJoin(item.MappingId, item.EmpId, currentRole))
            {
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} — {Role} BA confirmed via JOIN — " +
                    "checking if PMS advancement is stuck.",
                    item.MappingId, currentRole);

                // BA done but PMS stuck → try to unstick
                await TryUnstickViaAdvancement(item);
                return;
            }

            // ── Determine what is actually missing ────────────────────────────
            bool pmsDone = IsPmsRowsFilledByReviewer(item.MappingId, currentRole);
            bool baDone = hasBaGate && IsBaSubmittedForRole(item.MappingId, item.EmpId, currentRole);

            if (pmsDone && baDone)
            {
                // Both done but stuck — try advancement
                _log.LogInformation(
                    "[PMS Reminder] MappingId={Id} — {Role} both PMS+BA done but stuck — " +
                    "triggering advancement.",
                    item.MappingId, currentRole);
                await TryUnstickViaAdvancement(item);
                return;
            }

            string missingForms;
            if (hasBaGate)
            {
                if (!pmsDone && !baDone)
                    missingForms = "PMS Appraisal and Behavioural Audit";
                else if (!pmsDone)
                    missingForms = "PMS Appraisal";
                else
                    missingForms = "Behavioural Audit";
            }
            else
            {
                if (pmsDone) return; // nothing to remind about
                missingForms = "PMS Appraisal";
            }

            _log.LogInformation(
                "[PMS Reminder] MappingId={Id} — reminding {Role} ({Name}), Missing: {Missing}",
                item.MappingId, currentRole, currentName, missingForms);

            await SendReminderEmail(item, missingForms, currentRole, currentName);

            // ── Stamp last-reminder-sent timestamp ────────────────────────────
            StampReminderSent(item.MappingId, currentRole);
        }

        // ====================================================
        // GUARD 2: 1-HOUR GRACE WINDOW (core new logic)
        //
        // Returns (true, reason) if we should NOT send yet.
        //
        // Logic:
        //   - If RMSubmittedPMSAt or RMSubmittedBAAt is set and < 60 min ago → wait
        //   - If neither is set, check how long mapping has been at this status
        //     using UpdatedAt. If < 60 min → wait (reviewer may be actively filling)
        // ====================================================
        private (bool blocked, string reason) IsWithinGracePeriod(
            PendingReminderItem item, string role)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                string pmsFlagAt = role == "RM" ? "RMSubmittedPMSAt" : "HODSubmittedPMSAt";
                string baFlagAt = role == "RM" ? "RMSubmittedBAAt" : "HODSubmittedBAAt";

                bool hasPmsAt = ColExists(con, "EmpAppraisalMapping", pmsFlagAt);
                bool hasBaAt = ColExists(con, "EmpAppraisalMapping", baFlagAt);

                DateTime? pmsSubmittedAt = null;
                DateTime? baSubmittedAt = null;

                if (hasPmsAt || hasBaAt)
                {
                    var selectParts = new List<string>();
                    if (hasPmsAt) selectParts.Add(pmsFlagAt);
                    if (hasBaAt) selectParts.Add(baFlagAt);

                    using var cmd = new SqlCommand(
                        $"SELECT {string.Join(", ", selectParts)} " +
                        "FROM EmpAppraisalMapping WHERE MappingId=@Mid AND IsDeleted=0", con);
                    cmd.Parameters.AddWithValue("@Mid", item.MappingId);
                    using var dr = cmd.ExecuteReader();
                    if (dr.Read())
                    {
                        if (hasPmsAt && dr[pmsFlagAt] != DBNull.Value)
                            pmsSubmittedAt = Convert.ToDateTime(dr[pmsFlagAt]);
                        if (hasBaAt && dr[baFlagAt] != DBNull.Value)
                            baSubmittedAt = Convert.ToDateTime(dr[baFlagAt]);
                    }
                }

                // If PMS was submitted within grace period → wait
                if (pmsSubmittedAt.HasValue)
                {
                    double minsSincePms = (DateTime.Now - pmsSubmittedAt.Value).TotalMinutes;
                    if (minsSincePms < GraceMinutes)
                        return (true,
                            $"{pmsFlagAt}={pmsSubmittedAt:HH:mm} ({minsSincePms:F0} min ago, " +
                            $"grace={GraceMinutes} min)");
                }

                // If BA was submitted within grace period → wait
                if (baSubmittedAt.HasValue)
                {
                    double minsSinceBa = (DateTime.Now - baSubmittedAt.Value).TotalMinutes;
                    if (minsSinceBa < GraceMinutes)
                        return (true,
                            $"{baFlagAt}={baSubmittedAt:HH:mm} ({minsSinceBa:F0} min ago, " +
                            $"grace={GraceMinutes} min)");
                }

                // If NEITHER timestamp is set yet, the reviewer hasn't started at all.
                // Check how long the mapping has been sitting at this status via UpdatedAt.
                // If it just moved to this status (< 60 min), give time to start filling.
                if (!pmsSubmittedAt.HasValue && !baSubmittedAt.HasValue)
                {
                    bool hasUpdatedAt = ColExists(con, "EmpAppraisalMapping", "UpdatedAt");
                    string dateCol = hasUpdatedAt
                        ? "ISNULL(UpdatedAt, CreatedAt)" : "CreatedAt";

                    using var statusCmd = new SqlCommand(
                        $"SELECT {dateCol} FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    statusCmd.Parameters.AddWithValue("@Mid", item.MappingId);
                    var statusVal = statusCmd.ExecuteScalar();

                    if (statusVal != null && statusVal != DBNull.Value)
                    {
                        double minsSinceUpdate =
                            (DateTime.Now - Convert.ToDateTime(statusVal)).TotalMinutes;

                        if (minsSinceUpdate < GraceMinutes)
                            return (true,
                                $"No submission timestamps yet — status last changed " +
                                $"{minsSinceUpdate:F0} min ago, grace={GraceMinutes} min");
                    }
                }

                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[PMS Reminder] IsWithinGracePeriod failed for MappingId={Id} — " +
                    "allowing reminder", item.MappingId);
                return (false, string.Empty);
            }
        }

        // ====================================================
        // ANTI-SPAM: Was reminder sent recently?
        // ====================================================
        private bool WasReminderSentRecently(int mappingId, string role)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                string lastSentCol = role == "RM" ? "RMLastReminderSentAt"
                                   : role == "HOD" ? "HODLastReminderSentAt"
                                   : null;

                if (lastSentCol == null) return false;
                if (!ColExists(con, "EmpAppraisalMapping", lastSentCol)) return false;

                using var cmd = new SqlCommand(
                    $"SELECT {lastSentCol} FROM EmpAppraisalMapping " +
                    "WHERE MappingId=@Mid AND IsDeleted=0", con);
                cmd.Parameters.AddWithValue("@Mid", mappingId);
                var val = cmd.ExecuteScalar();

                if (val == null || val == DBNull.Value) return false;

                double minsSinceLast = (DateTime.Now - Convert.ToDateTime(val)).TotalMinutes;
                bool recent = minsSinceLast < ReminderCooldownMinutes;

                if (recent)
                    _log.LogInformation(
                        "[PMS Reminder] MappingId={Id} {Col}={Val} — last reminder " +
                        "{Mins:F0} min ago (cooldown={Cooldown} min)",
                        mappingId, lastSentCol, val, minsSinceLast, ReminderCooldownMinutes);

                return recent;
            }
            catch { return false; }
        }

        // ====================================================
        // STAMP: Record when reminder was sent
        // ====================================================
        private void StampReminderSent(int mappingId, string role)
        {
            try
            {
                string col = role == "RM" ? "RMLastReminderSentAt"
                           : role == "HOD" ? "HODLastReminderSentAt"
                           : null;
                if (col == null) return;

                using var con = new SqlConnection(_conn);
                con.Open();
                if (!ColExists(con, "EmpAppraisalMapping", col)) return;

                using var cmd = new SqlCommand(
                    $"UPDATE EmpAppraisalMapping SET {col}=GETDATE(), UpdatedAt=GETDATE() " +
                    "WHERE MappingId=@Mid AND IsDeleted=0", con);
                cmd.Parameters.AddWithValue("@Mid", mappingId);
                cmd.ExecuteNonQuery();
            }
            catch { /* non-fatal */ }
        }

        // ====================================================
        // GUARD 1: Is reminder suppressed?
        // ====================================================
        private bool IsReminderSuppressed(int mappingId)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                if (!ColExists(con, "EmpAppraisalMapping", "PmsReminderSuppressedUntil"))
                    return false;

                using var cmd = new SqlCommand(
                    "SELECT PmsReminderSuppressedUntil FROM EmpAppraisalMapping " +
                    "WHERE MappingId=@Mid AND IsDeleted=0", con);
                cmd.Parameters.AddWithValue("@Mid", mappingId);
                var val = cmd.ExecuteScalar();
                if (val == null || val == DBNull.Value) return false;

                bool suppressed = DateTime.Now < Convert.ToDateTime(val);
                if (suppressed)
                    _log.LogInformation(
                        "[PMS Reminder] MappingId={Id} suppressed until {Until}",
                        mappingId, val);
                return suppressed;
            }
            catch { return false; }
        }

        // ====================================================
        // GUARD 7: Cross-table JOIN on EmpId + CycleDates
        // ====================================================
        private bool IsBaSubmittedViaJoin(int mappingId, int empId, string role)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                if (!TableExists(con, "Behaviour_Audit_Main")) return false;

                string submitDateCol = role == "RM" ? "RM_SubmitDate" : "HOD_SubmitDate";
                if (!ColExists(con, "Behaviour_Audit_Main", submitDateCol)) return false;
                if (!ColExists(con, "EmpAppraisalMapping", "CycleFromDate")) return false;

                // Strategy 1: EmpId + cycle date window JOIN
                using (var joinCmd = new SqlCommand($@"
                    SELECT COUNT(1)
                    FROM   Behaviour_Audit_Main  ba
                    INNER JOIN EmpAppraisalMapping pms
                           ON  ba.EmpID       = pms.EmpId
                           AND ba.CreatedOn  >= pms.CycleFromDate
                           AND ba.CreatedOn  <= DATEADD(day, 1, pms.CycleToDate)
                    WHERE  pms.MappingId      = @MappingId
                      AND  pms.IsDeleted      = 0
                      AND  ba.{submitDateCol} IS NOT NULL", con))
                {
                    joinCmd.Parameters.AddWithValue("@MappingId", mappingId);
                    if ((int)joinCmd.ExecuteScalar() > 0)
                    {
                        BackfillBaFlag(con, mappingId, role);
                        BackfillBehaviourAuditIdFromJoin(con, mappingId);
                        return true;
                    }
                }

                // Strategy 2: Direct MappingId link on BA table
                if (ColExists(con, "Behaviour_Audit_Main", "MappingId"))
                {
                    using var cmd = new SqlCommand($@"
                        SELECT COUNT(1)
                        FROM Behaviour_Audit_Main
                        WHERE MappingId = @MappingId
                          AND {submitDateCol} IS NOT NULL", con);
                    cmd.Parameters.AddWithValue("@MappingId", mappingId);
                    if ((int)cmd.ExecuteScalar() > 0)
                    {
                        BackfillBaFlag(con, mappingId, role);
                        return true;
                    }
                }

                // Strategy 3: BehaviourAuditId JOIN
                if (ColExists(con, "EmpAppraisalMapping", "BehaviourAuditId"))
                {
                    using var baIdCmd = new SqlCommand($@"
                        SELECT COUNT(1)
                        FROM   EmpAppraisalMapping pms
                        INNER JOIN Behaviour_Audit_Main ba
                               ON ba.AuditID = pms.BehaviourAuditId
                        WHERE  pms.MappingId  = @MappingId
                          AND  pms.IsDeleted  = 0
                          AND  ba.{submitDateCol} IS NOT NULL", con);
                    baIdCmd.Parameters.AddWithValue("@MappingId", mappingId);
                    if ((int)baIdCmd.ExecuteScalar() > 0)
                    {
                        BackfillBaFlag(con, mappingId, role);
                        return true;
                    }
                }

                // Strategy 4: Broad EmpId (only if no cycle dates)
                bool hasCycleDates = false;
                using (var cycleChk = new SqlCommand(
                    "SELECT COUNT(1) FROM EmpAppraisalMapping " +
                    "WHERE MappingId=@Mid AND IsDeleted=0 " +
                    "AND CycleFromDate IS NOT NULL AND CycleToDate IS NOT NULL", con))
                {
                    cycleChk.Parameters.AddWithValue("@Mid", mappingId);
                    hasCycleDates = (int)cycleChk.ExecuteScalar() > 0;
                }

                if (!hasCycleDates && empId > 0)
                {
                    using var broadCmd = new SqlCommand($@"
                        SELECT COUNT(1)
                        FROM   Behaviour_Audit_Main
                        WHERE  EmpID          = @EmpId
                          AND  {submitDateCol} IS NOT NULL", con);
                    broadCmd.Parameters.AddWithValue("@EmpId", empId);
                    if ((int)broadCmd.ExecuteScalar() > 0)
                    {
                        BackfillBaFlag(con, mappingId, role);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[PMS Reminder] IsBaSubmittedViaJoin failed for MappingId={Id}", mappingId);
                return false;
            }
        }

        // ====================================================
        // BACKFILL HELPERS
        // ====================================================
        private void BackfillBaFlag(SqlConnection con, int mappingId, string role)
        {
            string flagCol = role == "RM" ? "RMSubmittedBA" : "HODSubmittedBA";
            if (!ColExists(con, "EmpAppraisalMapping", flagCol)) return;
            try
            {
                using var bf = new SqlCommand(
                    $"UPDATE EmpAppraisalMapping SET {flagCol}=1, UpdatedAt=GETDATE() " +
                    $"WHERE MappingId=@Mid AND IsDeleted=0 AND ISNULL({flagCol},0)=0", con);
                bf.Parameters.AddWithValue("@Mid", mappingId);
                int rows = bf.ExecuteNonQuery();
                if (rows > 0)
                    _log.LogInformation(
                        "[PMS Reminder] Backfilled {Col}=1 for MappingId={Id}", flagCol, mappingId);
            }
            catch { }
        }

        private void BackfillBehaviourAuditIdFromJoin(SqlConnection con, int mappingId)
        {
            if (!ColExists(con, "EmpAppraisalMapping", "BehaviourAuditId")) return;
            if (!ColExists(con, "EmpAppraisalMapping", "CycleFromDate")) return;
            try
            {
                using var cmd = new SqlCommand(@"
                    UPDATE pms
                    SET    pms.BehaviourAuditId = ba.AuditID,
                           pms.UpdatedAt        = GETDATE()
                    FROM   EmpAppraisalMapping pms
                    INNER JOIN (
                        SELECT TOP 1 ba2.AuditID, ba2.EmpID
                        FROM   Behaviour_Audit_Main ba2
                        INNER JOIN EmpAppraisalMapping pms2
                               ON  ba2.EmpID       = pms2.EmpId
                               AND ba2.CreatedOn  >= pms2.CycleFromDate
                               AND ba2.CreatedOn  <= DATEADD(day,1,pms2.CycleToDate)
                        WHERE  pms2.MappingId = @MappingId
                          AND  pms2.IsDeleted = 0
                        ORDER BY ba2.AuditID DESC
                    ) ba ON ba.EmpID = pms.EmpId
                    WHERE  pms.MappingId                   = @MappingId
                      AND  pms.IsDeleted                   = 0
                      AND  ISNULL(pms.BehaviourAuditId, 0) = 0", con);
                cmd.Parameters.AddWithValue("@MappingId", mappingId);
                int rows = cmd.ExecuteNonQuery();
                if (rows > 0)
                    _log.LogInformation(
                        "[PMS Reminder] Backfilled BehaviourAuditId for MappingId={Id}", mappingId);
            }
            catch { }
        }

        // ====================================================
        // PMS SUBMITTED CHECK
        // ====================================================
        private bool IsPmsSubmittedByReviewer(int mappingId, string role)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // Signal 1: PMS flag column
                string flagCol = role switch
                {
                    "RM" => "RMSubmittedPMS",
                    "HOD" => "HODSubmittedPMS",
                    _ => null
                };
                if (flagCol != null && ColExists(con, "EmpAppraisalMapping", flagCol))
                {
                    using var cmd = new SqlCommand(
                        $"SELECT ISNULL({flagCol},0) FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    cmd.Parameters.AddWithValue("@Mid", mappingId);
                    var v = cmd.ExecuteScalar();
                    if (v != null && v != DBNull.Value && Convert.ToBoolean(v))
                        return true;
                }

                // Signal 2: PMS timestamp column
                string pmsFlagAt = role == "RM" ? "RMSubmittedPMSAt" : "HODSubmittedPMSAt";
                if (ColExists(con, "EmpAppraisalMapping", pmsFlagAt))
                {
                    using var cmd2 = new SqlCommand(
                        $"SELECT {pmsFlagAt} FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    cmd2.Parameters.AddWithValue("@Mid", mappingId);
                    var tsVal = cmd2.ExecuteScalar();
                    if (tsVal != null && tsVal != DBNull.Value)
                        return true;
                }

                // Signal 3: Submitted-on date column (set by stored procedure)
                string submittedOnCol = role switch
                {
                    "RM" => "RmSubmittedOn",
                    "HOD" => "HodSubmittedOn",
                    _ => null
                };
                if (submittedOnCol != null &&
                    ColExists(con, "EmpAppraisalMapping", submittedOnCol))
                {
                    using var cmd3 = new SqlCommand(
                        $"SELECT {submittedOnCol} FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    cmd3.Parameters.AddWithValue("@Mid", mappingId);
                    var dateVal = cmd3.ExecuteScalar();
                    if (dateVal != null && dateVal != DBNull.Value) return true;
                }

                // Signal 4: KPI rows have ratings filled by this role
                string ratingCol = role switch
                {
                    "RM" => "RmRating",
                    "HOD" => "HodRating",
                    _ => null
                };
                if (ratingCol != null &&
                    ColExists(con, "EmpAppraisalKpiRows", ratingCol))
                {
                    using var kpiCmd = new SqlCommand(
                        $"SELECT COUNT(1) FROM EmpAppraisalKpiRows " +
                        $"WHERE MappingId=@Mid AND IsDeleted=0 AND {ratingCol} IS NOT NULL", con);
                    kpiCmd.Parameters.AddWithValue("@Mid", mappingId);
                    if ((int)kpiCmd.ExecuteScalar() > 0) return true;
                }

                return false;
            }
            catch { return false; }
        }

        // ====================================================
        // PMS ROWS FILLED CHECK
        // ====================================================
        private bool IsPmsRowsFilledByReviewer(int mappingId, string role)
        {
            try
            {
                string ratingCol = role switch
                {
                    "RM" => "RmRating",
                    "HOD" => "HodRating",
                    "UnitHead" => "UnitHeadRating",
                    "ModCommittee" => "ModCommitteeRating",
                    _ => null
                };
                if (ratingCol == null) return false;

                using var con = new SqlConnection(_conn);
                con.Open();
                if (!ColExists(con, "EmpAppraisalKpiRows", ratingCol)) return false;

                using var cmd = new SqlCommand(
                    $"SELECT COUNT(1) FROM EmpAppraisalKpiRows " +
                    $"WHERE MappingId=@Mid AND IsDeleted=0 AND {ratingCol} IS NOT NULL", con);
                cmd.Parameters.AddWithValue("@Mid", mappingId);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }

        // ====================================================
        // ENSURE BA FLAGS ARE CURRENT
        // ====================================================
        private async Task EnsureBaFlagsBeforeReminderCheck(int mappingId, int empId, string role)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                await con.OpenAsync();

                string flagCol = role == "RM" ? "RMSubmittedBA" : "HODSubmittedBA";
                string submitDateCol = role == "RM" ? "RM_SubmitDate" : "HOD_SubmitDate";
                string baFlagAt = role == "RM" ? "RMSubmittedBAAt" : "HODSubmittedBAAt";

                bool hasFlagCol = ColExists(con, "EmpAppraisalMapping", flagCol);
                bool hasBaTable = TableExists(con, "Behaviour_Audit_Main");
                if (!hasBaTable) return;

                // Already flagged?
                if (hasFlagCol)
                {
                    using var fc = new SqlCommand(
                        $"SELECT ISNULL({flagCol},0) FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    fc.Parameters.AddWithValue("@Mid", mappingId);
                    var fv = fc.ExecuteScalar();
                    if (fv != null && fv != DBNull.Value && Convert.ToBoolean(fv)) return;
                }

                if (!ColExists(con, "Behaviour_Audit_Main", submitDateCol)) return;

                // ── Primary: EmpId + cycle dates JOIN ────────────────────────────
                if (ColExists(con, "EmpAppraisalMapping", "CycleFromDate"))
                {
                    using var joinCmd = new SqlCommand($@"
                        SELECT COUNT(1)
                        FROM   Behaviour_Audit_Main  ba
                        INNER JOIN EmpAppraisalMapping pms
                               ON  ba.EmpID       = pms.EmpId
                               AND ba.CreatedOn  >= pms.CycleFromDate
                               AND ba.CreatedOn  <= DATEADD(day, 1, pms.CycleToDate)
                        WHERE  pms.MappingId      = @Mid
                          AND  pms.IsDeleted      = 0
                          AND  ba.{submitDateCol} IS NOT NULL", con);
                    joinCmd.Parameters.AddWithValue("@Mid", mappingId);

                    if ((int)await joinCmd.ExecuteScalarAsync() > 0)
                    {
                        if (hasFlagCol)
                        {
                            using var bf = new SqlCommand(
                                $"UPDATE EmpAppraisalMapping " +
                                $"SET {flagCol}=1, UpdatedAt=GETDATE() " +
                                $"WHERE MappingId=@Mid AND IsDeleted=0 " +
                                $"AND ISNULL({flagCol},0)=0", con);
                            bf.Parameters.AddWithValue("@Mid", mappingId);
                            await bf.ExecuteNonQueryAsync();
                        }

                        // Also stamp BAAt timestamp if missing
                        if (ColExists(con, "EmpAppraisalMapping", baFlagAt))
                        {
                            using var stampAt = new SqlCommand(
                                $"UPDATE EmpAppraisalMapping " +
                                $"SET {baFlagAt}=GETDATE(), UpdatedAt=GETDATE() " +
                                $"WHERE MappingId=@Mid AND IsDeleted=0 " +
                                $"AND {baFlagAt} IS NULL", con);
                            stampAt.Parameters.AddWithValue("@Mid", mappingId);
                            await stampAt.ExecuteNonQueryAsync();
                        }

                        BackfillBehaviourAuditIdFromJoin(con, mappingId);
                        return;
                    }
                }

                // ── Secondary: BehaviourAuditId ──────────────────────────────────
                int auditId = GetBaAuditIdForMapping(con, mappingId, empId, role);
                if (auditId <= 0) return;

                using var checkCmd = new SqlCommand(
                    $"SELECT {submitDateCol} FROM Behaviour_Audit_Main WHERE AuditID=@Aid", con);
                checkCmd.Parameters.AddWithValue("@Aid", auditId);
                var sdv = checkCmd.ExecuteScalar();

                if (sdv != null && sdv != DBNull.Value)
                {
                    if (hasFlagCol)
                    {
                        using var backfill = new SqlCommand(
                            $"UPDATE EmpAppraisalMapping SET {flagCol}=1, UpdatedAt=GETDATE() " +
                            $"WHERE MappingId=@Mid AND IsDeleted=0 AND ISNULL({flagCol},0)=0", con);
                        backfill.Parameters.AddWithValue("@Mid", mappingId);
                        await backfill.ExecuteNonQueryAsync();
                    }

                    if (ColExists(con, "EmpAppraisalMapping", baFlagAt))
                    {
                        using var stampAt = new SqlCommand(
                            $"UPDATE EmpAppraisalMapping " +
                            $"SET {baFlagAt}=GETDATE(), UpdatedAt=GETDATE() " +
                            $"WHERE MappingId=@Mid AND IsDeleted=0 AND {baFlagAt} IS NULL", con);
                        stampAt.Parameters.AddWithValue("@Mid", mappingId);
                        await stampAt.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[PMS Reminder] EnsureBaFlagsBeforeReminderCheck failed for MappingId={Id}",
                    mappingId);
            }
        }

        // ====================================================
        // IS BA SUBMITTED FOR ROLE (multi-signal)
        // ====================================================
        private bool IsBaSubmittedForRole(int mappingId, int empId, string role)
        {
            if (role != "RM" && role != "HOD") return true;
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                string flagCol = role == "RM" ? "RMSubmittedBA" : "HODSubmittedBA";

                // Signal 1: flag column
                if (ColExists(con, "EmpAppraisalMapping", flagCol))
                {
                    using var fc = new SqlCommand(
                        $"SELECT ISNULL({flagCol},0) FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@MappingId AND IsDeleted=0", con);
                    fc.Parameters.AddWithValue("@MappingId", mappingId);
                    var fv = fc.ExecuteScalar();
                    if (fv != null && fv != DBNull.Value && Convert.ToBoolean(fv))
                        return true;
                }

                // Signal 2: BAAt timestamp column
                string baFlagAt = role == "RM" ? "RMSubmittedBAAt" : "HODSubmittedBAAt";
                if (ColExists(con, "EmpAppraisalMapping", baFlagAt))
                {
                    using var atCmd = new SqlCommand(
                        $"SELECT {baFlagAt} FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@MappingId AND IsDeleted=0", con);
                    atCmd.Parameters.AddWithValue("@MappingId", mappingId);
                    var atVal = atCmd.ExecuteScalar();
                    if (atVal != null && atVal != DBNull.Value) return true;
                }

                // Signal 3: cross-table JOIN
                if (IsBaSubmittedViaJoin(mappingId, empId, role)) return true;

                if (!TableExists(con, "Behaviour_Audit_Main")) return true;

                int auditId = GetBaAuditIdForMapping(con, mappingId, empId, role);
                if (auditId == 0) return true; // No BA record → don't block

                string submitDateCol = role == "RM" ? "RM_SubmitDate" : "HOD_SubmitDate";
                string totalScoreCol = role == "RM" ? "RM_TotalScore" : "HOD_TotalScore";

                bool hasSubmitCol = ColExists(con, "Behaviour_Audit_Main", submitDateCol);
                bool hasTotalCol = ColExists(con, "Behaviour_Audit_Main", totalScoreCol);

                var selectParts = new List<string>
                {
                    "ISNULL(FinalStatus,'')  AS FinalStatus",
                    "ISNULL(AuditStatus,'') AS AuditStatus"
                };
                if (hasSubmitCol) selectParts.Add(submitDateCol);
                if (hasTotalCol) selectParts.Add($"ISNULL({totalScoreCol},0) AS TotalScore");

                string finalStatus = "", auditStatus = "";
                object submitDateVal = DBNull.Value;
                decimal totalScore = 0m;

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

                bool isPastStage = role switch
                {
                    "RM" => auditStatus is "Pending at HOD" or "Pending at UH"
                                       or "Pending at MC" or "Pending at JMD"
                                       or "Completed"
                          || finalStatus == "Completed",
                    "HOD" => auditStatus is "Pending at UH" or "Pending at MC"
                                       or "Pending at JMD" or "Completed"
                          || finalStatus == "Completed",
                    _ => false
                };
                if (isPastStage) return true;

                if (hasSubmitCol && submitDateVal != DBNull.Value)
                {
                    BackfillBaFlag(con, mappingId, role);
                    return true;
                }

                if (hasTotalCol && totalScore > 0) return true;

                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[PMS Reminder] IsBaSubmittedForRole exception for MappingId={Id} — " +
                    "not blocking", mappingId);
                return true;
            }
        }

        // ====================================================
        // WAS ADVANCEMENT EMAIL SENT RECENTLY
        // ====================================================
        private bool WasAdvancementEmailSentRecently(int mappingId, string stage)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                if (!TableExists(con, "PmsBaSyncLog") || stage == null) return false;

                using var cmd = new SqlCommand(@"
                    SELECT COUNT(1) FROM PmsBaSyncLog
                    WHERE MappingId  = @MappingId
                      AND ActionType = 'EMAIL_SENT'
                      AND Stage      = @Stage
                      AND TriggeredOn >= DATEADD(HOUR,-4,GETDATE())
                      AND IsDeleted  = 0", con);
                cmd.Parameters.AddWithValue("@MappingId", mappingId);
                cmd.Parameters.AddWithValue("@Stage", stage);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }

        // ====================================================
        // TRY UNSTICK VIA ADVANCEMENT
        // ====================================================
        private async Task TryUnstickViaAdvancement(PendingReminderItem item)
        {
            try
            {
                if (WasAdvancementEmailSentRecently(item.MappingId,
                    item.CurrentStatus switch
                    {
                        "RMReview" => "HOD",
                        "HODReview" => "UnitHead",
                        _ => null
                    }))
                    return;

                string justCompletedRole = item.CurrentStatus switch
                {
                    "RMReview" => "RM",
                    "HODReview" => "HOD",
                    _ => null
                };
                if (justCompletedRole == null) return;

                int baAuditId;
                using (var con = new SqlConnection(_conn))
                {
                    con.Open();
                    baAuditId = GetBaAuditIdForMapping(con, item.MappingId, item.EmpId,
                        justCompletedRole);
                }
                if (baAuditId <= 0) return;

                var mapping = GetMappingFromDb(item.MappingId);
                if (mapping == null) return;

                string nextName = justCompletedRole switch
                {
                    "RM" => mapping.HodName,
                    "HOD" => mapping.UnitHeadName,
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(nextName)) return;

                // NEW
                string pmsUrl = justCompletedRole switch
                {
                    "RM" => $"{BaseUrl}/Pms/HodAppraisal?mappingId={item.MappingId}",
                    "HOD" => $"{BaseUrl}/Pms/UnitHeadAppraisal?mappingId={item.MappingId}",
                    _ => $"{BaseUrl}/Pms/Index"
                };
                string baUrl = justCompletedRole == "RM"
                    ? $"{BaseUrl}/BehaviouralAudit/HODForm/{baAuditId}"
                    : string.Empty;

                switch (justCompletedRole)
                {
                    case "RM":
                        await _email.SendHodReviewNotificationAsync(mapping, pmsUrl, baUrl, BaseUrl);
                        break;
                    case "HOD":
                        await _email.SendUnitHeadReviewNotificationAsync(mapping, pmsUrl, BaseUrl);
                        break;
                }

                _log.LogInformation(
                    "[PMS Reminder Unstick] Sent advancement email MappingId={Id} role={Role}",
                    item.MappingId, justCompletedRole);

                try
                {
                    using var logCon = new SqlConnection(_conn);
                    await logCon.OpenAsync();
                    string nextStage = justCompletedRole == "RM" ? "HOD" : "UnitHead";
                    await PmsBaSyncService.WriteSyncLogAsync(logCon,
                        item.MappingId, item.EmpId, item.EmpName, baAuditId,
                        "EMAIL_SENT", nextStage,
                        $"[Reminder Unstick] {nextStage} review email sent. PmsUrl={pmsUrl}",
                        null, null, "PmsReminderService", true, null);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[PMS Reminder] TryUnstickViaAdvancement failed for MappingId={Id}",
                    item.MappingId);
            }
        }

        // ====================================================
        // GET BA AUDIT ID FOR MAPPING
        // ====================================================
        private int GetBaAuditIdForMapping(SqlConnection con, int mappingId, int empId,
            string reviewerRole = null)
        {
            try
            {
                // Step 1: EmpId + cycle dates JOIN
                if (mappingId > 0 &&
                    ColExists(con, "EmpAppraisalMapping", "CycleFromDate") &&
                    TableExists(con, "Behaviour_Audit_Main"))
                {
                    using var joinCmd = new SqlCommand(@"
                        SELECT TOP 1 ba.AuditID
                        FROM   Behaviour_Audit_Main ba
                        INNER JOIN EmpAppraisalMapping pms
                               ON  ba.EmpID       = pms.EmpId
                               AND ba.CreatedOn  >= pms.CycleFromDate
                               AND ba.CreatedOn  <= DATEADD(day, 1, pms.CycleToDate)
                        WHERE  pms.MappingId = @Mid
                          AND  pms.IsDeleted = 0
                        ORDER BY ba.AuditID DESC", con);
                    joinCmd.Parameters.AddWithValue("@Mid", mappingId);
                    var jv = joinCmd.ExecuteScalar();
                    if (jv != null && jv != DBNull.Value) return Convert.ToInt32(jv);
                }

                // Step 2: BehaviourAuditId
                if (ColExists(con, "EmpAppraisalMapping", "BehaviourAuditId") && mappingId > 0)
                {
                    using var mc = new SqlCommand(
                        "SELECT ISNULL(BehaviourAuditId,0) FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    mc.Parameters.AddWithValue("@Mid", mappingId);
                    var v = mc.ExecuteScalar();
                    int linked = (v != null && v != DBNull.Value) ? Convert.ToInt32(v) : 0;
                    if (linked > 0) return linked;
                }

                // Step 3: BA.MappingId direct link
                if (ColExists(con, "Behaviour_Audit_Main", "MappingId") && mappingId > 0)
                {
                    using var mc = new SqlCommand(
                        "SELECT TOP 1 AuditID FROM Behaviour_Audit_Main " +
                        "WHERE MappingId=@Mid ORDER BY AuditID DESC", con);
                    mc.Parameters.AddWithValue("@Mid", mappingId);
                    var v = mc.ExecuteScalar();
                    if (v != null && v != DBNull.Value)
                    {
                        int linked = Convert.ToInt32(v);
                        if (linked > 0) return linked;
                    }
                }

                // Step 4: Role-aware submitted audit
                if (!string.IsNullOrWhiteSpace(reviewerRole) && empId > 0)
                {
                    string submitCol = reviewerRole switch
                    {
                        "RM" => "RM_SubmitDate",
                        "HOD" => "HOD_SubmitDate",
                        _ => null
                    };
                    if (submitCol != null &&
                        ColExists(con, "Behaviour_Audit_Main", submitCol))
                    {
                        using var fa = new SqlCommand(
                            $"SELECT TOP 1 AuditID FROM Behaviour_Audit_Main " +
                            $"WHERE EmpID=@EmpId AND {submitCol} IS NOT NULL " +
                            "ORDER BY AuditID DESC", con);
                        fa.Parameters.AddWithValue("@EmpId", empId);
                        var fav = fa.ExecuteScalar();
                        if (fav != null && fav != DBNull.Value) return Convert.ToInt32(fav);
                        return 0;
                    }
                }

                // Step 5: Most recent completed audit
                if (empId > 0 && TableExists(con, "Behaviour_Audit_Main"))
                {
                    using var fallback = new SqlCommand(@"
                        SELECT TOP 1 AuditID FROM Behaviour_Audit_Main
                        WHERE  EmpID = @EmpId
                          AND  (FinalStatus='Completed' OR AuditStatus='Completed'
                               OR ISNULL(RM_TotalScore,0)>0 OR ISNULL(HOD_TotalScore,0)>0)
                        ORDER BY AuditID DESC", con);
                    fallback.Parameters.AddWithValue("@EmpId", empId);
                    var fv = fallback.ExecuteScalar();
                    return (fv != null && fv != DBNull.Value) ? Convert.ToInt32(fv) : 0;
                }

                return 0;
            }
            catch { return 0; }
        }

        // ====================================================
        // SEND REMINDER EMAIL
        // ====================================================
        private async Task SendReminderEmail(
            PendingReminderItem item, string missingForms,
            string roleOverride, string reviewerNameOverride)
        {
            var mapping = new EmpKpiMappingModel
            {
                MappingId = item.MappingId,
                EmpId = item.EmpId,
                EmpName = item.EmpName,
                EmpCode = item.EmpCode,
                Department = item.Department,
                Designation = item.Designation,
                AppraisalYear = item.AppraisalYear,
                AppraisalCycle = item.AppraisalCycle,
                CycleFromDate = item.CycleFromDate,
                CycleToDate = item.CycleToDate,
                Status = item.CurrentStatus,
                RmName = item.RmName,
                HodName = item.HodName,
                UnitHeadName = item.UnitHeadName,
                ModCommitteeNames = item.ModCommitteeNames,
            };

            // NEW
            string pmsUrl = roleOverride switch
            {
                "RM" => $"{BaseUrl}/Pms/RmAppraisal?mappingId={item.MappingId}",
                "HOD" => $"{BaseUrl}/Pms/HodAppraisal?mappingId={item.MappingId}",
                "UnitHead" => $"{BaseUrl}/Pms/UnitHeadAppraisal?mappingId={item.MappingId}",
                "ModCommittee" => $"{BaseUrl}/Pms/ModCommitteeAppraisal?mappingId={item.MappingId}",
                _ => $"{BaseUrl}/Pms/Index"
            };
            // Only include BA URL if BA is actually still pending
            bool baActuallyPending =
                (roleOverride == "RM" || roleOverride == "HOD")
                && !IsBaSubmittedViaJoin(item.MappingId, item.EmpId, roleOverride);

            string baUrl = string.Empty;
            if (baActuallyPending)
            {
                int baAuditId;
                using (var con = new SqlConnection(_conn))
                {
                    con.Open();
                    baAuditId = GetBaAuditIdForMapping(con, item.MappingId, item.EmpId,
                        roleOverride);
                }
                // NEW
                baUrl = baAuditId > 0
                    ? roleOverride switch
                    {
                        "RM" => $"{BaseUrl}/BehaviouralAudit/RMForm/{baAuditId}",
                        "HOD" => $"{BaseUrl}/BehaviouralAudit/HODForm/{baAuditId}",
                        _ => string.Empty
                    }
                    : string.Empty;
            }

            await _email.SendPendingReminderAsync(
                mapping, roleOverride, reviewerNameOverride,
                missingForms, pmsUrl, baUrl, BaseUrl);
        }

        // ====================================================
        // GET MAPPING FROM DB
        // ====================================================
        private EmpKpiMappingModel GetMappingFromDb(int mappingId)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // Step 1: App DB header
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
            FROM EmpAppraisalMapping m
            WHERE m.MappingId = @Id AND m.IsDeleted = 0", con))
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

                // Step 2: HRMS enrichment
                string empCode = "", empName = "", email = "", dept = "", desig = "";
                if (empId > 0)
                {
                    try
                    {
                        using var hrmsCon = new SqlConnection(_conn);
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
                    catch { }
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

        // ====================================================
        // GET PENDING REVIEWER MAPPINGS
        // ====================================================
        private List<PendingReminderItem> GetPendingReviewerMappings()
        {
            var list = new List<PendingReminderItem>();
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                var eligibleStatuses = string.Join(",",
                    StatusToCurrentReviewer.Keys.Select(s => $"'{s}'"));

                bool hasRmCol = ColExists(con, "EmpAppraisalMapping", "RmName");
                bool hasHodCol = ColExists(con, "EmpAppraisalMapping", "HodName");
                bool hasUhCol = ColExists(con, "EmpAppraisalMapping", "UnitHeadName");
                bool hasMcCol = ColExists(con, "EmpAppraisalMapping", "ModCommitteeNames");

                string rmSel = hasRmCol ? "ISNULL(m.RmName,'')" : "''";
                string hodSel = hasHodCol ? "ISNULL(m.HodName,'')" : "''";
                string uhSel = hasUhCol ? "ISNULL(m.UnitHeadName,'')" : "''";
                string mcSel = hasMcCol ? "ISNULL(m.ModCommitteeNames,'')" : "''";

                string suppressionFilter = ColExists(con, "EmpAppraisalMapping", "PmsReminderSuppressedUntil")
                    ? "AND (m.PmsReminderSuppressedUntil IS NULL OR m.PmsReminderSuppressedUntil < GETDATE())"
                    : "";

                // ── App DB query — no HRMS join here for performance ─────────────────
                // Employee details are enriched per-item below only when needed for email.
                string sql = $@"
            SELECT m.MappingId, m.EmpId,
                   m.AppraisalYear, m.AppraisalCycle,
                   m.CycleFromDate, m.CycleToDate,
                   m.[Status],
                   {rmSel}  AS RmName,
                   {hodSel} AS HodName,
                   {uhSel}  AS UnitHeadName,
                   {mcSel}  AS ModCommitteeNames
            FROM EmpAppraisalMapping m
            WHERE m.IsDeleted = 0
              AND m.[Status] IN ({eligibleStatuses})
              {suppressionFilter}";

                // ── Batch-load HRMS employee details for all EmpIds in one query ─────
                var rawItems = new List<(int MappingId, int EmpId, int AppraisalYear,
                    string AppraisalCycle, DateTime? CycleFromDate, DateTime? CycleToDate,
                    string Status, string RmName, string HodName, string UhName, string McNames)>();

                using (var cmd = new SqlCommand(sql, con))
                using (var dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                    {
                        rawItems.Add((
                            Convert.ToInt32(dr["MappingId"]),
                            dr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["EmpId"]),
                            Convert.ToInt32(dr["AppraisalYear"]),
                            dr["AppraisalCycle"].ToString(),
                            dr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleFromDate"]),
                            dr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleToDate"]),
                            dr["Status"].ToString(),
                            dr["RmName"].ToString(),
                            dr["HodName"].ToString(),
                            dr["UnitHeadName"].ToString(),
                            dr["ModCommitteeNames"].ToString()
                        ));
                    }
                }

                if (!rawItems.Any()) return list;

                // ── Batch HRMS lookup ────────────────────────────────────────────────
                var empIds = rawItems.Select(r => r.EmpId).Where(id => id > 0).Distinct().ToList();
                var hrmsMap = new Dictionary<int, (string EmpCode, string FullName, string Email,
                                                   string Dept, string Desig)>();

                if (empIds.Any())
                {
                    try
                    {
                        string idCsv = string.Join(",", empIds);
                        using var hrmsCon = new SqlConnection(_conn);
                        hrmsCon.Open();
                        using var hrmsCmd = new SqlCommand($@"
                    SELECT
                        e.Emp_ID,
                        ISNULL(e.Emp_code,'')  AS EMPCode,
                        ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
                        ISNULL(e.Work_Email,'') AS Email,
                        ISNULL(dept.Dept_Name,'')  AS DepartmentName,
                        ISNULL(des.Desig_Name,'')  AS DesignationName
                    FROM   [Orange_Hrms].[dbo].[T0080_EMP_MASTER] e
                    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER]  dept ON dept.Dept_Id = e.Dept_ID
                    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des  ON des.Desig_ID = e.Desig_Id
                    WHERE  e.Emp_ID IN ({idCsv})", hrmsCon);

                        using var hrDr = hrmsCmd.ExecuteReader();
                        while (hrDr.Read())
                        {
                            int eid = Convert.ToInt32(hrDr["Emp_ID"]);
                            hrmsMap[eid] = (
                                hrDr["EMPCode"].ToString(),
                                hrDr["FullName"].ToString(),
                                hrDr["Email"].ToString(),
                                hrDr["DepartmentName"].ToString(),
                                hrDr["DesignationName"].ToString()
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[PMS Reminder] HRMS batch lookup failed — proceeding with blanks");
                    }
                }

                // ── Build final list ─────────────────────────────────────────────────
                foreach (var r in rawItems)
                {
                    if (!StatusToCurrentReviewer.TryGetValue(r.Status, out var role)) continue;

                    string reviewerName = role switch
                    {
                        "RM" => r.RmName,
                        "HOD" => r.HodName,
                        "UnitHead" => r.UhName,
                        "ModCommittee" => r.McNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                   .FirstOrDefault()?.Trim() ?? "",
                        _ => ""
                    };
                    if (string.IsNullOrWhiteSpace(reviewerName)) continue;

                    hrmsMap.TryGetValue(r.EmpId, out var hrms);

                    list.Add(new PendingReminderItem
                    {
                        MappingId = r.MappingId,
                        EmpId = r.EmpId,
                        EmpName = hrms.FullName ?? "",
                        EmpCode = hrms.EmpCode ?? "",
                        Department = hrms.Dept ?? "",
                        Designation = hrms.Desig ?? "",
                        AppraisalYear = r.AppraisalYear,
                        AppraisalCycle = r.AppraisalCycle,
                        CycleFromDate = r.CycleFromDate,
                        CycleToDate = r.CycleToDate,
                        CurrentStatus = r.Status,
                        ReviewerRole = role,
                        ReviewerName = reviewerName,
                        RmName = r.RmName,
                        HodName = r.HodName,
                        UnitHeadName = r.UhName,
                        ModCommitteeNames = r.McNames,
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[PMS Reminder] GetPendingReviewerMappings failed");
            }
            return list;
        }

        // ====================================================
        // HELPERS
        // ====================================================
        private static string GetReviewerNameFromReader(SqlDataReader dr, string role) =>
            role switch
            {
                "RM" => dr["RmName"].ToString(),
                "HOD" => dr["HodName"].ToString(),
                "UnitHead" => dr["UnitHeadName"].ToString(),
                "ModCommittee" => dr["ModCommitteeNames"].ToString()
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault()?.Trim() ?? "",
                _ => ""
            };

        private bool ColExists(SqlConnection con, string table, string col)
        {
            try
            {
                using var cmd = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_NAME=@T AND COLUMN_NAME=@C", con);
                cmd.Parameters.AddWithValue("@T", table);
                cmd.Parameters.AddWithValue("@C", col);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }

        private bool TableExists(SqlConnection con, string table)
        {
            try
            {
                using var cmd = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME=@T", con);
                cmd.Parameters.AddWithValue("@T", table);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }
    }

    // ====================================================
    // DTOs
    // ====================================================
    public class PendingReminderItem
    {
        public int MappingId { get; set; }
        public int EmpId { get; set; }
        public string EmpName { get; set; }
        public string EmpCode { get; set; }
        public string Department { get; set; }
        public string Designation { get; set; }
        public int AppraisalYear { get; set; }
        public string AppraisalCycle { get; set; }
        public DateTime? CycleFromDate { get; set; }
        public DateTime? CycleToDate { get; set; }
        public string CurrentStatus { get; set; }
        public string ReviewerRole { get; set; }
        public string ReviewerName { get; set; }
        public string RmName { get; set; }
        public string HodName { get; set; }
        public string UnitHeadName { get; set; }
        public string ModCommitteeNames { get; set; }
    }
}
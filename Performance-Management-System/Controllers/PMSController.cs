using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;
using vgl.Areas.Identity.Data;
using vgl.DAL;
using vgl.Models;
using vgl.Services;
using vgl.Services.BellNotificationService;

namespace vgl.Controllers
{
    [Authorize(Roles = "SuperAdmin,Admin,User")]
    public class PmsController : Controller
    {
        private readonly string _conn;
        private readonly vglContext _context;
        private readonly UserManager<vglUser> _userManager;
        private readonly DataAccessLayer _dataAccessLayer;
        private readonly IEmailService _email;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<PmsController> _log;
        private readonly IPmsBaSyncService _baSync;
        private readonly IConfiguration _config;
        private readonly INotificationService _notificationService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAppBaseUrlProvider _baseUrlProvider;
        private const string _module = "Performance Management System";

        // 2. Store it in the constructor
        public PmsController(
    INotificationService notificationService,
    IServiceScopeFactory scopeFactory,
    vglContext context,
    UserManager<vglUser> userManager,
    DataAccessLayer dataAccessLayer,
    IConfiguration config,
    IEmailService email,
    IWebHostEnvironment env,
    IPmsBaSyncService baSync,
    ILogger<PmsController> logger,
    IAppBaseUrlProvider baseUrlProvider)
        {
            _notificationService = notificationService;
            _context = context;
            _userManager = userManager;
            _dataAccessLayer = dataAccessLayer;
            _conn = config.GetConnectionString("DbConStr");
            _config = config;          // ← ADD THIS
            _email = email;
            _env = env;
            _baSync = baSync;
            _log = logger;
            _scopeFactory = scopeFactory;
            _baseUrlProvider = baseUrlProvider;
        }

        // 3. Replace GetBaseUrl()
        private string GetBaseUrl() => _baseUrlProvider.GetBaseUrl(Request);

        private async Task<(bool allowed, vglUser user)> HasRoleAccessAsync(string roleKey)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return (false, null);

            if (User.IsInRole("SuperAdmin")) return (true, user);

            var per = _dataAccessLayer.GetUserModulePermission(
                user.Id, user.CurrentUserRole, $"{_module} - {roleKey}");

            return (per != null && per.CanView, user);
        }
        // ====================================================
        // INDEX
        // ====================================================
        public async Task<IActionResult> Index(int tab = 0)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(
                user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            if (User.IsInRole("SuperAdmin"))
            {
                return RedirectToAction("SuperAdminDashboard","PMS");
            }
           
            if(!User.IsInRole("SuperAdmin") && (per != null && per.ExportToExcel && per.CanView))
            {
                return RedirectToAction("EmployeeDashboard", "PMS");
            }
            if (!User.IsInRole("SuperAdmin") && (per != null && per.CanReview && per.CanView))
            {
                return RedirectToAction("RmDashboard", "PMS");
            }
            if (!User.IsInRole("SuperAdmin") && (per != null && per.CanCheck && per.CanView))
            {
                return RedirectToAction("HodDashboard", "PMS");
            }
            if (!User.IsInRole("SuperAdmin") && (per != null && per.CanPrint && per.CanView))
            {
                return RedirectToAction("UnitHeadDashboard", "PMS");
            }


            //if (User.IsInRole("SuperAdmin") && (per == null || !per.CanView))
            //{
            //    var roleRoutes = new (string roleKey, string action)[]
            //    {
            //    ("Employee",     "EmployeeDashboard"),
            //    ("RM",           "RmDashboard"),
            //    ("HOD",          "HodDashboard"),
            //    ("UnitHead",     "UnitHeadDashboard"),
            //    ("ModCommittee", "ModCommitteeDashboard"),
            //    ("JMD",          "JmdDashboard"),
            //    };

            //    foreach (var (roleKey, action) in roleRoutes)
            //    {
            //        var (hasAccess, _) = await HasRoleAccessAsync(roleKey);
            //        if (hasAccess)
            //            return RedirectToAction(action);
            //    }

            //    return RedirectToAction("AccessDenied", "Auth");
            //}
            try
            {
                using var ensureCon = new SqlConnection(_conn);
                ensureCon.Open();
                EnsureMasterTimestampColumns(ensureCon);
                EnsureMcJmdColumns(ensureCon);
                EnsureReviewerEmpIdColumns(ensureCon);

            }
            catch { }

            var vm = new PmsIndexVM
            {
                ActiveTab = tab,
                CurrentYear = DateTime.Now.Year
            };

            vm.Pmps = GetAllPmps();
            vm.Kras = GetAllKras();
            vm.Kpis = GetAllKpis();
            vm.EmpMappings = GetAllMasterMappings();
            vm.PmpDropdown = vm.Pmps.Select(p => new SelectListItem
            {
                Value = p.PmpId.ToString(),
                Text = $"{p.PmpCode} \u2013 {p.PmpName}"
            }).ToList();

            vm.KraDropdown = vm.Kras.Select(k => new SelectListItem
            {
                Value = k.KraId.ToString(),
                Text = $"{k.KraCode} \u2013 {k.KraName}"
            }).ToList();

            vm.EmployeeDropdown = GetEmployeeDropdown();
            vm.ReviewerDropdown = GetReviewerDropdown();

            vm.PasBaseMaster = GetPasBaseMaster();
            vm.PasRatingVersions = GetPasRatingVersions();
            vm.PasActiveVersion = vm.PasRatingVersions.FirstOrDefault(v => v.IsActive)
                                   ?? vm.PasRatingVersions.FirstOrDefault();

            if (vm.PasActiveVersion != null)
            {
                vm.PasRatingMaster = vm.PasActiveVersion.DetailRows
                    .Select(d => new PasRatingHikeRow
                    {
                        Id = d.Id,
                        Rating = d.Rating,
                        RatingLabel = d.RatingLabel,
                        HikeOverBasePercent = d.HikeOverBasePercent,
                        SortOrder = d.SortOrder
                    }).ToList();
            }
            else
            {
                vm.PasRatingMaster = GetPasRatingMaster();
            }

            vm.PasYearlyPAT = GetPasYearlyPAT();
            // In Index() action, after getting AnnualCompletedMappings:
            vm.AnnualCompletedMappings = GetAllEmpMappings()   // ← EmpAppraisalMapping, not master
     .Where(m => (m.AppraisalCycle ?? "").Trim().Equals("Annual", StringComparison.OrdinalIgnoreCase))
     .ToList();

            // Load saved MC/JMD ratings for annual mappings
            try
            {
                using var con3 = new SqlConnection(_conn);
                con3.Open();
                var annualIds = vm.AnnualCompletedMappings.Select(m => m.MappingId).ToList();
                if (annualIds.Any())
                {
                    // In Index(), replace the mcJmdCmd query text:
                    using var mcJmdCmd = new SqlCommand(
                          $@"SELECT MappingId,
                              ISNULL(AnnualMcRating,  ModCommitteeAvgRating) AS AnnualMcRating,
                              ISNULL(AnnualJmdRating, JmdAvgRating)          AS AnnualJmdRating
                       FROM   EmpAppraisalMapping 
                       WHERE  MappingId IN ({string.Join(",", annualIds)}) 
                       AND    IsDeleted = 0", con3);
                    using var mcJmdDr = mcJmdCmd.ExecuteReader();
                    var mcJmdMap = new Dictionary<int, (decimal? AnnualMc, decimal? AnnualJmd)>();
                    while (mcJmdDr.Read())
                    {
                        int mid = Convert.ToInt32(mcJmdDr["MappingId"]);
                        decimal? annualMc = mcJmdDr["AnnualMcRating"] == DBNull.Value
                                             ? (decimal?)null
                                             : Convert.ToDecimal(mcJmdDr["AnnualMcRating"]);
                        decimal? annualJmd = mcJmdDr["AnnualJmdRating"] == DBNull.Value
                                             ? (decimal?)null
                                             : Convert.ToDecimal(mcJmdDr["AnnualJmdRating"]);
                        mcJmdMap[mid] = (annualMc, annualJmd);
                    }
                    foreach (var m in vm.AnnualCompletedMappings)
                    {
                        if (mcJmdMap.TryGetValue(m.MappingId, out var saved))
                        {
                            m.McRating = saved.AnnualMc;   // AnnualMcRating  → McRating
                            m.JmdRating = saved.AnnualJmd;  // AnnualJmdRating → JmdRating
                        }
                    }
                }
            }
            catch { }

            return View(vm);
        }
        public async Task<IActionResult> SuperAdminDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");
            if (!User.IsInRole("SuperAdmin"))
                return RedirectToAction("AccessDenied", "Auth");
            return View("SuperAdminDashboard");
        }

        [HttpPost]
        public JsonResult SaveJustification(int empKpiRowId, string justification)
        {
            if (justification != null && justification.Length > 500)
                justification = justification.Substring(0, 500);

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                using var cmd = new SqlCommand(@"
                    UPDATE EmpAppraisalKpiRows
                    SET    EmpJustification = @Justification,
                           UpdatedOn = GETDATE()
                    WHERE  EmpKpiRowId = @RowId AND IsDeleted = 0", con);

                cmd.Parameters.AddWithValue("@Justification", (object?)justification ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RowId", empKpiRowId);

                int rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    return Json(new { success = false, message = "Row not found" });

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── PMS LINK OWNERSHIP CHECK ─────────────────────────────────────────────
        // Verifies that the currently logged-in user is actually the Employee/RM/
        // HOD/UnitHead/JMD/ModCommittee member this mapping was assigned to.
        // Prevents an already-authenticated user from opening someone else's
        // appraisal simply by having the mappingId in a forwarded/shared link.
        private async Task<(bool authorized, int loggedInEmpId, string loggedInName)> VerifyReviewerAccessAsync(
            EmpKpiMappingModel mapping, string role)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return (false, 0, "");

            int loggedInEmpId = await ResolveEmpIdAsync(user.Id, user.UserName);
            if (loggedInEmpId <= 0)
                return (false, 0, "");

            var (loggedInName, _) = GetHrmsEmployeeInfo(loggedInEmpId);

            bool authorized = role switch
            {
                "Employee" => mapping.EmpId == loggedInEmpId,

                "RM" => (mapping.RmEmpId.HasValue && mapping.RmEmpId.Value == loggedInEmpId)
                        || (!mapping.RmEmpId.HasValue && IsEmployeeReportingTo(mapping.EmpId, loggedInEmpId)),

                "HOD" => mapping.HodEmpId.HasValue && mapping.HodEmpId.Value == loggedInEmpId,

                "UnitHead" => mapping.UnitHeadEmpId.HasValue && mapping.UnitHeadEmpId.Value == loggedInEmpId,

                "JMD" => mapping.JmdEmpId.HasValue && mapping.JmdEmpId.Value == loggedInEmpId,

                "ModCommittee" => mapping.ModCommitteeEmpIds != null
                                   && mapping.ModCommitteeEmpIds.Contains(loggedInEmpId),

                _ => false
            };

            // ── Fallback for legacy rows where the *EmpId column was never backfilled ──
            if (!authorized)
            {
                authorized = role switch
                {
                    "RM" => !string.IsNullOrWhiteSpace(mapping.RmName)
                             && mapping.RmName.Equals(loggedInName, StringComparison.OrdinalIgnoreCase),
                    "HOD" => !string.IsNullOrWhiteSpace(mapping.HodName)
                             && mapping.HodName.Equals(loggedInName, StringComparison.OrdinalIgnoreCase),
                    "UnitHead" => !string.IsNullOrWhiteSpace(mapping.UnitHeadName)
                             && mapping.UnitHeadName.Equals(loggedInName, StringComparison.OrdinalIgnoreCase),
                    "JMD" => !string.IsNullOrWhiteSpace(mapping.JmdName)
                             && mapping.JmdName.Equals(loggedInName, StringComparison.OrdinalIgnoreCase),
                    "ModCommittee" => !string.IsNullOrWhiteSpace(mapping.ModCommitteeNames)
                             && mapping.ModCommitteeNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Any(n => n.Trim().Equals(loggedInName, StringComparison.OrdinalIgnoreCase)),
                    _ => false
                };
            }

            if (!authorized)
            {
                _log.LogWarning(
                    "[PMS Access Denied] UserId={UserId} (EmpId={LoggedInEmpId}, Name='{LoggedInName}') " +
                    "attempted to access MappingId={MappingId} as role={Role}. " +
                    "Expected: RmEmpId={RmEmpId} HodEmpId={HodEmpId} UhEmpId={UhEmpId} JmdEmpId={JmdEmpId}",
                    user.Id, loggedInEmpId, loggedInName, mapping.MappingId, role,
                    mapping.RmEmpId, mapping.HodEmpId, mapping.UnitHeadEmpId, mapping.JmdEmpId);
            }

            return (authorized, loggedInEmpId, loggedInName);
        }

        // Pulls ModCommittee member EmpIds for a single mapping (mirrors the batch
        // version used in GetAllEmpMappings, but scoped to one MappingId for the
        // per-request access check in ModCommitteeAppraisal()).
        private List<int> GetModCommitteeEmpIdsForMapping(string connStr, int mappingId)
        {
            var ids = new List<int>();
            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();
                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EmpAppraisalModCommitteeMember'", con))
                    if ((int)chk.ExecuteScalar() == 0) return ids;

                using var cmd = new SqlCommand(
                    "SELECT EmpId FROM EmpAppraisalModCommitteeMember WHERE MappingId=@Mid", con);
                cmd.Parameters.AddWithValue("@Mid", mappingId);
                using var dr = cmd.ExecuteReader();
                while (dr.Read()) ids.Add(Convert.ToInt32(dr["EmpId"]));
            }
            catch { /* best-effort */ }
            return ids;
        }

        // ====================================================
        // APPRAISAL VIEWS
        // ====================================================
        public async Task<IActionResult> MyAppraisal(int? mappingId = null)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Account/Login", new { area = "Identity" });

            EmpKpiMappingModel mapping = mappingId.HasValue && mappingId.Value > 0
                ? GetMappingByIdInternal(_conn, mappingId.Value)
                : GetMappingByUserId(user.Id, "Employee");

            if (mapping == null)
            {
                TempData["Info"] = "No active appraisal mapping found.";
                return View("AppraisalNoData");
            }

            // ── Ownership check ──
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mapping, "Employee");
            if (!authorized)
                return RedirectToAction("AccessDenied", "Auth");

            bool empCanEdit = mapping.Status == "Draft" || mapping.Status == "EmployeeFilled";
            return View("AppraisalFill", new AppraisalFillVM
            {
                Mapping = mapping,
                LoggedInUserId = user.Id,
                LoggedInName = user.UserName,
                Role = "Employee",
                CanEdit = empCanEdit
            });
        }
        public async Task<IActionResult> RmAppraisal(int mappingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var mapping = GetMappingByIdInternal(_conn, mappingId);
            if (mapping == null) { TempData["Error"] = "Not found."; return RedirectToAction("Index"); }

            // ── Ownership check ──
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mapping, "RM");
            if (!authorized)
                return RedirectToAction("AccessDenied", "Auth");

            return View("AppraisalFill", new AppraisalFillVM
            {
                Mapping = mapping,
                LoggedInUserId = user.Id,
                LoggedInName = user.UserName,
                Role = "RM",
                CanEdit = mapping.Status == "EmployeeFilled" || mapping.Status == "RMReview"
            });
        }
        public async Task<IActionResult> HodAppraisal(int mappingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var mapping = GetMappingByIdInternal(_conn, mappingId);
            if (mapping == null) { TempData["Error"] = "Not found."; return RedirectToAction("Index"); }

            // ── Ownership check ──
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mapping, "HOD");
            if (!authorized)
                return RedirectToAction("AccessDenied", "Auth");

            return View("AppraisalFill", new AppraisalFillVM
            {
                Mapping = mapping,
                LoggedInUserId = user.Id,
                LoggedInName = user.UserName,
                Role = "HOD",
                CanEdit = mapping.Status == "RMReview" || mapping.Status == "HODReview"
            });
        }
        public async Task<IActionResult> UnitHeadAppraisal(int mappingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var mapping = GetMappingByIdInternal(_conn, mappingId);
            if (mapping == null) { TempData["Error"] = "Not found."; return RedirectToAction("Index"); }

            // ── Ownership check ──
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mapping, "UnitHead");
            if (!authorized)
                return RedirectToAction("AccessDenied", "Auth");

            return View("AppraisalFill", new AppraisalFillVM
            {
                Mapping = mapping,
                LoggedInUserId = user.Id,
                LoggedInName = user.UserName,
                Role = "UnitHead",
                CanEdit = mapping.Status == "HODReview" || mapping.Status == "UnitHeadReview"
            });
        }
        public async Task<IActionResult> ModCommitteeAppraisal(int mappingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");
            var mapping = GetMappingByIdInternal(_conn, mappingId);
            if (mapping == null) { TempData["Error"] = "Not found."; return RedirectToAction("Index"); }

            // ── Ownership check (needs ModCommitteeEmpIds populated first) ──
            mapping.ModCommitteeEmpIds = GetModCommitteeEmpIdsForMapping(_conn, mapping.MappingId);
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mapping, "ModCommittee");
            if (!authorized)
                return RedirectToAction("AccessDenied", "Auth");

            using var con = new SqlConnection(_conn);
            con.Open();
            using var cmd = new SqlCommand(@"
        SELECT ModCommitteeAvgRating, ModCommitteeOverallRemarks
        FROM   EmpAppraisalMapping
        WHERE  MappingId = @Id AND IsDeleted = 0", con);
            cmd.Parameters.AddWithValue("@Id", mappingId);
            using var dr = cmd.ExecuteReader();
            if (dr.Read())
            {
                ViewBag.McOverallRating = dr[0] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(dr[0]);
                ViewBag.McOverallRemarks = dr[1] == DBNull.Value ? null : dr[1].ToString();
            }

            return View("AppraisalFill", new AppraisalFillVM
            {
                Mapping = mapping,
                LoggedInUserId = user.Id,
                LoggedInName = user.UserName,
                Role = "ModCommittee",
                CanEdit = mapping.Status == "UnitHeadReview"
            });
        }

        public async Task<IActionResult> JmdAppraisal(int mappingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");
            var mapping = GetMappingByIdInternal(_conn, mappingId);
            if (mapping == null) { TempData["Error"] = "Not found."; return RedirectToAction("Index"); }

            // ── Ownership check ──
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mapping, "JMD");
            if (!authorized)
                return RedirectToAction("AccessDenied", "Auth");

            using var con = new SqlConnection(_conn);
            con.Open();
            using var cmd = new SqlCommand(@"
        SELECT ModCommitteeAvgRating, ModCommitteeOverallRemarks,
               JmdAvgRating,          JmdOverallRemarks
        FROM   EmpAppraisalMapping
        WHERE  MappingId = @Id AND IsDeleted = 0", con);
            cmd.Parameters.AddWithValue("@Id", mappingId);
            using var dr = cmd.ExecuteReader();
            if (dr.Read())
            {
                ViewBag.McOverallRating = dr[0] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(dr[0]);
                ViewBag.McOverallRemarks = dr[1] == DBNull.Value ? null : dr[1].ToString();
                ViewBag.JmdOverallRating = dr[2] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(dr[2]);
                ViewBag.JmdOverallRemarks = dr[3] == DBNull.Value ? null : dr[3].ToString();
            }

            return View("AppraisalFill", new AppraisalFillVM
            {
                Mapping = mapping,
                LoggedInUserId = user.Id,
                LoggedInName = user.UserName,
                Role = "JMD",
                CanEdit = mapping.Status == "ModCommitteeReview"
            });
        }

        public IActionResult GetAnnualReport()
        {
            var mappings = GetAllEmpMappings()
                .Where(m => (m.AppraisalCycle ?? "").Trim()
                            .Equals("Annual", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var yearlyPATList = GetPasYearlyPAT();
            var ratingVersions = GetPasRatingVersions();
            var activeVersion = ratingVersions.FirstOrDefault(v => v.IsActive) ?? ratingVersions.FirstOrDefault();

            var reportRows = new List<AnnualReportRow>();
            var savedRatings = new Dictionary<int, (decimal? AnnualMc, decimal? AnnualJmd)>();

            try
            {
                using var con2 = new SqlConnection(_conn);
                con2.Open();
                EnsureMcJmdColumnsStatic(con2);
                var ids = mappings.Select(m => m.MappingId).ToList();
                if (ids.Any())
                {
                    using var loadCmd = new SqlCommand(
                        $@"SELECT MappingId,
                   ISNULL(AnnualMcRating,  ModCommitteeAvgRating) AS AnnualMcRating,
                   ISNULL(AnnualJmdRating, JmdAvgRating)          AS AnnualJmdRating
                   FROM   EmpAppraisalMapping
                   WHERE  MappingId IN ({string.Join(",", ids)})
                   AND    IsDeleted = 0", con2);
                    using var ldr = loadCmd.ExecuteReader();
                    while (ldr.Read())
                    {
                        int mid = Convert.ToInt32(ldr["MappingId"]);
                        decimal? annualMc = ldr["AnnualMcRating"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(ldr["AnnualMcRating"]);
                        decimal? annualJmd = ldr["AnnualJmdRating"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(ldr["AnnualJmdRating"]);
                        savedRatings[mid] = (annualMc, annualJmd);
                    }
                }
            }
            catch { }

            foreach (var m in mappings)
            {
                var fullMapping = GetMappingByIdInternal(_conn, m.MappingId);
                if (fullMapping == null) continue;

                var yearlyEntry = yearlyPATList.FirstOrDefault(y => {
                    var parts = y.FinancialYear?.Split('-', '/') ?? Array.Empty<string>();
                    if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out int fyStart))
                        return fyStart == m.AppraisalYear;
                    return y.FinancialYear == m.AppraisalYear.ToString();
                });

                decimal? GetPerRowAvg(EmpKpiRow r)
                {
                    var vals = new List<decimal>();
                    if (r.EmpRating.HasValue) vals.Add(r.EmpRating.Value);
                    if (r.RmRating.HasValue) vals.Add(r.RmRating.Value);
                    if (r.HodRating.HasValue) vals.Add(r.HodRating.Value);
                    if (r.UnitHeadRating.HasValue) vals.Add(r.UnitHeadRating.Value);
                    return vals.Any() ? (decimal?)Math.Round(vals.Average(), 2) : null;
                }

                decimal bscScore = Math.Round(fullMapping.KpiRows.Sum(r => {
                    var rt = GetPerRowAvg(r);
                    return rt.HasValue ? Math.Round(r.IndividualWeightage / 100m * rt.Value, 2) : 0m;
                }), 2);

                if (bscScore == 0m && fullMapping.KpiRows.Any())
                {
                    decimal? GetCascadeRating(EmpKpiRow r) =>
                        r.HrFinalRating ?? r.JmdRating ?? r.ModCommitteeRating ??
                        r.UnitHeadRating ?? r.HodRating ?? r.RmRating ?? r.EmpRating;

                    bscScore = Math.Round(fullMapping.KpiRows.Sum(r => {
                        var rt = GetCascadeRating(r);
                        return rt.HasValue ? Math.Round(r.IndividualWeightage / 100m * rt.Value, 2) : 0m;
                    }), 2);
                }

                var (auditExists, auditCompleted, behScore) = GetBehaviouralAuditStatus(m.EmpId, m.MappingId);

                decimal finalScore = auditCompleted
                    ? Math.Round(0.8m * bscScore + 0.2m * behScore, 2)
                    : Math.Round(bscScore, 2);

                decimal basePercent = yearlyEntry?.BasePercentage ?? 0m;

                decimal? savedMcForCalc = savedRatings.TryGetValue(m.MappingId, out var srCalc) ? srCalc.AnnualMc : null;
                decimal? savedJmdForCalc = savedRatings.TryGetValue(m.MappingId, out var srCalc2) ? srCalc2.AnnualJmd : null;

                decimal effectiveRatingForCalc = savedJmdForCalc.HasValue ? savedJmdForCalc.Value
                                               : savedMcForCalc.HasValue ? savedMcForCalc.Value
                                               : finalScore;

                int calcRating = (int)Math.Min(5, Math.Max(1, Math.Round(effectiveRatingForCalc)));
                decimal hikeOverBase = 0m;
                string ratingLabel = $"Rating {calcRating}";
                if (activeVersion?.DetailRows != null)
                {
                    var dr = activeVersion.DetailRows.FirstOrDefault(d => d.Rating == calcRating);
                    if (dr != null) { hikeOverBase = dr.HikeOverBasePercent; ratingLabel = dr.RatingLabel; }
                }

                decimal finalIncrPct = basePercent > 0
                    ? Math.Round(basePercent * (1m + hikeOverBase / 100m), 4)
                    : 0m;

                string jmdRemark = "";
                DateTime? doj = null;

                try
                {
                    using var con = new Microsoft.Data.SqlClient.SqlConnection(_conn);
                    con.Open();
                    using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                        "SELECT DateOfJoining FROM UserDetails WHERE Emp_Id = @EmpId", con);
                    cmd.Parameters.AddWithValue("@EmpId", m.EmpId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read() && !reader.IsDBNull(0))
                        doj = reader.GetDateTime(0);
                }
                catch { }

                jmdRemark = fullMapping.KpiRows.LastOrDefault()?.JmdRemarks ?? "";

                reportRows.Add(new AnnualReportRow
                {
                    MappingId = m.MappingId,
                    EmpId = m.EmpId,
                    EmpCode = m.EmpCode,
                    EmpName = m.EmpName,
                    Department = m.Department,
                    Designation = m.Designation,
                    UnitName = m.UnitName,
                    DateOfJoining = doj,
                    AppraisalYear = m.AppraisalYear,
                    FinalScore = bscScore,
                    CalcRating = calcRating,
                    RatingLabel = ratingLabel,
                    BasePercent = basePercent,
                    HikeOverBase = hikeOverBase,
                    FinalIncrPct = finalIncrPct,
                    BehaviouralCompleted = auditCompleted,
                    JmdRemark = jmdRemark,
                    McRating = savedRatings.TryGetValue(m.MappingId, out var sr) ? sr.AnnualMc : null,
                    JmdRating = savedRatings.TryGetValue(m.MappingId, out var sr2) ? sr2.AnnualJmd : null,
                });
            }

            ViewBag.ActiveVersion = activeVersion;
            return View("AnnualReportPMSandBA", reportRows);
        }

        // ====================================================
        // REPLACEMENT: GetBaScoresForAnnualReport
        // Reads RM_TotalScore / HOD_TotalScore directly from
        // Behaviour_Audit_Main instead of Behaviour_Audit_Details.
        // Drop-in replacement — same endpoint signature and JSON shape.
        // ====================================================

        [HttpGet]
        public IActionResult GetBaScoresForAnnualReport(int mappingId, int empId)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // ── 1. Check BA table exists ──────────────────────────────────
                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Main'", con))
                    if ((int)chk.ExecuteScalar() == 0)
                        return Json(new { rmScore = (decimal?)null, hodScore = (decimal?)null, baCompleted = false });

                // ── 2. Resolve auditId (same priority as before) ─────────────
                int auditId = 0;

                // Priority 1: BehaviourAuditId linked directly on the PMS mapping
                if (mappingId > 0)
                {
                    bool hasBaIdCol = false;
                    using (var cc = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='BehaviourAuditId'", con))
                        hasBaIdCol = (int)cc.ExecuteScalar() > 0;

                    if (hasBaIdCol)
                    {
                        using var mc = new SqlCommand(
                            "SELECT ISNULL(BehaviourAuditId,0) FROM EmpAppraisalMapping " +
                            "WHERE MappingId=@Mid AND IsDeleted=0", con);
                        mc.Parameters.AddWithValue("@Mid", mappingId);
                        var linked = mc.ExecuteScalar();
                        if (linked != null && linked != DBNull.Value)
                        {
                            int linkedId = Convert.ToInt32(linked);
                            if (linkedId > 0) auditId = linkedId;
                        }
                    }
                }

                // Priority 2: cycle-scoped fallback
                if (auditId == 0 && empId > 0)
                {
                    DateTime? cycleFrom = null, cycleTo = null;
                    if (mappingId > 0)
                    {
                        using var dateCmd = new SqlCommand(
                            "SELECT CycleFromDate, CycleToDate FROM EmpAppraisalMapping " +
                            "WHERE MappingId=@Mid AND IsDeleted=0", con);
                        dateCmd.Parameters.AddWithValue("@Mid", mappingId);
                        using var ddr = dateCmd.ExecuteReader();
                        if (ddr.Read())
                        {
                            cycleFrom = ddr[0] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(ddr[0]);
                            cycleTo = ddr[1] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(ddr[1]);
                        }
                    }

                    if (cycleFrom.HasValue && cycleTo.HasValue)
                    {
                        using var cmd = new SqlCommand(@"
                    SELECT TOP 1 AuditID FROM Behaviour_Audit_Main
                    WHERE EmpID = @EmpId
                      AND CreatedOn >= @From
                      AND CreatedOn <= DATEADD(day, 1, @To)
                    ORDER BY AuditID DESC", con);
                        cmd.Parameters.AddWithValue("@EmpId", empId);
                        cmd.Parameters.AddWithValue("@From", cycleFrom.Value);
                        cmd.Parameters.AddWithValue("@To", cycleTo.Value);
                        var v = cmd.ExecuteScalar();
                        if (v != null && v != DBNull.Value) auditId = Convert.ToInt32(v);
                    }
                    else
                    {
                        using var fallback = new SqlCommand(@"
                    SELECT TOP 1 AuditID FROM Behaviour_Audit_Main
                    WHERE EmpID = @EmpId ORDER BY AuditID DESC", con);
                        fallback.Parameters.AddWithValue("@EmpId", empId);
                        var fv = fallback.ExecuteScalar();
                        if (fv != null && fv != DBNull.Value) auditId = Convert.ToInt32(fv);
                    }
                }

                if (auditId == 0)
                    return Json(new { rmScore = (decimal?)null, hodScore = (decimal?)null, baCompleted = false });

                // ── 3. Read scores + status directly from Behaviour_Audit_Main ─
                //       RM_TotalScore / HOD_TotalScore  — raw sums
                //       RM_Rating     / HOD_Rating      — already normalised (0-5)
                //       FinalStatus   / AuditStatus     — for completion flag
                //
                // Strategy:
                //   • Prefer RM_Rating / HOD_Rating if they are on a 0-5 scale (value ≤ 5).
                //   • Otherwise fall back to RM_TotalScore / HOD_TotalScore and normalise.
                //   • MaxScore denominator comes from Behaviour_AuditList (same as before).

                decimal rmRaw = 0m, hodRaw = 0m;
                decimal rmRating = 0m, hodRating = 0m;
                bool hasRmRating = false, hasHodRating = false;
                bool baCompleted = false;

                // Detect which columns exist (schema may vary across environments)
                bool hasRmTotalScore = ColExistsInTable(con, "Behaviour_Audit_Main", "RM_TotalScore");
                bool hasHodTotalScore = ColExistsInTable(con, "Behaviour_Audit_Main", "HOD_TotalScore");
                bool hasRmRatingCol = ColExistsInTable(con, "Behaviour_Audit_Main", "RM_Rating");
                bool hasHodRatingCol = ColExistsInTable(con, "Behaviour_Audit_Main", "HOD_Rating");

                // Build a SELECT that reads whatever columns exist
                var selectParts = new List<string>
        {
            "ISNULL(FinalStatus,'')  AS FinalStatus",
            "ISNULL(AuditStatus,'') AS AuditStatus"
        };
                if (hasRmTotalScore) selectParts.Add("ISNULL(RM_TotalScore,0)  AS RM_TotalScore");
                if (hasHodTotalScore) selectParts.Add("ISNULL(HOD_TotalScore,0) AS HOD_TotalScore");
                if (hasRmRatingCol) selectParts.Add("ISNULL(RM_Rating,0)      AS RM_Rating");
                if (hasHodRatingCol) selectParts.Add("ISNULL(HOD_Rating,0)     AS HOD_Rating");

                using (var mainCmd = new SqlCommand(
                    $"SELECT {string.Join(", ", selectParts)} " +
                    "FROM Behaviour_Audit_Main WHERE AuditID = @Aid", con))
                {
                    mainCmd.Parameters.AddWithValue("@Aid", auditId);
                    using var dr = mainCmd.ExecuteReader();
                    if (dr.Read())
                    {
                        string fs = dr["FinalStatus"].ToString();
                        string as2 = dr["AuditStatus"].ToString();
                        baCompleted = fs == "Completed" || as2 == "Completed";

                        if (hasRmTotalScore)
                            rmRaw = Convert.ToDecimal(dr["RM_TotalScore"]);
                        if (hasHodTotalScore)
                            hodRaw = Convert.ToDecimal(dr["HOD_TotalScore"]);

                        if (hasRmRatingCol)
                        {
                            rmRating = Convert.ToDecimal(dr["RM_Rating"]);
                            hasRmRating = rmRating > 0;
                        }
                        if (hasHodRatingCol)
                        {
                            hodRating = Convert.ToDecimal(dr["HOD_Rating"]);
                            hasHodRating = hodRating > 0;
                        }
                    }
                }

                // ── 4. Determine MaxScore for normalisation ───────────────────
                //       Only needed when the pre-calculated *Rating columns are
                //       absent or 0 and we must normalise the TotalScore.
                decimal maxTotal = 0m;

                if ((!hasRmRating || rmRating == 0) || (!hasHodRating || hodRating == 0))
                {
                    // Try Behaviour_AuditList first
                    try
                    {
                        bool auditListExists = false;
                        using (var tlChk = new SqlCommand(
                            "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_AuditList'", con))
                            auditListExists = (int)tlChk.ExecuteScalar() > 0;

                        if (auditListExists)
                        {
                            using var mx = new SqlCommand(
                                "SELECT ISNULL(SUM(CAST(MaxScore AS DECIMAL(10,2))),0) " +
                                "FROM Behaviour_AuditList WHERE Category='BA'", con);
                            var mv = mx.ExecuteScalar();
                            if (mv != null && mv != DBNull.Value)
                                maxTotal = Convert.ToDecimal(mv);
                        }
                    }
                    catch { }

                    // Fallback: infer from detail rows
                    if (maxTotal <= 0)
                    {
                        try
                        {
                            using var inferMax = new SqlCommand(
                                "SELECT COUNT(*) * 10.0 FROM Behaviour_Audit_Details WHERE AuditID = @Aid", con);
                            inferMax.Parameters.AddWithValue("@Aid", auditId);
                            var iv = inferMax.ExecuteScalar();
                            if (iv != null && iv != DBNull.Value)
                                maxTotal = Convert.ToDecimal(iv);
                        }
                        catch { }
                    }

                    if (maxTotal <= 0) maxTotal = 100m; // absolute fallback
                }

                // ── 5. Compute final 0-5 scores ───────────────────────────────
                //
                // If RM_Rating / HOD_Rating exist and are already ≤ 5 use them
                // directly.  If they are on a larger scale (e.g. 0-100) or are
                // absent, normalise the TotalScore.

                decimal rmScore5 = 0m;
                decimal hodScore5 = 0m;

                if (hasRmRating && rmRating > 0)
                {
                    // Already in 0-5 band → use as-is; if somehow > 5, normalise
                    rmScore5 = rmRating <= 5
                        ? Math.Round(rmRating, 2)
                        : Math.Round(rmRating / maxTotal * 5m, 2);
                }
                else if (rmRaw > 0)
                {
                    rmScore5 = rmRaw <= 5
                        ? Math.Round(rmRaw, 2)
                        : Math.Round(rmRaw / maxTotal * 5m, 2);
                }

                if (hasHodRating && hodRating > 0)
                {
                    hodScore5 = hodRating <= 5
                        ? Math.Round(hodRating, 2)
                        : Math.Round(hodRating / maxTotal * 5m, 2);
                }
                else if (hodRaw > 0)
                {
                    hodScore5 = hodRaw <= 5
                        ? Math.Round(hodRaw, 2)
                        : Math.Round(hodRaw / maxTotal * 5m, 2);
                }

                return Json(new
                {
                    auditId,
                    baCompleted,
                    rmScore = rmScore5 > 0 ? (decimal?)rmScore5 : null,
                    hodScore = hodScore5 > 0 ? (decimal?)hodScore5 : null
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    error = ex.ToString(),
                    rmScore = (decimal?)null,
                    hodScore = (decimal?)null,
                    baCompleted = false
                });
            }
        }
        private void EnsureMcJmdColumnsStatic(SqlConnection con) => EnsureMcJmdColumns(con);
        // ====================================================
        // PRINT APPRAISAL
        // ====================================================
        public IActionResult PrintAppraisal(int mappingId)
        {
            if (mappingId <= 0) return Content("Invalid mapping ID.");
            var mapping = GetMappingByIdInternal(_conn, mappingId);
            if (mapping == null) return Content("Mapping not found.");

            var yearlyPATList = GetPasYearlyPAT();
            var ratingVersions = GetPasRatingVersions();

            var yearlyEntry = yearlyPATList.FirstOrDefault(y =>
            {
                var parts = y.FinancialYear?.Split('-', '/') ?? Array.Empty<string>();
                if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out int fyStart))
                    return fyStart == mapping.AppraisalYear;
                return y.FinancialYear == mapping.AppraisalYear.ToString();
            });

            var activeVersion = ratingVersions.FirstOrDefault(v => v.IsActive)
                                ?? ratingVersions.FirstOrDefault();

            // ── Behavioural Audit: exists + completed flags from existing method ──
            var (auditExists, auditCompleted, _) = GetBehaviouralAuditStatus(mapping.EmpId, mappingId);

            // ── BA Score: recalculate from RM_Rating + HOD_Rating directly ──
            // (avoids stale Rating column which may have old value before rollup fix)
            decimal behaviouralScore = 0m;
            try
            {
                using var baCon = new SqlConnection(_conn);
                baCon.Open();

                int auditId = 0;

                // Priority 1: BehaviourAuditId linked directly on the mapping
                bool hasBaIdCol = ColExistsInTable(baCon, "EmpAppraisalMapping", "BehaviourAuditId");
                if (hasBaIdCol)
                {
                    using var mc = new SqlCommand(
                        "SELECT ISNULL(BehaviourAuditId,0) FROM EmpAppraisalMapping WHERE MappingId=@Mid AND IsDeleted=0", baCon);
                    mc.Parameters.AddWithValue("@Mid", mappingId);
                    var linked = mc.ExecuteScalar();
                    if (linked != null && linked != DBNull.Value)
                    {
                        int linkedId = Convert.ToInt32(linked);
                        if (linkedId > 0) auditId = linkedId;
                    }
                }

                // Priority 2: cycle-scoped fallback
                if (auditId == 0 && mapping.EmpId > 0)
                {
                    bool baTableExists = false;
                    using (var chk = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Main'", baCon))
                        baTableExists = (int)chk.ExecuteScalar() > 0;

                    if (baTableExists)
                    {
                        if (mapping.CycleFromDate.HasValue && mapping.CycleToDate.HasValue)
                        {
                            using var fallbackCmd = new SqlCommand(@"
          SELECT TOP 1 AuditID FROM Behaviour_Audit_Main
          WHERE  EmpID = @EmpId
            AND  CreatedOn >= @From
            AND  CreatedOn <= DATEADD(day, 1, @To)
          ORDER BY AuditID DESC", baCon);
                            fallbackCmd.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                            fallbackCmd.Parameters.AddWithValue("@From", mapping.CycleFromDate.Value);
                            fallbackCmd.Parameters.AddWithValue("@To", mapping.CycleToDate.Value);
                            var fv = fallbackCmd.ExecuteScalar();
                            if (fv != null && fv != DBNull.Value) auditId = Convert.ToInt32(fv);
                        }
                        else
                        {
                            using var fallbackCmd = new SqlCommand(@"
          SELECT TOP 1 AuditID FROM Behaviour_Audit_Main
          WHERE  EmpID = @EmpId
          ORDER BY AuditID DESC", baCon);
                            fallbackCmd.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                            var fv = fallbackCmd.ExecuteScalar();
                            if (fv != null && fv != DBNull.Value) auditId = Convert.ToInt32(fv);
                        }
                    }
                }

                if (auditId > 0)
                {
                    bool hasRmRating = ColExistsInTable(baCon, "Behaviour_Audit_Main", "RM_Rating");
                    bool hasHodRating = ColExistsInTable(baCon, "Behaviour_Audit_Main", "HOD_Rating");

                    var selectParts = new List<string> { "ISNULL(Rating, 0) AS Rating" };
                    if (hasRmRating) selectParts.Add("ISNULL(RM_Rating,  0) AS RM_Rating");
                    if (hasHodRating) selectParts.Add("ISNULL(HOD_Rating, 0) AS HOD_Rating");

                    using var baCmd = new SqlCommand(
                        $"SELECT {string.Join(", ", selectParts)} FROM Behaviour_Audit_Main WHERE AuditID = @Aid", baCon);
                    baCmd.Parameters.AddWithValue("@Aid", auditId);
                    using var baDr = baCmd.ExecuteReader();
                    if (baDr.Read())
                    {
                        decimal rmRating = hasRmRating ? Convert.ToDecimal(baDr["RM_Rating"]) : 0m;
                        decimal hodRating = hasHodRating ? Convert.ToDecimal(baDr["HOD_Rating"]) : 0m;

                        // Normalise to 0-5 if stored as raw sum
                        decimal rmScore5 = rmRating > 0 ? (rmRating <= 5 ? Math.Round(rmRating, 2) : Math.Round(rmRating / 100m * 5m, 2)) : 0m;
                        decimal hodScore5 = hodRating > 0 ? (hodRating <= 5 ? Math.Round(hodRating, 2) : Math.Round(hodRating / 100m * 5m, 2)) : 0m;

                        if (rmScore5 > 0 && hodScore5 > 0)
                            behaviouralScore = Math.Round((rmScore5 + hodScore5) / 2m, 2);
                        else if (rmScore5 > 0)
                            behaviouralScore = rmScore5;
                        else if (hodScore5 > 0)
                            behaviouralScore = hodScore5;

                        // Fallback to stored Rating column if RM/HOD ratings missing
                        if (behaviouralScore == 0m)
                        {
                            decimal fallback = Convert.ToDecimal(baDr["Rating"]);
                            behaviouralScore = fallback <= 5
                                ? Math.Round(fallback, 2)
                                : Math.Round(fallback / 100m * 5m, 2);
                        }
                    }
                }
            }
            catch { }

            // ── Load MC/JMD overall ratings ──
            decimal? mcOverallRating = null;
            decimal? jmdOverallRating = null;
            string mcOverallRemarks = "";
            string jmdOverallRemarks = "";
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                EnsureMcJmdColumns(con);
                using var cmd = new SqlCommand(@"
                  SELECT ISNULL(AnnualMcRating,  ISNULL(ModCommitteeAvgRating, 0)) AS McRating,
                         ISNULL(AnnualJmdRating, ISNULL(JmdAvgRating, 0))          AS JmdRating,
                         ISNULL(ModCommitteeOverallRemarks, '') AS McRemarks,
                         ISNULL(JmdOverallRemarks, '')          AS JmdRemarks
                  FROM   EmpAppraisalMapping
                  WHERE  MappingId = @Id AND IsDeleted = 0", con);
                cmd.Parameters.AddWithValue("@Id", mappingId);
                using var dr = cmd.ExecuteReader();
                if (dr.Read())
                {
                    var mc = Convert.ToDecimal(dr[0]);
                    var jmd = Convert.ToDecimal(dr[1]);
                    mcOverallRating = mc > 0 ? mc : (decimal?)null;
                    jmdOverallRating = jmd > 0 ? jmd : (decimal?)null;
                    mcOverallRemarks = dr[2].ToString();
                    jmdOverallRemarks = dr[3].ToString();
                }
            }
            catch { }

            ViewBag.YearlyPAT = yearlyEntry;
            ViewBag.ActiveRatingVersion = activeVersion;
            ViewBag.BehaviouralAuditExists = auditExists;
            ViewBag.BehaviouralAuditCompleted = auditCompleted;
            ViewBag.BehaviouralScore = behaviouralScore;
            ViewBag.McOverallRating = mcOverallRating;
            ViewBag.JmdOverallRating = jmdOverallRating;
            ViewBag.McOverallRemarks = mcOverallRemarks;
            ViewBag.JmdOverallRemarks = jmdOverallRemarks;

            return View("PrintAppraisal", mapping);
        }


        // ====================================================
        // DEDICATED DASHBOARDS
        // ====================================================  

        /// <summary>
        /// Employee's personal appraisal dashboard.
        /// Shows active cycle + pipeline + KPI snapshot + history table.
        /// View: Views/Pms/EmployeeDashboard.cshtml
        private async Task<int> ResolveEmpIdAsync(string userId, string Emp_code)
        {
            // Priority 1 — HRMS by email (authoritative source; this is the same
            // Work_Email → Emp_ID match that RmEmpId/HodEmpId/UnitHeadEmpId/JmdEmpId
            // are resolved against at assignment time via ResolveEmpIdByName, so
            // using it here first keeps "who is logged in" and "who was assigned"
            // pointing at the same EmpId).
            if (!string.IsNullOrWhiteSpace(Emp_code))
            {
                try
                {
                    string orgConn = _config.GetConnectionString("OrgConStr");
                    using var con = new SqlConnection(orgConn);
                    con.Open();
                    using var cmd = new SqlCommand(
                        "SELECT TOP 1 Emp_ID FROM T0080_EMP_MASTER " +
                        "WHERE Emp_Code = @EmpCode AND Cmp_ID = 2", con); 
                    cmd.Parameters.AddWithValue("@EmpCode", Emp_code.Trim());
                    var v = cmd.ExecuteScalar();
                    if (v != null && v != DBNull.Value)
                    {
                        int id = Convert.ToInt32(v);
                        if (id > 0) return id;
                    }
                }
                catch(Exception ex) { return 0;  }
            }

            // Priority 2 — fallback: appraisal mapping table, only reached when
            // the HRMS email match above found nothing (e.g. login email not yet
            // synced to Work_Email). NOTE: this can return a stale/unrelated EmpId
            // if the logged-in person also happens to have their own row in
            // EmpAppraisalMapping as an appraisee — that's why it's demoted to a
            // fallback rather than tried first.
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(
                    "SELECT TOP 1 EmpId FROM EmpAppraisalMapping " +
                    "WHERE UserId=@Uid AND IsDeleted=0 ORDER BY MappingId DESC", con);
                cmd.Parameters.AddWithValue("@Uid", userId ?? "");
                var v = cmd.ExecuteScalar();
                if (v != null && v != DBNull.Value)
                {
                    int id = Convert.ToInt32(v);
                    if (id > 0) return id;
                }
            }
            catch { }

            return 0;
        }
        // ── HELPER: resolve name + dept from T0080_EMP_MASTER ───────
        private (string fullName, string department) GetHrmsEmployeeInfo(int empId)
        {
            if (empId <= 0) return ("", "");
            try
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();
                using var cmd = new SqlCommand(@"
SELECT
    ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
    ISNULL(dept.Dept_Name,'') AS Department
FROM   T0080_EMP_MASTER e
LEFT JOIN T0040_DEPARTMENT_MASTER dept ON dept.Dept_Id = e.Dept_ID
WHERE  e.Emp_ID = @EmpId AND e.Cmp_ID = 2", con);
                cmd.Parameters.AddWithValue("@EmpId", empId);
                  using var dr = cmd.ExecuteReader();
                if (dr.Read())
                    return (dr["FullName"]?.ToString() ?? "", dr["Department"]?.ToString() ?? "");
            }
            catch { }
            return ("", "");
        }

        // ====================================================
        // EMPLOYEE DASHBOARD
        // ====================================================
        public async Task<IActionResult> EmployeeDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            string empCode = "", empName = "", designation = "", department = "", rmName = "";
            int empId = await ResolveEmpIdAsync(user.Id, user.Email);

            if (empId > 0)
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();
                using var cmd = new SqlCommand(@"
SELECT
    ISNULL(e.Emp_code,'')                                                          AS EMPCode,
    ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
    ISNULL(d.Desig_Name,'')                                                        AS Designation,
    ISNULL(dept.Dept_Name,'')                                                      AS Department,
    ISNULL(LTRIM(RTRIM(ISNULL(rm.Emp_First_Name,'')+' '+ISNULL(rm.Emp_Last_Name,''))),'') AS RmName
FROM   T0080_EMP_MASTER e
LEFT JOIN T0040_DESIGNATION_MASTER d    ON d.Desig_ID  = e.Desig_Id
LEFT JOIN T0040_DEPARTMENT_MASTER  dept ON dept.Dept_Id = e.Dept_ID
LEFT JOIN T0080_EMP_MASTER         rm   ON rm.Emp_ID   = e.Emp_Superior
WHERE  e.Emp_ID = @EmpId", con);
                cmd.Parameters.AddWithValue("@EmpId", empId);
                using var dr = cmd.ExecuteReader();
                if (dr.Read())
                {
                    empCode = dr["EMPCode"].ToString();
                    empName = dr["FullName"].ToString();
                    designation = dr["Designation"].ToString();
                    department = dr["Department"].ToString();
                    rmName = dr["RmName"].ToString();
                }
            }

            var allMappings = GetAllEmpMappings()
                .Where(m => m.EmpId == empId)
                .OrderByDescending(m => m.AppraisalYear)
                .ThenByDescending(m => m.MappingId)
                .ToList();

            var active = allMappings.FirstOrDefault(m => m.Status != "HRFinalized")
                         ?? allMappings.FirstOrDefault();

            EmpKpiMappingModel activeWithRows = null;
            if (active != null)
                activeWithRows = GetMappingByIdInternal(_conn, active.MappingId);

            var past = allMappings
                .Where(m => m.MappingId != (active?.MappingId ?? 0))
                .ToList();

            var vm = new AppraisalDashboardVM
            {
                EmpName = empName,
                EmpCode = empCode,
                Designation = designation,
                Department = department,
                RmName = rmName,
                ActiveMapping = activeWithRows,
                PastMappings = past
            };

            return View("EmployeeDashboard", vm);
        }

        // ====================================================
        // RM DASHBOARD
        // ====================================================
        // ============================================================
        // SHARED HELPERS — add these once, anywhere in the private
        // methods section of PmsController
        // ============================================================

        /// <summary>
        /// Normalizes a name for comparison: trims, collapses any run of
        /// whitespace to a single space, uppercases. The SQL-side resolver
        /// (ResolveEmpIdByName) already normalizes this way; the in-memory
        /// dashboard .Equals() checks did not — that mismatch is the root
        /// cause of reviewer dashboards silently showing nothing despite a
        /// "correct-looking" stored name.
        /// </summary>
        private static string NormalizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return System.Text.RegularExpressions.Regex
                .Replace(s.Trim(), @"\s+", " ")
                .ToUpperInvariant();
        }

        /// <summary>
        /// Opportunistic self-heal: when a mapping was matched to a reviewer
        /// by NAME, DEPARTMENT, or REPORTING-LINE (i.e. its EmpId column was
        /// NULL), persist the resolved EmpId immediately so every subsequent
        /// request hits the fast, reliable EmpId-based match instead of
        /// re-doing a name comparison every time. Fire-and-forget — never
        /// let this block or fail the dashboard load.
        /// </summary>
        private void SelfHealReviewerEmpId(int mappingId, string empIdColumn, int resolvedEmpId)
        {
            if (resolvedEmpId <= 0) return;

            // Whitelist the column name — never interpolate untrusted input into SQL.
            var allowed = new HashSet<string> { "RmEmpId", "HodEmpId", "UnitHeadEmpId", "JmdEmpId" };
            if (!allowed.Contains(empIdColumn)) return;

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(
                    $"UPDATE EmpAppraisalMapping SET {empIdColumn} = @EmpId " +
                    $"WHERE MappingId = @Mid AND {empIdColumn} IS NULL AND IsDeleted = 0", con);
                cmd.Parameters.AddWithValue("@EmpId", resolvedEmpId);
                cmd.Parameters.AddWithValue("@Mid", mappingId);
                cmd.ExecuteNonQuery();
            }
            catch { /* self-heal is best-effort; never break the dashboard over it */ }
        }

        // ============================================================
        // RM DASHBOARD (hardened)
        // ============================================================
        public async Task<IActionResult> RmDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            int rmEmpId = await ResolveEmpIdAsync(user.Id, user.UserName);
            var (reviewerName, department) = GetHrmsEmployeeInfo(rmEmpId);
            string normalizedReviewerName = NormalizeName(reviewerName);

            var mappings = new List<EmpKpiMappingModel>();

            foreach (var m in GetAllEmpMappings())
            {
                bool matchedByEmpId = m.RmEmpId.HasValue && m.RmEmpId.Value == rmEmpId;

                bool matchedByName = !m.RmEmpId.HasValue
                    && normalizedReviewerName != ""
                    && NormalizeName(m.RmName) == normalizedReviewerName;

                bool matchedByReportingLine = !m.RmEmpId.HasValue
                    && !matchedByName
                    && rmEmpId > 0
                    && IsEmployeeReportingTo(m.EmpId, rmEmpId);

                if (!(matchedByEmpId || matchedByName || matchedByReportingLine))
                    continue;

                mappings.Add(m);

                // Self-heal: we resolved the correct EmpId via name or reporting
                // line — persist it so future requests hit the EmpId fast-path.
                if (!m.RmEmpId.HasValue && rmEmpId > 0 && (matchedByName || matchedByReportingLine))
                    SelfHealReviewerEmpId(m.MappingId, "RmEmpId", rmEmpId);
            }

            mappings = mappings.OrderByDescending(m => m.MappingId).ToList();

            var vm = new ReviewerDashboardVM
            {
                ReviewerName = reviewerName,
                Department = department,
                Role = "RM",
                Mappings = mappings
            };

            return View("RmDashboard", vm);
        }

        // ============================================================
        // HOD DASHBOARD (hardened)
        // ============================================================
        public async Task<IActionResult> HodDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            int hodEmpId = await ResolveEmpIdAsync(user.Id, user.UserName);
            var (reviewerName, department) = GetHrmsEmployeeInfo(hodEmpId);
            string normalizedReviewerName = NormalizeName(reviewerName);
            string normalizedDepartment = NormalizeName(department);

            var mappings = new List<EmpKpiMappingModel>();

            foreach (var m in GetAllEmpMappings())
            {
                bool matchedByEmpId = m.HodEmpId.HasValue && m.HodEmpId.Value == hodEmpId;

                bool matchedByName = !m.HodEmpId.HasValue
                    && normalizedReviewerName != ""
                    && NormalizeName(m.HodName) == normalizedReviewerName;

                // Department is a LAST-RESORT fallback — only used when neither
                // EmpId nor name matched, so it can't mask a real EmpId/name bug.
                bool matchedByDepartment = !m.HodEmpId.HasValue
                    && !matchedByName
                    && normalizedDepartment != ""
                    && NormalizeName(m.Department) == normalizedDepartment;

                if (!(matchedByEmpId || matchedByName || matchedByDepartment))
                    continue;

                mappings.Add(m);

                // Self-heal on name match (we know the correct EmpId for certain).
                // Deliberately NOT self-healing on department-only matches — that
                // fallback doesn't guarantee this HOD is the *intended* reviewer
                // for this specific row, just that they share a department.
                if (!m.HodEmpId.HasValue && hodEmpId > 0 && matchedByName)
                    SelfHealReviewerEmpId(m.MappingId, "HodEmpId", hodEmpId);
            }

            mappings = mappings.OrderByDescending(m => m.MappingId).ToList();

            var vm = new ReviewerDashboardVM
            {
                ReviewerName = reviewerName,
                Department = department,
                Role = "HOD",
                Mappings = mappings
            };

            return View("HodDashboard", vm);
        }

        // ============================================================
        // UNIT HEAD DASHBOARD (hardened)
        // ============================================================
        public async Task<IActionResult> UnitHeadDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            int uhEmpId = await ResolveEmpIdAsync(user.Id, user.UserName);
            var (reviewerName, department) = GetHrmsEmployeeInfo(uhEmpId);
            string normalizedReviewerName = NormalizeName(reviewerName);

            var mappings = new List<EmpKpiMappingModel>();

            foreach (var m in GetAllEmpMappings())
            {
                bool matchedByEmpId = m.UnitHeadEmpId.HasValue && m.UnitHeadEmpId.Value == uhEmpId;

                bool matchedByName = !m.UnitHeadEmpId.HasValue
                    && normalizedReviewerName != ""
                    && NormalizeName(m.UnitHeadName) == normalizedReviewerName;

                if (!(matchedByEmpId || matchedByName))
                    continue;

                mappings.Add(m);

                if (!m.UnitHeadEmpId.HasValue && uhEmpId > 0 && matchedByName)
                    SelfHealReviewerEmpId(m.MappingId, "UnitHeadEmpId", uhEmpId);
            }

            mappings = mappings.OrderByDescending(m => m.MappingId).ToList();

            var vm = new ReviewerDashboardVM
            {
                ReviewerName = reviewerName,
                Department = department,
                Role = "UnitHead",
                Mappings = mappings
            };

            return View("UnitHeadDashboard", vm);
        }

        // ============================================================
        // GetReviewerMappingsJson (hardened) — this is the AJAX endpoint
        // that actually feeds the tabbed dashboard UI (Pending / Waiting /
        // Awaiting HOD / Completed / All), so it needs the identical fix
        // or the action-method fixes above won't be visible on screen.
        // ============================================================
        [HttpGet]
        public async Task<IActionResult> GetReviewerMappingsJson(string role, int year = 0)
        {
            string roleUpper = (role ?? "").Trim().ToUpperInvariant();

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Json(new { error = "Not authenticated." });

            // ── Resolve this user's HRMS identity ─────────────────────────────────────
            int empId = await ResolveEmpIdAsync(user.Id, user.UserName);
            var (reviewerName, department) = GetHrmsEmployeeInfo(empId);
            string normalizedReviewerName = NormalizeName(reviewerName);
            string normalizedDepartment = NormalizeName(department);

            _log.LogInformation(
                "[GetReviewerMappingsJson] role={Role} userId={Uid} empId={EmpId} name='{Name}'",
                roleUpper, user.Id, empId, reviewerName);

            // ── Build the filtered mapping list ───────────────────────────────────────
            List<EmpKpiMappingModel> mappings = new();
            var allMappings = GetAllEmpMappings();

            switch (roleUpper)
            {
                case "RM":
                    foreach (var m in allMappings)
                    {
                        bool byEmpId = m.RmEmpId.HasValue && m.RmEmpId.Value == empId;
                        bool byName = !m.RmEmpId.HasValue
                            && normalizedReviewerName != ""
                            && NormalizeName(m.RmName) == normalizedReviewerName;
                        bool byReportingLine = !m.RmEmpId.HasValue
                            && !byName
                            && empId > 0
                            && IsEmployeeReportingTo(m.EmpId, empId);

                        if (!(byEmpId || byName || byReportingLine)) continue;

                        mappings.Add(m);
                        if (!m.RmEmpId.HasValue && empId > 0 && (byName || byReportingLine))
                            SelfHealReviewerEmpId(m.MappingId, "RmEmpId", empId);
                    }
                    break;

                case "HOD":
                    foreach (var m in allMappings)
                    {
                        bool byEmpId = m.HodEmpId.HasValue && m.HodEmpId.Value == empId;
                        bool byName = !m.HodEmpId.HasValue
                            && normalizedReviewerName != ""
                            && NormalizeName(m.HodName) == normalizedReviewerName;
                        bool byDepartment = !m.HodEmpId.HasValue
                            && !byName
                            && normalizedDepartment != ""
                            && NormalizeName(m.Department) == normalizedDepartment;

                        if (!(byEmpId || byName || byDepartment)) continue;

                        mappings.Add(m);
                        if (!m.HodEmpId.HasValue && empId > 0 && byName)
                            SelfHealReviewerEmpId(m.MappingId, "HodEmpId", empId);
                    }
                    break;

                case "UNITHEAD":
                    foreach (var m in allMappings)
                    {
                        bool byEmpId = m.UnitHeadEmpId.HasValue && m.UnitHeadEmpId.Value == empId;
                        bool byName = !m.UnitHeadEmpId.HasValue
                            && normalizedReviewerName != ""
                            && NormalizeName(m.UnitHeadName) == normalizedReviewerName;

                        if (!(byEmpId || byName)) continue;

                        mappings.Add(m);
                        if (!m.UnitHeadEmpId.HasValue && empId > 0 && byName)
                            SelfHealReviewerEmpId(m.MappingId, "UnitHeadEmpId", empId);
                    }
                    break;

                case "EMPLOYEE":
                    mappings = allMappings.Where(m => m.EmpId == empId).ToList();
                    break;

                case "MODCOMMITTEE":
                    mappings = allMappings.Where(m =>
                            (m.ModCommitteeEmpIds != null && m.ModCommitteeEmpIds.Contains(empId))
                            || (!string.IsNullOrWhiteSpace(m.ModCommitteeNames)
                                && m.ModCommitteeNames
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Any(n => NormalizeName(n) == normalizedReviewerName
                                              && normalizedReviewerName != "")))
                        .ToList();
                    // Note: no self-heal here — ModCommitteeEmpIds is a multi-value
                    // list (EmpAppraisalModCommitteeMember table), not a single
                    // column, so it isn't safe to blind-write from this loop.
                    break;

                case "JMD":
                    foreach (var m in allMappings)
                    {
                        bool byEmpId = m.JmdEmpId.HasValue && m.JmdEmpId.Value == empId;
                        bool byName = !m.JmdEmpId.HasValue
                            && normalizedReviewerName != ""
                            && NormalizeName(m.JmdName) == normalizedReviewerName;

                        if (!(byEmpId || byName)) continue;

                        mappings.Add(m);
                        if (!m.JmdEmpId.HasValue && empId > 0 && byName)
                            SelfHealReviewerEmpId(m.MappingId, "JmdEmpId", empId);
                    }
                    break;

                default:
                    return Json(new { error = "Invalid role: " + roleUpper });
            }

            if (year > 0)
                mappings = mappings.Where(m => m.AppraisalYear == year).ToList();

            _log.LogInformation(
                "[GetReviewerMappingsJson] role={Role} empId={EmpId} name='{Name}' → {Count} mappings",
                roleUpper, empId, reviewerName, mappings.Count);

            string baseUrl = GetBaseUrl();

            var result = mappings.Select(m =>
            {
                string pmsUrl = roleUpper switch
                {
                    "RM" => $"{baseUrl}/Pms/RmAppraisal?mappingId={m.MappingId}",
                    "HOD" => $"{baseUrl}/Pms/HodAppraisal?mappingId={m.MappingId}",
                    "UNITHEAD" => $"{baseUrl}/Pms/UnitHeadAppraisal?mappingId={m.MappingId}",
                    "EMPLOYEE" => $"{baseUrl}/Pms/MyAppraisal?mappingId={m.MappingId}",
                    "MODCOMMITTEE" => $"{baseUrl}/Pms/ModCommitteeAppraisal?mappingId={m.MappingId}",
                    "JMD" => $"{baseUrl}/Pms/JmdAppraisal?mappingId={m.MappingId}",
                    _ => ""
                };

                int baAuditId = 0;
                if (roleUpper == "RM" || roleUpper == "HOD")
                    baAuditId = GetBehaviouralAuditId(_conn, m.EmpId, m.MappingId);

                string baUrl = "";
                if (baAuditId > 0)
                {
                    baUrl = roleUpper switch
                    {
                        "RM" => $"{baseUrl}/BehaviouralAudit/RMForm/{baAuditId}",
                        "HOD" => $"{baseUrl}/BehaviouralAudit/HODForm/{baAuditId}",
                        _ => ""
                    };
                }

                bool pmsPending = roleUpper switch
                {
                    "RM" => m.Status == "EmployeeFilled" || m.Status == "RMReview",
                    "HOD" => m.Status == "RMReview" || m.Status == "HODReview",
                    "UNITHEAD" => m.Status == "HODReview" || m.Status == "UnitHeadReview",
                    "EMPLOYEE" => m.Status == "Draft" || m.Status == "EmployeeFilled",
                    "MODCOMMITTEE" => m.Status == "UnitHeadReview" || m.Status == "ModCommitteeReview",
                    "JMD" => m.Status == "ModCommitteeReview" || m.Status == "JMDReview",
                    _ => false
                };

                bool baPending = false;
                if (roleUpper == "RM")
                {
                    bool rmSubmittedPms = StageIndex(m.Status) >= StageIndex("RMReview");
                    if (rmSubmittedPms || pmsPending)
                    {
                        bool rmBaDone = baAuditId > 0
                            && IsBaCompletedByReviewer(_conn, m.EmpId, "RM", m.MappingId);
                        baPending = baAuditId > 0 && !rmBaDone;
                    }
                }
                else if (roleUpper == "HOD")
                {
                    bool hodSubmittedPms = StageIndex(m.Status) >= StageIndex("HODReview");
                    if (hodSubmittedPms || pmsPending)
                    {
                        bool hodBaDone = baAuditId > 0
                            && IsBaCompletedByReviewer(_conn, m.EmpId, "HOD", m.MappingId);
                        baPending = baAuditId > 0 && !hodBaDone;
                    }
                }

                string myOwnStageStatus = roleUpper switch
                {
                    "EMPLOYEE" => "EmployeeFilled",
                    "RM" => "RMReview",
                    "HOD" => "HODReview",
                    "UNITHEAD" => "UnitHeadReview",
                    "MODCOMMITTEE" => "ModCommitteeReview",
                    "JMD" => "JMDReview",
                    _ => ""
                };

                bool isMyPending;
                switch (roleUpper)
                {
                    case "RM":
                        if (m.Status == "EmployeeFilled")
                            isMyPending = true;   // RM hasn't reviewed yet
                        else if (m.Status == "RMReview")
                            isMyPending = baPending;  // RM submitted PMS — only pending if BA still outstanding
                        else
                            isMyPending = false;
                        break;

                    case "HOD":
                        if (m.Status == "RMReview")
                        {
                            bool rmBaDone = baAuditId > 0
                                ? IsBaCompletedByReviewer(_conn, m.EmpId, "RM", m.MappingId)
                                : false;

                            if (baAuditId == 0)
                            {
                                bool baTableExists = false;
                                try
                                {
                                    using var chk = new SqlConnection(_conn);
                                    chk.Open();
                                    using var cc = new SqlCommand(
                                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Main'", chk);
                                    baTableExists = (int)cc.ExecuteScalar() > 0;
                                }
                                catch { }
                                isMyPending = !baTableExists;
                            }
                            else
                            {
                                isMyPending = rmBaDone;
                            }
                        }
                        else
                        {
                            // HODReview (just submitted) and any later stage — only "pending" if HOD's
                            // own BA is still outstanding. Mirrors the RM branch above exactly.
                            isMyPending = baPending;
                        }
                        break;

                    case "UNITHEAD":
                        if (m.Status == "HODReview")
                        {
                            int hodBaAuditId = GetBehaviouralAuditId(_conn, m.EmpId, m.MappingId);
                            bool hodBaDone = hodBaAuditId > 0
                                ? IsBaCompletedByReviewer(_conn, m.EmpId, "HOD", m.MappingId)
                                : false;

                            if (hodBaAuditId == 0)
                            {
                                bool baTableExists = false;
                                try
                                {
                                    using var chk = new SqlConnection(_conn);
                                    chk.Open();
                                    using var cc = new SqlCommand(
                                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Main'", chk);
                                    baTableExists = (int)cc.ExecuteScalar() > 0;
                                }
                                catch { }
                                isMyPending = !baTableExists;
                            }
                            else
                                isMyPending = hodBaDone;
                        }
                        else if (m.Status == "UnitHeadReview")
                            isMyPending = false;
                        else
                            isMyPending = false;
                        break;

                    case "EMPLOYEE":
                        isMyPending = pmsPending;
                        break;

                    case "MODCOMMITTEE":
                        isMyPending = m.Status == "UnitHeadReview" || m.Status == "ModCommitteeReview";
                        break;

                    case "JMD":
                        isMyPending = m.Status == "ModCommitteeReview" || m.Status == "JMDReview";
                        break;

                    default:
                        isMyPending = false;
                        break;
                }

                bool? rmBaDoneForHod = null;
                bool? hodBaDoneForUh = null;

                if (roleUpper == "HOD" && m.Status == "RMReview")
                {
                    int rmBaId = GetBehaviouralAuditId(_conn, m.EmpId, m.MappingId);
                    rmBaDoneForHod = rmBaId > 0
                        ? IsBaCompletedByReviewer(_conn, m.EmpId, "RM", m.MappingId)
                        : (bool?)null;
                }

                if (roleUpper == "UNITHEAD" && m.Status == "HODReview")
                {
                    int hodBaId = GetBehaviouralAuditId(_conn, m.EmpId, m.MappingId);
                    hodBaDoneForUh = hodBaId > 0
                        ? IsBaCompletedByReviewer(_conn, m.EmpId, "HOD", m.MappingId)
                        : (bool?)null;
                }

                return new
                {
                    mappingId = m.MappingId,
                    empId = m.EmpId,
                    empName = m.EmpName,
                    empCode = m.EmpCode,
                    designation = m.Designation,
                    department = m.Department,
                    rmName = m.RmName,
                    hodName = m.HodName,
                    unitHeadName = m.UnitHeadName,
                    appraisalYear = m.AppraisalYear,
                    appraisalCycle = m.AppraisalCycle,
                    cycleFromDate = m.CycleFromDate?.ToString("dd-MMM-yy") ?? "",
                    cycleToDate = m.CycleToDate?.ToString("dd-MMM-yy") ?? "",
                    status = m.Status,
                    kpiCount = m.KpiRows.Count,
                    pmsUrl,
                    baUrl,
                    hasBaLink = !string.IsNullOrEmpty(baUrl),
                    pmsPending,
                    baPending,
                    isMyPending,
                    myActionStatus = roleUpper switch
                    {
                        "RM" => "EmployeeFilled",
                        "HOD" => "RMReview",
                        "UNITHEAD" => "HODReview",
                        "EMPLOYEE" => "Draft",
                        _ => ""
                    },
                    myOwnStageStatus,
                    rmBaDoneForHod,
                    hodBaDoneForUh
                };
            })
            .OrderByDescending(x => x.isMyPending)
            .ThenByDescending(x => x.appraisalYear)
            .ThenBy(x => x.empName)
            .ToList();

            // ── Distinct FY years for the year-filter dropdown ────────────────────────
            var allYears = mappings
                .Select(m => m.AppraisalYear)
                .Distinct()
                .OrderByDescending(y => y)
                .ToList();

            return Json(new
            {
                success = true,
                years = allYears,
                rows = result
            });
        }
        public async Task<IActionResult> ModCommitteeDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            int mcEmpId = await ResolveEmpIdAsync(user.Id, user.UserName);
            var (reviewerName, department) = GetHrmsEmployeeInfo(mcEmpId);

            var mappings = GetAllEmpMappings()
                .Where(m =>
                    (m.ModCommitteeEmpIds != null && m.ModCommitteeEmpIds.Contains(mcEmpId))
                    || (!string.IsNullOrWhiteSpace(m.ModCommitteeNames)
                        && m.ModCommitteeNames
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Any(n => n.Trim().Equals(reviewerName, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(m => m.MappingId)
                .ToList();

            var vm = new ReviewerDashboardVM
            {
                ReviewerName = reviewerName,
                Department = department,
                Role = "ModCommittee",
                Mappings = mappings
            };

            return View("ModCommitteeDashboard", vm);
        }

     


        public async Task<IActionResult> JmdDashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            int jmdEmpId = await ResolveEmpIdAsync(user.Id, user.UserName);
            var (reviewerName, department) = GetHrmsEmployeeInfo(jmdEmpId);

            var mappings = GetAllEmpMappings()
                .Where(m =>
                    (m.JmdEmpId.HasValue && m.JmdEmpId.Value == jmdEmpId)
                    || (!m.JmdEmpId.HasValue && !string.IsNullOrWhiteSpace(m.JmdName)
                        && m.JmdName.Equals(reviewerName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(m => m.MappingId)
                .ToList();

            var vm = new ReviewerDashboardVM
            {
                ReviewerName = reviewerName,
                Department = department,
                Role = "JMD",
                Mappings = mappings
            };

            return View("JmdDashboard", vm);
        }

        // ====================================================
        // HOD DASHBOARD
        // ====================================================
      

        // ====================================================
        // SAVE CHANGE LOG
        // ====================================================
        [HttpPost]
        public async Task<IActionResult> SaveChangeLog([FromBody] ChangeLogRequest req)
        {
            if (req == null || req.Changes == null || !req.Changes.Any())
                return Json(new { success = true, message = "Nothing to log." });

            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // PAS_ChangeLog already exists — no CREATE needed
                foreach (var c in req.Changes)
                {
                    using var ins = new SqlCommand(@"
                INSERT INTO PAS_ChangeLog
                    (TableName, RecordId, ActionType, FieldName,
                     OldValue,  NewValue, ChangedBy,  ChangedOn, IPAddress)
                VALUES
                    ('EmpAppraisalMapping', @RecordId, @ActionType, @FieldName,
                     @OldValue, @NewValue, @By, GETDATE(), @IP)", con);

                    ins.Parameters.AddWithValue("@RecordId", req.MappingId);
                    ins.Parameters.AddWithValue("@ActionType", req.ActionType ?? "EDIT");
                    ins.Parameters.AddWithValue("@FieldName", c.FieldName ?? "");
                    ins.Parameters.AddWithValue("@OldValue", (object?)(c.OldValue) ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@NewValue", (object?)(c.NewValue) ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@By", by);
                    ins.Parameters.AddWithValue("@IP", ip);
                    ins.ExecuteNonQuery();
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private void LogKpiChangesToDb(
    SqlConnection con,
    int masterMappingId,
    List<KpiDetailRequest> newKpiDetails,
    string changedBy,
    string ipAddress = "")
        {
            try
            {
                // Read current rows before the soft-delete so we can diff
                var before = new List<(int kpiId, string name, decimal wt, decimal target)>();
                using (var sel = new SqlCommand(@"
            SELECT KpiId,
                   ISNULL(KpiName,'')       AS KpiName,
                   IndividualWeightage,
                   Target
            FROM   EmpMappingMasterKpi
            WHERE  MasterMappingId = @Mid AND IsDeleted = 0
            ORDER BY SortOrder", con))
                {
                    sel.Parameters.AddWithValue("@Mid", masterMappingId);
                    using var dr = sel.ExecuteReader();
                    while (dr.Read())
                        before.Add((
                            Convert.ToInt32(dr["KpiId"]),
                            dr["KpiName"].ToString(),
                            Convert.ToDecimal(dr["IndividualWeightage"]),
                            Convert.ToDecimal(dr["Target"])
                        ));
                }

                var newList = newKpiDetails ?? new List<KpiDetailRequest>();
                var afterIds = newList.Select(k => k.KpiId).ToHashSet();
                var beforeIds = before.Select(b => b.kpiId).ToHashSet();

                var changes = new List<(string field, string oldVal, string newVal)>();

                // Removed KPIs
                foreach (var b in before.Where(b => !afterIds.Contains(b.kpiId)))
                    changes.Add(("KPI Removed",
                        $"[{b.kpiId}] {b.name} | Wt={b.wt}% | Target={b.target}",
                        ""));

                // Added KPIs
                foreach (var a in newList.Where(a => !beforeIds.Contains(a.KpiId)))
                    changes.Add(("KPI Added",
                        "",
                        $"[{a.KpiId}] {a.KpiName} | Wt={a.IndividualWeightage}% | Target={a.Target}"));

                // Edited KPIs — check each changed field individually
                foreach (var a in newList.Where(a => beforeIds.Contains(a.KpiId)))
                {
                    var b = before.First(x => x.kpiId == a.KpiId);
                    var newName = (a.KpiName ?? "").Trim();

                    if (b.name != newName)
                        changes.Add(($"KPI [{a.KpiId}] Name", b.name, newName));

                    if (Math.Abs(b.wt - (decimal)a.IndividualWeightage) > 0.001m)
                        changes.Add(($"KPI [{a.KpiId}] Weight",
                            $"{b.wt}%", $"{a.IndividualWeightage}%"));

                    if (Math.Abs(b.target - (decimal)a.Target) > 0.001m)
                        changes.Add(($"KPI [{a.KpiId}] Target",
                            $"{b.target}", $"{a.Target}"));
                }

                if (!changes.Any()) return;

                // Action type: CREATE if this is the first time KPIs are being added,
                // EDIT if we are updating an existing set
                string actionType = !before.Any() ? "KPI_CREATE" : "KPI_EDIT";

                foreach (var (field, oldVal, newVal) in changes)
                {
                    using var ins = new SqlCommand(@"
                INSERT INTO PAS_ChangeLog
                    (TableName,            RecordId, ActionType, FieldName,
                     OldValue,             NewValue, ChangedBy,  ChangedOn, IPAddress)
                VALUES
                    ('EmpMappingMasterKpi', @RecordId, @ActionType, @FieldName,
                     @OldValue,             @NewValue, @By,        GETDATE(), @IP)", con);

                    ins.Parameters.AddWithValue("@RecordId", masterMappingId);
                    ins.Parameters.AddWithValue("@ActionType", actionType);
                    ins.Parameters.AddWithValue("@FieldName", field);
                    ins.Parameters.AddWithValue("@OldValue",
                        string.IsNullOrEmpty(oldVal) ? (object)DBNull.Value : oldVal);
                    ins.Parameters.AddWithValue("@NewValue",
                        string.IsNullOrEmpty(newVal) ? (object)DBNull.Value : newVal);
                    ins.Parameters.AddWithValue("@By", changedBy);
                    ins.Parameters.AddWithValue("@IP",
                        string.IsNullOrEmpty(ipAddress) ? (object)DBNull.Value : ipAddress);
                    ins.ExecuteNonQuery();
                }
            }
            catch { /* audit logging must never throw */ }
        }

        private void EnsureChangeLogTable()
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(@"
                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PmsChangeLog')
                    BEGIN
                        CREATE TABLE PmsChangeLog (
                            LogId       INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            MappingId   INT NOT NULL,
                            ActionType  NVARCHAR(20) NOT NULL,
                            FieldName   NVARCHAR(200) NOT NULL,
                            OldValue    NVARCHAR(500) NULL,
                            NewValue    NVARCHAR(500) NULL,
                            ChangedBy   NVARCHAR(450) NOT NULL,
                            ChangedOn   DATETIME NOT NULL DEFAULT GETDATE(),
                            SessionJson NVARCHAR(MAX) NULL
                        );
                        CREATE NONCLUSTERED INDEX IX_PmsChangeLog_MappingId ON PmsChangeLog(MappingId);
                    END", con);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ====================================================
        // GET PREVIOUS CYCLES
        // ====================================================
        [HttpGet]
        public IActionResult GetPreviousCycles(string userId, int year)
        {
            try
            {
                var list = new List<object>();
                using var con = new SqlConnection(_conn);
                con.Open();

                int empId = 0;
                using (var ec = new SqlCommand("SELECT ISNULL(Emp_Id,0) FROM UserDetails WHERE UserId=@U", con))
                {
                    ec.Parameters.AddWithValue("@U", userId ?? "");
                    var v = ec.ExecuteScalar();
                    if (v != null && v != DBNull.Value) empId = Convert.ToInt32(v);
                }
                if (empId == 0) return Json(list);

                using var cmd = new SqlCommand(@"
                    SELECT AppraisalCycle, CycleFromDate, CycleToDate, [Status]
                    FROM   EmpAppraisalMapping
                    WHERE  EmpId=@E AND AppraisalYear=@Y AND IsDeleted=0
                    ORDER BY MappingId", con);
                cmd.Parameters.AddWithValue("@E", empId);
                cmd.Parameters.AddWithValue("@Y", year);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(new
                    {
                        cycle = dr["AppraisalCycle"].ToString(),
                        fromDate = dr["CycleFromDate"] == DBNull.Value ? "" : Convert.ToDateTime(dr["CycleFromDate"]).ToString("yyyy-MM-dd"),
                        toDate = dr["CycleToDate"] == DBNull.Value ? "" : Convert.ToDateTime(dr["CycleToDate"]).ToString("yyyy-MM-dd"),
                        status = dr["Status"].ToString()
                    });
                return Json(list);
            }
            catch (Exception ex) { return Json(new { error = ex.Message }); }
        }
        
        // ====================================================
        [HttpPost]
        public async Task<IActionResult> AssignCycleToEmployees([FromBody] AssignCycleRequest req)
        {
            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";

            if (req == null || req.MappingIds == null || !req.MappingIds.Any())
                return Json(new { success = false, message = "No employees selected." });
            if (string.IsNullOrWhiteSpace(req.AppraisalCycle))
                return Json(new { success = false, message = "Appraisal cycle is required." });
            if (string.IsNullOrWhiteSpace(req.CycleFromDate) || string.IsNullOrWhiteSpace(req.CycleToDate))
                return Json(new { success = false, message = "Date range is required." });

            int ok = 0, fail = 0;
            string baseUrl = GetBaseUrl();

            // ── NEW: resolve reviewer names → EmpIds ONCE, before the loop ──
            // Mirrors Behaviour_Audit_Main.RM_ID/HOD_ID — never re-resolved by name at
            // notification time, which is what was silently dropping Rajeev's bell.
            using (var ensureCon = new SqlConnection(_conn))
            {
                ensureCon.Open();
                EnsureReviewerEmpIdColumns(ensureCon);
            }
            int rmEmpIdResolved = ResolveEmpIdByName(req.RmName);
            int hodEmpIdResolved = ResolveEmpIdByName(req.HodName);
            int uhEmpIdResolved = ResolveEmpIdByName(req.UnitHeadName);
            int jmdEmpIdResolved = ResolveEmpIdByName(req.JmdName);

            // ── DEDUP: resolve each mappingId to an EmpId, then process only once per EmpId ──
            var processedEmpIds = new HashSet<int>();

            foreach (int mappingId in req.MappingIds.Distinct())
            {
                try
                {
                    using var con = new SqlConnection(_conn);
                    con.Open();

                    string prevCycle = "", prevRm = "", prevHod = "", prevUh = "", prevMod = "", prevJmd = "";
                    string prevStatus = "Draft";
                    int prevEmpId = 0;
                    string prevUserId = "";
                    bool foundInAppraisalTable = false;

                    // ── Step 1: Try EmpAppraisalMapping first ──
                    using (var prev = new SqlCommand(@"
                    SELECT ISNULL(AppraisalCycle,'')     AS AppraisalCycle,
                           ISNULL(RmName,'')             AS RmName,
                           ISNULL(HodName,'')            AS HodName,
                           ISNULL(UnitHeadName,'')       AS UnitHeadName,
                           ISNULL(ModCommitteeNames,'')  AS ModCommitteeNames,
                           ISNULL(JmdName,'')            AS JmdName,
                           ISNULL([Status],'Draft')      AS [Status],
                           EmpId,
                           ISNULL(UserId,'')             AS UserId
                    FROM   EmpAppraisalMapping
                    WHERE  MappingId = @Id AND IsDeleted = 0", con))
                    {
                        prev.Parameters.AddWithValue("@Id", mappingId);
                        using var dr = prev.ExecuteReader();
                        if (dr.Read())
                        {
                            foundInAppraisalTable = true;
                            prevCycle = dr["AppraisalCycle"].ToString() ?? "";
                            prevRm = dr["RmName"].ToString() ?? "";
                            prevHod = dr["HodName"].ToString() ?? "";
                            prevUh = dr["UnitHeadName"].ToString() ?? "";
                            prevMod = dr["ModCommitteeNames"].ToString() ?? "";
                            prevJmd = dr["JmdName"].ToString() ?? "";
                            prevStatus = dr["Status"].ToString() ?? "Draft";
                            prevEmpId = dr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["EmpId"]);
                            prevUserId = dr["UserId"].ToString() ?? "";
                        }
                    }

                    // ── Step 2: If not found, look up from EmpMappingMaster ──
                    if (!foundInAppraisalTable)
                    {
                        using var masterLookup = new SqlCommand(@"
                        SELECT mm.EmpId,
                               mm.UserId,
                               ISNULL(mm.RmName,'')            AS RmName,
                               ISNULL(mm.HodName,'')           AS HodName,
                               ISNULL(mm.UnitHeadName,'')      AS UnitHeadName,
                               ISNULL(mm.ModCommitteeNames,'') AS ModCommitteeNames,
                               ISNULL(mm.JmdName,'')           AS JmdName
                        FROM   EmpMappingMaster mm
                        WHERE  mm.MasterMappingId = @Id AND mm.IsDeleted = 0", con);
                        masterLookup.Parameters.AddWithValue("@Id", mappingId);
                        using var mdr = masterLookup.ExecuteReader();
                        if (mdr.Read())
                        {
                            prevEmpId = mdr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(mdr["EmpId"]);
                            prevUserId = mdr["UserId"].ToString() ?? "";
                            prevRm = mdr["RmName"].ToString() ?? "";
                            prevHod = mdr["HodName"].ToString() ?? "";
                            prevUh = mdr["UnitHeadName"].ToString() ?? "";
                            prevMod = mdr["ModCommitteeNames"].ToString() ?? "";
                            prevJmd = mdr["JmdName"].ToString() ?? "";
                        }

                        if (prevEmpId == 0) { fail++; continue; }
                    }
                   
                    // ── DEDUP GUARD: skip if we already processed this employee ──
                    if (!processedEmpIds.Add(prevEmpId))
                    {
                        // Already handled this employee in a previous iteration — skip silently
                        ok++; // count as success so the UI doesn't show a false failure
                        continue;
                    }

                    bool hasModJmd = ColExistsInTable(con, "EmpAppraisalMapping", "ModCommitteeNames");
                    bool hasRmCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmName");
                    bool hasHodCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodName");
                    bool hasUhCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadName");

                    // ── NEW: reviewer EmpId column existence checks ──
                    bool hasRmEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmEmpId");
                    bool hasHodEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodEmpId");
                    bool hasUhEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadEmpId");
                    bool hasJmdEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "JmdEmpId");

                    int effectiveMappingId = 0;

                    // Immediately after resolving prevEmpId (in both the found/not-found branches), add:
                    using (var strictDup = new SqlCommand(@"
    SELECT MappingId FROM EmpAppraisalMapping
    WHERE  EmpId = @E AND AppraisalYear = @Y AND AppraisalCycle = @C AND IsDeleted = 0
    ORDER BY MappingId DESC", con))
                    {
                        strictDup.Parameters.AddWithValue("@E", prevEmpId);
                        strictDup.Parameters.AddWithValue("@Y", req.AppraisalYear > 0 ? req.AppraisalYear : DateTime.Now.Year);
                        strictDup.Parameters.AddWithValue("@C", req.AppraisalCycle);
                        using var ddr = strictDup.ExecuteReader();
                        var foundIds = new List<int>();
                        while (ddr.Read()) foundIds.Add(Convert.ToInt32(ddr["MappingId"]));
                        ddr.Close();

                        if (foundIds.Count > 1)
                        {
                            // Multiple live rows already exist for the same emp/year/cycle — collapse to the newest,
                            // soft-delete the rest, before continuing (self-healing).
                            var keep = foundIds.Max();
                            var toRemove = foundIds.Where(x => x != keep).ToList(); 
                            if (toRemove.Any())
                            {
                                using var fix = new SqlCommand(
                                    $"UPDATE EmpAppraisalMapping SET IsDeleted=1, DeletedBy=@By, DeletedOn=GETDATE() " +
                                    $"WHERE MappingId IN ({string.Join(",", toRemove)})", con);
                                fix.Parameters.AddWithValue("@By", by);
                                fix.ExecuteNonQuery();
                            }
                        }
                    }
                    // ── Step 3: Check if EmpAppraisalMapping already exists for emp + year + cycle ──
                    using (var dupChk = new SqlCommand(@"
                        SELECT ISNULL(MIN(MappingId), 0)
                        FROM   EmpAppraisalMapping
                        WHERE  EmpId          = @E
                          AND  AppraisalYear  = @Y
                          AND  AppraisalCycle = @C
                          AND  IsDeleted      = 0", con))
                    {
                        dupChk.Parameters.AddWithValue("@E", prevEmpId);
                        dupChk.Parameters.AddWithValue("@Y", req.AppraisalYear > 0 ? req.AppraisalYear : DateTime.Now.Year);
                        dupChk.Parameters.AddWithValue("@C", req.AppraisalCycle);
                        var dupVal = dupChk.ExecuteScalar();
                        if (dupVal != null && dupVal != DBNull.Value)
                            effectiveMappingId = Convert.ToInt32(dupVal);
                    }

                    if (effectiveMappingId > 0)
                    {
                        // ── EXISTING row → UPDATE only ──
                        var sets = new System.Text.StringBuilder();
                        sets.Append("CycleFromDate=@From, CycleToDate=@To, AppraisalYear=@Year");
                        if (!req.IsEdit) sets.Append(", [Status]='Draft'");
                        if (hasModJmd) sets.Append(", ModCommitteeNames=@ModNames, JmdName=@JmdName");
                        if (hasRmCol) sets.Append(", RmName=@RmName");
                        if (hasHodCol) sets.Append(", HodName=@HodName");
                        if (hasUhCol) sets.Append(", UnitHeadName=@UhName");
                        // ── NEW: persist reviewer EmpId columns ──
                        if (hasRmEmpIdCol) sets.Append(", RmEmpId=@RmEmpId");
                        if (hasHodEmpIdCol) sets.Append(", HodEmpId=@HodEmpId");
                        if (hasUhEmpIdCol) sets.Append(", UnitHeadEmpId=@UhEmpId");
                        if (hasJmdEmpIdCol) sets.Append(", JmdEmpId=@JmdEmpId");
                        sets.Append(", UpdatedBy=@By, UpdatedAt=GETDATE()");

                        using var updExisting = new SqlCommand(
                            $"UPDATE EmpAppraisalMapping SET {sets} WHERE MappingId=@Mid AND IsDeleted=0", con);
                        updExisting.Parameters.AddWithValue("@From", DateTime.TryParse(req.CycleFromDate, out var fd2) ? (object)fd2 : DBNull.Value);
                        updExisting.Parameters.AddWithValue("@To", DateTime.TryParse(req.CycleToDate, out var td2) ? (object)td2 : DBNull.Value);
                        updExisting.Parameters.AddWithValue("@Year", req.AppraisalYear > 0 ? req.AppraisalYear : DateTime.Now.Year);
                        if (hasModJmd) { updExisting.Parameters.AddWithValue("@ModNames", req.ModCommitteeNames ?? ""); 
                            updExisting.Parameters.AddWithValue("@JmdName", req.JmdName ?? ""); }
                        if (hasRmCol) updExisting.Parameters.AddWithValue("@RmName", req.RmName ?? "");
                        if (hasHodCol) updExisting.Parameters.AddWithValue("@HodName", req.HodName ?? "");
                        if (hasUhCol) updExisting.Parameters.AddWithValue("@UhName", req.UnitHeadName ?? "");
                        // ── NEW: EmpId parameters ──
                        if (hasRmEmpIdCol) updExisting.Parameters.AddWithValue("@RmEmpId", rmEmpIdResolved > 0 ? rmEmpIdResolved : (object)DBNull.Value);
                        if (hasHodEmpIdCol) updExisting.Parameters.AddWithValue("@HodEmpId", hodEmpIdResolved > 0 ? hodEmpIdResolved : (object)DBNull.Value);
                        if (hasUhEmpIdCol) updExisting.Parameters.AddWithValue("@UhEmpId", uhEmpIdResolved > 0 ? uhEmpIdResolved : (object)DBNull.Value);
                        if (hasJmdEmpIdCol) updExisting.Parameters.AddWithValue("@JmdEmpId", jmdEmpIdResolved > 0 ? jmdEmpIdResolved : (object)DBNull.Value);
                        updExisting.Parameters.AddWithValue("@By", by);
                        updExisting.Parameters.AddWithValue("@Mid", effectiveMappingId);
                        updExisting.ExecuteNonQuery();

                        if (!req.IsEdit)
                        {
                            using var rkCmd = new SqlCommand(@"
                            UPDATE EmpAppraisalKpiRows
                            SET    EmpActual=NULL,          EmpRating=NULL,
                                   RmActual=NULL,           RmRating=NULL,           RmRemarks=NULL,
                                   HodActual=NULL,          HodRating=NULL,          HodRemarks=NULL,
                                   UnitHeadActual=NULL,     UnitHeadRating=NULL,     UnitHeadRemarks=NULL,
                                   ModCommitteeActual=NULL, ModCommitteeRating=NULL, ModCommitteeRemarks=NULL,
                                   JmdActual=NULL,          JmdRating=NULL,          JmdRemarks=NULL,
                                   HrFinalRating=NULL,      WeightedRating=NULL
                            WHERE  MappingId = @Id AND IsDeleted = 0", con);
                            rkCmd.Parameters.AddWithValue("@Id", effectiveMappingId);
                            rkCmd.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        // ── No existing row → INSERT ──
                        var insColsSb = new System.Text.StringBuilder();
                        var insValsSb = new System.Text.StringBuilder();
                        insColsSb.Append("EmpId, UserId, AppraisalYear, AppraisalCycle, CycleFromDate, CycleToDate, [Status], CreatedBy, CreatedAt, IsDeleted");
                        insValsSb.Append("@EmpId, @UserId, @Year, @Cycle, @From, @To, 'Draft', @By, GETDATE(), 0");
                        if (hasModJmd) { insColsSb.Append(", ModCommitteeNames, JmdName"); insValsSb.Append(", @ModNames, @JmdName"); }
                        if (hasRmCol) { insColsSb.Append(", RmName"); insValsSb.Append(", @RmName"); }
                        if (hasHodCol) { insColsSb.Append(", HodName"); insValsSb.Append(", @HodName"); }
                        if (hasUhCol) { insColsSb.Append(", UnitHeadName"); insValsSb.Append(", @UhName"); }
                        // ── NEW: EmpId columns on insert ──
                        if (hasRmEmpIdCol) { insColsSb.Append(", RmEmpId"); insValsSb.Append(", @RmEmpId"); }
                        if (hasHodEmpIdCol) { insColsSb.Append(", HodEmpId"); insValsSb.Append(", @HodEmpId"); }
                        if (hasUhEmpIdCol) { insColsSb.Append(", UnitHeadEmpId"); insValsSb.Append(", @UhEmpId"); }
                        if (hasJmdEmpIdCol) { insColsSb.Append(", JmdEmpId"); insValsSb.Append(", @JmdEmpId"); }

                        using var ins = new SqlCommand(
                            $"INSERT INTO EmpAppraisalMapping ({insColsSb}) OUTPUT INSERTED.MappingId VALUES ({insValsSb})", con);
                        ins.Parameters.AddWithValue("@EmpId", prevEmpId);
                        ins.Parameters.AddWithValue("@UserId", prevUserId);
                        ins.Parameters.AddWithValue("@Year", req.AppraisalYear > 0 ? req.AppraisalYear : DateTime.Now.Year);
                        ins.Parameters.AddWithValue("@Cycle", req.AppraisalCycle);
                        ins.Parameters.AddWithValue("@From", DateTime.TryParse(req.CycleFromDate, out var fdIns) ? (object)fdIns : DBNull.Value);
                        ins.Parameters.AddWithValue("@To", DateTime.TryParse(req.CycleToDate, out var tdIns) ? (object)tdIns : DBNull.Value);
                        ins.Parameters.AddWithValue("@By", by);
                        if (hasModJmd) { ins.Parameters.AddWithValue("@ModNames", req.ModCommitteeNames ?? ""); ins.Parameters.AddWithValue("@JmdName", req.JmdName ?? ""); }
                        if (hasRmCol) ins.Parameters.AddWithValue("@RmName", req.RmName ?? "");
                        if (hasHodCol) ins.Parameters.AddWithValue("@HodName", req.HodName ?? "");
                        if (hasUhCol) ins.Parameters.AddWithValue("@UhName", req.UnitHeadName ?? "");
                        // ── NEW: EmpId parameters on insert ──
                        if (hasRmEmpIdCol) ins.Parameters.AddWithValue("@RmEmpId", rmEmpIdResolved > 0 ? rmEmpIdResolved : (object)DBNull.Value);
                        if (hasHodEmpIdCol) ins.Parameters.AddWithValue("@HodEmpId", hodEmpIdResolved > 0 ? hodEmpIdResolved : (object)DBNull.Value);
                        if (hasUhEmpIdCol) ins.Parameters.AddWithValue("@UhEmpId", uhEmpIdResolved > 0 ? uhEmpIdResolved : (object)DBNull.Value);
                        if (hasJmdEmpIdCol) ins.Parameters.AddWithValue("@JmdEmpId", jmdEmpIdResolved > 0 ? jmdEmpIdResolved : (object)DBNull.Value);

                        var insResult = ins.ExecuteScalar();
                        if (insResult == null || insResult == DBNull.Value) { fail++; continue; }
                        effectiveMappingId = Convert.ToInt32(insResult);

                        // Copy KPIs from master
                        using var copyKpi = new SqlCommand(@"
                        IF EXISTS (
                            SELECT 1 FROM EmpMappingMasterKpi mk
                            JOIN EmpMappingMaster mm ON mm.MasterMappingId = mk.MasterMappingId
                            WHERE mm.EmpId = @EmpId AND mk.IsDeleted = 0
                        )
                        BEGIN
                            INSERT INTO EmpAppraisalKpiRows
                                (MappingId, KpiId, PmpId, KraId, KpiName, UnitOfMeasure,
                                 IndividualWeightage, OverallWeightage, Target,
                                 IsDeleted, CreatedBy, CreatedAt)
                            SELECT @NewId, mk.KpiId, mk.PmpId, mk.KraId, mk.KpiName, mk.UnitOfMeasure,
                                   mk.IndividualWeightage, 0, mk.Target,
                                   0, @By, GETDATE()
                            FROM EmpMappingMasterKpi mk
                            JOIN EmpMappingMaster mm ON mm.MasterMappingId = mk.MasterMappingId
                            WHERE mm.EmpId = @EmpId AND mk.IsDeleted = 0
                            ORDER BY mk.SortOrder
                        END
                        ELSE
                        BEGIN
                            INSERT INTO EmpAppraisalKpiRows
                                (MappingId, KpiId, PmpId, KraId, KpiName, UnitOfMeasure,
                                 IndividualWeightage, OverallWeightage, Target,
                                 IsDeleted, CreatedBy, CreatedAt)
                            SELECT @NewId, KpiId, PmpId, KraId, KpiName, UnitOfMeasure,
                                   IndividualWeightage, OverallWeightage, Target,
                                   0, @By, GETDATE()
                            FROM EmpAppraisalKpiRows
                            WHERE MappingId = @OldId AND IsDeleted = 0
                        END", con);
                        copyKpi.Parameters.AddWithValue("@NewId", effectiveMappingId);
                        copyKpi.Parameters.AddWithValue("@OldId", mappingId);
                        copyKpi.Parameters.AddWithValue("@EmpId", prevEmpId);
                        copyKpi.Parameters.AddWithValue("@By", by);
                        copyKpi.ExecuteNonQuery();
                    }

                    ok++;

                    // ── Background: BA sync + email (unchanged) ────────────────────────
                    int capturedId = effectiveMappingId;
                    string capturedBase = baseUrl;
                    string capturedConn = _conn;
                    string capturedPrevStatus = prevStatus;
                    string capPrevRm = prevRm;
                    string capPrevHod = prevHod;
                    string capPrevUh = prevUh;
                    string capPrevMod = prevMod;
                    string capPrevJmd = prevJmd;
                    string newRm = req.RmName ?? "";
                    string newHod = req.HodName ?? "";
                    string newUh = req.UnitHeadName ?? "";
                    string newMod = req.ModCommitteeNames ?? "";
                    string newJmd = req.JmdName ?? "";
                    IEmailService capturedEmail = _email;
                    ILogger<PmsController> capturedLog = _log;
                    bool capturedIsEdit = req.IsEdit;
                    string capturedBy = by;
                    IPmsBaSyncService capturedBaSync = _baSync;

                    _ = Task.Run(async () =>
                    {
                        using var logCon = new SqlConnection(capturedConn);
                        try { await logCon.OpenAsync(); }
                        catch (Exception ex)
                        {
                            capturedLog.LogError(ex, "[PMS SyncLog] Could not open log connection for mappingId={Id}", capturedId);
                            return;
                        }

                        try
                        {
                            var mapping = GetMappingByIdInternal(capturedConn, capturedId);
                            if (mapping == null)
                            {
                                await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, 0, null, 0, "ERROR", null,
                                    "GetMappingByIdInternal returned null", null, null, capturedBy, false, "Mapping not found");
                                return;
                            }

                            await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName, 0,
                                capturedIsEdit ? "CYCLE_EDITED" : "CYCLE_ASSIGNED", null,
                                $"Cycle={mapping.AppraisalCycle}, Year={mapping.AppraisalYear}, From={mapping.CycleFromDate:dd-MMM-yy}, To={mapping.CycleToDate:dd-MMM-yy}",
                                null,
                                $"RM={mapping.RmName}, HOD={mapping.HodName}, UH={mapping.UnitHeadName}, MC={mapping.ModCommitteeNames}, JMD={mapping.JmdName}",
                                capturedBy, true, null);

                            PmsBaSyncResult syncResult;
                            try { syncResult = await capturedBaSync.SyncReviewersAsync(mapping, capturedBy); }
                            catch (Exception ex)
                            {
                                capturedLog.LogError(ex, "[PMS-BA Sync] SyncReviewersAsync threw for mappingId={Id}", capturedId);
                                await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                    0, "ERROR", null, "SyncReviewersAsync threw an unhandled exception",
                                    null, null, capturedBy, false, ex.Message);
                                syncResult = new PmsBaSyncResult { AuditId = 0, Success = false, ErrorMsg = ex.Message };
                            }

                            int auditId = syncResult.AuditId;
                            if (auditId > 0)
                            {
                                try
                                {
                                    using var baLinkCon = new SqlConnection(capturedConn);
                                    await baLinkCon.OpenAsync();
                                    using var baLinkCmd = new SqlCommand(
                                        "UPDATE EmpAppraisalMapping SET BehaviourAuditId=@AuditId WHERE MappingId=@Mid AND IsDeleted=0",
                                        baLinkCon);
                                    baLinkCmd.Parameters.AddWithValue("@AuditId", auditId);
                                    baLinkCmd.Parameters.AddWithValue("@Mid", capturedId);
                                    await baLinkCmd.ExecuteNonQueryAsync();

                                    var cycleMapping = GetMappingByIdInternal(capturedConn, capturedId);
                                    string cycleName = cycleMapping?.AppraisalCycle ?? "";
                                    if (!string.IsNullOrWhiteSpace(cycleName))
                                    {
                                        using var auditTypeCon = new SqlConnection(capturedConn);
                                        await auditTypeCon.OpenAsync();
                                        using var auditTypeCmd = new SqlCommand(@"
                                            UPDATE Behaviour_Audit_Main
                                            SET    AuditType = @CycleName
                                            WHERE  AuditID   = @AuditId
                                              AND  (AuditType IS NULL OR AuditType = '' OR AuditType != @CycleName)",
                                            auditTypeCon);
                                        auditTypeCmd.Parameters.AddWithValue("@CycleName", cycleName);
                                        auditTypeCmd.Parameters.AddWithValue("@AuditId", auditId);
                                        await auditTypeCmd.ExecuteNonQueryAsync();

                                        await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                            auditId, "BA_AUDITTYPE_SET", null,
                                            $"AuditType set to '{cycleName}' on AuditID={auditId}",
                                            null, null, capturedBy, true, null);
                                    }

                                    await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                        auditId, "BA_LINK_SAVED", null,
                                        $"BehaviourAuditId={auditId} saved to MappingId={capturedId}",
                                        null, null, capturedBy, true, null);
                                }
                                catch (Exception ex)
                                {
                                    capturedLog.LogWarning(ex,
                                        "[PMS] Could not save BehaviourAuditId={AuditId} for mappingId={Id}", auditId, capturedId);
                                }
                            }

                            if (!syncResult.Success)
                                await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                    0, "ERROR", null,
                                    $"BA Sync failed — {syncResult.ErrorMsg}. Email will still be attempted.",
                                    null, null, capturedBy, false, syncResult.ErrorMsg);

                            string pmsUrl = $"{capturedBase}/Pms/MyAppraisal?mappingId={capturedId}";

                            if (!capturedIsEdit)
                            {
                                try
                                {
                                    await capturedEmail.SendAppraisalCycleAssignedAsync(mapping, pmsUrl, string.Empty, capturedBase); 

                                    string notificationMessage = $"This is testing mail send from PMS For {mapping.EmpName}";                                    
                                   
                                    using (var scope = _scopeFactory.CreateScope())
                                    {
                                        var notificationService =
                                            scope.ServiceProvider.GetRequiredService<INotificationService>();

                                        await notificationService.CreateNotificationAsync(
                                            mapping.UserId,
                                            capturedBy,
                                            capturedBy,
                                            notificationMessage,
                                            "PMS",
                                            "Appraisal",
                                            pmsUrl);
                                    }
                                    await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                        auditId, "EMAIL_SENT", "Employee",
                                        $"Cycle-assigned email sent. PmsUrl={pmsUrl}",
                                        null, null, capturedBy, true, null);
                                }
                                catch (Exception ex)
                                {
                                    capturedLog.LogError(ex, "[PMS Email] SendAppraisalCycleAssigned failed for mappingId={Id}", capturedId);
                                    await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                        auditId, "EMAIL_FAILED", "Employee",
                                        $"Cycle-assigned email FAILED. PmsUrl={pmsUrl}",
                                        null, null, capturedBy, false, ex.Message);
                                }

                                // ── Bell notification: Employee ─────────────────────────
                                // CHANGED: route through NotifyPmsStage — uses mapping.EmpId,
                                // never a name match, so this can never silently fail.
                                // ── Bell notification: Employee ─────────────────────────
                                try
                                {
                                    if (!string.IsNullOrWhiteSpace(mapping.UserId))
                                    {
                                        
                                        string notificationMessage = $"Appraisal cycle '{mapping.AppraisalCycle} {mapping.AppraisalYear}' has been assigned to you.";                                         
                                         
                                        using (var scope = _scopeFactory.CreateScope())
                                        {
                                            var notificationService =
                                                scope.ServiceProvider.GetRequiredService<INotificationService>();

                                            await notificationService.CreateNotificationAsync(
                                                mapping.UserId,
                                                capturedBy,
                                                capturedBy,
                                                notificationMessage,
                                                "PMS",
                                                "Appraisal",
                                                pmsUrl);
                                        }


                                    }
                                    else
                                    {
                                        NotifyPmsStage(mapping, "Employee", mapping.EmpId, mapping.EmpName, capturedBy, pmsUrl);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    capturedLog.LogWarning(ex, "[PMS Bell] Employee notification failed for mappingId={Id}", capturedId);
                                }
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(capturedPrevStatus) && capturedPrevStatus != "Draft")
                                {
                                    try
                                    {
                                        await TriggerReviewerChangeNotifications(
                                            capturedEmail, mapping, capturedBase, capturedPrevStatus,
                                            capPrevRm, capPrevHod, capPrevUh, capPrevMod, capPrevJmd,
                                            newRm, newHod, newUh, newMod, newJmd,
                                            capturedId, auditId);                                         
                                        string notificationMessage = $"This is testing mail send from PMS For Trigger Reviewer Change Notification {mapping.EmpName}";

                                        using (var scope = _scopeFactory.CreateScope())
                                        {
                                            var notificationService =
                                                scope.ServiceProvider.GetRequiredService<INotificationService>();

                                            await notificationService.CreateNotificationAsync(
                                                mapping.UserId,
                                                capturedBy,
                                                capturedBy,
                                                notificationMessage,
                                                "PMS",
                                                "Appraisal",
                                                pmsUrl);
                                        }

                                        await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                            auditId, "EMAIL_SENT", capturedPrevStatus,
                                            "Reviewer-change notification triggered",
                                            $"RM={capPrevRm}, HOD={capPrevHod}, UH={capPrevUh}, MC={capPrevMod}, JMD={capPrevJmd}",
                                            $"RM={newRm}, HOD={newHod}, UH={newUh}, MC={newMod}, JMD={newJmd}",
                                            capturedBy, true, null);
                                    }
                                    catch (Exception ex)
                                    {
                                        capturedLog.LogError(ex, "[PMS Email] TriggerReviewerChangeNotifications failed for mappingId={Id}", capturedId);
                                        await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                            auditId, "EMAIL_FAILED", capturedPrevStatus,
                                            "Reviewer-change notification FAILED",
                                            $"RM={capPrevRm}, HOD={capPrevHod}, UH={capPrevUh}",
                                            $"RM={newRm}, HOD={newHod}, UH={newUh}",
                                            capturedBy, false, ex.Message);
                                    }
                                }
                                else
                                {
                                    await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, mapping.EmpId, mapping.EmpName,
                                        auditId, "EMAIL_SKIPPED", null,
                                        $"IsEdit=true but Status='{capturedPrevStatus}' — no reviewer email sent",
                                        null, null, capturedBy, true, null);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            capturedLog.LogError(ex, "[PMS Task.Run] Unhandled exception for mappingId={Id}", capturedId);
                            try
                            {
                                await PmsBaSyncService.WriteSyncLogAsync(logCon, capturedId, 0, null, 0, "ERROR", null,
                                    "Task.Run outer catch — unhandled exception", null, null, capturedBy, false, ex.Message);
                            }
                            catch { }
                        }
                    });
                }
                catch { fail++; }
            }

            return Json(new
            {
                success = ok > 0,
                ok,
                fail,
                message = $"{ok} employee(s) notified" + (fail > 0 ? $" ({fail} failed)" : "") + "."
            });
        }

        // ══════════════════════════════════════════════════════════════
        // NOTIFICATION HELPERS
        // ══════════════════════════════════════════════════════════════

        
        // Persists the reviewer EmpId columns, mirroring Behaviour_Audit_Main.RM_ID/HOD_ID.
        // Once these are stored, no notification path ever needs to re-resolve a name.
        private void EnsureReviewerEmpIdColumns(SqlConnection con)
        {
            try
            {
                using var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='RmEmpId')
                ALTER TABLE EmpAppraisalMapping ADD RmEmpId INT NULL;
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='HodEmpId')
                ALTER TABLE EmpAppraisalMapping ADD HodEmpId INT NULL;
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='UnitHeadEmpId')
                ALTER TABLE EmpAppraisalMapping ADD UnitHeadEmpId INT NULL;
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='JmdEmpId')
                ALTER TABLE EmpAppraisalMapping ADD JmdEmpId INT NULL;", con);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // Whitespace/case-tolerant name → EmpId lookup. Only ever called once, at the
        // moment a reviewer is assigned/edited — never on the hot notification path.
        private int ResolveEmpIdByName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return 0;
            string target = System.Text.RegularExpressions.Regex.Replace(fullName.Trim(), @"\s+", " ");

            try
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();
                using var cmd = new SqlCommand(@"
            SELECT TOP 1 Emp_ID
            FROM   T0080_EMP_MASTER
            WHERE  Cmp_ID = 2
              AND  ISNULL(Emp_Left,'N') <> 'Y'
              AND  UPPER(LTRIM(RTRIM(REPLACE(
                       ISNULL(Emp_First_Name,'')+' '+ISNULL(Emp_Last_Name,''), '  ', ' '
                   )))) = UPPER(@Name)", con);
                cmd.Parameters.AddWithValue("@Name", target);
                var v = cmd.ExecuteScalar();
                if (v != null && v != DBNull.Value) return Convert.ToInt32(v);
            }
            catch { }
            return 0;
        }

        // Unified PMS stage notification. Resolves through the stored EmpId column
        // (never a UserId, never a name match at send-time — same pattern as
        // Behaviour_Audit_Main.RM_ID/HOD_ID). When a BA form is also pending for this
        // reviewer (RM/HOD), pass baUrl so the bell fires a SECOND notification
        // carrying the BA link, matching what the assignment emails already do.
        private void NotifyPmsStage(
            EmpKpiMappingModel mapping, string role, int? storedEmpId, string fallbackName,
            string by, string pmsUrl, string baUrl = null)
        {
            int targetEmpId = storedEmpId ?? 0;
            if (targetEmpId <= 0) targetEmpId = ResolveEmpIdByName(fallbackName);

            if (targetEmpId <= 0)
            {
                _log.LogWarning("[PMS Bell] Could not resolve EmpId for {Role} on mappingId={Id}",
                    role, mapping.MappingId);
                return;
            }

            // ── KEY FIX: resolve to AspNetUsers.Id (GUID), not EmpId ──
            string targetUserId = GetUserIdByEmpId_Fixed(targetEmpId);
            if (string.IsNullOrWhiteSpace(targetUserId))
            {
                _log.LogWarning("[PMS Bell] Could not resolve UserId for EmpId={EmpId} role={Role}",
                    targetEmpId, role);
                return;
            }

            string pmsMsg = role switch
            {
                "Employee" => $"Appraisal cycle '{mapping.AppraisalCycle} {mapping.AppraisalYear}' has been assigned to you.",
                "RM" => $"{mapping.EmpName}'s self-appraisal is pending your review as RM.",
                "HOD" => $"{mapping.EmpName}'s appraisal (RM review done) is pending your review as HOD.",
                "UnitHead" => $"{mapping.EmpName}'s appraisal (HOD review done) is pending your review as Unit Head.",
                _ => $"{mapping.EmpName}'s appraisal needs your attention."
            };

            try
            {
                // Pass UserId (GUID), not EmpId                
                _notificationService.CreateNotificationAsync(
                                        targetUserId,
                                        by,
                                        by,
                                        pmsMsg,
                                        "Info",
                                        "PMS",
                                        pmsUrl);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PMS Bell] PMS notification failed for {Role} on mappingId={Id}",
                    role, mapping.MappingId);
            }

            if (!string.IsNullOrWhiteSpace(baUrl))
            {
                try
                { 

                    _notificationService.CreateNotificationAsync(
                                       targetUserId,
                                       by,
                                       by,
                                       $"{mapping.EmpName}'s Behavioural Audit is also pending your scoring as {role}.",
                                       "HR_Behaviour_Audit_Notification",
                                       "Behaviour Audit",
                                       baUrl);



                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[PMS Bell] BA notification failed for {Role} on mappingId={Id}",
                        role, mapping.MappingId);
                }
            }
        }
        // ★ NEW — PLACEHOLDER, NEEDS YOUR INPUT:
        // RM/HOD/UnitHead/ModCommittee/JMD are stored on the mapping as plain name
        // strings (RmName, HodName, etc.), not AspNetUsers Ids, so bell notifications
        // for those roles need a name → UserId lookup. This guesses at a "FullName"
        // column on AspNetUsers — replace the SQL below with whatever your system
        // actually uses (ideally the SAME lookup SendToNextReviewer/
        // TriggerReviewerChangeNotifications already use internally to find an email
        // address, so the bell and the email always agree on who the recipient is).
        private string GetUserIdByEmpId_Fixed(int empId)
        {
            if (empId <= 0) return null;
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // Get Work_Email from HRMS T0080_EMP_MASTER using Emp_ID
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var orgCon = new SqlConnection(orgConn);
                orgCon.Open();

                using var hrmsCmd = new SqlCommand(
                    "SELECT TOP 1 ISNULL(Work_Email,'') FROM T0080_EMP_MASTER " +
                    "WHERE Emp_ID = @EmpId AND Cmp_ID = 2", orgCon);
                hrmsCmd.Parameters.AddWithValue("@EmpId", empId);
                var emailVal = hrmsCmd.ExecuteScalar();
                string workEmail = emailVal?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(workEmail)) return null;

                // Match email against UserDetails.Email to get UserId
                using var udCmd = new SqlCommand(
                    "SELECT TOP 1 ISNULL(UserId,'') FROM UserDetails " +
                    "WHERE Email = @Email AND LEN(ISNULL(UserId,'')) > 10", con);
                udCmd.Parameters.AddWithValue("@Email", workEmail.Trim());
                var udVal = udCmd.ExecuteScalar();
                if (udVal != null && udVal != DBNull.Value &&
                    !string.IsNullOrWhiteSpace(udVal.ToString()))
                    return udVal.ToString();

                return null;
            }
            catch { return null; }
        }

        // ====================================================
        // REVIEWER CHANGE NOTIFICATIONS
        // ====================================================
        private async Task TriggerReviewerChangeNotifications(
             IEmailService emailService, EmpKpiMappingModel mapping, string baseUrl, string currentStatus,
             string prevRm, string prevHod, string prevUh, string prevMod, string prevJmd,
             string newRm, string newHod, string newUh, string newMod, string newJmd,
             int mappingId, int auditId)
        {
            // Only the three active reviewer stages; MC/JMD removed.
            var pipeline = new[]
            {
                new ReviewerStage("EmployeeFilled", prevRm,  newRm,  $"/Pms/RmAppraisal?mappingId={mappingId}",       "RM"),
                new ReviewerStage("RMReview",       prevHod, newHod, $"/Pms/HodAppraisal?mappingId={mappingId}",      "HOD"),
                new ReviewerStage("HODReview",      prevUh,  newUh,  $"/Pms/UnitHeadAppraisal?mappingId={mappingId}", "UnitHead"),
            };

            int currentStageIndex = currentStatus switch
            {
                "Draft" => -1,
                "EmployeeFilled" => 0,
                "RMReview" => 1,
                "HODReview" => 2,
                "UnitHeadReview" => 3,
                _ => -1
            };

            if (currentStageIndex < 0) return;

            for (int i = currentStageIndex; i < pipeline.Length; i++)
            {
                var stage = pipeline[i];
                string prev = (stage.PrevName ?? "").Trim();
                string next = (stage.NewName ?? "").Trim();
                bool nowEmpty = string.IsNullOrWhiteSpace(next);
                bool nameChanged = !string.Equals(prev, next, StringComparison.OrdinalIgnoreCase);
                bool wasEmpty = string.IsNullOrWhiteSpace(prev);
                if (nowEmpty) continue;
                if (nameChanged || wasEmpty)
                {
                    string fullPmsUrl = $"{baseUrl}{stage.ReviewUrl}";
                    if (stage.ActionRole == "UnitHead")
                    {
                        // UH: PMS only, no BA url
                        await emailService.SendUnitHeadReviewNotificationAsync(mapping, fullPmsUrl, baseUrl);
                    }
                    else
                    {
                        string baUrl = GetBaUrlForRole(baseUrl, auditId, stage.ActionRole);
                        await SendReviewerNotificationByRole(emailService, mapping, baseUrl, stage.ActionRole, fullPmsUrl, baUrl);
                    }
                    return;
                }
                return;
            }
        }


        private async Task SendReviewerNotificationByRole(
            IEmailService emailService, EmpKpiMappingModel mapping, string baseUrl,
            string role, string pmsUrl, string behaviouralAuditUrl)
        {
            switch (role)
            {
                case "RM":
                    await emailService.SendRmReviewNotificationAsync(
                        mapping, pmsUrl, behaviouralAuditUrl, baseUrl);
                    break;
                case "HOD":
                    await emailService.SendHodReviewNotificationAsync(
                        mapping, pmsUrl, behaviouralAuditUrl, baseUrl);
                    break;
                case "UnitHead":
                    // UH gets PMS link only — no BA url parameter
                    await emailService.SendUnitHeadReviewNotificationAsync(
                        mapping, pmsUrl, baseUrl);
                    break;
                // ModCommittee and JMD: deliberately no email
                default:
                    break;
            }
        }
        private record ReviewerStage(string StatusThatMeansThisIsNext, string PrevName, string NewName, string ReviewUrl, string ActionRole);

        // ====================================================
        // SEND TO NEXT REVIEWER IN CHAIN
        // ====================================================
        private async Task<string> SendToNextReviewer(
     IEmailService emailService, EmpKpiMappingModel mapping, string baseUrl,
     int mappingId, string justCompletedRole, string connStr)
        {
            int auditId = GetBehaviouralAuditId(connStr, mapping.EmpId, mappingId);

            var fullChain = new[]
            {
        ("Employee", mapping.RmName,       $"{baseUrl}/Pms/RmAppraisal?mappingId={mappingId}",       "RM"),
        ("RM",       mapping.HodName,      $"{baseUrl}/Pms/HodAppraisal?mappingId={mappingId}",      "HOD"),
        ("HOD",      mapping.UnitHeadName, $"{baseUrl}/Pms/UnitHeadAppraisal?mappingId={mappingId}", "UnitHead"),
    };

            foreach (var (triggerRole, name, pmsUrl, action) in fullChain)
            {
                if (!string.Equals(justCompletedRole, triggerRole, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(name))
                {
                    await SkipReviewStageInDb(mappingId, action);
                    string nextRole = action switch
                    {
                        "RM" => "RM",
                        "HOD" => "HOD",
                        "UnitHead" => "UnitHead",
                        _ => null
                    };
                    if (nextRole != null)
                        return await SendToNextReviewer(emailService, mapping, baseUrl, mappingId, nextRole, connStr);
                    break;
                }

                if (action == "UnitHead")
                {
                    await emailService.SendUnitHeadReviewNotificationAsync(mapping, pmsUrl, baseUrl);
                }
                else
                {
                    string baUrl = GetBaUrlForRole(baseUrl, auditId, action);
                    await SendReviewerNotificationByRole(emailService, mapping, baseUrl, action, pmsUrl, baUrl);
                }

                // ── LOG the send so reminder service won't re-fire ──────────────
                try
                {
                    using var logCon = new SqlConnection(connStr);
                    await logCon.OpenAsync();
                    await PmsBaSyncService.WriteSyncLogAsync(
                        logCon,
                        mappingId: mappingId,
                        empId: mapping.EmpId,
                        empName: mapping.EmpName,
                        auditId: auditId,
                        actionType: "EMAIL_SENT",
                        stage: action,
                        details: $"[PmsController] {action} review email sent. PmsUrl={pmsUrl}",
                        oldValue: null,
                        newValue: null,
                        triggeredBy: "PmsController",
                        isSuccess: true,
                        errorMessage: null);
                }
                catch { /* never throw from logging */ }

                return pmsUrl;
            }

            // All reviewer stages complete — notify HR
            await emailService.SendHrFinalizeNotificationAsync(mapping, baseUrl);
            return null;
        }

        // ── After SendToNextReviewer or email calls, add notifications ──

        // Helper to get UserId from EmpId (add this private method to PmsController)
        private string GetUserIdByEmpId(int empId)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(
                    "SELECT TOP 1 ISNULL(UserId,'') FROM EmpAppraisalMapping " +
                    "WHERE EmpId=@EmpId AND IsDeleted=0 ORDER BY MappingId DESC", con);
                cmd.Parameters.AddWithValue("@EmpId", empId);
                var v = cmd.ExecuteScalar();
                return v?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // Helper to get EmpId by name from HRMS (add this)
        private int GetEmpIdByName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return 0;
            try
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();
                using var cmd = new SqlCommand(@"
            SELECT TOP 1 Emp_ID FROM T0080_EMP_MASTER
            WHERE LTRIM(RTRIM(ISNULL(Emp_First_Name,'')+' '+ISNULL(Emp_Last_Name,''))) = @Name
            AND Cmp_ID = 2", con);
                cmd.Parameters.AddWithValue("@Name", fullName.Trim());
                var v = cmd.ExecuteScalar();
                return v != null && v != DBNull.Value ? Convert.ToInt32(v) : 0;
            }
            catch { return 0; }
        }
        private async Task SkipReviewStageInDb(int mappingId, string skippedRole)
        {
            string newStatus = skippedRole switch
            {
                "RM" => "RMReview",
                "HOD" => "HODReview",
                "UnitHead" => "UnitHeadReview",
                "ModCommittee" => "ModCommitteeReview",
                _ => null
            };
            if (newStatus == null) return;
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(@"
                    UPDATE EmpAppraisalMapping
                    SET    [Status]  = @Status, UpdatedAt = GETDATE()
                    WHERE  MappingId = @Id AND IsDeleted = 0", con);
                cmd.Parameters.AddWithValue("@Status", newStatus);
                cmd.Parameters.AddWithValue("@Id", mappingId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ====================================================
        // SUBMIT APPRAISAL
        // ====================================================
        // ====================================================
        [HttpPost]
        public async Task<IActionResult> SubmitAppraisalFill([FromBody] AppraisalFillRequest req)
        {
            if (req == null || req.MappingId <= 0)
                return Json(new { success = false, message = "Invalid mapping." });

            var user = await _userManager.GetUserAsync(User);
            string by = user.Id;

            // ── Ownership check before allowing any submission ──
            var mappingForAuth = GetMappingByIdInternal(_conn, req.MappingId);
            if (mappingForAuth == null)
                return Json(new { success = false, message = "Mapping not found." });

            if (req.FillerRole == "ModCommittee")
                mappingForAuth.ModCommitteeEmpIds = GetModCommitteeEmpIdsForMapping(_conn, req.MappingId);

            string checkRole = req.FillerRole switch
            {
                "UnitHead" => "UnitHead",
                _ => req.FillerRole // Employee, RM, HOD, ModCommittee, JMD line up directly
            };
            var (authorized, _, _) = await VerifyReviewerAccessAsync(mappingForAuth, checkRole);
            if (!authorized)
                return Json(new { success = false, message = "You are not authorized to submit this appraisal." });

            // ...rest of existing method unchanged...
            if (req == null || req.MappingId <= 0)
                return Json(new { success = false, message = "Invalid mapping." });

            bool isMcOrJmd = req.FillerRole == "ModCommittee" || req.FillerRole == "JMD";

            if (!isMcOrJmd && (req.Rows == null || !req.Rows.Any()))
                return Json(new { success = false, message = "No data submitted." });

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                string allowed = req.FillerRole switch
                {
                    "Employee" => "Draft",
                    "RM" => "EmployeeFilled",
                    "HOD" => "RMReview",
                    "UnitHead" => "HODReview",
                    "ModCommittee" => "UnitHeadReview",
                    "JMD" => "ModCommitteeReview",
                    _ => ""
                };

                string resultStatus = req.FillerRole switch
                {
                    "Employee" => "EmployeeFilled",
                    "RM" => "RMReview",
                    "HOD" => "HODReview",
                    "UnitHead" => "UnitHeadReview",
                    _ => null
                };

                string currentStatus;
                bool isReEdit = false;
                using (var chk = new SqlCommand(
                    "SELECT [Status] FROM EmpAppraisalMapping WHERE MappingId=@Id AND IsDeleted=0", con))
                {
                    chk.Parameters.AddWithValue("@Id", req.MappingId);
                    currentStatus = chk.ExecuteScalar()?.ToString();

                    bool isFirstSubmission = currentStatus == allowed;
                    bool isReEditableStage = resultStatus != null && currentStatus == resultStatus;

                    if (!isFirstSubmission && !isReEditableStage)
                        return Json(new { success = false, message = $"Not in correct stage for {req.FillerRole}. Current status: {currentStatus}" });

                    isReEdit = isReEditableStage;
                }
                if (req.FillerRole == "ModCommittee")
                {
                    if (req.McOverallRating == null || req.McOverallRating <= 0 || req.McOverallRating > 5)
                        return Json(new { success = false, message = "Please provide a valid MC Overall Rating (0.01 – 5.00)." });

                    if (req.Rows != null && req.Rows.Any())
                    {
                        foreach (var row in req.Rows)
                        {
                            using var updRow = new SqlCommand(@"
                            UPDATE EmpAppraisalKpiRows
                            SET    ModCommitteeRemarks = @Remarks, UpdatedBy = @By, UpdatedAt = GETDATE()
                            WHERE  EmpKpiRowId = @RowId AND MappingId = @MappingId AND IsDeleted = 0", con);
                            updRow.Parameters.AddWithValue("@Remarks", (object?)(row.Remarks) ?? DBNull.Value);
                            updRow.Parameters.AddWithValue("@By", by);
                            updRow.Parameters.AddWithValue("@RowId", row.EmpKpiRowId);
                            updRow.Parameters.AddWithValue("@MappingId", req.MappingId);
                            updRow.ExecuteNonQuery();
                        }
                    }

                    using var updMapping = new SqlCommand(@"
                            UPDATE EmpAppraisalMapping
                            SET    [Status]                   = 'ModCommitteeReview',
                                   ModCommitteeAvgRating      = @AvgRating,
                                   ModCommitteeOverallRemarks = @OverallRemarks,
                                   ModCommitteeSubmittedOn    = GETDATE(),
                                   UpdatedBy                  = @By,
                                   UpdatedAt                  = GETDATE()
                            WHERE  MappingId = @Id AND IsDeleted = 0", con);
                    updMapping.Parameters.AddWithValue("@AvgRating", req.McOverallRating);
                    updMapping.Parameters.AddWithValue("@OverallRemarks", (object?)(req.McOverallRemarks) ?? DBNull.Value);
                    updMapping.Parameters.AddWithValue("@By", by);
                    updMapping.Parameters.AddWithValue("@Id", req.MappingId);
                    updMapping.ExecuteNonQuery();
                }
                else if (req.FillerRole == "JMD")
                {
                    if (req.JmdOverallRating == null || req.JmdOverallRating <= 0 || req.JmdOverallRating > 5)
                        return Json(new { success = false, message = "Please provide a valid JMD Overall Rating (0.01 – 5.00)." });

                    if (req.Rows != null && req.Rows.Any())
                    {
                        foreach (var row in req.Rows)
                        {
                            using var updRow = new SqlCommand(@"
                    UPDATE EmpAppraisalKpiRows
                    SET    JmdRemarks = @Remarks, UpdatedBy = @By, UpdatedAt = GETDATE()
                    WHERE  EmpKpiRowId = @RowId AND MappingId = @MappingId AND IsDeleted = 0", con);
                            updRow.Parameters.AddWithValue("@Remarks", (object?)(row.Remarks) ?? DBNull.Value);
                            updRow.Parameters.AddWithValue("@By", by);
                            updRow.Parameters.AddWithValue("@RowId", row.EmpKpiRowId);
                            updRow.Parameters.AddWithValue("@MappingId", req.MappingId);
                            updRow.ExecuteNonQuery();
                        }
                    }

                    using var updMapping = new SqlCommand(@"
                    UPDATE EmpAppraisalMapping
                    SET    [Status]           = 'JMDReview',
                           JmdAvgRating       = @JmdRating,
                           JmdFinalBscScore   = @JmdRating,
                           JmdFinalBscRemarks = @JmdRemarks,
                           JmdOverallRemarks  = @JmdRemarks,
                           JmdSubmittedOn     = GETDATE(),
                           UpdatedBy          = @By,
                           UpdatedAt          = GETDATE()
                    WHERE  MappingId = @Id AND IsDeleted = 0", con);
                    updMapping.Parameters.AddWithValue("@JmdRating", req.JmdOverallRating);
                    updMapping.Parameters.AddWithValue("@JmdRemarks", (object?)(req.JmdOverallRemarks) ?? DBNull.Value);
                    updMapping.Parameters.AddWithValue("@By", by);
                    updMapping.Parameters.AddWithValue("@Id", req.MappingId);
                    updMapping.ExecuteNonQuery();
                }
                else
                {
                    // ── Employee / RM / HOD / UnitHead path ──────────────────────────

                    if (req.Rows == null || !req.Rows.Any())
                        return Json(new { success = false, message = "No data submitted." });

                    var expectedRowIds = new List<int>();
                    using (var fetchRows = new SqlCommand(
                        "SELECT EmpKpiRowId FROM EmpAppraisalKpiRows WHERE MappingId=@Id AND IsDeleted=0", con))
                    {
                        fetchRows.Parameters.AddWithValue("@Id", req.MappingId);
                        using var dr = fetchRows.ExecuteReader();
                        while (dr.Read()) expectedRowIds.Add(Convert.ToInt32(dr["EmpKpiRowId"]));
                    }

                    var submittedIds = req.Rows.Select(r => r.EmpKpiRowId).ToHashSet();
                    var missingIds = expectedRowIds.Where(id => !submittedIds.Contains(id)).ToList();
                    if (missingIds.Any())
                        return Json(new { success = false, message = $"Please fill all {expectedRowIds.Count} KPI(s). {missingIds.Count} KPI(s) missing." });

                    var missingActual = req.Rows.Where(r => r.Actual == null).ToList();
                    if (missingActual.Any())
                        return Json(new { success = false, message = $"Actual value missing for {missingActual.Count} KPI(s)." });

                    var badRatings = req.Rows.Where(r => r.Rating < 1 || r.Rating > 5).ToList();
                    if (badRatings.Any())
                        return Json(new { success = false, message = $"Rating must be 1–5. {badRatings.Count} KPI(s) have invalid rating." });

                    string spName = req.FillerRole switch
                    {
                        "Employee" => "sp_EmpAppraisal_FillByEmployee",
                        "RM" => "sp_EmpAppraisal_FillByRM",
                        "HOD" => "sp_EmpAppraisal_FillByHOD",
                        "UnitHead" => "sp_EmpAppraisal_FillByUnitHead",
                        _ => null
                    };
                    if (spName == null)
                        return Json(new { success = false, message = "Invalid filler role." });

                    var rowsJson = JsonSerializer.Serialize(req.Rows.Select(r => new
                    {
                        EmpKpiRowId = r.EmpKpiRowId,
                        Actual = r.Actual,
                        Rating = r.Rating,
                        Remarks = r.Remarks ?? ""
                    }));

                    using var cmd = new SqlCommand(spName, con) { CommandType = CommandType.StoredProcedure };
                    cmd.Parameters.AddWithValue("@MappingId", req.MappingId);
                    cmd.Parameters.AddWithValue("@By", by);
                    cmd.Parameters.AddWithValue("@Rows", rowsJson);
                    cmd.ExecuteNonQuery();
                     

                    _notificationService.CreateNotificationAsync(
                        "6a42038c-a0ae-4c83-9cd0-00b0b7ddfe09",
                        by,
                        by,
                       $"TEST NOTIFICATION for false ",
                        "Info",
                        "PMS",
                        "/Pms/EmployeeDashboard");
                    // ── Audit log + re-stamp submission timestamp on RE-EDIT ──────────
                    if (isReEdit)
                    {
                        string editIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
                        LogPasChangeDetailed(con, "EmpAppraisalMapping", req.MappingId,
                            "PMS_REEDIT", req.FillerRole,
                            null,
                            $"{req.FillerRole} re-edited their submission while status was '{currentStatus}'.",
                            by, editIp);

                        string submittedOnCol = req.FillerRole switch
                        {
                            "Employee" => "EmpSubmittedOn",
                            "RM" => "RmSubmittedOn",
                            "HOD" => "HodSubmittedOn",
                            "UnitHead" => "UnitHeadSubmittedOn",
                            _ => null
                        };
                        if (submittedOnCol != null && ColExistsInTable(con, "EmpAppraisalMapping", submittedOnCol))
                        {
                            using var stampCmd = new SqlCommand(
                                $"UPDATE EmpAppraisalMapping SET {submittedOnCol} = GETDATE() WHERE MappingId=@Id AND IsDeleted=0", con);
                            stampCmd.Parameters.AddWithValue("@Id", req.MappingId);
                            stampCmd.ExecuteNonQuery();
                        }
                    }

                    // ── Stamp PMS submission flags + timestamps synchronously ─────────
                    if (req.FillerRole == "RM" || req.FillerRole == "HOD")
                    {
                        try
                        {
                            string pmsFlag = req.FillerRole == "RM" ? "RMSubmittedPMS" : "HODSubmittedPMS";
                            string pmsFlagAt = req.FillerRole == "RM" ? "RMSubmittedPMSAt" : "HODSubmittedPMSAt";

                            using (var ensureCol = new SqlCommand($@"
                            IF NOT EXISTS (
                                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                                WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='{pmsFlagAt}'
                            )
                            ALTER TABLE EmpAppraisalMapping ADD {pmsFlagAt} DATETIME NULL", con))
                                ensureCol.ExecuteNonQuery();

                            bool flagExists = false;
                            using (var cc = new SqlCommand(
                                "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                                "WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME=@col", con))
                            {
                                cc.Parameters.AddWithValue("@col", pmsFlag);
                                flagExists = (int)cc.ExecuteScalar() > 0;
                            }

                            var setParts = new List<string>
                            {
                                $"{pmsFlagAt} = GETDATE()",
                                "UpdatedAt = GETDATE()",
                                "UpdatedBy = @By"
                            };
                            if (flagExists) setParts.Add($"{pmsFlag} = 1");

                            using var flagCmd = new SqlCommand(
                                $"UPDATE EmpAppraisalMapping SET {string.Join(", ", setParts)} " +
                                "WHERE MappingId=@Mid AND IsDeleted=0", con);
                            flagCmd.Parameters.AddWithValue("@By", by);
                            flagCmd.Parameters.AddWithValue("@Mid", req.MappingId);
                            flagCmd.ExecuteNonQuery();
                        }
                        catch { /* non-fatal — must never block the PMS submission */ }
                    }

                    if (req.FillerRole == "Employee" || req.FillerRole == "UnitHead")
                    {
                        using var touchCmd = new SqlCommand(@"
                        UPDATE EmpAppraisalMapping
                        SET    UpdatedAt = GETDATE(),
                               UpdatedBy = @By
                        WHERE  MappingId = @Id AND IsDeleted = 0", con);
                        touchCmd.Parameters.AddWithValue("@By", by);
                        touchCmd.Parameters.AddWithValue("@Id", req.MappingId);
                        touchCmd.ExecuteNonQuery();
                    }

                } // ── end else (Employee/RM/HOD/UnitHead) ─────────────────────────────

                string nextStep = req.FillerRole switch
                {
                    "Employee" => "Self appraisal submitted to Reporting Manager.",
                    "RM" => "RM review submitted. Forwarded to HOD.",
                    "HOD" => "HOD review submitted. Forwarded to Unit Head.",
                    "UnitHead" => "Unit Head review submitted. Forwarded to Moderation Committee.",
                    "ModCommittee" => "Moderation Committee review submitted. Forwarded to JMD.",
                    "JMD" => "JMD review complete. Appraisal forwarded to HR.",
                    _ => "Submitted."
                };

                string redirectUrl = req.FillerRole switch
                {
                    "Employee" => Url.Action("EmployeeDashboard", "Pms"),
                    "RM" => Url.Action("RmDashboard", "Pms"),
                    "HOD" => Url.Action("HodDashboard", "Pms"),
                    "UnitHead" => Url.Action("UnitHeadDashboard", "Pms"),
                    "ModCommittee" => Url.Action("ModCommitteeDashboard", "Pms"),
                    "JMD" => Url.Action("JmdDashboard", "Pms"),
                    _ => Url.Action("Index", "Pms")
                };

                if (!isReEdit)
                {
                    string baseUrl = GetBaseUrl();
                    int capturedMappingId = req.MappingId;
                    string capturedRole = req.FillerRole;
                    string capturedConn = _conn;
                    IEmailService capturedEmail = _email;
                    ILogger<PmsController> capturedLog = _log;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var mapping = GetMappingByIdInternal(capturedConn, capturedMappingId);
                            if (mapping == null)
                            {
                                capturedLog.LogWarning(
                                    "[PMS Submit] Task.Run: mapping null for mappingId={Id}", capturedMappingId);
                                return;
                            }

                            // ── UnitHead → notify HR only, no BA gate ─────────────────────
                            if (capturedRole == "UnitHead")
                            {
                                await capturedEmail.SendHrFinalizeNotificationAsync(mapping, baseUrl);
                                capturedLog.LogInformation(
                                    "[PMS Submit] UH submitted → HR notified for mappingId={Id}", capturedMappingId);
                                return;
                            }

                            // ── ModCommittee / JMD → no email ─────────────────────────────
                            if (capturedRole == "ModCommittee" || capturedRole == "JMD")
                                return;

                            // ── Employee → notify RM immediately (employee has no BA form) ─
                            if (capturedRole == "Employee")
                            {
                                capturedLog.LogInformation(
                                    "[PMS Submit] Employee submitted → notifying RM for mappingId={Id}", capturedMappingId);

                                await Task.Delay(3000);

                                var freshMapping = GetMappingByIdInternal(capturedConn, capturedMappingId);
                                if (freshMapping != null) mapping = freshMapping;

                                // Send RM notification. RM will see PMS form + BA form.
                                // RM must fill BOTH before PMS advances to HOD.
                                await SendToNextReviewer(capturedEmail, mapping, baseUrl,
                                    capturedMappingId, capturedRole, capturedConn);

                                // ── Bell notification for RM ──
                                // CHANGED: route through NotifyPmsStage using mapping.RmEmpId
                                // (stored at assignment time) — no name-matching fallback needed
                                // unless the EmpId column was never populated. Includes RM's
                                // pending BA form link as a second notification, mirroring the
                                // assignment email which already carries both links.
                                try
                                {
                                    string rmPmsUrl = $"{baseUrl}/Pms/RmDashboard?mappingId={capturedMappingId}";
                                    int rmBaAuditId = GetBehaviouralAuditId(capturedConn, mapping.EmpId, capturedMappingId);
                                    string rmBaUrl = GetBaUrlForRole(baseUrl, rmBaAuditId, "RM");
                                    NotifyPmsStage(mapping, "RM", mapping.RmEmpId, mapping.RmName, by, rmPmsUrl, rmBaUrl);
                                }
                                catch (Exception ex)
                                {
                                    capturedLog.LogWarning(ex, "[PMS Bell] RM notification failed for mappingId={Id}", capturedMappingId);
                                }

                                return;
                            }

                            // ── RM path ───────────────────────────────────────────────────
                            if (capturedRole == "RM")
                            {
                                int baAuditId = GetBehaviouralAuditId(capturedConn, mapping.EmpId, capturedMappingId);

                                if (baAuditId == 0 && mapping.CycleFromDate.HasValue && mapping.CycleToDate.HasValue)
                                {
                                    try
                                    {
                                        using var fallbackCon = new SqlConnection(capturedConn);
                                        await fallbackCon.OpenAsync();
                                        using var fallbackCmd = new SqlCommand(@"
            SELECT TOP 1 AuditID
            FROM   Behaviour_Audit_Main
            WHERE  EmpID     = @EmpId
              AND  CreatedOn >= @From
              AND  CreatedOn <= DATEADD(day, 1, @To)
            ORDER BY AuditID DESC", fallbackCon);
                                        fallbackCmd.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                                        fallbackCmd.Parameters.AddWithValue("@From", mapping.CycleFromDate.Value);
                                        fallbackCmd.Parameters.AddWithValue("@To", mapping.CycleToDate.Value);
                                        var fv = await fallbackCmd.ExecuteScalarAsync();
                                        if (fv != null && fv != DBNull.Value) baAuditId = Convert.ToInt32(fv);
                                    }
                                    catch { }
                                }

                                if (baAuditId == 0)
                                {
                                    capturedLog.LogInformation(
                                        "[PMS Submit] RM submitted PMS but NO BA audit found for mappingId={Id} empId={EmpId}. " +
                                        "PMS advancement blocked until BA is created and submitted.",
                                        capturedMappingId, mapping.EmpId);
                                    return;
                                }

                                await RollupBaScoresToMain(capturedConn, baAuditId, "RM");

                                bool baComplete;
                                try
                                {
                                    using var baCon = new SqlConnection(capturedConn);
                                    baCon.Open();
                                    baComplete = IsBaCompletedForAudit(baCon, baAuditId, "RM", capturedMappingId);
                                }
                                catch
                                {
                                    baComplete = false;
                                }

                                capturedLog.LogInformation(
                                    "[PMS Submit] RM: mappingId={Id} baAuditId={BaId} baComplete={BaComplete}",
                                    capturedMappingId, baAuditId, baComplete);

                                if (!baComplete)
                                {
                                    capturedLog.LogInformation(
                                        "[PMS Submit] RM BA still pending for mappingId={Id}. " +
                                        "PMS advancement blocked. PmsReminderService will advance after RM submits BA.",
                                        capturedMappingId);
                                    return;
                                }

                                capturedLog.LogInformation(
                                    "[PMS Submit] RM PMS + BA both complete for mappingId={Id} → advancing to HOD",
                                    capturedMappingId);
                                await SendToNextReviewer(capturedEmail, mapping, baseUrl,
                                    capturedMappingId, capturedRole, capturedConn);

                                // ── Bell notification for HOD ──
                                // CHANGED: route through NotifyPmsStage using mapping.HodEmpId,
                                // includes HOD's BA link via the already-resolved baAuditId.
                                try
                                {
                                    string hodPmsUrl = $"{baseUrl}/Pms/HodDashboard?mappingId={capturedMappingId}";
                                    string hodBaUrl = GetBaUrlForRole(baseUrl, baAuditId, "HOD");
                                    NotifyPmsStage(mapping, "HOD", mapping.HodEmpId, mapping.HodName, by, hodPmsUrl, hodBaUrl);
                                }
                                catch (Exception ex)
                                {
                                    capturedLog.LogWarning(ex, "[PMS Bell] HOD notification failed for mappingId={Id}", capturedMappingId);
                                }

                                return;
                            }

                            // ── HOD path ──────────────────────────────────────────────────
                            if (capturedRole == "HOD")
                            {
                                int baAuditId = GetBehaviouralAuditId(capturedConn, mapping.EmpId, capturedMappingId);

                                if (baAuditId == 0 && mapping.CycleFromDate.HasValue && mapping.CycleToDate.HasValue)
                                {
                                    try
                                    {
                                        using var fallbackCon = new SqlConnection(capturedConn);
                                        await fallbackCon.OpenAsync();
                                        using var fallbackCmd = new SqlCommand(@"
            SELECT TOP 1 AuditID
            FROM   Behaviour_Audit_Main
            WHERE  EmpID     = @EmpId
              AND  CreatedOn >= @From
              AND  CreatedOn <= DATEADD(day, 1, @To)
            ORDER BY AuditID DESC", fallbackCon);
                                        fallbackCmd.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                                        fallbackCmd.Parameters.AddWithValue("@From", mapping.CycleFromDate.Value);
                                        fallbackCmd.Parameters.AddWithValue("@To", mapping.CycleToDate.Value);
                                        var fv = await fallbackCmd.ExecuteScalarAsync();
                                        if (fv != null && fv != DBNull.Value) baAuditId = Convert.ToInt32(fv);
                                    }
                                    catch { }
                                }

                                if (baAuditId == 0)
                                {
                                    capturedLog.LogInformation(
                                        "[PMS Submit] HOD submitted PMS but NO BA audit found for mappingId={Id}. Blocked.",
                                        capturedMappingId);
                                    return;
                                }

                                await RollupBaScoresToMain(capturedConn, baAuditId, "HOD");

                                bool baComplete;
                                try
                                {
                                    using var baCon = new SqlConnection(capturedConn);
                                    baCon.Open();
                                    baComplete = IsBaCompletedForAudit(baCon, baAuditId, "HOD", capturedMappingId);
                                }
                                catch
                                {
                                    baComplete = false;
                                }

                                capturedLog.LogInformation(
                                    "[PMS Submit] HOD: mappingId={Id} baAuditId={BaId} baComplete={BaComplete}",
                                    capturedMappingId, baAuditId, baComplete);

                                if (!baComplete)
                                {
                                    capturedLog.LogInformation(
                                        "[PMS Submit] HOD BA still pending for mappingId={Id}. Blocked.",
                                        capturedMappingId);
                                    return;
                                }

                                capturedLog.LogInformation(
                                    "[PMS Submit] HOD PMS + BA both complete for mappingId={Id} → advancing to UnitHead",
                                    capturedMappingId);
                                await SendToNextReviewer(capturedEmail, mapping, baseUrl,
                                    capturedMappingId, capturedRole, capturedConn);

                                // ── Bell notification for UnitHead ──
                                // CHANGED: route through NotifyPmsStage using mapping.UnitHeadEmpId.
                                // UnitHead has no BA form, so no baUrl is passed.
                                try
                                {
                                    string uhPmsUrl = $"{baseUrl}/Pms/UnitHeadDashboard?mappingId={capturedMappingId}";
                                    NotifyPmsStage(mapping, "UnitHead", mapping.UnitHeadEmpId, mapping.UnitHeadName, by, uhPmsUrl);
                                }
                                catch (Exception ex)
                                {
                                    capturedLog.LogWarning(ex, "[PMS Bell] UnitHead notification failed for mappingId={Id}", capturedMappingId);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            capturedLog.LogError(ex,
                                "[PMS Email] SubmitAppraisalFill Task.Run failed for mappingId={Id} role={Role}",
                                capturedMappingId, capturedRole);
                        }
                    });
                }
                
                return Json(new { success = true, message = nextStep, redirectUrl });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
        // PMP CRUD

        // ====================================================
        // PMP CRUD
        // ====================================================
        [HttpPost]
                [ValidateAntiForgeryToken]
                public async Task<IActionResult> SavePmp(int pmpId, string pmpName)
                {
                    var user = await _userManager.GetUserAsync(User);
                    string by = user?.Id ?? "System";
                    pmpName = pmpName?.Trim();
                    if (string.IsNullOrEmpty(pmpName)) { TempData["Error"] = "Perspective Name is required."; return RedirectToAction("Index", new { tab = 1 }); }
                    try
                    {
                        using var con = new SqlConnection(_conn); con.Open();
                        using (var chk = new SqlCommand("SELECT COUNT(1) FROM PmpMaster WHERE LTRIM(RTRIM(PmpName))=@N AND IsDeleted=0 AND PmpId<>@Id", con))
                        { chk.Parameters.AddWithValue("@N", pmpName); chk.Parameters.AddWithValue("@Id", pmpId); if ((int)chk.ExecuteScalar() > 0) { TempData["Error"] = $"Perspective '{pmpName}' already exists."; return RedirectToAction("Index", new { tab = 1 }); } }
                        using var cmd = new SqlCommand("sp_Pmp_Save", con) { CommandType = CommandType.StoredProcedure };
                        cmd.Parameters.AddWithValue("@PmpId", pmpId); cmd.Parameters.AddWithValue("@PmpName", pmpName); cmd.Parameters.AddWithValue("@By", by);
                        cmd.ExecuteNonQuery();
                        TempData["Success"] = pmpId == 0 ? $"Perspective '{pmpName}' added." : $"Perspective '{pmpName}' updated.";
                    }
                    catch (Exception ex) { TempData["Error"] = "Error: " + ex.Message; }
                    return RedirectToAction("Index", new { tab = 1 });
                }


                // ====================================================
                // KRA CRUD
                // ====================================================
                [HttpPost]
                [ValidateAntiForgeryToken]
                public async Task<IActionResult> SaveKra(int kraId, int pmpId, string kraName)
                {
                    var user = await _userManager.GetUserAsync(User); string by = user?.Id ?? "System";
                    kraName = kraName?.Trim();
                    if (string.IsNullOrEmpty(kraName)) { TempData["Error"] = "KRA Name required."; return RedirectToAction("Index", new { tab = 2 }); }
                    if (pmpId <= 0) { TempData["Error"] = "Select a Perspective."; return RedirectToAction("Index", new { tab = 2 }); }
                    try
                    {
                        using var con = new SqlConnection(_conn); con.Open();
                using (var chk = new SqlCommand("SELECT COUNT(1) FROM KraMaster WHERE LTRIM(RTRIM(KraName))=@N AND PmpId=@PmpId AND IsDeleted=0 AND KraId<>@Id", con))
                { chk.Parameters.AddWithValue("@N", kraName); chk.Parameters.AddWithValue("@PmpId", pmpId); chk.Parameters.AddWithValue("@Id", kraId); if ((int)chk.ExecuteScalar() > 0) { TempData["Error"] = $"KRA '{kraName}' already exists under this Perspective."; return RedirectToAction("Index", new { tab = 2 }); } }
                using var cmd = new SqlCommand("sp_Kra_Save", con) { CommandType = CommandType.StoredProcedure };
                        cmd.Parameters.AddWithValue("@KraId", kraId); cmd.Parameters.AddWithValue("@PmpId", pmpId); cmd.Parameters.AddWithValue("@KraName", kraName); cmd.Parameters.AddWithValue("@By", by);
                        cmd.ExecuteNonQuery();
                        TempData["Success"] = kraId == 0 ? $"KRA '{kraName}' added." : $"KRA '{kraName}' updated.";
                    }
                    catch (Exception ex) { TempData["Error"] = "Error: " + ex.Message; }
                    return RedirectToAction("Index", new { tab = 2 });
                }
        [HttpGet]
        public IActionResult GetKpisByMapping(int pmpId, int kraId)
        {
            var list = GetKpisByPmpKraInternal(pmpId, kraId);
            return Json(list.Select(k => new {
                k.KpiId,
                k.KpiName,
                k.UnitOfMeasure,
                k.IndividualWeightage,
                k.Target
            }));
        }
        // ====================================================
        // KPI AJAX
        // ====================================================
        [HttpPost]
        public async Task<IActionResult> SaveKpis([FromBody] KpiSaveRequest req)
        {
            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";

            if (req == null || req.Rows == null || !req.Rows.Any())
                return Json(new { success = false, message = "No KPI rows." });
            if (req.PmpId <= 0 || req.KraId <= 0)
                return Json(new { success = false, message = "Select PMP and KRA." });
            foreach (var r in req.Rows)
                if (string.IsNullOrWhiteSpace(r.KpiName))
                    return Json(new { success = false, message = "All KPI names required." });

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                foreach (var row in req.Rows)
                {
                    // ── Duplicate check within same KRA ──────────────────────────
                    using (var dupChk = new SqlCommand(
                        "SELECT COUNT(1) FROM KpiMaster " +
                        "WHERE LTRIM(RTRIM(KpiName))=@N AND KraId=@KraId " +
                        "AND PmpId=@PmpId AND IsDeleted=0 AND KpiId<>@KpiId", con))
                    {
                        dupChk.Parameters.AddWithValue("@N", row.KpiName.Trim());
                        dupChk.Parameters.AddWithValue("@KraId", req.KraId);
                        dupChk.Parameters.AddWithValue("@PmpId", req.PmpId);
                        dupChk.Parameters.AddWithValue("@KpiId", row.KpiId);
                        if ((int)dupChk.ExecuteScalar() > 0)
                            return Json(new
                            {
                                success = false,
                                message = $"KPI '{row.KpiName}' already exists under this KRA."
                            });
                    }

                    // ── Save to KpiMaster via SP ──────────────────────────────────
                    using var cmd = new SqlCommand("sp_Kpi_Save", con)
                    { CommandType = CommandType.StoredProcedure };
                    cmd.Parameters.AddWithValue("@KpiId", row.KpiId);
                    cmd.Parameters.AddWithValue("@PmpId", req.PmpId);
                    cmd.Parameters.AddWithValue("@KraId", req.KraId);
                    cmd.Parameters.AddWithValue("@KpiName", row.KpiName.Trim());
                    cmd.Parameters.AddWithValue("@UnitOfMeasure", row.UnitOfMeasure ?? "%");
                    cmd.Parameters.AddWithValue("@IndividualWeightage", row.IndividualWeightage);
                    cmd.Parameters.AddWithValue("@OverallWeightage", 0);
                    cmd.Parameters.AddWithValue("@Target", row.Target);
                    cmd.Parameters.AddWithValue("@By", by);
                    cmd.ExecuteNonQuery();

                    // ── Sync to EmpMappingMasterKpi for all master mappings
                    //    that reference this KpiId ──────────────────────────────
                    if (row.KpiId > 0)
                    {
                        using var syncMaster = new SqlCommand(@"
                    UPDATE EmpMappingMasterKpi
                    SET    KpiName             = @KpiName,
                           UnitOfMeasure       = @Unit,
                           IndividualWeightage = @Wt,
                           Target              = @Tgt,
                           UpdatedBy           = @By,
                           UpdatedAt           = GETDATE()
                    WHERE  KpiId      = @KpiId
                      AND  IsDeleted  = 0", con);
                        syncMaster.Parameters.AddWithValue("@KpiName", row.KpiName.Trim());
                        syncMaster.Parameters.AddWithValue("@Unit", row.UnitOfMeasure ?? "%");
                        syncMaster.Parameters.AddWithValue("@Wt", (decimal)row.IndividualWeightage);
                        syncMaster.Parameters.AddWithValue("@Tgt", (decimal)row.Target);
                        syncMaster.Parameters.AddWithValue("@By", by);
                        syncMaster.Parameters.AddWithValue("@KpiId", row.KpiId);
                        syncMaster.ExecuteNonQuery();

                        // ── Also sync to active (non-finalized) EmpAppraisalKpiRows ──
                        using var syncAppraisal = new SqlCommand(@"
                    UPDATE r
                    SET    r.KpiName             = @KpiName,
                           r.UnitOfMeasure       = @Unit,
                           r.IndividualWeightage = @Wt,
                           r.Target              = @Tgt,
                           r.UpdatedBy           = @By,
                           r.UpdatedAt           = GETDATE()
                    FROM   EmpAppraisalKpiRows r
                    INNER JOIN EmpAppraisalMapping m
                           ON  m.MappingId = r.MappingId
                           AND m.IsDeleted = 0
                           AND m.[Status] NOT IN ('HRFinalized')
                    WHERE  r.KpiId     = @KpiId
                      AND  r.IsDeleted = 0", con);
                        syncAppraisal.Parameters.AddWithValue("@KpiName", row.KpiName.Trim());
                        syncAppraisal.Parameters.AddWithValue("@Unit", row.UnitOfMeasure ?? "%");
                        syncAppraisal.Parameters.AddWithValue("@Wt", (decimal)row.IndividualWeightage);
                        syncAppraisal.Parameters.AddWithValue("@Tgt", (decimal)row.Target);
                        syncAppraisal.Parameters.AddWithValue("@By", by);
                        syncAppraisal.Parameters.AddWithValue("@KpiId", row.KpiId);
                        syncAppraisal.ExecuteNonQuery();
                    }
                }

                return Json(new
                {
                    success = true,
                    message = $"{req.Rows.Count} KPI(s) saved and synced to all active mappings."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "DB Error: " + ex.Message });
            }
        }
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeletePmp(int id)
        {
            if (id <= 0)
                return Json(new { success = false, message = "Invalid ID." });

            // Safe way to get username — no redirect risk
            string by = User?.Identity?.Name ?? "System";

            try
            {
                await using var con = new SqlConnection(_conn);
                await con.OpenAsync();

                // Check for linked active KRAs first
                await using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM KraMaster WHERE PmpId = @Id AND IsDeleted = 0", con))
                {
                    chk.Parameters.AddWithValue("@Id", id);
                    int count = (int)await chk.ExecuteScalarAsync();
                    if (count > 0)
                        return Json(new
                        {
                            success = false,
                            message = $"Cannot delete — {count} active KRA(s) are linked. Remove them first."
                        });
                }

                await using var cmd = new SqlCommand(@"
            UPDATE PmpMaster
            SET    IsDeleted  = 1,
                   DeletedBy  = @By,
                   DeletedOn  = GETDATE(),
                   ModifiedBy = @By,
                   ModifiedOn = GETDATE()
            WHERE  PmpId     = @Id
            AND    IsDeleted = 0", con);

                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@By", by);

                int rows = await cmd.ExecuteNonQueryAsync();

                return rows > 0
                    ? Json(new { success = true, message = "Perspective deleted successfully." })
                    : Json(new { success = false, message = "Record not found or already deleted." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeleteKra(int id)
        {
            if (id <= 0)
                return Json(new { success = false, message = "Invalid ID." });

            var user = await _userManager.GetUserAsync(User);
            string by = user?.UserName ?? "System";

            try
            {
                using var con = new SqlConnection(_conn);
                await con.OpenAsync();

                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM KpiMaster WHERE KraId=@Id AND IsDeleted=0", con))
                {
                    chk.Parameters.AddWithValue("@Id", id);
                    int count = (int)await chk.ExecuteScalarAsync();
                    if (count > 0)
                        return Json(new { success = false, message = "Cannot delete — active KPIs are linked. Delete KPIs first." });
                }

                using var cmd = new SqlCommand(@"
            UPDATE KraMaster
            SET    IsDeleted  = 1,
                   DeletedBy  = @By,
                   DeletedOn  = GETDATE(),
                   ModifiedBy = @By,
                   ModifiedOn = GETDATE()
            WHERE  KraId     = @Id
            AND    IsDeleted = 0", con);

                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@By", by);
                int rows = await cmd.ExecuteNonQueryAsync();

                if (rows == 0)
                    return Json(new { success = false, message = "Record not found or already deleted." });

                return Json(new { success = true, message = "KRA deleted successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeleteKpi(int id)
        {
            if (id <= 0)
                return Json(new { success = false, message = "Invalid ID." });

            var user = await _userManager.GetUserAsync(User);
            string by = user?.UserName ?? "System";

            try
            {
                using var con = new SqlConnection(_conn);
                await con.OpenAsync();

                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM KpiMaster WHERE KpiId=@Id AND IsDeleted=0", con))
                {
                    chk.Parameters.AddWithValue("@Id", id);
                    if ((int)await chk.ExecuteScalarAsync() == 0)
                        return Json(new { success = false, message = "KPI not found or already deleted." });
                }

                using var cmd = new SqlCommand("sp_Kpi_Delete", con);
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@KpiId", id);
                cmd.Parameters.AddWithValue("@By", by);
                await cmd.ExecuteNonQueryAsync();

                return Json(new { success = true, message = "KPI deleted successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
        // ====================================================
        // SAVE EMPLOYEE MAPPING
        [HttpPost]
        public async Task<IActionResult> SaveEmpMapping([FromBody] EmpMappingSaveRequest req)
        {
            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

            if (req == null) return Json(new { success = false, message = "Invalid request." });

            // ── RESOLVE UserId → EmpId (numeric) ─────────────────────────────────
            // The dropdown now sends EmpId (int) in the UserId field.
            string empIdRaw = (req.UserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(empIdRaw))
                return Json(new { success = false, message = "Please select an employee." });

            if (!int.TryParse(empIdRaw, out int resolvedEmpId) || resolvedEmpId <= 0)
                return Json(new { success = false, message = "Invalid employee selected. EmpId must be a positive integer." });

            // Keep userId as a string for backward-compat with SP parameter
            string spUserIdParam = empIdRaw;

            // ── CYCLE-ONLY UPDATE ─────────────────────────────────────────────────
            if (req.CycleUpdateOnly == true)
            {
                try
                {
                    using var c2 = new SqlConnection(_conn);
                    c2.Open();
                    using var cu = new SqlCommand(@"
                    UPDATE EmpAppraisalMapping
                    SET    AppraisalCycle  = @Cycle,
                           CycleFromDate   = @From,
                           CycleToDate     = @To,
                           AppraisalYear   = @Year,
                           UpdatedBy       = @By,
                           UpdatedAt       = GETDATE()
                    WHERE  MappingId = @Id
                      AND  IsDeleted = 0", c2);
                    cu.Parameters.AddWithValue("@Cycle", req.AppraisalCycle ?? "");
                    cu.Parameters.AddWithValue("@From",
                        DateTime.TryParse(req.CycleFromDate, out var fd2) ? (object)fd2 : DBNull.Value);
                    cu.Parameters.AddWithValue("@To",
                        DateTime.TryParse(req.CycleToDate, out var td2) ? (object)td2 : DBNull.Value);
                    cu.Parameters.AddWithValue("@Year",
                        req.AppraisalYear > 0 ? req.AppraisalYear : DateTime.Now.Year);
                    cu.Parameters.AddWithValue("@By", by);
                    cu.Parameters.AddWithValue("@Id", req.MappingId);
                    cu.ExecuteNonQuery();
                    return Json(new { success = true, message = "Cycle updated." });
                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = ex.Message });
                }
            }

            // ── VALIDATION ────────────────────────────────────────────────────────
            if (req.KpiIds == null || !req.KpiIds.Any())
                return Json(new { success = false, message = "Add at least one KPI." });

            if (req.AppraisalYear <= 0)
                return Json(new { success = false, message = "Appraisal year is required." });

            // ── REVIEWER VALIDATION ───────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(req.RmName))
                return Json(new { success = false, message = "Reporting Manager (RM) is required." });
            if (string.IsNullOrWhiteSpace(req.HodName))
                return Json(new { success = false, message = "Head of Department (HOD) is required." });
            if (string.IsNullOrWhiteSpace(req.UnitHeadName))
                return Json(new { success = false, message = "Unit Head (UH) is required." });

            // ── KPI WEIGHTAGE VALIDATION ──────────────────────────────────────────
            if (req.KpiDetails != null && req.KpiDetails.Any())
            {
                decimal totalWt = Math.Round(
                    req.KpiDetails.Sum(k => (decimal)k.IndividualWeightage), 2);
                if (totalWt != 100m)
                    return Json(new
                    {
                        success = false,
                        message = $"Total KPI weightage must be exactly 100%. Current: {totalWt}%"
                    });
            }

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // ── DUPLICATE GUARD (new records only) ────────────────────────────
                // The DB unique constraint UQ_EmpMappingMaster_User_Year is keyed on
                // (EmpId, AppraisalYear). A soft-deleted row will still block INSERT,
                // so we detect it and either reject (active) or reactivate (deleted).
                if (req.MappingId == 0)
                {
                    int existingMasterId = 0;
                    bool existingIsDeleted = false;

                    using (var findExisting = new SqlCommand(@"
SELECT TOP 1 MasterMappingId, IsDeleted
FROM   EmpMappingMaster
WHERE  EmpId = @EmpId
  AND  AppraisalYear = @Y", con))
                    {
                        findExisting.Parameters.AddWithValue("@EmpId", resolvedEmpId);
                        findExisting.Parameters.AddWithValue("@Y", req.AppraisalYear);

                        using var dr = findExisting.ExecuteReader();
                        if (dr.Read())
                        {
                            existingMasterId = Convert.ToInt32(dr["MasterMappingId"]);
                            existingIsDeleted = Convert.ToBoolean(dr["IsDeleted"]);
                        }
                    }

                    if (existingMasterId > 0)
                    {
                        if (!existingIsDeleted)
                        {
                            return Json(new
                            {
                                success = false,
                                message = "A mapping already exists for this employee and year. " +
                                          "Please edit the existing mapping instead."
                            });
                        }
                        else
                        {
                            // Reactivate the soft-deleted master row so the upcoming SP call
                            // does an UPDATE instead of an INSERT (which would hit the unique key).
                            using var undelete = new SqlCommand(@"
UPDATE EmpMappingMaster
SET    IsDeleted  = 0,
       DeletedBy  = NULL,
       DeletedOn  = NULL
WHERE  MasterMappingId = @Id", con);
                            undelete.Parameters.AddWithValue("@Id", existingMasterId);
                            undelete.ExecuteNonQuery();

                            // Also reactivate KPI rows so STEP 3 starts from a clean baseline.
                            using var undeleteKpi = new SqlCommand(@"
UPDATE EmpMappingMasterKpi
SET    IsDeleted = 0
WHERE  MasterMappingId = @Id", con);
                            undeleteKpi.Parameters.AddWithValue("@Id", existingMasterId);
                            undeleteKpi.ExecuteNonQuery();

                            req.MappingId = existingMasterId;
                        }
                    }
                }

                // ── STEP 1: Capture BEFORE state for diff ─────────────────────────
                var beforeKpis = new List<(int kpiId, string kpiName, decimal wt,
                                           decimal target, string unit)>();

                if (req.MappingId > 0)
                {
                    using var sel = new SqlCommand(@"
                    SELECT KpiId,
                           ISNULL(KpiName,'')        AS KpiName,
                           IndividualWeightage,
                           Target,
                           ISNULL(UnitOfMeasure,'%') AS UnitOfMeasure
                    FROM   EmpMappingMasterKpi
                    WHERE  MasterMappingId = @Mid
                      AND  IsDeleted = 0
                    ORDER  BY SortOrder", con);
                    sel.Parameters.AddWithValue("@Mid", req.MappingId);
                    using var sdr = sel.ExecuteReader();
                    while (sdr.Read())
                        beforeKpis.Add((
                            Convert.ToInt32(sdr["KpiId"]),
                            sdr["KpiName"].ToString()!,
                            Convert.ToDecimal(sdr["IndividualWeightage"]),
                            Convert.ToDecimal(sdr["Target"]),
                            sdr["UnitOfMeasure"].ToString()!
                        ));
                }

                // ── STEP 2: Upsert master record via SP ───────────────────────────
                using var masterCmd = new SqlCommand("sp_EmpMappingMaster_Save", con)
                {
                    CommandType = System.Data.CommandType.StoredProcedure
                };
                masterCmd.Parameters.AddWithValue("@MasterMappingId", req.MappingId);
                masterCmd.Parameters.AddWithValue("@UserId", spUserIdParam);          // string (legacy SP compat)
                masterCmd.Parameters.AddWithValue("@AppraisalYear", req.AppraisalYear);
                masterCmd.Parameters.AddWithValue("@RmName", req.RmName ?? "");
                masterCmd.Parameters.AddWithValue("@HodName", req.HodName ?? "");
                masterCmd.Parameters.AddWithValue("@UnitHeadName", req.UnitHeadName ?? "");
                masterCmd.Parameters.AddWithValue("@ModCommitteeNames", req.ModCommitteeNames ?? "");
                masterCmd.Parameters.AddWithValue("@JmdName", req.JmdName ?? "");
                masterCmd.Parameters.AddWithValue("@KpiIds",
                    string.Join(",", req.KpiIds.Distinct()));
                masterCmd.Parameters.AddWithValue("@By", by);

                var masterOutParam = new SqlParameter("@NewMasterMappingId",
                    System.Data.SqlDbType.Int)
                { Direction = System.Data.ParameterDirection.Output };
                masterCmd.Parameters.Add(masterOutParam);
                masterCmd.ExecuteNonQuery();

                int masterMappingId = Convert.ToInt32(masterOutParam.Value);

                // ── STEP 2b: Stamp EmpId on the master row ────────────────────────
                // The SP may not know about EmpId (legacy column), so we stamp it now.
                using (var stampEmp = new SqlCommand(@"
                    UPDATE EmpMappingMaster
                    SET    EmpId     = @EmpId,
                           UpdatedAt = GETDATE(),
                           UpdatedBy = @By
                    WHERE  MasterMappingId = @Mid", con))
                {
                    stampEmp.Parameters.AddWithValue("@EmpId", resolvedEmpId);
                    stampEmp.Parameters.AddWithValue("@By", by);
                    stampEmp.Parameters.AddWithValue("@Mid", masterMappingId);
                    stampEmp.ExecuteNonQuery();
                }

                // ── STEP 3: Refresh master KPI rows ──────────────────────────────
                // Soft-delete all existing KPI rows, then re-insert from the request.
                using (var del = new SqlCommand(@"
                    UPDATE EmpMappingMasterKpi
                    SET    IsDeleted = 1
                    WHERE  MasterMappingId = @Mid", con))
                {
                    del.Parameters.AddWithValue("@Mid", masterMappingId);
                    del.ExecuteNonQuery();
                }

                var afterKpis = req.KpiDetails ?? new List<KpiDetailForSave>();
                int sortOrder = 1;

                foreach (var kd in afterKpis)
                {
                    using var ins = new SqlCommand(@"
                    INSERT INTO EmpMappingMasterKpi
                        (MasterMappingId, KpiId, PmpId, KraId, KpiName,
                         UnitOfMeasure, IndividualWeightage, Target,
                         SortOrder, IsDeleted, CreatedBy, CreatedAt)
                    VALUES
                        (@Mid, @KpiId, @PmpId, @KraId, @Name,
                         @Unit, @Wt, @Tgt,
                         @Sort, 0, @By, GETDATE())", con);
                    ins.Parameters.AddWithValue("@Mid", masterMappingId);
                    ins.Parameters.AddWithValue("@KpiId", kd.KpiId);
                    ins.Parameters.AddWithValue("@PmpId", kd.PmpId);
                    ins.Parameters.AddWithValue("@KraId", kd.KraId);
                    ins.Parameters.AddWithValue("@Name", (kd.KpiName ?? "").Trim());
                    ins.Parameters.AddWithValue("@Unit", kd.UnitOfMeasure ?? "%");
                    ins.Parameters.AddWithValue("@Wt", (decimal)kd.IndividualWeightage);
                    ins.Parameters.AddWithValue("@Tgt", (decimal)kd.Target);
                    ins.Parameters.AddWithValue("@Sort", sortOrder++);
                    ins.Parameters.AddWithValue("@By", by);
                    ins.ExecuteNonQuery();
                }

                // ── STEP 4: Diff & write to PAS_ChangeLog ────────────────────────
                var afterIds = afterKpis.Select(k => k.KpiId).ToHashSet();
                var beforeIds = beforeKpis.Select(b => b.kpiId).ToHashSet();
                var changeLogs = new List<(string field, string oldVal, string newVal)>();

                // Removed KPIs
                foreach (var b in beforeKpis.Where(b => !afterIds.Contains(b.kpiId)))
                    changeLogs.Add(("KPI Removed",
                        $"[{b.kpiId}] {b.kpiName} | Wt={b.wt}% | Target={b.target}", ""));

                // Added KPIs
                foreach (var a in afterKpis.Where(a => !beforeIds.Contains(a.KpiId)))
                    changeLogs.Add(("KPI Added", "",
                        $"[{a.KpiId}] {a.KpiName} | Wt={a.IndividualWeightage}% | Target={a.Target}"));

                // Modified KPIs
                foreach (var a in afterKpis.Where(a => beforeIds.Contains(a.KpiId)))
                {
                    var b = beforeKpis.First(x => x.kpiId == a.KpiId);

                    if (b.kpiName != (a.KpiName ?? "").Trim())
                        changeLogs.Add(($"KPI [{a.KpiId}] Name",
                            b.kpiName, (a.KpiName ?? "").Trim()));

                    if (Math.Abs(b.wt - (decimal)a.IndividualWeightage) > 0.001m)
                        changeLogs.Add(($"KPI [{a.KpiId}] Weight ({a.KpiName})",
                            $"{b.wt}%", $"{a.IndividualWeightage}%"));

                    if (Math.Abs(b.target - (decimal)a.Target) > 0.001m)
                        changeLogs.Add(($"KPI [{a.KpiId}] Target ({a.KpiName})",
                            $"{b.target}", $"{a.Target}"));

                    if (!string.Equals(b.unit, a.UnitOfMeasure ?? "%",
                            StringComparison.OrdinalIgnoreCase))
                        changeLogs.Add(($"KPI [{a.KpiId}] Unit ({a.KpiName})",
                            b.unit, a.UnitOfMeasure ?? "%"));
                }

                string actionType = req.MappingId == 0 ? "MASTER_CREATE" : "MASTER_EDIT";

                foreach (var (field, oldVal, newVal) in changeLogs)
                    LogPasChangeDetailed(con, "EmpMappingMasterKpi", masterMappingId,
                        actionType, field,
                        string.IsNullOrEmpty(oldVal) ? null : oldVal,
                        string.IsNullOrEmpty(newVal) ? null : newVal,
                        by, ip);

                // ── STEP 5: Sync to active EmpAppraisalKpiRows (UPDATE ONLY) ─────
                // Active cycles are frozen — only update weight/target/name/unit on
                // EXISTING rows.  Adding/removing KPIs is not permitted in-flight;
                // structural changes take effect from the next assigned cycle only.
                var appraisalMappingIds = new List<int>();

                using (var amCmd = new SqlCommand(@"
                    SELECT MappingId
                    FROM   EmpAppraisalMapping
                    WHERE  EmpId      = @EmpId
                      AND  IsDeleted  = 0
                      AND  [Status]   NOT IN ('HRFinalized')", con))
                {
                    // Use resolved numeric EmpId for the join — reliable across all rows.
                    amCmd.Parameters.AddWithValue("@EmpId", resolvedEmpId);

                    using var amDr = amCmd.ExecuteReader();
                    while (amDr.Read())
                        appraisalMappingIds.Add(Convert.ToInt32(amDr["MappingId"]));
                }

                foreach (int appraisalMappingId in appraisalMappingIds)
                {
                    foreach (var a in afterKpis)
                    {
                        using var updRow = new SqlCommand(@"
                        UPDATE EmpAppraisalKpiRows
                        SET    KpiName             = @Name,
                               UnitOfMeasure       = @Unit,
                               IndividualWeightage = @Wt,
                               Target              = @Tgt,
                               UpdatedBy           = @By,
                               UpdatedAt           = GETDATE()
                        WHERE  MappingId  = @MappingId
                          AND  KpiId      = @KpiId
                          AND  IsDeleted  = 0", con);
                        updRow.Parameters.AddWithValue("@Name", (a.KpiName ?? "").Trim());
                        updRow.Parameters.AddWithValue("@Unit", a.UnitOfMeasure ?? "%");
                        updRow.Parameters.AddWithValue("@Wt", (decimal)a.IndividualWeightage);
                        updRow.Parameters.AddWithValue("@Tgt", (decimal)a.Target);
                        updRow.Parameters.AddWithValue("@By", by);
                        updRow.Parameters.AddWithValue("@MappingId", appraisalMappingId);
                        updRow.Parameters.AddWithValue("@KpiId", a.KpiId);
                        updRow.ExecuteNonQuery();
                    }

                    // Audit trail for the sync
                    foreach (var (field, oldVal, newVal) in changeLogs)
                        LogPasChangeDetailed(con, "EmpAppraisalKpiRows", appraisalMappingId,
                            "KPI_SYNC_FROM_MASTER", field,
                            string.IsNullOrEmpty(oldVal) ? null : oldVal,
                            string.IsNullOrEmpty(newVal) ? null : newVal,
                            by, ip);
                }

                string verb = req.MappingId == 0 ? "created" : "updated";
                string syncMsg = appraisalMappingIds.Any()
                    ? $" Existing KPI targets/weights updated in {appraisalMappingIds.Count} " +
                      "active cycle(s). New/removed KPIs take effect from the next assigned cycle."
                    : "";

                return Json(new
                {
                    success = true,
                    message = $"Master mapping {verb}.{syncMsg} (#{masterMappingId})",
                    mappingId = masterMappingId
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "DB Error: " + ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> DeleteEmpMapping(int id)
        {
            var user = await _userManager.GetUserAsync(User); string by = user?.Id ?? "System";
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using var cmd = new SqlCommand("sp_EmpAppraisal_Delete", con) { CommandType = CommandType.StoredProcedure };
                cmd.Parameters.AddWithValue("@MappingId", id); cmd.Parameters.AddWithValue("@By", by); cmd.ExecuteNonQuery();
                return Json(new { success = true, message = "Mapping deleted." });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // ====================================================
        // GET MAPPING DETAIL
        // ====================================================
        public IActionResult GetMappingDetail(int mappingId)
        {
            try
            {
                var mapping = GetMappingByIdInternal(_conn, mappingId);
                if (mapping == null)
                    mapping = GetMasterMappingByIdInternal(_conn, mappingId);
                if (mapping == null) return Json(new { error = "Mapping not found." });
                return Json(new
                {
                    mapping.MappingId,
                    mapping.UserId,
                    mapping.EmpId,
                    mapping.EmpCode,
                    mapping.EmpName,
                    mapping.Designation,
                    mapping.Department,
                    mapping.Email,
                    mapping.ReportTo,
                    mapping.AppraisalYear,
                    mapping.AppraisalCycle,
                    mapping.RmName,
                    mapping.HodName,
                    mapping.UnitHeadName,
                    mapping.ModCommitteeNames,
                    mapping.JmdName,
                    cycleFromDate = mapping.CycleFromDate?.ToString("yyyy-MM-dd") ?? "",
                    cycleToDate = mapping.CycleToDate?.ToString("yyyy-MM-dd") ?? "",
                    lastUpdatedAt = mapping.LastUpdatedAt?.ToString("dd-MMM-yy HH:mm") ?? "",
                    lastUpdatedBy = mapping.LastUpdatedBy ?? "",
                    mapping.Status,
                    kpiRows = mapping.KpiRows.Select(r => new
                    {
                        r.EmpKpiRowId,
                        r.SrNo,
                        r.KpiId,
                        r.KpiCode,
                        r.KpiName,
                        r.PmpId,
                        r.PmpName,
                        r.KraId,
                        r.KraName,
                        r.UnitOfMeasure,
                        r.IndividualWeightage,
                        r.OverallWeightage,
                        r.Target,
                        r.EmpActual,
                        r.EmpRating,
                        r.RmActual,
                        r.RmRating,
                        r.RmRemarks,
                        r.HodActual,
                        r.HodRating,
                        r.HodRemarks,
                        r.UnitHeadActual,
                        r.UnitHeadRating,
                        r.UnitHeadRemarks,
                        r.ModCommitteeRating,
                        r.ModCommitteeRemarks,
                        r.JmdRating,
                        r.JmdRemarks,
                        r.HrFinalRating,
                        r.WeightedRating
                    })
                });
            }
            catch (Exception ex) { return Json(new { error = ex.Message }); }
        }
        public IActionResult GetMasterMappingDetail(int masterMappingId)
        {
            try
            {
                // ── Cache for 30s so repeated opens of same mapping are instant ──
                Response.Headers["Cache-Control"] = "private, max-age=30";

                using var con = new SqlConnection(_conn);
                con.Open();

                using var cmd = new SqlCommand(@"
    SELECT
        mm.MasterMappingId,
        mm.EmpId,
        mm.UserId,
        mm.AppraisalYear,
        ISNULL(mm.RmName,'')            AS RmName,
        ISNULL(mm.HodName,'')           AS HodName,
        ISNULL(mm.UnitHeadName,'')      AS UnitHeadName,
        ISNULL(mm.ModCommitteeNames,'') AS ModCommitteeNames,
        ISNULL(mm.JmdName,'')           AS JmdName,
        mm.CreatedAt,
        ISNULL(mm.UpdatedAt, mm.CreatedAt) AS UpdatedAt,
        ISNULL(mm.UpdatedBy, mm.CreatedBy) AS UpdatedBy,
        ISNULL(e.Emp_code,'')           AS EMPCode,
        ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
        ISNULL(e.Work_Email,'')         AS Email,
        ISNULL(dept.Dept_Name,'')       AS DepartmentName,
        ISNULL(des.Desig_Name,'')       AS DesignationName
    FROM EmpMappingMaster mm
    LEFT JOIN [Orange_Hrms].[dbo].[T0080_EMP_MASTER]        e    ON e.Emp_ID    = mm.EmpId
    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER]  dept ON dept.Dept_Id = e.Dept_ID
    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des  ON des.Desig_ID = e.Desig_Id
    WHERE mm.MasterMappingId = @Id AND mm.IsDeleted = 0", con);
                cmd.Parameters.AddWithValue("@Id", masterMappingId);

                using var dr = cmd.ExecuteReader();
                if (!dr.Read())
                    return Json(new { error = "Master mapping not found." });

                var header = new
                {
                    mappingId = Convert.ToInt32(dr["MasterMappingId"]),
                    empId = dr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["EmpId"]),
                    userId = dr["UserId"]?.ToString() ?? "",
                    empCode = dr["EMPCode"]?.ToString() ?? "",
                    empName = dr["FullName"]?.ToString() ?? "",
                    email = dr["Email"]?.ToString() ?? "",
                    designation = dr["DesignationName"]?.ToString() ?? "",
                    department = dr["DepartmentName"]?.ToString() ?? "",
                    appraisalYear = dr["AppraisalYear"] == DBNull.Value ? 0 : Convert.ToInt32(dr["AppraisalYear"]),
                    rmName = dr["RmName"]?.ToString() ?? "",
                    hodName = dr["HodName"]?.ToString() ?? "",
                    unitHeadName = dr["UnitHeadName"]?.ToString() ?? "",
                    modCommitteeNames = dr["ModCommitteeNames"]?.ToString() ?? "",
                    jmdName = dr["JmdName"]?.ToString() ?? "",
                    lastUpdatedAt = dr["UpdatedAt"] == DBNull.Value ? ""
                                         : Convert.ToDateTime(dr["UpdatedAt"]).ToString("dd-MMM-yy HH:mm"),
                    lastUpdatedBy = dr["UpdatedBy"]?.ToString() ?? "",
                    status = "Master",
                    appraisalCycle = "",
                    cycleFromDate = "",
                    cycleToDate = ""
                };
                dr.Close();

                // ── Single query for all KPI rows with LEFT JOINs ──────────────────
                // LEFT JOIN so soft-deleted master records don't silently drop rows
                var kpiRows = new List<object>();
                using var kpiCmd = new SqlCommand(@"
            SELECT
                mk.MasterKpiId          AS EmpKpiRowId,
                mk.KpiId,
                mk.PmpId,
                mk.KraId,
                mk.KpiName,
                mk.UnitOfMeasure,
                mk.IndividualWeightage,
                mk.Target,
                mk.SortOrder,
                ISNULL(k.KpiCode,'')                                    AS KpiCode,
                ISNULL(p.PmpName, 'PMP-' + CAST(mk.PmpId AS NVARCHAR)) AS PmpName,
                ISNULL(r.KraName, 'KRA-' + CAST(mk.KraId AS NVARCHAR)) AS KraName
            FROM  EmpMappingMasterKpi mk
            LEFT JOIN KpiMaster  k ON k.KpiId = mk.KpiId
            LEFT JOIN PmpMaster  p ON p.PmpId = mk.PmpId
            LEFT JOIN KraMaster  r ON r.KraId = mk.KraId
            WHERE mk.MasterMappingId = @Id AND mk.IsDeleted = 0
            ORDER BY mk.SortOrder, mk.MasterKpiId", con);
                kpiCmd.Parameters.AddWithValue("@Id", masterMappingId);

                using var kdr = kpiCmd.ExecuteReader();
                int srNo = 1;
                while (kdr.Read())
                {
                    kpiRows.Add(new
                    {
                        empKpiRowId = Convert.ToInt32(kdr["EmpKpiRowId"]),
                        srNo = srNo++,
                        kpiId = Convert.ToInt32(kdr["KpiId"]),
                        kpiCode = kdr["KpiCode"]?.ToString() ?? "",
                        kpiName = kdr["KpiName"]?.ToString() ?? "",
                        pmpId = Convert.ToInt32(kdr["PmpId"]),
                        pmpName = kdr["PmpName"]?.ToString() ?? "",
                        kraId = Convert.ToInt32(kdr["KraId"]),
                        kraName = kdr["KraName"]?.ToString() ?? "",
                        unitOfMeasure = kdr["UnitOfMeasure"]?.ToString() ?? "%",
                        individualWeightage = Convert.ToDecimal(kdr["IndividualWeightage"]),
                        overallWeightage = 0,
                        target = Convert.ToDecimal(kdr["Target"])
                    });
                }

                return Json(new
                {
                    header.mappingId,
                    header.empId,
                    header.userId,
                    header.empCode,
                    header.empName,
                    header.email,
                    header.designation,
                    header.department,
                    header.appraisalYear,
                    header.rmName,
                    header.hodName,
                    header.unitHeadName,
                    header.modCommitteeNames,
                    header.jmdName,
                    header.lastUpdatedAt,
                    header.lastUpdatedBy,
                    header.status,
                    header.appraisalCycle,
                    header.cycleFromDate,
                    header.cycleToDate,
                    kpiRows
                });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }


        public IActionResult GetAllMappingsJson()
        {
            try
            {
                var data = GetAllMasterMappings();

                // If master mappings empty, try appraisal mappings as fallback
                if (!data.Any())
                    data = GetAllEmpMappings();

                return Json(data.Select(m => new
                {
                    m.MappingId,
                    m.SrNo,
                    m.UserId,
                    m.EmpId,
                    m.EmpCode,
                    m.EmpName,
                    m.Designation,
                    m.Department,
                    m.Email,
                    m.AppraisalYear,
                    m.AppraisalCycle,
                    m.RmName,
                    m.HodName,
                    m.UnitHeadName,
                    m.ModCommitteeNames,
                    m.JmdName,
                    cycleFromDate = m.CycleFromDate?.ToString("dd-MMM-yy") ?? "",
                    cycleToDate = m.CycleToDate?.ToString("dd-MMM-yy") ?? "",
                    lastUpdatedAt = m.LastUpdatedAt.HasValue
                        ? m.LastUpdatedAt.Value.ToString("dd-MMM-yy HH:mm")
                        : "",
                    lastUpdatedBy = m.LastUpdatedBy ?? "",
                    m.Status,
                    kpiCount = m.KpiCount > 0 ? m.KpiCount : m.KpiRows.Count,
                    createdAt = m.CreatedAt?.ToString("dd-MMM-yy") ?? "—"
                }));
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }
        // ====================================================
        // PAS BASE PERCENTAGE MASTER
        // ====================================================
        [HttpPost]
        public async Task<IActionResult> SavePasBaseRow([FromBody] PasBaseRowDto dto)
        {
            try
            {
                var user = await _userManager.GetUserAsync(User);
                string by = user?.Id ?? "System";
                string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

                using var con = new SqlConnection(_conn);
                con.Open();

                if (dto.Id > 0)
                {
                    string oldRangeLabel = ""; decimal? oldMin = null; decimal oldMax = 0, oldBase = 0; int oldSort = 0;
                    using (var fetch = new SqlCommand(@"SELECT RangeLabel, PATRevenueMin, PATRevenueMax, BasePercentage, SortOrder FROM PAS_BasePercentageMaster WHERE Id=@Id AND IsDeleted=0", con))
                    {
                        fetch.Parameters.AddWithValue("@Id", dto.Id);
                        using var fr = fetch.ExecuteReader();
                        if (fr.Read())
                        {
                            oldRangeLabel = fr["RangeLabel"]?.ToString() ?? "";
                            oldMin = fr["PATRevenueMin"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(fr["PATRevenueMin"]);
                            oldMax = Convert.ToDecimal(fr["PATRevenueMax"]);
                            oldBase = Convert.ToDecimal(fr["BasePercentage"]);
                            oldSort = Convert.ToInt32(fr["SortOrder"]);
                        }
                    }
                    using (var cmd = new SqlCommand(@"UPDATE PAS_BasePercentageMaster SET RangeLabel=@RangeLabel,PATRevenueMin=@PATRevenueMin,PATRevenueMax=@PATRevenueMax,BasePercentage=@BasePercentage,SortOrder=@SortOrder,ModifiedBy=@By,ModifiedOn=GETDATE() WHERE Id=@Id AND IsDeleted=0", con))
                    {
                        cmd.Parameters.AddWithValue("@RangeLabel", dto.RangeLabel);
                        cmd.Parameters.AddWithValue("@PATRevenueMin", (object?)dto.PATRevenueMin ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@PATRevenueMax", dto.PATRevenueMax);
                        cmd.Parameters.AddWithValue("@BasePercentage", dto.BasePercentage);
                        cmd.Parameters.AddWithValue("@SortOrder", dto.SortOrder);
                        cmd.Parameters.AddWithValue("@By", by);
                        cmd.Parameters.AddWithValue("@Id", dto.Id);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var (fieldName, oldVal, newVal) in new[]
                    {
                        ("RangeLabel",    oldRangeLabel,              dto.RangeLabel ?? ""),
                        ("PATRevenueMin", oldMin?.ToString() ?? "NULL", dto.PATRevenueMin?.ToString() ?? "NULL"),
                        ("PATRevenueMax", oldMax.ToString(),           dto.PATRevenueMax.ToString()),
                        ("BasePercentage",oldBase.ToString(),          dto.BasePercentage.ToString()),
                        ("SortOrder",     oldSort.ToString(),          dto.SortOrder.ToString()),
                    })
                    {
                        if (oldVal != newVal)
                            LogPasChangeDetailed(con, "PAS_BasePercentageMaster", dto.Id, "UPDATE", fieldName, oldVal, newVal, by, ipAddress);
                    }
                }
                else
                {
                    using (var chk = new SqlCommand(@"SELECT COUNT(1) FROM PAS_BasePercentageMaster WHERE LTRIM(RTRIM(RangeLabel))=LTRIM(RTRIM(@RangeLabel)) AND IsDeleted=0", con))
                    {
                        chk.Parameters.AddWithValue("@RangeLabel", dto.RangeLabel);
                        if ((int)chk.ExecuteScalar() > 0)
                            return Json(new { success = false, message = $"Range label '{dto.RangeLabel}' already exists." });
                    }
                    int newId;
                    using (var cmd = new SqlCommand(@"INSERT INTO PAS_BasePercentageMaster(RangeLabel,PATRevenueMin,PATRevenueMax,BasePercentage,SortOrder,CreatedBy,CreatedAt) OUTPUT INSERTED.Id VALUES(@RangeLabel,@PATRevenueMin,@PATRevenueMax,@BasePercentage,@SortOrder,@By,GETDATE())", con))
                    {
                        cmd.Parameters.AddWithValue("@RangeLabel", dto.RangeLabel);
                        cmd.Parameters.AddWithValue("@PATRevenueMin", (object?)dto.PATRevenueMin ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@PATRevenueMax", dto.PATRevenueMax);
                        cmd.Parameters.AddWithValue("@BasePercentage", dto.BasePercentage);
                        cmd.Parameters.AddWithValue("@SortOrder", dto.SortOrder);
                        cmd.Parameters.AddWithValue("@By", by);
                        newId = (int)cmd.ExecuteScalar();
                    }
                    LogPasChangeDetailed(con, "PAS_BasePercentageMaster", newId, "INSERT", "ALL", null,
                        $"RangeLabel={dto.RangeLabel}, Min={dto.PATRevenueMin?.ToString() ?? "NULL"}, Max={dto.PATRevenueMax}, Base={dto.BasePercentage}%",
                        by, ipAddress);
                }

                return Json(new { success = true, message = "Range saved successfully." });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }


        /// <summary>
        [HttpGet]
        public IActionResult GetEmployeeReportingManager(int empId)
        {
            if (empId <= 0) return Json(new { found = false });

            try
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();

                using var cmd = new SqlCommand(@"
            SELECT
                sup.Emp_ID                                                                              AS RmEmpId,
                ISNULL(sup.Emp_code,'')                                                                  AS RmEmpCode,
                ISNULL(LTRIM(RTRIM(ISNULL(sup.Emp_First_Name,'')+' '+ISNULL(sup.Emp_Last_Name,''))),'')   AS RmFullName,
                ISNULL(des.Desig_Name,'')                                                                AS RmDesignation
            FROM   T0080_EMP_MASTER e
            INNER JOIN T0080_EMP_MASTER sup
                    ON  sup.Emp_ID = e.Emp_Superior
                    AND sup.Cmp_ID = e.Cmp_ID
                    AND ISNULL(sup.Emp_Left,'N') <> 'Y'
            LEFT JOIN T0040_DESIGNATION_MASTER des ON des.Desig_ID = sup.Desig_Id
            WHERE  e.Emp_ID = @EmpId
              AND  e.Cmp_ID = 2
              AND  e.Emp_Superior IS NOT NULL
              AND  e.Emp_Superior > 0", con);
                cmd.Parameters.AddWithValue("@EmpId", empId);

                using var dr = cmd.ExecuteReader();
                if (dr.Read())
                {
                    return Json(new
                    {
                        found = true,
                        rmEmpId = Convert.ToInt32(dr["RmEmpId"]),
                        rmEmpCode = dr["RmEmpCode"].ToString(),
                        rmName = dr["RmFullName"].ToString(),
                        rmDesignation = dr["RmDesignation"].ToString()
                    });
                }

                // Employee has no Emp_Superior set (e.g. top of hierarchy) — nothing to suggest
                return Json(new { found = false });
            }
            catch (Exception ex)
            {
                return Json(new { found = false, error = ex.Message });
            }
        }
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpPost]
        [IgnoreAntiforgeryToken]

        public async Task<IActionResult> DeletePasBaseRow(int id)
        {
            try
            {
                var user = await _userManager.GetUserAsync(User);
                string by = user?.Id ?? "System";
                string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

                using var con = new SqlConnection(_conn);
                con.Open();
                using (var chk = new SqlCommand(@"SELECT COUNT(1) FROM PAS_YearlyPATEntry WHERE BasePercentageMasterId=@Id AND IsDeleted=0", con))
                {
                    chk.Parameters.AddWithValue("@Id", id);
                    if ((int)chk.ExecuteScalar() > 0)
                        return Json(new { success = false, message = "Cannot delete — this range is referenced by one or more yearly PAT entries." });
                }
                using var cmd = new SqlCommand(@"UPDATE PAS_BasePercentageMaster SET IsDeleted=1,DeletedBy=@By,DeletedOn=GETDATE(),ModifiedBy=@By,ModifiedOn=GETDATE() WHERE Id=@Id AND IsDeleted=0", con);
                cmd.Parameters.AddWithValue("@By", by);
                cmd.Parameters.AddWithValue("@Id", id);
                int rows = cmd.ExecuteNonQuery();
                if (rows == 0) return Json(new { success = false, message = "Record not found or already deleted." });
                LogPasChangeDetailed(con, "PAS_BasePercentageMaster", id, "DELETE", "IsDeleted", "0", "1", by, ipAddress);
                return Json(new { success = true, message = "Range deleted successfully." });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        private void LogPasChangeDetailed(SqlConnection con, string tableName, int recordId, string actionType,
            string? fieldName, string? oldValue, string? newValue, string changedBy, string? ipAddress = null)
        {
            try
            {
                using (var ensure = new SqlCommand(@"
                    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PAS_ChangeLog')
                    BEGIN
                        CREATE TABLE PAS_ChangeLog (LogId INT IDENTITY(1,1) NOT NULL PRIMARY KEY, TableName NVARCHAR(100) NOT NULL, RecordId INT NOT NULL, ActionType NVARCHAR(20) NOT NULL, FieldName NVARCHAR(200) NULL, OldValue NVARCHAR(1000) NULL, NewValue NVARCHAR(1000) NULL, ChangedBy NVARCHAR(450) NOT NULL, ChangedOn DATETIME NOT NULL DEFAULT GETDATE(), SessionJson NVARCHAR(MAX) NULL, IPAddress NVARCHAR(50) NULL, Remarks NVARCHAR(500) NULL);
                        CREATE NONCLUSTERED INDEX IX_PAS_ChangeLog_Table_Record ON PAS_ChangeLog (TableName, RecordId);
                    END", con))
                    ensure.ExecuteNonQuery();

                using var log = new SqlCommand(@"INSERT INTO PAS_ChangeLog(TableName,RecordId,ActionType,FieldName,OldValue,NewValue,ChangedBy,ChangedOn,IPAddress) VALUES(@Table,@RecordId,@Action,@FieldName,@OldValue,@NewValue,@By,GETDATE(),@IP)", con);
                log.Parameters.AddWithValue("@Table", tableName);
                log.Parameters.AddWithValue("@RecordId", recordId);
                log.Parameters.AddWithValue("@Action", actionType);
                log.Parameters.AddWithValue("@FieldName", (object?)fieldName ?? DBNull.Value);
                log.Parameters.AddWithValue("@OldValue", (object?)oldValue ?? DBNull.Value);
                log.Parameters.AddWithValue("@NewValue", (object?)newValue ?? DBNull.Value);
                log.Parameters.AddWithValue("@By", changedBy);
                log.Parameters.AddWithValue("@IP", (object?)ipAddress ?? DBNull.Value);
                log.ExecuteNonQuery();
            }
            catch { }
        }

        private void LogPasChange(SqlConnection con, string tableName, int recordId, string actionType, string details, string changedBy)
            => LogPasChangeDetailed(con, tableName, recordId, actionType, null, null, details, changedBy, null);

        // ====================================================
        // RATING MATRIX
        // ====================================================
        [HttpPost]
        public IActionResult SaveRatingMatrix([FromBody] SaveRatingMatrixDto dto)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                foreach (var r in dto.Rows ?? new())
                {
                    using var cmd = new SqlCommand(@"UPDATE PAS_RatingHikeMaster SET RatingLabel=@RatingLabel, HikeOverBasePercent=@HikeOverBasePercent, UpdatedAt=GETDATE() WHERE Id=@Id", con);
                    cmd.Parameters.AddWithValue("@RatingLabel", r.RatingLabel ?? "");
                    cmd.Parameters.AddWithValue("@HikeOverBasePercent", r.HikeOverBasePercent);
                    cmd.Parameters.AddWithValue("@Id", r.Id);
                    cmd.ExecuteNonQuery();
                }
                return Json(new { success = true, message = "Rating matrix saved." });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        [HttpPost]
        public IActionResult SaveRmvVersion([FromBody] SaveRmvVersionDto dto)
        {
            try
            {
                if (!DateTime.TryParse(dto.EffectiveDate, out var effDate))
                    return Json(new { success = false, message = "Invalid Effective Date." });

                int versionId;
                using var con = new SqlConnection(_conn); con.Open();
                using var tx = con.BeginTransaction();

                if (dto.IsActive)
                {
                    using var deact = new SqlCommand("UPDATE PAS_RatingMasterVersion SET IsActive=0", con, tx);
                    deact.ExecuteNonQuery();
                }

                if (dto.Id > 0)
                {
                    using var upd = new SqlCommand(@"UPDATE PAS_RatingMasterVersion SET VersionName=@VersionName,EffectiveDate=@EffectiveDate,BasePctSample=@BasePctSample,IsActive=@IsActive,Remarks=@Remarks,UpdatedAt=GETDATE(),UpdatedBy=@UpdatedBy WHERE Id=@Id", con, tx);
                    upd.Parameters.AddWithValue("@VersionName", dto.VersionName);
                    upd.Parameters.AddWithValue("@EffectiveDate", effDate);
                    upd.Parameters.AddWithValue("@BasePctSample", (object?)dto.BasePctSample ?? DBNull.Value);
                    upd.Parameters.AddWithValue("@IsActive", dto.IsActive);
                    upd.Parameters.AddWithValue("@Remarks", (object?)(dto.Remarks) ?? DBNull.Value);
                    upd.Parameters.AddWithValue("@UpdatedBy", User.Identity?.Name ?? "System");
                    upd.Parameters.AddWithValue("@Id", dto.Id);
                    upd.ExecuteNonQuery();
                    versionId = dto.Id;
                    using var del = new SqlCommand("DELETE FROM PAS_RatingHikeDetail WHERE VersionId=@id", con, tx);
                    del.Parameters.AddWithValue("@id", versionId); del.ExecuteNonQuery();
                }
                else
                {
                    using var ins = new SqlCommand(@"INSERT INTO PAS_RatingMasterVersion(VersionName,EffectiveDate,BasePctSample,IsActive,Remarks,CreatedBy) OUTPUT INSERTED.Id VALUES(@VersionName,@EffectiveDate,@BasePctSample,@IsActive,@Remarks,@CreatedBy)", con, tx);
                    ins.Parameters.AddWithValue("@VersionName", dto.VersionName);
                    ins.Parameters.AddWithValue("@EffectiveDate", effDate);
                    ins.Parameters.AddWithValue("@BasePctSample", (object?)dto.BasePctSample ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@IsActive", dto.IsActive);
                    ins.Parameters.AddWithValue("@Remarks", (object?)(dto.Remarks) ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@CreatedBy", User.Identity?.Name ?? "System");
                    versionId = (int)ins.ExecuteScalar();
                }

                foreach (var r in dto.DetailRows ?? new())
                {
                    using var insD = new SqlCommand(@"INSERT INTO PAS_RatingHikeDetail(VersionId,Rating,RatingLabel,RatingRangeMin,RatingRangeMax,DistributionPercent,HikeOverBasePercent,SortOrder) VALUES(@VersionId,@Rating,@RatingLabel,@RatingRangeMin,@RatingRangeMax,@DistributionPercent,@HikeOverBasePercent,@SortOrder)", con, tx);
                    insD.Parameters.AddWithValue("@VersionId", versionId);
                    insD.Parameters.AddWithValue("@Rating", r.Rating);
                    insD.Parameters.AddWithValue("@RatingLabel", r.RatingLabel ?? "");
                    insD.Parameters.AddWithValue("@RatingRangeMin", r.RatingRangeMin);
                    insD.Parameters.AddWithValue("@RatingRangeMax", r.RatingRangeMax);
                    insD.Parameters.AddWithValue("@DistributionPercent", r.DistributionPercent);
                    insD.Parameters.AddWithValue("@HikeOverBasePercent", r.HikeOverBasePercent);
                    insD.Parameters.AddWithValue("@SortOrder", r.SortOrder);
                    insD.ExecuteNonQuery();
                }

                tx.Commit();
                return Json(new { success = true, message = "Rating matrix version saved.", versionId });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        [HttpPost]
        public IActionResult ActivateRmvVersion(int id)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using var tx = con.BeginTransaction();
                using (var deact = new SqlCommand("UPDATE PAS_RatingMasterVersion SET IsActive=0", con, tx)) deact.ExecuteNonQuery();
                using (var act = new SqlCommand("UPDATE PAS_RatingMasterVersion SET IsActive=1 WHERE Id=@id", con, tx))
                { act.Parameters.AddWithValue("@id", id); act.ExecuteNonQuery(); }
                tx.Commit();
                return Json(new { success = true, message = "Version activated." });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        [HttpPost]
        public IActionResult DeleteRmvVersion(int id)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                bool isActive;
                using (var chk = new SqlCommand("SELECT ISNULL(IsActive,0) FROM PAS_RatingMasterVersion WHERE Id=@id", con))
                { chk.Parameters.AddWithValue("@id", id); var val = chk.ExecuteScalar(); isActive = val != null && val != DBNull.Value && Convert.ToBoolean(val); }
                if (isActive) return Json(new { success = false, message = "Cannot delete the active version. Set another version as active first." });
                using (var del = new SqlCommand("DELETE FROM PAS_RatingMasterVersion WHERE Id=@id", con))
                { del.Parameters.AddWithValue("@id", id); del.ExecuteNonQuery(); }
                return Json(new { success = true });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // ====================================================
        // KPI DOCUMENT UPLOAD / DOWNLOAD / DELETE
        // ====================================================
        [HttpPost]
        public async Task<IActionResult> UploadKpiDoc(IFormFile file, int empKpiRowId, int mappingId, string uploaderRole)
        {
            if (file == null || file.Length == 0) return Json(new { success = false, message = "No file received." });
            var allowed = new[] { ".jpg", ".jpeg", ".pdf", ".doc", ".docx" };
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!allowed.Contains(ext)) return Json(new { success = false, message = "Only JPEG, PDF, DOC/DOCX allowed." });
            if (file.Length > 10 * 1024 * 1024) return Json(new { success = false, message = "File must be under 10 MB." });

            var uploads = Path.Combine(_env.WebRootPath, "kpi-docs");
            Directory.CreateDirectory(uploads);
            var safeName = $"{Guid.NewGuid()}{ext}";
            var fullPath = Path.Combine(uploads, safeName);
            using (var fs = new FileStream(fullPath, FileMode.Create))
                await file.CopyToAsync(fs);

            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using (var ensure = new SqlCommand(@"IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='KpiDocuments') BEGIN CREATE TABLE KpiDocuments (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, EmpKpiRowId INT NOT NULL, MappingId INT NOT NULL, UploaderRole NVARCHAR(50) NOT NULL, FileName NVARCHAR(500) NOT NULL, OriginalFileName NVARCHAR(500) NOT NULL, UploadedAt DATETIME NOT NULL DEFAULT GETDATE()); CREATE NONCLUSTERED INDEX IX_KpiDocuments_RowId ON KpiDocuments(EmpKpiRowId); END", con))
                    ensure.ExecuteNonQuery();

                int newId;
                using (var cmd = new SqlCommand(@"INSERT INTO KpiDocuments(EmpKpiRowId,MappingId,UploaderRole,FileName,OriginalFileName,UploadedAt) OUTPUT INSERTED.Id VALUES(@RowId,@MappingId,@Role,@FileName,@OriginalFileName,GETDATE())", con))
                {
                    cmd.Parameters.AddWithValue("@RowId", empKpiRowId);
                    cmd.Parameters.AddWithValue("@MappingId", mappingId);
                    cmd.Parameters.AddWithValue("@Role", uploaderRole ?? "");
                    cmd.Parameters.AddWithValue("@FileName", safeName);
                    cmd.Parameters.AddWithValue("@OriginalFileName", file.FileName);
                    newId = (int)cmd.ExecuteScalar();
                }
                return Json(new { success = true, docId = newId, fileName = file.FileName, role = uploaderRole });
            }
            catch (Exception ex)
            {
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public IActionResult DeleteKpiDoc(int id)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                string fileName = null;
                using (var fetch = new SqlCommand("SELECT FileName FROM KpiDocuments WHERE Id=@Id", con))
                { fetch.Parameters.AddWithValue("@Id", id); var val = fetch.ExecuteScalar(); if (val == null || val == DBNull.Value) return Json(new { success = false, message = "Not found." }); fileName = val.ToString(); }
                using (var del = new SqlCommand("DELETE FROM KpiDocuments WHERE Id=@Id", con))
                { del.Parameters.AddWithValue("@Id", id); del.ExecuteNonQuery(); }
                if (!string.IsNullOrEmpty(fileName)) { var path = Path.Combine(_env.WebRootPath, "kpi-docs", fileName); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                return Json(new { success = true });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        public IActionResult DownloadKpiDoc(int id)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                string fileName = null, originalFileName = null;
                using (var cmd = new SqlCommand("SELECT FileName, OriginalFileName FROM KpiDocuments WHERE Id=@Id", con))
                { cmd.Parameters.AddWithValue("@Id", id); using var dr = cmd.ExecuteReader(); if (!dr.Read()) return NotFound(); fileName = dr["FileName"].ToString(); originalFileName = dr["OriginalFileName"].ToString(); }
                var path = Path.Combine(_env.WebRootPath, "kpi-docs", fileName);
                if (!System.IO.File.Exists(path)) return NotFound();
                var extFile = Path.GetExtension(fileName).ToLower();
                var mimeMap = new Dictionary<string, string> { { ".pdf", "application/pdf" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".doc", "application/msword" }, { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" } };
                var mime = mimeMap.TryGetValue(extFile, out var m) ? m : "application/octet-stream";
                return PhysicalFile(path, mime, originalFileName);
            }
            catch { return NotFound(); }
        }

        // ====================================================
        // YEARLY PAT ENTRY
        // ====================================================
        [HttpPost]
        public IActionResult SavePasYearlyEntry([FromBody] PasYearlyEntryDto dto)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                if (dto.Id > 0)
                {
                    using var cmd = new SqlCommand(@"UPDATE PAS_YearlyPATEntry SET FinancialYear=@FinancialYear,FYStartDate=@FYStartDate,FYEndDate=@FYEndDate,PATRevenuePercentDirect=@PATRevenuePercentDirect,PATAmount=@PATAmount,RevenueAmount=@RevenueAmount,PATRevenuePercent=@PATRevenuePercent,BasePercentageMasterId=@BasePercentageMasterId,BasePercentage=@BasePercentage,Remarks=@Remarks,IsFinalized=@IsFinalized,UpdatedAt=GETDATE(),UpdatedBy=@UpdatedBy WHERE Id=@Id", con);
                    cmd.Parameters.AddWithValue("@FinancialYear", dto.FinancialYear ?? "");
                    cmd.Parameters.AddWithValue("@FYStartDate", dto.FYStartDate == default ? DBNull.Value : (object)dto.FYStartDate);
                    cmd.Parameters.AddWithValue("@FYEndDate", dto.FYEndDate == default ? DBNull.Value : (object)dto.FYEndDate);
                    cmd.Parameters.AddWithValue("@PATRevenuePercentDirect", (object?)dto.PATRevenuePercentDirect ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PATAmount", (object?)dto.PATAmount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@RevenueAmount", (object?)dto.RevenueAmount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PATRevenuePercent", (object?)dto.PATRevenuePercent ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@BasePercentageMasterId", (object?)dto.BasePercentageMasterId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@BasePercentage", (object?)dto.BasePercentage ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Remarks", (object?)(dto.Remarks) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@IsFinalized", dto.IsFinalized);
                    cmd.Parameters.AddWithValue("@UpdatedBy", (object?)(User.Identity?.Name) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Id", dto.Id);
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    using var cmd = new SqlCommand(@"INSERT INTO PAS_YearlyPATEntry(FinancialYear,FYStartDate,FYEndDate,PATRevenuePercentDirect,PATAmount,RevenueAmount,PATRevenuePercent,BasePercentageMasterId,BasePercentage,Remarks,IsFinalized,CreatedBy) VALUES(@FinancialYear,@FYStartDate,@FYEndDate,@PATRevenuePercentDirect,@PATAmount,@RevenueAmount,@PATRevenuePercent,@BasePercentageMasterId,@BasePercentage,@Remarks,@IsFinalized,@CreatedBy)", con);
                    cmd.Parameters.AddWithValue("@FinancialYear", dto.FinancialYear ?? "");
                    cmd.Parameters.AddWithValue("@FYStartDate", dto.FYStartDate == default ? DBNull.Value : (object)dto.FYStartDate);
                    cmd.Parameters.AddWithValue("@FYEndDate", dto.FYEndDate == default ? DBNull.Value : (object)dto.FYEndDate);
                    cmd.Parameters.AddWithValue("@PATRevenuePercentDirect", (object?)dto.PATRevenuePercentDirect ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PATAmount", (object?)dto.PATAmount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@RevenueAmount", (object?)dto.RevenueAmount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PATRevenuePercent", (object?)dto.PATRevenuePercent ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@BasePercentageMasterId", (object?)dto.BasePercentageMasterId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@BasePercentage", (object?)dto.BasePercentage ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Remarks", (object?)(dto.Remarks) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@IsFinalized", dto.IsFinalized);
                    cmd.Parameters.AddWithValue("@CreatedBy", (object?)(User.Identity?.Name) ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                return Json(new { success = true, message = "Yearly PAT entry saved." });
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("UQ_FinancialYear"))
                    return Json(new { success = false, message = "An entry for this Financial Year already exists." });
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public IActionResult DeletePasYearly(int id)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using var cmd = new SqlCommand("DELETE FROM PAS_YearlyPATEntry WHERE Id=@id", con);
                cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
                return Json(new { success = true });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }



        /// <summary>
        /// Returns BA audit status and behavioural score using IDENTICAL logic to
        /// GetBaScoresForAnnualReport so PrintAppraisal and Annual Report always match.
        /// </summary>
        private (bool auditExists, bool auditCompleted, decimal behaviouralScore)
     GetBehaviouralAuditStatus(int empId, int mappingId = 0)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // ── 1. Check BA table exists ──
                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Main'", con))
                    if ((int)chk.ExecuteScalar() == 0) return (false, false, 0m);

                // ── 2. Resolve auditId ──
                int auditId = 0;

                // Priority 1: directly linked BehaviourAuditId on the mapping
                if (mappingId > 0)
                {
                    bool hasBaIdCol = false;
                    using (var cc = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='BehaviourAuditId'", con))
                        hasBaIdCol = (int)cc.ExecuteScalar() > 0;

                    if (hasBaIdCol)
                    {
                        using var mc = new SqlCommand(
                            "SELECT BehaviourAuditId FROM EmpAppraisalMapping " +
                            "WHERE MappingId=@Mid AND IsDeleted=0", con);
                        mc.Parameters.AddWithValue("@Mid", mappingId);
                        var linked = mc.ExecuteScalar();
                        // Explicitly handle: null means no row, DBNull means column is NULL
                        if (linked != null && linked != DBNull.Value)
                        {
                            int linkedId = Convert.ToInt32(linked);
                            if (linkedId > 0) auditId = linkedId;
                        }
                    }
                }

                // Priority 2: cycle-scoped fallback (NO FinalStatus filter)
                if (auditId == 0 && empId > 0)
                {
                    DateTime? cycleFrom = null, cycleTo = null;
                    if (mappingId > 0)
                    {
                        using var dateCmd = new SqlCommand(
                            "SELECT CycleFromDate, CycleToDate FROM EmpAppraisalMapping " +
                            "WHERE MappingId=@Mid AND IsDeleted=0", con);
                        dateCmd.Parameters.AddWithValue("@Mid", mappingId);
                        using var ddr = dateCmd.ExecuteReader();
                        if (ddr.Read())
                        {
                            cycleFrom = ddr[0] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(ddr[0]);
                            cycleTo = ddr[1] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(ddr[1]);
                        }
                    }

                    if (cycleFrom.HasValue && cycleTo.HasValue)
                    {
                        // ← FinalStatus filter REMOVED — find any audit in window
                        using var cmd = new SqlCommand(@"
                    SELECT TOP 1 AuditID
                    FROM   Behaviour_Audit_Main
                    WHERE  EmpID = @EmpId
                      AND  CreatedOn >= @From
                      AND  CreatedOn <= DATEADD(day, 1, @To)
                    ORDER BY AuditID DESC", con);
                        cmd.Parameters.AddWithValue("@EmpId", empId);
                        cmd.Parameters.AddWithValue("@From", cycleFrom.Value);
                        cmd.Parameters.AddWithValue("@To", cycleTo.Value);
                        var v = cmd.ExecuteScalar();
                        if (v != null && v != DBNull.Value)
                            auditId = Convert.ToInt32(v);
                    }
                    else
                    {
                        // No cycle dates — broad fallback, NO FinalStatus filter
                        using var fallback = new SqlCommand(@"
                    SELECT TOP 1 AuditID
                    FROM   Behaviour_Audit_Main
                    WHERE  EmpID = @EmpId
                    ORDER BY AuditID DESC", con);
                        fallback.Parameters.AddWithValue("@EmpId", empId);
                        var fv = fallback.ExecuteScalar();
                        if (fv != null && fv != DBNull.Value)
                            auditId = Convert.ToInt32(fv);
                    }
                }

                if (auditId == 0) return (false, false, 0m);

                // ── 3. Check completion ──
                bool auditCompleted = false;
                using (var sc = new SqlCommand(
                    "SELECT ISNULL(FinalStatus,''), ISNULL(AuditStatus,'') " +
                    "FROM Behaviour_Audit_Main WHERE AuditID=@Aid", con))
                {
                    sc.Parameters.AddWithValue("@Aid", auditId);
                    using var sr = sc.ExecuteReader();
                    if (!sr.Read()) return (false, false, 0m);
                    string fs = sr.GetString(0);
                    string as2 = sr.GetString(1);
                    // Completed = either FinalStatus OR both RM+HOD have submitted
                    auditCompleted = fs == "Completed" || as2 == "Completed";
                }

                // ── 4. Get MaxScore denominator from AuditList ──
                decimal maxTotal = 0m;
                try
                {
                    bool auditListExists = false;
                    using (var tlChk = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_AuditList'", con))
                        auditListExists = (int)tlChk.ExecuteScalar() > 0;

                    if (auditListExists)
                    {
                        using var mx = new SqlCommand(
                            "SELECT ISNULL(SUM(CAST(MaxScore AS DECIMAL(10,2))),0) " +
                            "FROM Behaviour_AuditList WHERE Category='BA'", con);
                        var mv = mx.ExecuteScalar();
                        if (mv != null && mv != DBNull.Value)
                            maxTotal = Convert.ToDecimal(mv);
                    }
                }
                catch { }

                // ── 5. Get RM + HOD scores from Behaviour_Audit_Details ──
                // ── 5. Get RM + HOD scores from Behaviour_Audit_Details ──
                decimal rmSum = 0m, hodSum = 0m;
                bool rmFound = false, hodFound = false;

                bool hasDetails = false;
                using (var dchk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Details'", con))
                {
                    hasDetails = Convert.ToInt32(dchk.ExecuteScalar()) > 0;
                }

                if (hasDetails)
                {
                    using var scoreCmd = new SqlCommand(@"
        SELECT
            ISNULL(SUM(CAST(RM_Score AS DECIMAL(10,2))),0) AS RmTotal,
            ISNULL(SUM(CAST(HOD_Score AS DECIMAL(10,2))),0) AS HodTotal,
            COUNT(*) AS TotalRows
        FROM Behaviour_Audit_Details
        WHERE AuditID = @Aid", con);

                    scoreCmd.Parameters.AddWithValue("@Aid", auditId);

                    using var sdr = scoreCmd.ExecuteReader();

                    if (sdr.Read())
                    {
                        rmSum = Convert.ToDecimal(sdr["RmTotal"]);
                        hodSum = Convert.ToDecimal(sdr["HodTotal"]);

                        int totalRows = Convert.ToInt32(sdr["TotalRows"]);

                        rmFound = totalRows > 0;
                        hodFound = totalRows > 0;
                    }
                }
                // ── 6. Fallback to main table if detail scores missing ──
                if (!rmFound || !hodFound)
                {
                    try
                    {
                        using var mq = new SqlCommand(@"
            SELECT
                ISNULL(Rating,0) AS Rating
            FROM Behaviour_Audit_Main
            WHERE AuditID = @Aid", con);

                        mq.Parameters.AddWithValue("@Aid", auditId);

                        var ratingObj = mq.ExecuteScalar();

                        if (ratingObj != null && ratingObj != DBNull.Value)
                        {
                            decimal rating = Convert.ToDecimal(ratingObj);

                            if (!rmFound)
                                rmSum = rating;

                            if (!hodFound)
                                hodSum = rating;
                        }
                    }
                    catch
                    {
                    }
                }
                // ── 7. Determine max possible score ──
                // If maxTotal still 0, derive from detail rows (max per row * row count)
                if (maxTotal <= 0)
                {
                    try
                    {
                        using var maxCmd = new SqlCommand(@"
                    SELECT ISNULL(SUM(CAST(MaxScore AS DECIMAL(10,2))), 0)
                    FROM Behaviour_AuditList", con);
                        var mv2 = maxCmd.ExecuteScalar();
                        if (mv2 != null && mv2 != DBNull.Value)
                            maxTotal = Convert.ToDecimal(mv2);
                    }
                    catch { }
                }

                // Last resort: infer max from the detail data itself
                // (each criterion seems to have max=10 based on your data showing 8,10,5 etc.)
                if (maxTotal <= 0)
                {
                    try
                    {
                        using var inferMax = new SqlCommand(@"
                    SELECT COUNT(*) * 10.0 
                    FROM Behaviour_Audit_Details 
                    WHERE AuditID = @Aid", con);
                        inferMax.Parameters.AddWithValue("@Aid", auditId);
                        var iv = inferMax.ExecuteScalar();
                        if (iv != null && iv != DBNull.Value)
                            maxTotal = Convert.ToDecimal(iv);
                    }
                    catch { }
                }

                if (maxTotal <= 0) maxTotal = 100m; // absolute fallback

                // ── 8. Normalise to 0–5 scale ──
                decimal rmScore5 = rmSum > 5 ? Math.Round(rmSum / maxTotal * 5m, 2) : Math.Round(rmSum, 2);
                decimal hodScore5 = hodSum > 5 ? Math.Round(hodSum / maxTotal * 5m, 2) : Math.Round(hodSum, 2);

                // ── 9. Average RM + HOD ──
                decimal behScore = 0m;
                bool hasRm = rmSum > 0;
                bool hasHod = hodSum > 0;

                if (hasRm && hasHod)
                    behScore = Math.Round((rmScore5 + hodScore5) / 2m, 2);
                else if (hasRm)
                    behScore = rmScore5;
                else if (hasHod)
                    behScore = hodScore5;

                return (true, auditCompleted, behScore);
            }
            catch { return (false, false, 0m); }
        }
        [HttpGet]
        public IActionResult GetPasYearlyPATJson()
        {
            var list = GetPasYearlyPAT();
            return Json(list.Select(y => new
            {
                id = y.Id,
                financialYear = y.FinancialYear,
                basePercentage = y.BasePercentage,
                pATRevenuePercentDirect = y.PATRevenuePercentDirect,
                pATRevenuePercent = y.PATRevenuePercent,
                isFinalized = y.IsFinalized
            }));
        }

        [HttpGet]
        public IActionResult GetSavedMcJmdRatings(int mappingId)
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                EnsureMcJmdColumns(con);
                using var cmd = new SqlCommand(@"
            SELECT 
                ISNULL(AnnualMcRating,  ModCommitteeAvgRating) AS McRating,
                ISNULL(AnnualJmdRating, JmdAvgRating)          AS JmdRating
            FROM EmpAppraisalMapping
            WHERE MappingId = @Id AND IsDeleted = 0", con);
                cmd.Parameters.AddWithValue("@Id", mappingId);
                using var dr = cmd.ExecuteReader();
                if (!dr.Read()) return Json(new { error = "Not found" });
                return Json(new
                {
                    mcRating = dr[0] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(dr[0]),
                    jmdRating = dr[1] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(dr[1])
                });
            }
            catch (Exception ex) { return Json(new { error = ex.Message }); }
        }
        [HttpPost]
        public async Task<IActionResult> SaveMcJmdRatings([FromBody] SaveMcJmdRequest req)
        {
            if (req == null || req.MappingId <= 0)
                return Json(new { success = false, message = "Invalid request." });

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                EnsureMcJmdColumns(con);

                var user = await _userManager.GetUserAsync(User);
                string by = user?.Id ?? "System";
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

                var setParts = new List<string> { "UpdatedAt = GETDATE()", "UpdatedBy = @By" };
                if (req.McRating.HasValue) setParts.Add("AnnualMcRating  = @McRating");
                if (req.JmdRating.HasValue)
                {
                    setParts.Add("AnnualJmdRating = @JmdRating");
                    // Mark as JMDReview when JMD rating is set (only upgrade, never downgrade)
                    setParts.Add("[Status] = CASE WHEN [Status] NOT IN ('HRFinalized') THEN 'JMDReview' ELSE [Status] END");
                }

                using var cmd = new SqlCommand(
                    $"UPDATE EmpAppraisalMapping SET {string.Join(", ", setParts)} " +
                    "WHERE MappingId = @Id AND IsDeleted = 0", con);

                cmd.Parameters.AddWithValue("@By", by);
                cmd.Parameters.AddWithValue("@Id", req.MappingId);
                if (req.McRating.HasValue) cmd.Parameters.AddWithValue("@McRating", req.McRating.Value);
                if (req.JmdRating.HasValue) cmd.Parameters.AddWithValue("@JmdRating", req.JmdRating.Value);

                int rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    return Json(new { success = false, message = "Record not found." });

                // ── Audit to PAS_ChangeLog ──────────────────────────────────────────
                if (req.McRating.HasValue)
                    LogPasChangeDetailed(con, "EmpAppraisalMapping", req.MappingId,
                        "MC_RATING_SET", "AnnualMcRating",
                        null, req.McRating.Value.ToString("0.##"),
                        by, ip);

                if (req.JmdRating.HasValue)
                    LogPasChangeDetailed(con, "EmpAppraisalMapping", req.MappingId,
                        "JMD_RATING_SET", "AnnualJmdRating",
                        null, req.JmdRating.Value.ToString("0.##"),
                        by, ip);
                // ───────────────────────────────────────────────────────────────────

                return Json(new { success = true, message = "Ratings saved." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private void EnsureMcJmdColumns(SqlConnection con)
        {
            try
            {
                using var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='AnnualMcRating')
                ALTER TABLE EmpAppraisalMapping ADD AnnualMcRating DECIMAL(4,2) NULL;
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                           WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='AnnualJmdRating')
                ALTER TABLE EmpAppraisalMapping ADD AnnualJmdRating DECIMAL(4,2) NULL;", con);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        [HttpGet]
        public IActionResult GetEmployeeGrossSalary(int empId)
        {
            try
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();

                // ── Step 1: Confirm employee belongs to Cmp_ID = 2, capture left status ──
                bool empFound = false;
                bool empLeft = false;
                DateTime? empLeftDate = null;

                using (var empCmd = new SqlCommand(@"
            SELECT ISNULL(Emp_Left,'N') AS Emp_Left, Emp_Left_Date
            FROM   T0080_EMP_MASTER
            WHERE  Emp_ID = @EmpId AND Cmp_ID = 2", con))
                {
                    empCmd.Parameters.AddWithValue("@EmpId", empId);
                    using var edr = empCmd.ExecuteReader();
                    if (edr.Read())
                    {
                        empFound = true;
                        empLeft = (edr["Emp_Left"]?.ToString() ?? "N").Trim()
                                  .Equals("Y", StringComparison.OrdinalIgnoreCase);
                        empLeftDate = edr["Emp_Left_Date"] == DBNull.Value
                            ? (DateTime?)null
                            : Convert.ToDateTime(edr["Emp_Left_Date"]);
                    }
                }

                if (!empFound)
                    return Json(new { success = false, gross = 0m, source = "none", reason = "EmpId not found under Cmp_ID=2" });

                // Cutoff date — today for active employees, capped at Emp_Left_Date if they've left
                DateTime asOfDate = (empLeft && empLeftDate.HasValue) ? empLeftDate.Value : DateTime.Now;

                // ── Priority 1: MOST RECENT increment row as of asOfDate ──
                // Deliberately NOT filtering on Is_Master_Rec — that flag can lag behind
                // reality. Instead we pick whichever row has the latest Increment_Date
                // that has actually occurred by asOfDate.
                decimal? incrementGross = null;
                using (var cmd = new SqlCommand(@"
            SELECT TOP 1 i.Gross_Salary
            FROM   T0095_INCREMENT i
            WHERE  i.Emp_ID = @EmpId
              AND  i.Cmp_ID = 2
              AND  i.Increment_Date <= @AsOf
            ORDER BY i.Increment_Date DESC, i.Increment_ID DESC", con))
                {
                    cmd.Parameters.AddWithValue("@EmpId", empId);
                    cmd.Parameters.AddWithValue("@AsOf", asOfDate);
                    var val = cmd.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                        incrementGross = Convert.ToDecimal(val);
                }

                if (incrementGross.HasValue && incrementGross.Value > 0)
                    return Json(new { success = true, gross = incrementGross.Value, source = "increment" });

                // ── Priority 2: No qualifying increment row — fall back to salary table ──
                using (var cmd = new SqlCommand(@"
            SELECT TOP 1 s.Gross_Salary
            FROM   T0200_MONTHLY_SALARY s
            WHERE  s.Emp_ID = @EmpId
              AND  s.Cmp_ID = 2
              AND  s.Salary_Status = 'P'
              AND  s.Month_End_Date <= @AsOf
            ORDER BY s.Month_End_Date DESC, s.Sal_Tran_ID DESC", con))
                {
                    cmd.Parameters.AddWithValue("@EmpId", empId);
                    cmd.Parameters.AddWithValue("@AsOf", asOfDate);
                    var val = cmd.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                    {
                        decimal gross = Convert.ToDecimal(val);
                        if (gross > 0)
                            return Json(new { success = true, gross, source = "salary" });
                    }
                }

                return Json(new { success = false, gross = 0m, source = "none" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, gross = 0m, error = ex.Message });
            }
        }
        [HttpGet]
        public IActionResult GetPmsScoresForAnnualReport(int mappingId)
        {
            try
            {
                var fullMapping = GetMappingByIdInternal(_conn, mappingId);
                if (fullMapping == null)
                    return Json(new { error = "Not found" });

                var rows = fullMapping.KpiRows;

                // ── Per-KPI average of ALL filled reviewers (Self + RM + HOD + UH)
                //    EXACTLY matching PrintAppraisal View 3 per-KPI avg column
                decimal? GetPerRowAvg(EmpKpiRow r)
                {
                    var vals = new List<decimal>();
                    if (r.EmpRating.HasValue) vals.Add(r.EmpRating.Value);
                    if (r.RmRating.HasValue) vals.Add(r.RmRating.Value);
                    if (r.HodRating.HasValue) vals.Add(r.HodRating.Value);
                    if (r.UnitHeadRating.HasValue) vals.Add(r.UnitHeadRating.Value);
                    return vals.Any() ? (decimal?)Math.Round(vals.Average(), 2) : null;
                }

                // ── CRITICAL: normalize by actual total weight (matches PrintAppraisal _totalKpiWt logic)
                //    If weights sum to 100 → same as /100. If they don't, this normalises correctly.
                decimal totalKpiWt = rows.Sum(r => r.IndividualWeightage);

                // ── BSC Score = SUM(IndividualWeightage/totalKpiWt × per-KPI-avg)
                //    This is IDENTICAL to PrintAppraisal View 3 Wt.Rating column orange total
                decimal bscScore = 0m;
                if (totalKpiWt > 0)
                {
                    bscScore = Math.Round(
                        rows.Sum(r => {
                            var rt = GetPerRowAvg(r);
                            if (!rt.HasValue) return 0m;
                            return Math.Round((r.IndividualWeightage / totalKpiWt) * rt.Value, 4);
                        }), 2);
                }

                // ── Fallback: cascade to deepest filled stage if no reviewers filled yet
                if (bscScore == 0m && rows.Any() && totalKpiWt > 0)
                {
                    decimal? GetCascade(EmpKpiRow r) =>
                        r.HrFinalRating ?? r.JmdRating ?? r.ModCommitteeRating ??
                        r.UnitHeadRating ?? r.HodRating ?? r.RmRating ?? r.EmpRating;

                    bscScore = Math.Round(
                        rows.Sum(r => {
                            var rt = GetCascade(r);
                            return rt.HasValue
                                ? Math.Round(r.IndividualWeightage / totalKpiWt * rt.Value, 4)
                                : 0m;
                        }), 2);
                }

                // ── Individual reviewer simple averages (for display-only Self/RM/HOD/UH columns)
                decimal? SimpleAvg(Func<EmpKpiRow, decimal?> get)
                {
                    var filled = rows.Where(r => get(r).HasValue).ToList();
                    if (!filled.Any()) return null;
                    return Math.Round(filled.Average(r => get(r)!.Value), 2);
                }

                return Json(new
                {
                    empScore = (object)(SimpleAvg(r => r.EmpRating) ?? (decimal?)null),
                    rmScore = (object)(SimpleAvg(r => r.RmRating) ?? (decimal?)null),
                    hodScore = (object)(SimpleAvg(r => r.HodRating) ?? (decimal?)null),
                    uhScore = (object)(SimpleAvg(r => r.UnitHeadRating) ?? (decimal?)null),
                    bscScore = (object)bscScore   // ← Normalized Wt.Rating SUM = PrintAppraisal orange total
                });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> DeleteMasterMapping(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // ── Resolve EmpId for this master row ──
                int empId = 0;
                using (var getEmp = new SqlCommand(
                    "SELECT EmpId FROM EmpMappingMaster WHERE MasterMappingId=@Id AND IsDeleted=0", con))
                {
                    getEmp.Parameters.AddWithValue("@Id", id);
                    var v = getEmp.ExecuteScalar();
                    if (v == null || v == DBNull.Value)
                        return Json(new { success = false, message = "Mapping not found or already deleted." });
                    empId = Convert.ToInt32(v);
                }

                // ── VALIDATION: block delete if any assigned cycle is IN PROGRESS ──
                // ("In progress" = anything beyond Draft — employee/reviewers have started filling it)
                var activeCycles = new List<(string cycle, int year, string status)>();
                using (var chk = new SqlCommand(@"
            SELECT AppraisalCycle, AppraisalYear, [Status]
            FROM   EmpAppraisalMapping
            WHERE  EmpId = @EmpId AND IsDeleted = 0", con))
                {
                    chk.Parameters.AddWithValue("@EmpId", empId);
                    using var dr = chk.ExecuteReader();
                    while (dr.Read())
                        activeCycles.Add((
                            dr["AppraisalCycle"].ToString(),
                            Convert.ToInt32(dr["AppraisalYear"]),
                            dr["Status"].ToString()));
                }

                // ── REPLACE WITH THIS ──
                if (activeCycles.Any())
                {
                    string list = string.Join(", ", activeCycles.Select(c => $"{c.cycle} {c.year} ({c.status})"));
                    return Json(new
                    {
                        success = false,
                        message = $"Cannot delete — an appraisal cycle has already been assigned to this employee: {list}. " +
                                   "Please remove the cycle assignment from the 'Sent Cycles' tab first, then delete this mapping."
                    });
                }

                // ── Safe to proceed: soft-delete master + KPI rows ──
                using (var kpi = new SqlCommand(
                    "UPDATE EmpMappingMasterKpi SET IsDeleted=1 WHERE MasterMappingId=@Id", con))
                { kpi.Parameters.AddWithValue("@Id", id); kpi.ExecuteNonQuery(); }

                using (var cmd = new SqlCommand(
                    "UPDATE EmpMappingMaster SET IsDeleted=1, DeletedBy=@By, DeletedOn=GETDATE() WHERE MasterMappingId=@Id", con))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.Parameters.AddWithValue("@By", by);
                    cmd.ExecuteNonQuery();
                }

                // ── CASCADE: soft-delete any still-Draft cycle assignments so no ghost rows remain ──
                int cascaded = 0;
                using (var cascade = new SqlCommand(@"
            UPDATE EmpAppraisalMapping
            SET    IsDeleted = 1, DeletedBy = @By, DeletedOn = GETDATE()
            WHERE  EmpId = @EmpId AND IsDeleted = 0 AND [Status] = 'Draft'", con))
                {
                    cascade.Parameters.AddWithValue("@EmpId", empId);
                    cascade.Parameters.AddWithValue("@By", by);
                    cascaded = cascade.ExecuteNonQuery();
                }

                string msg = "Mapping deleted." +
                    (cascaded > 0 ? $" {cascaded} unstarted cycle assignment(s) removed as well." : "");

                return Json(new { success = true, message = msg });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // Add to PmsController
        [HttpPost]
        public async Task<IActionResult> TryAdvancePmsAfterBa(
            [FromBody] AdvancePmsAfterBaRequest req)
        {
            if (req == null || req.AuditId <= 0)
                return Json(new { success = false });

            try
            {
                // Find mappings linked to this audit that are stuck at EmployeeFilled or RMReview
                // i.e. PMS was submitted by RM/Employee but BA was blocking advancement
                using var con = new SqlConnection(_conn);
                con.Open();

                // Find mapping linked to this auditId
                var mappingIds = new List<int>();
                using (var cmd = new SqlCommand(
                    @"SELECT MappingId FROM EmpAppraisalMapping 
              WHERE BehaviourAuditId = @AuditId 
                AND IsDeleted = 0
                AND [Status] IN ('EmployeeFilled', 'RMReview')", con))
                {
                    cmd.Parameters.AddWithValue("@AuditId", req.AuditId);
                    using var dr = cmd.ExecuteReader();
                    while (dr.Read())
                        mappingIds.Add(Convert.ToInt32(dr["MappingId"]));
                }

                string baseUrl = GetBaseUrl();

                foreach (int mappingId in mappingIds)
                {
                    var mapping = GetMappingByIdInternal(_conn, mappingId);
                    if (mapping == null) continue;

                    string completedRole = mapping.Status == "EmployeeFilled" ? "Employee" : "RM";

                    // Verify BA is now actually complete for this role
                    int baAuditId = GetBehaviouralAuditId(_conn, mapping.EmpId, mappingId);
                    if (baAuditId == 0) continue;

                    bool baComplete;
                    using (var baCon = new SqlConnection(_conn))
                    {
                        baCon.Open();
                        baComplete = IsBaCompletedForAudit(baCon, baAuditId, completedRole, mappingId);
                    }

                    if (!baComplete) continue;

                    _log.LogInformation(
                        "[PMS TryAdvance] BA now complete for mappingId={Id} role={Role} — advancing",
                        mappingId, completedRole);

                    var user = await _userManager.GetUserAsync(User);
                    string by = user?.Id ?? "System";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SendToNextReviewer(_email, mapping, baseUrl, mappingId,
                                completedRole, _conn);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex,
                                "[PMS TryAdvance] SendToNextReviewer failed for mappingId={Id}",
                                mappingId);
                        }
                    });
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[PMS TryAdvance] Failed for auditId={AuditId}", req.AuditId);
                return Json(new { success = false, message = ex.Message });
            }
        }

        // Clears cycle assignment without deleting the mapping or KPI rows
        [HttpPost]
        public async Task<IActionResult> DeleteCycleAssignment(int mappingId)
        {
            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                bool hasRmCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmName");
                bool hasHodCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodName");
                bool hasUhCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadName");
                bool hasModCol = ColExistsInTable(con, "EmpAppraisalMapping", "ModCommitteeNames");
                bool hasJmdCol = ColExistsInTable(con, "EmpAppraisalMapping", "JmdName");
                var sets = new System.Text.StringBuilder(
                    "AppraisalCycle='', CycleFromDate=NULL, CycleToDate=NULL," +
                    "[Status]='Draft', UpdatedBy=@By, UpdatedAt=GETDATE()");

                if (hasRmCol) sets.Append(", RmName=NULL");
                if (hasHodCol) sets.Append(", HodName=NULL");
                if (hasUhCol) sets.Append(", UnitHeadName=NULL");
                if (hasModCol) sets.Append(", ModCommitteeNames=NULL");
                if (hasJmdCol) sets.Append(", JmdName=NULL");
                sets.Append(@",
                    IsDeleted = 1,
                    DeletedBy = @By,
                    DeletedOn = GETDATE()
                ");
                // Also clear KPI fill data so it can be used fresh next cycle
                using var kpiCmd = new SqlCommand(@"
            UPDATE EmpAppraisalKpiRows
            SET  EmpActual=NULL, EmpRating=NULL,
                 RmActual=NULL,  RmRating=NULL,  RmRemarks=NULL,
                 HodActual=NULL, HodRating=NULL, HodRemarks=NULL,
                 UnitHeadActual=NULL, UnitHeadRating=NULL, UnitHeadRemarks=NULL,
                 ModCommitteeActual=NULL, ModCommitteeRating=NULL,
                 ModCommitteeRemarks=NULL, JmdActual=NULL, JmdRating=NULL,
                 JmdRemarks=NULL, HrFinalRating=NULL, WeightedRating=NULL
            WHERE MappingId=@Id AND IsDeleted=0", con);
                kpiCmd.Parameters.AddWithValue("@Id", mappingId);
                kpiCmd.ExecuteNonQuery();

                using var cmd = new SqlCommand(
                    $"UPDATE EmpAppraisalMapping SET {sets} WHERE MappingId=@Id AND IsDeleted=0", con);
                cmd.Parameters.AddWithValue("@By", by);
                cmd.Parameters.AddWithValue("@Id", mappingId);
                int rows = cmd.ExecuteNonQuery();

                if (rows == 0)
                    return Json(new { success = false, message = "Mapping not found." });

                return Json(new { success = true, message = "Cycle assignment removed." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult DebugMappings()
        {
            var result = new List<object>();
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                // Check table exists
                int tableExists = 0;
                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EmpMappingMaster'", con))
                    tableExists = (int)chk.ExecuteScalar();

                // Check columns
                bool hasUpdatedAt = ColExistsInTable(con, "EmpMappingMaster", "UpdatedAt");
                bool hasUpdatedBy = ColExistsInTable(con, "EmpMappingMaster", "UpdatedBy");

                // Count rows
                int rowCount = 0;
                if (tableExists > 0)
                {
                    using var cnt = new SqlCommand(
                        "SELECT COUNT(1) FROM EmpMappingMaster WHERE IsDeleted=0", con);
                    rowCount = (int)cnt.ExecuteScalar();
                }

                // Count KPI rows
                int kpiCount = 0;
                using (var kchk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EmpMappingMasterKpi'", con))
                {
                    if ((int)kchk.ExecuteScalar() > 0)
                    {
                        using var kcnt = new SqlCommand(
                            "SELECT COUNT(1) FROM EmpMappingMasterKpi WHERE IsDeleted=0", con);
                        kpiCount = (int)kcnt.ExecuteScalar();
                    }
                }

                return Json(new
                {
                    tableExists,
                    hasUpdatedAt,
                    hasUpdatedBy,
                    masterMappingRowCount = rowCount,
                    masterKpiRowCount = kpiCount,
                    connString = _conn.Substring(0, Math.Min(50, _conn.Length)) + "..."
                });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message, stack = ex.StackTrace });
            }
        }
        // Updates only reviewer fields — called from the Edit Reviewers modal in Sent Cycles
        [HttpPost]
        public async Task<IActionResult> UpdateReviewers([FromBody] UpdateReviewersRequest req)
        {
            if (req == null || req.MappingId <= 0)
                return Json(new { success = false, message = "Invalid request." });

            var user = await _userManager.GetUserAsync(User);
            string by = user?.Id ?? "System";

            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                bool hasRmCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmName");
                bool hasHodCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodName");
                bool hasUhCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadName");
                bool hasModCol = ColExistsInTable(con, "EmpAppraisalMapping", "ModCommitteeNames");
                bool hasJmdCol = ColExistsInTable(con, "EmpAppraisalMapping", "JmdName");

                var sets = new System.Text.StringBuilder("UpdatedBy=@By, UpdatedAt=GETDATE()");
                if (hasRmCol) sets.Append(", RmName=@Rm");
                if (hasHodCol) sets.Append(", HodName=@Hod");
                if (hasUhCol) sets.Append(", UnitHeadName=@Uh");
                if (hasModCol) sets.Append(", ModCommitteeNames=@Mod");
                if (hasJmdCol) sets.Append(", JmdName=@Jmd");

                using var cmd = new SqlCommand(
                    $"UPDATE EmpAppraisalMapping SET {sets} WHERE MappingId=@Id AND IsDeleted=0", con);
                cmd.Parameters.AddWithValue("@By", by);
                cmd.Parameters.AddWithValue("@Id", req.MappingId);
                if (hasRmCol) cmd.Parameters.AddWithValue("@Rm", req.RmName ?? "");
                if (hasHodCol) cmd.Parameters.AddWithValue("@Hod", req.HodName ?? "");
                if (hasUhCol) cmd.Parameters.AddWithValue("@Uh", req.UnitHeadName ?? "");
                if (hasModCol) cmd.Parameters.AddWithValue("@Mod", req.ModCommitteeNames ?? "");
                if (hasJmdCol) cmd.Parameters.AddWithValue("@Jmd", req.JmdName ?? "");

                int rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    return Json(new { success = false, message = "Record not found." });

                // ── Read current status + BA audit id ──────────────────────────────
                string currentStatus = "Draft";
                int baAuditId = 0;
                using (var sc = new SqlCommand(@"
            SELECT [Status], ISNULL(BehaviourAuditId,0) AS BaId
            FROM   EmpAppraisalMapping
            WHERE  MappingId=@Id AND IsDeleted=0", con))
                {
                    sc.Parameters.AddWithValue("@Id", req.MappingId);
                    using var scr = sc.ExecuteReader();
                    if (scr.Read())
                    {
                        currentStatus = scr["Status"]?.ToString() ?? "Draft";
                        baAuditId = Convert.ToInt32(scr["BaId"]);
                    }
                }

                // ── Determine which reviewers actually changed ──────────────────────
                bool rmChanged = !string.Equals(req.PrevRmName ?? "", req.RmName ?? "", StringComparison.OrdinalIgnoreCase);
                bool hodChanged = !string.Equals(req.PrevHodName ?? "", req.HodName ?? "", StringComparison.OrdinalIgnoreCase);
                bool uhChanged = !string.Equals(req.PrevUhName ?? "", req.UnitHeadName ?? "", StringComparison.OrdinalIgnoreCase);

                // ── Fire targeted email only when cycle is active ───────────────────
                if (currentStatus != "Draft" && (rmChanged || hodChanged || uhChanged))
                {
                    string baseUrl = GetBaseUrl();
                    int capturedId = req.MappingId;
                    int capturedBaId = baAuditId;
                    string capturedConn = _conn;
                    string capturedStatus = currentStatus;
                    IEmailService capturedEmail = _email;
                    ILogger<PmsController> capturedLog = _log;

                    bool snapRmChanged = rmChanged;
                    bool snapHodChanged = hodChanged;
                    bool snapUhChanged = uhChanged;
                    string newRm = req.RmName ?? "";
                    string newHod = req.HodName ?? "";
                    string newUh = req.UnitHeadName ?? "";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var mapping = GetMappingByIdInternal(capturedConn, capturedId);
                            if (mapping == null) return;

                            if (capturedBaId == 0)
                                capturedBaId = GetBehaviouralAuditId(capturedConn, mapping.EmpId, capturedId);

                            var notified = new List<string>();

                            // ── RM changed and RM is the current waiting actor ──────
                            // Status = EmployeeFilled means employee submitted, RM must act
                            if (snapRmChanged && !string.IsNullOrWhiteSpace(newRm)
                                && capturedStatus == "EmployeeFilled")
                            {
                                string pmsUrl = $"{baseUrl}/Pms/RmAppraisal?mappingId={capturedId}";
                                string baUrl = capturedBaId > 0
                                    ? $"{baseUrl}/BehaviouralAudit/RMForm/{capturedBaId}"
                                    : string.Empty;
                                await capturedEmail.SendRmReviewNotificationAsync(mapping, pmsUrl, baUrl, baseUrl);
                                notified.Add($"RM ({newRm})");
                            }

                            // ── HOD changed and HOD is the current waiting actor ────
                            // Status = RMReview means RM submitted, HOD must act
                            if (snapHodChanged && !string.IsNullOrWhiteSpace(newHod)
                                && capturedStatus == "RMReview")
                            {
                                string pmsUrl = $"{baseUrl}/Pms/HodAppraisal?mappingId={capturedId}";
                                string baUrl = capturedBaId > 0
                                    ? $"{baseUrl}/BehaviouralAudit/HODForm/{capturedBaId}"
                                    : string.Empty;
                                await capturedEmail.SendHodReviewNotificationAsync(mapping, pmsUrl, baUrl, baseUrl);
                                notified.Add($"HOD ({newHod})");
                            }

                            // ── UH changed and UH is the current waiting actor ──────
                            // Status = HODReview means HOD submitted, UH must act
                            if (snapUhChanged && !string.IsNullOrWhiteSpace(newUh)
                                && capturedStatus == "HODReview")
                            {
                                string pmsUrl = $"{baseUrl}/Pms/UnitHeadAppraisal?mappingId={capturedId}";
                                await capturedEmail.SendUnitHeadReviewNotificationAsync(mapping, pmsUrl, baseUrl);
                                notified.Add($"UH ({newUh})");
                            }

                            if (notified.Any())
                                capturedLog.LogInformation(
                                    "[UpdateReviewers] Email sent for mappingId={Id}: {List}",
                                    capturedId, string.Join(", ", notified));
                            else
                                capturedLog.LogInformation(
                                    "[UpdateReviewers] Reviewer changed but not current actor — no email. " +
                                    "mappingId={Id} status={Status}", capturedId, capturedStatus);
                        }
                        catch (Exception ex)
                        {
                            capturedLog.LogError(ex,
                                "[UpdateReviewers] Email task failed for mappingId={Id}", capturedId);
                        }
                    });
                }

                // ── Build friendly response message ────────────────────────────────
                var changedList = new List<string>();
                if (rmChanged) changedList.Add("RM");
                if (hodChanged) changedList.Add("HOD");
                if (uhChanged) changedList.Add("UH");

                string msg = changedList.Any()
                    ? $"Reviewers updated ({string.Join(", ", changedList)} changed). " +
                      (currentStatus != "Draft"
                          ? "Email sent to the newly assigned reviewer."
                          : "Cycle not yet active — no email sent.")
                    : "Reviewers saved (no changes detected).";

                return Json(new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        public IActionResult GetAppraisalMappingsJson()
        {
            try
            {
                var mappings = GetAllEmpMappings();

                // Batch-load MC/JMD ratings for Annual mappings
                var ratingsMap = new Dictionary<int, (decimal? Mc, decimal? Jmd)>();
                var annualIds = mappings
                    .Where(m => (m.AppraisalCycle ?? "").Trim()
                                .Equals("Annual", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.MappingId).ToList();

                if (annualIds.Any())
                {
                    using var con = new SqlConnection(_conn);
                    con.Open();
                    EnsureMcJmdColumns(con);
                    using var cmd = new SqlCommand($@"
SELECT MappingId,
       ISNULL(AnnualMcRating,  ModCommitteeAvgRating) AS McRating,
       ISNULL(AnnualJmdRating, JmdAvgRating)          AS JmdRating
FROM   EmpAppraisalMapping
WHERE  MappingId IN ({string.Join(",", annualIds)}) AND IsDeleted = 0", con);
                    using var dr = cmd.ExecuteReader();
                    while (dr.Read())
                    {
                        int mid = Convert.ToInt32(dr["MappingId"]);
                        ratingsMap[mid] = (
                            dr["McRating"] == DBNull.Value ? null : Convert.ToDecimal(dr["McRating"]),
                            dr["JmdRating"] == DBNull.Value ? null : Convert.ToDecimal(dr["JmdRating"])
                        );
                    }
                }

                return Json(mappings.Select(m => new
                {
                    m.MappingId,
                    m.UserId,
                    m.EmpId,
                    m.EmpCode,
                    m.EmpName,
                    m.Designation,
                    m.Department,
                    m.Email,
                    m.AppraisalYear,
                    m.AppraisalCycle,
                    m.RmName,
                    m.HodName,
                    m.UnitHeadName,
                    m.ModCommitteeNames,
                    m.JmdName,
                    cycleFromDate = m.CycleFromDate?.ToString("dd-MMM-yy") ?? "",
                    cycleToDate = m.CycleToDate?.ToString("dd-MMM-yy") ?? "",
                    m.Status,
                    kpiCount = m.KpiRows.Count,
                    createdAt = m.CreatedAt?.ToString("dd-MMM-yy") ?? "—",
                    mcRating = ratingsMap.TryGetValue(m.MappingId, out var r1) ? r1.Mc : (decimal?)null,
                    jmdRating = ratingsMap.TryGetValue(m.MappingId, out var r2) ? r2.Jmd : (decimal?)null
                }));
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }
        public class AdvancePmsAfterBaRequest
        {
            public int AuditId { get; set; }
            public string Stage { get; set; } // "RM" or "HOD"
        }


        [HttpGet]
        public IActionResult DebugMasterMappingsQuery()
        {
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(@"
            SELECT TOP 5
                mm.MasterMappingId, mm.EmpId, mm.AppraisalYear,
                ISNULL(e.Emp_code,'') AS EMPCode,
                ISNULL(dept.Dept_Name,'') AS DepartmentName,
                ISNULL(des.Desig_Name,'') AS DesignationName
            FROM EmpMappingMaster mm
            LEFT JOIN [Orange_Hrms].[dbo].[T0080_EMP_MASTER] e ON e.Emp_ID = mm.EmpId
            LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER] dept ON dept.Dept_Id = e.Dept_ID
            LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des ON des.Desig_ID = e.Desig_Id
            WHERE mm.IsDeleted = 0
            ORDER BY mm.MasterMappingId DESC", con);
                using var dr = cmd.ExecuteReader();
                int count = 0;
                while (dr.Read()) count++;
                return Json(new { success = true, rowsRead = count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.ToString() });
            }
        }
        // ====================================================
        // REVIEWER DASHBOARD — UNIFIED MAPPINGS ENDPOINT
        // Used by RM / HOD / UnitHead / Employee dashboards.
        // Returns mappings relevant to the logged-in reviewer with
        // FY filter applied server-side, and PMS + BA links pre-built.
        // Pending/Completed split is computed by the client using
        // the "myPendingStatus" field returned per row.
   
        // ====================================================
        // PRIVATE HELPERS
        // ====================================================


        private void EnsureMasterTimestampColumns(SqlConnection con)
        {
            try
            {
                using var cmd = new SqlCommand(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME='EmpMappingMaster' AND COLUMN_NAME='UpdatedAt'
            )
                ALTER TABLE EmpMappingMaster ADD UpdatedAt DATETIME NULL;
 
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME='EmpMappingMaster' AND COLUMN_NAME='UpdatedBy'
            )
                ALTER TABLE EmpMappingMaster ADD UpdatedBy NVARCHAR(450) NULL;
        ", con);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private EmpKpiMappingModel GetMasterMappingByIdInternal(string connStr, int masterMappingId)
        {
            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();

                EmpKpiMappingModel mapping = null;
                using (var cmd = new SqlCommand(@"
    SELECT
        mm.MasterMappingId             AS MappingId,
        mm.EmpId,
        mm.UserId,
        mm.AppraisalYear,
        ''                             AS AppraisalCycle,
        NULL                           AS CycleFromDate,
        NULL                           AS CycleToDate,
        'Master'                       AS [Status],
        mm.CreatedBy,
        mm.CreatedAt,
        ISNULL(e.Emp_code,'')          AS EMPCode,
        ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
        ISNULL(e.Work_Email,'')        AS Email,
        CAST(ISNULL(e.Emp_Superior,0) AS NVARCHAR(50)) AS ReportTo,
        ISNULL(dept.Dept_Name,'')      AS DepartmentName,
        ''                             AS SubDepartmentName,
        ISNULL(des.Desig_Name,'')      AS DesignationName,
        ISNULL(mm.RmName,'')           AS RmName,
        ISNULL(mm.HodName,'')          AS HodName,
        ISNULL(mm.UnitHeadName,'')     AS UnitHeadName,
        ISNULL(mm.ModCommitteeNames,'')AS ModCommitteeNames,
        ISNULL(mm.JmdName,'')          AS JmdName,
        ''                             AS UnitName
    FROM EmpMappingMaster mm
    LEFT JOIN [Orange_Hrms].[dbo].[T0080_EMP_MASTER]        e    ON e.Emp_ID    = mm.EmpId
    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER]  dept ON dept.Dept_Id = e.Dept_ID
    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des  ON des.Desig_ID = e.Desig_Id
    WHERE mm.MasterMappingId = @Id AND mm.IsDeleted = 0", con))
                {
                    cmd.Parameters.AddWithValue("@Id", masterMappingId);
                    using var dr = cmd.ExecuteReader();
                    if (dr.Read())
                        mapping = MapMappingFromReaderSafe(dr, true);
                }

                if (mapping == null) return null;

                using (var kpiCmd = new SqlCommand(@"
            SELECT
                mk.Id                          AS EmpKpiRowId,
                mk.KpiId, mk.PmpId, mk.KraId,
                mk.KpiName, mk.UnitOfMeasure,
                mk.IndividualWeightage,
                0                              AS OverallWeightage,
                mk.Target,
                NULL AS EmpActual,   NULL AS EmpRating,
                NULL AS RmActual,    NULL AS RmRating,    '' AS RmRemarks,
                NULL AS HodActual,   NULL AS HodRating,   '' AS HodRemarks,
                NULL AS UnitHeadActual,  NULL AS UnitHeadRating,  '' AS UnitHeadRemarks,
                NULL AS ModCommitteeActual, NULL AS ModCommitteeRating, '' AS ModCommitteeRemarks,
                NULL AS JmdActual,   NULL AS JmdRating,   '' AS JmdRemarks,
                NULL AS HrFinalRating,   NULL AS WeightedRating,
                ISNULL(k.KpiCode,'')           AS KpiCode,
                ISNULL(p.PmpName,'')           AS PmpName,
                ISNULL(r.KraName,'')           AS KraName
            FROM EmpMappingMasterKpi mk
            INNER JOIN KpiMaster  k ON k.KpiId = mk.KpiId AND k.IsDeleted = 0
            INNER JOIN PmpMaster  p ON p.PmpId = mk.PmpId AND p.IsDeleted = 0
            INNER JOIN KraMaster  r ON r.KraId = mk.KraId AND r.IsDeleted = 0
            WHERE mk.MasterMappingId = @Id AND mk.IsDeleted = 0
            ORDER BY mk.SortOrder", con))
                {
                    kpiCmd.Parameters.AddWithValue("@Id", masterMappingId);
                    using var kdr = kpiCmd.ExecuteReader();
                    int i = 1;
                    while (kdr.Read())
                        mapping.KpiRows.Add(MapKpiRowFromReader(kdr, i++));
                }

                return mapping;
            }
            catch { return null; }
        }
        // ====================================================
        // FIXED: RollupBaScoresToMain
        // REMOVED: RM_SubmitDate / HOD_SubmitDate auto-set
        // Those dates must ONLY be set by BehaviouralAuditController.SubmitScore
        // Setting them here was causing IsBaCompletedByReviewer to think BA was
        // done when RM had only submitted PMS — breaking the gate entirely.
        // ====================================================
        private async Task RollupBaScoresToMain(string connStr, int auditId, string role)
        {
            try
            {
                using var con = new SqlConnection(connStr);
                await con.OpenAsync();

                // ── Get max possible score from AuditList ──
                decimal maxTotal = 100m;
                try
                {
                    bool auditListExists = false;
                    using (var tlChk = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_AuditList'", con))
                        auditListExists = (int)tlChk.ExecuteScalar() > 0;

                    if (auditListExists)
                    {
                        using var mx = new SqlCommand(
                            "SELECT ISNULL(SUM(CAST(MaxScore AS DECIMAL(10,2))),0) " +
                            "FROM Behaviour_AuditList WHERE Category='BA'", con);
                        var mv = mx.ExecuteScalar();
                        if (mv != null && mv != DBNull.Value)
                        {
                            decimal val = Convert.ToDecimal(mv);
                            if (val > 0) maxTotal = val;
                        }
                    }
                }
                catch { }

                // ── Check detail table exists ──
                bool hasDetails = false;
                using (var dchk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Details'", con))
                    hasDetails = Convert.ToInt32(dchk.ExecuteScalar()) > 0;

                if (!hasDetails) return;

                // ── Verify detail rows exist for this audit ──
                int detailCount = 0;
                using (var cnt = new SqlCommand(
                    "SELECT COUNT(1) FROM Behaviour_Audit_Details WHERE AuditID = @AuditId", con))
                {
                    cnt.Parameters.AddWithValue("@AuditId", auditId);
                    detailCount = Convert.ToInt32(cnt.ExecuteScalar());
                }
                if (detailCount == 0) return;

                if (role == "RM")
                {
                    // ── Guard: only roll up if RM has actually submitted BA ──
                    // Check RM_SubmitDate is already set (by BehaviouralAuditController)
                    // If not set, RM hasn't done BA yet — don't touch anything
                    bool rmSubmitted = false;
                    using (var rmChk = new SqlCommand(
                        "SELECT RM_SubmitDate FROM Behaviour_Audit_Main WHERE AuditID = @AuditId", con))
                    {
                        rmChk.Parameters.AddWithValue("@AuditId", auditId);
                        var rmSd = rmChk.ExecuteScalar();
                        rmSubmitted = rmSd != null && rmSd != DBNull.Value;
                    }
                    if (!rmSubmitted) return; // BA not yet submitted by RM — nothing to roll up

                    // ── Check RM_Score column exists ──
                    bool hasRmScore = false;
                    using (var cc = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_NAME='Behaviour_Audit_Details' AND COLUMN_NAME='RM_Score'", con))
                        hasRmScore = (int)cc.ExecuteScalar() > 0;

                    if (!hasRmScore) return;

                    // ── Roll up scores only — DO NOT touch RM_SubmitDate ──
                    using var cmd = new SqlCommand(@"
                UPDATE Behaviour_Audit_Main
                SET
                    RM_TotalScore = (
                        SELECT ISNULL(SUM(CAST(RM_Score AS DECIMAL(10,2))), 0)
                        FROM Behaviour_Audit_Details
                        WHERE AuditID = @AuditId
                    ),
                    RM_Rating = (
                        SELECT ROUND(
                            ISNULL(SUM(CAST(RM_Score AS DECIMAL(10,2))), 0)
                            / @MaxTotal * 5, 2
                        )
                        FROM Behaviour_Audit_Details
                        WHERE AuditID = @AuditId
                    )
                WHERE AuditID = @AuditId", con);
                    cmd.Parameters.AddWithValue("@AuditId", auditId);
                    cmd.Parameters.AddWithValue("@MaxTotal", maxTotal);
                    await cmd.ExecuteNonQueryAsync();

                    _log.LogInformation(
                        "[BA Rollup] RM scores rolled up for AuditID={AuditId}", auditId);
                }
                else if (role == "HOD")
                {
                    // ── Guard: only roll up if HOD has actually submitted BA ──
                    bool hodSubmitted = false;
                    using (var hodChk = new SqlCommand(
                        "SELECT HOD_SubmitDate FROM Behaviour_Audit_Main WHERE AuditID = @AuditId", con))
                    {
                        hodChk.Parameters.AddWithValue("@AuditId", auditId);
                        var hodSd = hodChk.ExecuteScalar();
                        hodSubmitted = hodSd != null && hodSd != DBNull.Value;
                    }
                    if (!hodSubmitted) return; // BA not yet submitted by HOD — nothing to roll up

                    // ── Check HOD_Score column exists ──
                    bool hasHodScore = false;
                    using (var cc = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_NAME='Behaviour_Audit_Details' AND COLUMN_NAME='HOD_Score'", con))
                        hasHodScore = (int)cc.ExecuteScalar() > 0;

                    if (!hasHodScore) return;

                    // ── Roll up scores only — DO NOT touch HOD_SubmitDate ──
                    using var cmd = new SqlCommand(@"
                UPDATE Behaviour_Audit_Main
                SET
                    HOD_TotalScore = (
                        SELECT ISNULL(SUM(CAST(HOD_Score AS DECIMAL(10,2))), 0)
                        FROM Behaviour_Audit_Details
                        WHERE AuditID = @AuditId
                    ),
                    HOD_Rating = (
                        SELECT ROUND(
                            ISNULL(SUM(CAST(HOD_Score AS DECIMAL(10,2))), 0)
                            / @MaxTotal * 5, 2
                        )
                        FROM Behaviour_Audit_Details
                        WHERE AuditID = @AuditId
                    )
                WHERE AuditID = @AuditId", con);
                    cmd.Parameters.AddWithValue("@AuditId", auditId);
                    cmd.Parameters.AddWithValue("@MaxTotal", maxTotal);
                    await cmd.ExecuteNonQueryAsync();

                    _log.LogInformation(
                        "[BA Rollup] HOD scores rolled up for AuditID={AuditId}", auditId);

                    // ── If both RM + HOD have submitted BA → mark audit Completed ──
                    // Only do this check when HOD has just submitted (we guarded above)
                    decimal rmTotal = 0, hodTotal = 0;
                    using (var checkCmd = new SqlCommand(@"
                SELECT
                    ISNULL(RM_TotalScore,  0) AS RmTotal,
                    ISNULL(HOD_TotalScore, 0) AS HodTotal
                FROM Behaviour_Audit_Main
                WHERE AuditID = @AuditId", con))
                    {
                        checkCmd.Parameters.AddWithValue("@AuditId", auditId);
                        using var dr = await checkCmd.ExecuteReaderAsync();
                        if (await dr.ReadAsync())
                        {
                            rmTotal = Convert.ToDecimal(dr["RmTotal"]);
                            hodTotal = Convert.ToDecimal(dr["HodTotal"]);
                        }
                    }

                    if (rmTotal > 0 && hodTotal > 0)
                    {
                        decimal avgRaw = (rmTotal + hodTotal) / 2m;
                        decimal avgRating = Math.Round(avgRaw / maxTotal * 5m, 2);

                        using var finalCmd = new SqlCommand(@"
                    UPDATE Behaviour_Audit_Main
                    SET
                        TotalScore         = @AvgRaw,
                        Rating             = @AvgRating,
                        FinalStatus        = 'Completed',
                        AuditStatus        = 'Completed',
                        FinalCompletedDate = CASE
                            WHEN FinalCompletedDate IS NULL THEN GETDATE()
                            ELSE FinalCompletedDate
                        END
                    WHERE AuditID = @AuditId", con);
                        finalCmd.Parameters.AddWithValue("@AvgRaw", avgRaw);
                        finalCmd.Parameters.AddWithValue("@AvgRating", avgRating);
                        finalCmd.Parameters.AddWithValue("@AuditId", auditId);
                        await finalCmd.ExecuteNonQueryAsync();

                        _log.LogInformation(
                            "[BA Rollup] AuditID={AuditId} marked Completed — RM={RmTotal} HOD={HodTotal}",
                            auditId, rmTotal, hodTotal);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[PMS BA Rollup] Failed for auditId={AuditId} role={Role}",
                    auditId, role);
            }
        }


        [HttpGet]
        public async Task<IActionResult> DebugMyIdentity()
        {
            var user = await _userManager.GetUserAsync(User);
            int empId = await ResolveEmpIdAsync(user.Id, user.UserName);
            return Json(new
            {
                aspNetUserId = user?.Id,
                aspNetEmail = user?.Email,
                resolvedEmpId = empId
            });
        }
        // ====================================================
        // FIXED: IsBaCompletedByReviewer
        //
        // Root causes fixed:
        // 1. RollupBaScoresToMain no longer poisons RM/HOD_SubmitDate —
        //    so the submit-date check is now trustworthy.
        // 2. passedStages check was too broad ("HOD" substring could
        //    match "Pending at HOD") — tightened to exact stage strings.
        // 3. Added explicit check: if BA detail rows exist with scores
        //    but submit date is still null (edge case), treat as not done.
        // 4. If no BA record exists at all for this mapping/cycle → true
        //    (don't block PMS — BA simply hasn't been initiated yet).
        // ====================================================
        private bool IsBaCompletedForAudit(SqlConnection con, int auditId, string reviewerRole, int mappingId = 0)
        {
            string submitDateCol = reviewerRole switch
            {
                "RM" => "RM_SubmitDate",
                "HOD" => "HOD_SubmitDate",
                _ => null
            };
            string totalScoreCol = reviewerRole switch
            {
                "RM" => "RM_TotalScore",
                "HOD" => "HOD_TotalScore",
                _ => null
            };
            string detailScoreCol = reviewerRole switch
            {
                "RM" => "RM_Score",
                "HOD" => "HOD_Score",
                _ => null
            };

            if (submitDateCol == null) return true;

            // ── Read main table signals in one query ──────────────────────────
            string auditStatus = "", finalStatus = "";
            object submitDateVal = DBNull.Value;
            decimal totalScore = 0m;
            bool hasTotalScoreCol = false;
            bool hasSubmitDateCol = false;

            try
            {
                using (var colChk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_NAME='Behaviour_Audit_Main' AND COLUMN_NAME=@col", con))
                {
                    colChk.Parameters.AddWithValue("@col", submitDateCol);
                    hasSubmitDateCol = (int)colChk.ExecuteScalar() > 0;
                }
                using (var colChk2 = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_NAME='Behaviour_Audit_Main' AND COLUMN_NAME=@col", con))
                {
                    colChk2.Parameters.AddWithValue("@col", totalScoreCol);
                    hasTotalScoreCol = (int)colChk2.ExecuteScalar() > 0;
                }
            }
            catch { }

            var selectParts = new List<string>
            {
                "ISNULL(FinalStatus,'')  AS FinalStatus",
                "ISNULL(AuditStatus,'') AS AuditStatus"
            };
            if (hasSubmitDateCol) selectParts.Add($"{submitDateCol}");
            if (hasTotalScoreCol) selectParts.Add($"ISNULL({totalScoreCol},0) AS TotalScore");

            using (var mainCmd = new SqlCommand(
                $"SELECT {string.Join(", ", selectParts)} " +
                "FROM Behaviour_Audit_Main WHERE AuditID=@Aid", con))
            {
                mainCmd.Parameters.AddWithValue("@Aid", auditId);
                using var dr = mainCmd.ExecuteReader();
                if (!dr.Read())
                    return true; // Row missing → don't block

                finalStatus = dr["FinalStatus"].ToString();
                auditStatus = dr["AuditStatus"].ToString();
                if (hasSubmitDateCol) submitDateVal = dr[submitDateCol];
                if (hasTotalScoreCol) totalScore = Convert.ToDecimal(dr["TotalScore"]);
            }

            // ── Signal (a): audit fully completed ─────────────────────────────
            if (string.Equals(finalStatus, "Completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(auditStatus, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("[BA Check] AuditID={AuditId} FinalStatus/AuditStatus=Completed → BA done for {Role}", auditId, reviewerRole);
                return true;
            }

            // ── Signal (b): workflow stage has passed this reviewer ────────────
            // In IsBaCompletedForAudit, fix Signal (b) for RM:
            bool isPastThisStage = reviewerRole switch
            {
                "RM" => auditStatus == "Pending at HOD"
                     || auditStatus == "Pending at UH"
                     || auditStatus == "Pending at MC"
                     || auditStatus == "Pending at JMD"
                     || auditStatus == "Completed"          // ← add exact match
                     || auditStatus.StartsWith("Completed", StringComparison.OrdinalIgnoreCase)
                     || finalStatus == "Completed",          // ← ALSO check finalStatus here
                "HOD" => auditStatus == "Pending at UH"
                      || auditStatus == "Pending at MC"
                      || auditStatus == "Pending at JMD"
                      || auditStatus == "Completed"
                      || auditStatus.StartsWith("Completed", StringComparison.OrdinalIgnoreCase)
                      || finalStatus == "Completed",         // ← add this
                _ => false
            };
            if (isPastThisStage)
            {
                _log.LogInformation("[BA Check] AuditID={AuditId} AuditStatus='{Status}' → past {Role} stage, BA done", auditId, auditStatus, reviewerRole);
                return true;
            }

            // ── Signal (c): submit date set ───────────────────────────────────
            if (hasSubmitDateCol && submitDateVal != DBNull.Value)
            {
                _log.LogInformation("[BA Check] AuditID={AuditId} {Col} is set → BA done for {Role}", auditId, submitDateCol, reviewerRole);
                return true;
            }

            // ── Signal (d): total score > 0 rolled up in main table ───────────
            if (hasTotalScoreCol && totalScore > 0)
            {
                _log.LogInformation("[BA Check] AuditID={AuditId} {ScoreCol}={Score} > 0 → BA scores exist for {Role}", auditId, totalScoreCol, totalScore, reviewerRole);
                return true;
            }

            // ── Signal (e): detail rows have scores > 0 ───────────────────────
            try
            {
                bool hasDetails = false;
                using (var dchk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Details'", con))
                    hasDetails = Convert.ToInt32(dchk.ExecuteScalar()) > 0;

                if (hasDetails)
                {
                    bool hasDetailScoreCol = false;
                    using (var colChk = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_NAME='Behaviour_Audit_Details' AND COLUMN_NAME=@col", con))
                    {
                        colChk.Parameters.AddWithValue("@col", detailScoreCol);
                        hasDetailScoreCol = (int)colChk.ExecuteScalar() > 0;
                    }

                    if (hasDetailScoreCol)
                    {
                        using var detailCmd = new SqlCommand(
                            $"SELECT ISNULL(SUM(CAST({detailScoreCol} AS DECIMAL(10,2))),0) " +
                            "FROM Behaviour_Audit_Details WHERE AuditID=@Aid", con);
                        detailCmd.Parameters.AddWithValue("@Aid", auditId);
                        decimal detailSum = Convert.ToDecimal(detailCmd.ExecuteScalar());
                        if (detailSum > 0)
                        {
                            _log.LogInformation("[BA Check] AuditID={AuditId} detail {ScoreCol} sum={Sum} > 0 → BA done for {Role}", auditId, detailScoreCol, detailSum, reviewerRole);
                            return true;
                        }
                    }
                }
            }
            catch { /* detail check is best-effort */ }

            // All signals negative → BA not yet submitted
            _log.LogInformation(
                "[BA Check] AuditID={AuditId} role={Role} — all signals negative → BA NOT done. Blocking PMS advancement.",
                auditId, reviewerRole);
            return false;
        }

        private bool IsBaCompletedByReviewer(
       string connStr, int empId, string reviewerRole, int mappingId = 0)
        {
            // UH, MC, JMD are NOT BA reviewers — never block them
            if (reviewerRole != "RM" && reviewerRole != "HOD")
                return true;

            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();

                // If BA module doesn't exist — don't block PMS flow
                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES " +
                    "WHERE TABLE_NAME='Behaviour_Audit_Main'", con))
                    if ((int)chk.ExecuteScalar() == 0) return true;

                // Resolve audit id
                int auditId = GetBehaviouralAuditId(connStr, empId, mappingId);

                // KEY FIX: No BA record → BA was not yet assigned.
                // RM/HOD cannot have completed BA if no audit exists.
                // Block PMS advancement until BA is created and submitted.
                if (auditId == 0) return false;

                // Delegate to the multi-signal check
                return IsBaCompletedForAudit(con, auditId, reviewerRole, mappingId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[BA Check] IsBaCompletedByReviewer exception — BLOCKING PMS to be safe. " +
                    "role={Role} empId={EmpId} mappingId={MappingId}",
                    reviewerRole, empId, mappingId);
                return false; // On error, block PMS (safe default)
            }
        }

        /// <summary>
        /// Core multi-signal BA completion check given a known auditId.
        /// Extracted so SubmitAppraisalFill can pass its already-resolved auditId
        /// and skip the lookup entirely (avoids the async race condition).
        /// </summary>
        /// <summary>
        /// Helper used by RmDashboard to check reporting-line without opening connections per mapping.
        /// </summary>
        private bool IsEmployeeReportingTo(int empId, int managerEmpId)
        {
            if (empId <= 0 || managerEmpId <= 0) return false;
            try
            {
                string orgConn = _config.GetConnectionString("OrgConStr");
                using var con = new SqlConnection(orgConn);
                con.Open();
                using var cmd = new SqlCommand(
                    "SELECT COUNT(1) FROM T0080_EMP_MASTER " +
                    "WHERE Emp_ID=@E AND Emp_Superior=@M AND Cmp_ID=2", con);
                cmd.Parameters.AddWithValue("@E", empId);
                cmd.Parameters.AddWithValue("@M", managerEmpId);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }
        // ====================================================
        // FIXED: GetBehaviouralAuditId
        // Scopes fallback lookup to the mapping's cycle date range
        // so old cycle audits never bleed into a new cycle.
        // ====================================================
        private static int GetBehaviouralAuditId(string connStr, int empId, int mappingId = 0)
        {
            if (empId <= 0) return 0;
            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();

                string mappingCycle = null;
                if (mappingId > 0)
                {
                    using var cycCmd = new SqlCommand(
                        "SELECT AppraisalCycle FROM EmpAppraisalMapping WHERE MappingId=@Mid AND IsDeleted=0", con);
                    cycCmd.Parameters.AddWithValue("@Mid", mappingId);
                    var cycVal = cycCmd.ExecuteScalar();
                    mappingCycle = cycVal == null || cycVal == DBNull.Value ? null : cycVal.ToString();
                }

                // Step 1 — prefer the audit explicitly linked to this mapping,
                // BUT only if its AuditType matches this mapping's cycle.
                if (mappingId > 0)
                {
                    bool hasBaIdCol = false;
                    using (var cc = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_NAME='EmpAppraisalMapping' AND COLUMN_NAME='BehaviourAuditId'", con))
                        hasBaIdCol = (int)cc.ExecuteScalar() > 0;

                    if (hasBaIdCol)
                    {
                        using var mc = new SqlCommand(
                            "SELECT ISNULL(BehaviourAuditId,0) FROM EmpAppraisalMapping " +
                            "WHERE MappingId=@Mid AND IsDeleted=0", con);
                        mc.Parameters.AddWithValue("@Mid", mappingId);
                        var linked = mc.ExecuteScalar();
                        if (linked != null && linked != DBNull.Value)
                        {
                            int linkedId = Convert.ToInt32(linked);
                            if (linkedId > 0)
                            {
                                // Verify the linked audit's type actually matches this cycle
                                using var typeChk = new SqlCommand(
                                    "SELECT ISNULL(AuditType,'') FROM Behaviour_Audit_Main WHERE AuditID=@Aid", con);
                                typeChk.Parameters.AddWithValue("@Aid", linkedId);
                                var typeVal = typeChk.ExecuteScalar();
                                string linkedType = typeVal?.ToString() ?? "";

                                bool typeMatches = string.IsNullOrWhiteSpace(mappingCycle)
                                    || string.IsNullOrWhiteSpace(linkedType)
                                    || linkedType.Equals(mappingCycle, StringComparison.OrdinalIgnoreCase);

                                if (typeMatches) return linkedId;
                                // else: stale cross-cycle link — fall through and re-resolve
                            }
                        }
                    }
                }

                // Step 2 — check BA table exists
                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Behaviour_Audit_Main'", con))
                    if ((int)chk.ExecuteScalar() == 0) return 0;

                // Step 3 — cycle-scoped lookup, ALSO filtered by AuditType when known
                DateTime? cycleFrom = null, cycleTo = null;
                if (mappingId > 0)
                {
                    using var dateCmd = new SqlCommand(
                        "SELECT CycleFromDate, CycleToDate FROM EmpAppraisalMapping " +
                        "WHERE MappingId=@Mid AND IsDeleted=0", con);
                    dateCmd.Parameters.AddWithValue("@Mid", mappingId);
                    using var ddr = dateCmd.ExecuteReader();
                    if (ddr.Read())
                    {
                        cycleFrom = ddr[0] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(ddr[0]);
                        cycleTo = ddr[1] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(ddr[1]);
                    }
                }

                if (cycleFrom.HasValue && cycleTo.HasValue)
                {
                    string typeFilter = !string.IsNullOrWhiteSpace(mappingCycle)
                        ? "AND ISNULL(AuditType,'') = @Cycle "
                        : "";

                    using var cmd = new SqlCommand($@"
                SELECT TOP 1 AuditID
                FROM   Behaviour_Audit_Main
                WHERE  EmpID = @EmpId
                  AND  CreatedOn >= @From
                  AND  CreatedOn <= DATEADD(day, 1, @To)
                  {typeFilter}
                ORDER BY AuditID DESC", con);
                    cmd.Parameters.AddWithValue("@EmpId", empId);
                    cmd.Parameters.AddWithValue("@From", cycleFrom.Value);
                    cmd.Parameters.AddWithValue("@To", cycleTo.Value);
                    if (!string.IsNullOrWhiteSpace(mappingCycle))
                        cmd.Parameters.AddWithValue("@Cycle", mappingCycle);
                    var v = cmd.ExecuteScalar();
                    if (v != null && v != DBNull.Value)
                        return Convert.ToInt32(v);

                    // Retry without type filter only if no AuditType was tagged on any audit yet
                    if (!string.IsNullOrWhiteSpace(mappingCycle))
                    {
                        using var cmd2 = new SqlCommand(@"
                    SELECT TOP 1 AuditID
                    FROM   Behaviour_Audit_Main
                    WHERE  EmpID = @EmpId
                      AND  CreatedOn >= @From
                      AND  CreatedOn <= DATEADD(day, 1, @To)
                      AND  (AuditType IS NULL OR AuditType = '')
                    ORDER BY AuditID DESC", con);
                        cmd2.Parameters.AddWithValue("@EmpId", empId);
                        cmd2.Parameters.AddWithValue("@From", cycleFrom.Value);
                        cmd2.Parameters.AddWithValue("@To", cycleTo.Value);
                        var v2 = cmd2.ExecuteScalar();
                        if (v2 != null && v2 != DBNull.Value)
                            return Convert.ToInt32(v2);
                    }
                }

                // Step 4 — global fallback, but STILL filtered by AuditType if we know the cycle.
                // Never blindly grab "most recent audit for this EmpId" across cycles.
                if (!string.IsNullOrWhiteSpace(mappingCycle))
                {
                    using var fallbackTyped = new SqlCommand(@"
                SELECT TOP 1 AuditID
                FROM   Behaviour_Audit_Main
                WHERE  EmpID = @EmpId
                  AND  ISNULL(AuditType,'') = @Cycle
                ORDER BY AuditID DESC", con);
                    fallbackTyped.Parameters.AddWithValue("@EmpId", empId);
                    fallbackTyped.Parameters.AddWithValue("@Cycle", mappingCycle);
                    var fvTyped = fallbackTyped.ExecuteScalar();
                    if (fvTyped != null && fvTyped != DBNull.Value)
                        return Convert.ToInt32(fvTyped);

                    return 0; // known cycle, nothing tagged for it yet — don't guess
                }

                using var fallback = new SqlCommand(@"
            SELECT TOP 1 AuditID
            FROM   Behaviour_Audit_Main
            WHERE  EmpID = @EmpId
            ORDER BY AuditID DESC", con);
                fallback.Parameters.AddWithValue("@EmpId", empId);
                var fv = fallback.ExecuteScalar();
                return (fv != null && fv != DBNull.Value) ? Convert.ToInt32(fv) : 0;
            }
            catch { return 0; }
        }
        private static int StageIndex(string status)
        {
            return (status ?? "").Trim() switch
            {
                "Draft" => 0,
                "EmployeeFilled" => 1,
                "RMReview" => 2,
                "HODReview" => 3,
                "UnitHeadReview" => 4,
                "ModCommitteeReview" => 5,
                "JMDReview" => 6,
                "HRFinalized" => 7,
                _ => -1
            };
        }
        private static string GetBaUrlForRole(string baseUrl, int auditId, string reviewerRole)
        {
            // Only RM and HOD have BA forms; all other roles get empty string
            if (auditId <= 0) return string.Empty;

            string path = reviewerRole switch
            {
                "RM" => $"/BehaviouralAudit/RMForm/{auditId}",
                "HOD" => $"/BehaviouralAudit/HODForm/{auditId}",
                "UnitHead" => $"/BehaviouralAudit/UHForm/{auditId}", // new form, if it exists
                _ => string.Empty
            };

            return string.IsNullOrEmpty(path) ? string.Empty : $"{baseUrl}{path}";
        }

        private bool ColExistsInTable(SqlConnection con, string table, string column)
        {
            try
            {
                using var cmd = new SqlCommand("SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@T AND COLUMN_NAME=@C", con);
                cmd.Parameters.AddWithValue("@T", table); cmd.Parameters.AddWithValue("@C", column);
                return (int)cmd.ExecuteScalar() > 0;
            }
            catch { return false; }
        }

        private HashSet<string> GetKpiRowColumns(SqlConnection con)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cmd = new SqlCommand("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='EmpAppraisalKpiRows'", con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read()) set.Add(dr.GetString(0));
            }
            catch { }
            return set;
        }

        private List<PasBasePercentageRow> GetPasBaseMaster()
        {
            var list = new List<PasBasePercentageRow>();
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using (var chk = new SqlCommand("SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PAS_BasePercentageMaster'", con))
                    if ((int)chk.ExecuteScalar() == 0) return list;
                using var cmd = new SqlCommand("SELECT Id,RangeLabel,PATRevenueMin,PATRevenueMax,BasePercentage,SortOrder FROM PAS_BasePercentageMaster WHERE IsDeleted=0 ORDER BY SortOrder", con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(new PasBasePercentageRow { Id = Convert.ToInt32(dr["Id"]), RangeLabel = dr["RangeLabel"].ToString(), PATRevenueMin = dr["PATRevenueMin"] == DBNull.Value ? null : Convert.ToDecimal(dr["PATRevenueMin"]), PATRevenueMax = Convert.ToDecimal(dr["PATRevenueMax"]), BasePercentage = Convert.ToDecimal(dr["BasePercentage"]), SortOrder = Convert.ToInt32(dr["SortOrder"]) });
            }
            catch { }
            return list;
        }

        private List<PasRatingVersionRow> GetPasRatingVersions()
        {
            var versions = new List<PasRatingVersionRow>();
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using (var chk = new SqlCommand("SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PAS_RatingMasterVersion'", con))
                    if ((int)chk.ExecuteScalar() == 0) return versions;

                using (var cmd = new SqlCommand("SELECT Id,VersionName,EffectiveDate,BasePctSample,IsActive,Remarks,CreatedAt,CreatedBy FROM PAS_RatingMasterVersion ORDER BY EffectiveDate DESC", con))
                using (var dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                        versions.Add(new PasRatingVersionRow { Id = Convert.ToInt32(dr["Id"]), VersionName = dr["VersionName"].ToString(), EffectiveDate = Convert.ToDateTime(dr["EffectiveDate"]), BasePctSample = dr["BasePctSample"] == DBNull.Value ? null : Convert.ToDecimal(dr["BasePctSample"]), IsActive = Convert.ToBoolean(dr["IsActive"]), Remarks = dr["Remarks"] == DBNull.Value ? null : dr["Remarks"].ToString(), CreatedAt = Convert.ToDateTime(dr["CreatedAt"]), CreatedBy = dr["CreatedBy"] == DBNull.Value ? null : dr["CreatedBy"].ToString() });
                }

                if (!versions.Any()) return versions;

                var details = new List<PasRatingDetailRow>();
                using (var cmd = new SqlCommand("SELECT Id,VersionId,Rating,RatingLabel,RatingRangeMin,RatingRangeMax,DistributionPercent,HikeOverBasePercent,SortOrder FROM PAS_RatingHikeDetail ORDER BY VersionId,SortOrder", con))
                using (var dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                        details.Add(new PasRatingDetailRow { Id = Convert.ToInt32(dr["Id"]), VersionId = Convert.ToInt32(dr["VersionId"]), Rating = Convert.ToInt32(dr["Rating"]), RatingLabel = dr["RatingLabel"].ToString(), RatingRangeMin = Convert.ToDecimal(dr["RatingRangeMin"]), RatingRangeMax = Convert.ToDecimal(dr["RatingRangeMax"]), DistributionPercent = Convert.ToDecimal(dr["DistributionPercent"]), HikeOverBasePercent = Convert.ToDecimal(dr["HikeOverBasePercent"]), SortOrder = Convert.ToInt32(dr["SortOrder"]) });
                }

                foreach (var v in versions)
                    v.DetailRows = details.Where(d => d.VersionId == v.Id).OrderBy(d => d.SortOrder).ToList();
            }
            catch { }
            return versions;
        }

        private List<PasRatingHikeRow> GetPasRatingMaster()
        {
            var list = new List<PasRatingHikeRow>();
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using (var chk = new SqlCommand("SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PAS_RatingHikeMaster'", con))
                    if ((int)chk.ExecuteScalar() == 0) return list;
                using var cmd = new SqlCommand("SELECT Id,Rating,RatingLabel,HikeOverBasePercent,SortOrder FROM PAS_RatingHikeMaster ORDER BY Rating DESC", con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(new PasRatingHikeRow { Id = Convert.ToInt32(dr["Id"]), Rating = Convert.ToInt32(dr["Rating"]), RatingLabel = dr["RatingLabel"].ToString(), HikeOverBasePercent = Convert.ToDecimal(dr["HikeOverBasePercent"]), SortOrder = Convert.ToInt32(dr["SortOrder"]) });
            }
            catch { }
            return list;
        }

        private List<PasYearlyPATEntryRow> GetPasYearlyPAT()
        {
            var list = new List<PasYearlyPATEntryRow>();
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using (var chk = new SqlCommand("SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PAS_YearlyPATEntry'", con))
                    if ((int)chk.ExecuteScalar() == 0) return list;

                bool hasDirectCol = ColExistsInTable(con, "PAS_YearlyPATEntry", "PATRevenuePercentDirect");
                string directSel = hasDirectCol ? ", PATRevenuePercentDirect" : ", NULL AS PATRevenuePercentDirect";

                using var cmd = new SqlCommand($"SELECT Id,FinancialYear,FYStartDate,FYEndDate,PATAmount,RevenueAmount,PATRevenuePercent,BasePercentage,Remarks,IsFinalized,CreatedAt{directSel} FROM PAS_YearlyPATEntry ORDER BY FinancialYear DESC", con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(new PasYearlyPATEntryRow { Id = Convert.ToInt32(dr["Id"]), FinancialYear = dr["FinancialYear"].ToString(), FYStartDate = dr["FYStartDate"] == DBNull.Value ? default : Convert.ToDateTime(dr["FYStartDate"]), FYEndDate = dr["FYEndDate"] == DBNull.Value ? default : Convert.ToDateTime(dr["FYEndDate"]), PATAmount = dr["PATAmount"] == DBNull.Value ? null : Convert.ToDecimal(dr["PATAmount"]), RevenueAmount = dr["RevenueAmount"] == DBNull.Value ? null : Convert.ToDecimal(dr["RevenueAmount"]), PATRevenuePercent = dr["PATRevenuePercent"] == DBNull.Value ? null : Convert.ToDecimal(dr["PATRevenuePercent"]), PATRevenuePercentDirect = dr["PATRevenuePercentDirect"] == DBNull.Value ? null : Convert.ToDecimal(dr["PATRevenuePercentDirect"]), BasePercentage = dr["BasePercentage"] == DBNull.Value ? null : Convert.ToDecimal(dr["BasePercentage"]), Remarks = dr["Remarks"] == DBNull.Value ? null : dr["Remarks"].ToString(), IsFinalized = Convert.ToBoolean(dr["IsFinalized"]), CreatedAt = dr["CreatedAt"] == DBNull.Value ? default : Convert.ToDateTime(dr["CreatedAt"]) });
            }
            catch { }
            return list;
        }

        private List<PmpModel> GetAllPmps()
        {
            var list = new List<PmpModel>(); using var con = new SqlConnection(_conn); con.Open();
            using var cmd = new SqlCommand("SELECT PmpId,PmpCode,PmpName,IsActive,CreatedBy,CreatedAt FROM PmpMaster WHERE IsDeleted=0 ORDER BY PmpId", con);
            using var dr = cmd.ExecuteReader(); int i = 1;
            while (dr.Read()) list.Add(new PmpModel { SrNo = i++, PmpId = Convert.ToInt32(dr["PmpId"]), PmpCode = dr["PmpCode"].ToString(), PmpName = dr["PmpName"].ToString(), IsActive = Convert.ToBoolean(dr["IsActive"]), CreatedAt = dr["CreatedAt"] == DBNull.Value ? null : Convert.ToDateTime(dr["CreatedAt"]) });
            return list;
        }
        private List<KpiDocumentVM> GetDocumentsForRow(SqlConnection con, int empKpiRowId, int mappingId = 0)
        {
            var list = new List<KpiDocumentVM>();
            try
            {
                using (var chk = new SqlCommand("SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='KpiDocuments'", con))
                    if ((int)chk.ExecuteScalar() == 0) return list;

                string sql = mappingId > 0
                    ? "SELECT Id,FileName,OriginalFileName,UploaderRole,EmpKpiRowId FROM KpiDocuments WHERE EmpKpiRowId=@RowId AND MappingId=@MappingId ORDER BY UploadedAt"
                    : "SELECT Id,FileName,OriginalFileName,UploaderRole,EmpKpiRowId FROM KpiDocuments WHERE EmpKpiRowId=@RowId ORDER BY UploadedAt";

                using var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@RowId", empKpiRowId);
                if (mappingId > 0) cmd.Parameters.AddWithValue("@MappingId", mappingId);

                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(new KpiDocumentVM
                    {
                        Id = Convert.ToInt32(dr["Id"]),
                        FileName = dr["FileName"].ToString(),
                        OriginalFileName = dr["OriginalFileName"].ToString(),
                        UploaderRole = dr["UploaderRole"].ToString(),
                        EmpKpiRowId = Convert.ToInt32(dr["EmpKpiRowId"])
                    });
            }
            catch { }
            return list;
        }

        private List<KraModel> GetAllKras()
        {
            var list = new List<KraModel>(); using var con = new SqlConnection(_conn); con.Open();
            using var cmd = new SqlCommand(@"SELECT k.KraId,k.KraCode,k.KraName,k.IsActive,k.CreatedBy,k.CreatedAt,k.PmpId,ISNULL(p.PmpCode,'') AS PmpCode,ISNULL(p.PmpName,'') AS PmpName FROM KraMaster k LEFT JOIN PmpMaster p ON p.PmpId=k.PmpId AND p.IsDeleted=0 WHERE k.IsDeleted=0 ORDER BY k.PmpId,k.KraId", con);
            using var dr = cmd.ExecuteReader(); int i = 1;
            while (dr.Read()) list.Add(new KraModel { SrNo = i++, KraId = Convert.ToInt32(dr["KraId"]), KraCode = dr["KraCode"].ToString(), KraName = dr["KraName"].ToString(), IsActive = Convert.ToBoolean(dr["IsActive"]), CreatedAt = dr["CreatedAt"] == DBNull.Value ? null : Convert.ToDateTime(dr["CreatedAt"]), PmpId = dr["PmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["PmpId"]), PmpCode = dr["PmpCode"].ToString(), PmpName = dr["PmpName"].ToString() });
            return list;
        }

        private List<KpiModel> GetAllKpis()
        {
            var list = new List<KpiModel>(); using var con = new SqlConnection(_conn); con.Open();
            using var cmd = new SqlCommand(@"SELECT k.KpiId,k.KpiCode,k.PmpId,k.KraId,k.KpiName,k.UnitOfMeasure,k.IndividualWeightage,k.OverallWeightage,k.Target,k.CreatedAt,p.PmpCode,p.PmpName,r.KraCode,r.KraName FROM KpiMaster k INNER JOIN PmpMaster p ON p.PmpId=k.PmpId AND p.IsDeleted=0 INNER JOIN KraMaster r ON r.KraId=k.KraId AND r.IsDeleted=0 WHERE k.IsDeleted=0 ORDER BY k.PmpId,k.KraId,k.KpiId", con);
            using var dr = cmd.ExecuteReader(); int i = 1;
            while (dr.Read()) list.Add(new KpiModel { SrNo = i++, KpiId = Convert.ToInt32(dr["KpiId"]), KpiCode = dr["KpiCode"].ToString(), PmpId = Convert.ToInt32(dr["PmpId"]), KraId = Convert.ToInt32(dr["KraId"]), KpiName = dr["KpiName"].ToString(), UnitOfMeasure = dr["UnitOfMeasure"].ToString(), IndividualWeightage = Convert.ToDecimal(dr["IndividualWeightage"]), OverallWeightage = Convert.ToDecimal(dr["OverallWeightage"]), Target = Convert.ToDecimal(dr["Target"]), PmpCode = dr["PmpCode"].ToString(), PmpName = dr["PmpName"].ToString(), KraCode = dr["KraCode"].ToString(), KraName = dr["KraName"].ToString(), CreatedAt = dr["CreatedAt"] == DBNull.Value ? null : Convert.ToDateTime(dr["CreatedAt"]) });
            return list;
        }

        private List<KpiModel> GetKpisByPmpKraInternal(int pmpId, int kraId)
        {
            var list = new List<KpiModel>(); using var con = new SqlConnection(_conn); con.Open();
            using var cmd = new SqlCommand("SELECT KpiId,KpiCode,KpiName,UnitOfMeasure,IndividualWeightage,OverallWeightage,Target FROM KpiMaster WHERE PmpId=@PmpId AND KraId=@KraId AND IsDeleted=0 ORDER BY KpiId", con);
            cmd.Parameters.AddWithValue("@PmpId", pmpId); cmd.Parameters.AddWithValue("@KraId", kraId);
            using var dr = cmd.ExecuteReader();
            while (dr.Read()) list.Add(new KpiModel { KpiId = Convert.ToInt32(dr["KpiId"]), KpiCode = dr["KpiCode"].ToString(), KpiName = dr["KpiName"].ToString(), UnitOfMeasure = dr["UnitOfMeasure"].ToString(), IndividualWeightage = Convert.ToDecimal(dr["IndividualWeightage"]), OverallWeightage = Convert.ToDecimal(dr["OverallWeightage"]), Target = Convert.ToDecimal(dr["Target"]) });
            return list;
        }
        private List<SelectListItem> GetEmployeeDropdown()
        {
            var list = new List<SelectListItem>();
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(@"
            SELECT 
                e.Emp_ID,
                ISNULL(e.Emp_code,'') AS EMPCode,
                ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
                ISNULL(g.Grd_Name,'') AS GradeName
            FROM   [Orange_Hrms].[dbo].[T0080_EMP_MASTER] e
            LEFT JOIN [Orange_Hrms].[dbo].[T0040_GRADE_MASTER] g ON g.Grd_ID = e.Grd_ID
            WHERE  ISNULL(e.Emp_Left, 'N') <> 'Y'
              AND  ISNULL(e.Emp_First_Name,'') <> ''
              AND  e.Cmp_ID = 2
            ORDER BY e.Emp_First_Name", con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    list.Add(new SelectListItem
                    {
                        Value = dr["Emp_ID"].ToString(),
                        Text = $"{dr["EMPCode"]} – {dr["FullName"]}"
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetEmployeeDropdown error: {ex.Message}");
            }
            return list;
        }
        [HttpGet]
        public async Task<IActionResult> SearchEmployees(string term)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
                return Json(new List<object>());

            var results = new List<object>();

            try
            {
                using var con = new SqlConnection(_conn);
                await con.OpenAsync();

                using var cmd = new SqlCommand(@"
    SELECT TOP 20
           e.Emp_ID                                                                   AS EmpId,
           ISNULL(e.Emp_code,'')                                                       AS EmpCode,
           LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'') + ' ' + ISNULL(e.Emp_Last_Name,''))) AS FullName,
           ISNULL(des.Desig_Name,'')                                                   AS Designation,
           ISNULL(dept.Dept_Name,'')                                                   AS Department
    FROM   [Orange_Hrms].[dbo].[T0080_EMP_MASTER] e
    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des  ON des.Desig_ID = e.Desig_Id
    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER]  dept ON dept.Dept_Id = e.Dept_ID
    WHERE  ISNULL(e.Emp_Left, 'N') <> 'Y'
      AND  e.Cmp_ID = 2
      AND  ISNULL(e.Emp_First_Name,'') <> ''
      AND (e.Emp_code       LIKE '%' + @Term + '%'
        OR e.Emp_First_Name LIKE '%' + @Term + '%'
        OR e.Emp_Last_Name  LIKE '%' + @Term + '%')
    ORDER BY e.Emp_First_Name", con);

                cmd.Parameters.AddWithValue("@Term", term.Trim());

                using var dr = await cmd.ExecuteReaderAsync();
                while (await dr.ReadAsync())
                {
                    var empId = Convert.ToInt32(dr["EmpId"]);
                    var empCode = dr["EmpCode"].ToString();
                    var fullName = dr["FullName"].ToString()!.Trim();
                    results.Add(new
                    {
                        empId = empId,
                        empCode = empCode,
                        fullName = fullName,
                        designation = dr["Designation"].ToString(),
                        department = dr["Department"].ToString(),
                        display = $"{empCode} – {fullName}",
                        value = empId.ToString()   // sent to SaveEmpMapping as UserId
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SearchEmployees error: {ex.Message}");
            }

            return Json(results);
        }
        private List<SelectListItem> GetReviewerDropdown()
        {
            var list = new List<SelectListItem>();
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();
                using var cmd = new SqlCommand(@"
            SELECT
                ISNULL(e.Emp_code,'') AS EMPCode,
                ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName
            FROM   [Orange_Hrms].[dbo].[T0080_EMP_MASTER] e
            WHERE  ISNULL(e.Emp_Left, 'N') <> 'Y'
              AND  e.Cmp_ID = 2
              AND  ISNULL(e.Emp_First_Name,'') <> ''
            ORDER BY e.Emp_First_Name", con);
                using var dr = cmd.ExecuteReader();
                list.Add(new SelectListItem { Value = "", Text = "— Not Assigned —" });
                while (dr.Read())
                {
                    list.Add(new SelectListItem
                    {
                        Value = dr["FullName"].ToString(),
                        Text = $"{dr["EMPCode"]} – {dr["FullName"]}"
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetReviewerDropdown error: {ex.Message}");
            }
            return list;
        }
        private List<EmpKpiMappingModel> GetAllEmpMappings()
        {
            var list = new List<EmpKpiMappingModel>();
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EmpAppraisalMapping'", con))
                    if ((int)chk.ExecuteScalar() == 0) return list;

                bool hasModJmd = ColExistsInTable(con, "EmpAppraisalMapping", "ModCommitteeNames");
                bool hasRmCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmName");
                bool hasHodCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodName");
                bool hasUhCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadName");

                // ── NEW: detect the EmpId columns (safe even before migration runs elsewhere) ──
                bool hasRmEmpId = ColExistsInTable(con, "EmpAppraisalMapping", "RmEmpId");
                bool hasHodEmpId = ColExistsInTable(con, "EmpAppraisalMapping", "HodEmpId");
                bool hasUhEmpId = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadEmpId");
                bool hasJmdEmpId = ColExistsInTable(con, "EmpAppraisalMapping", "JmdEmpId");

                string modJmdCols = hasModJmd
                    ? ", ISNULL(m.ModCommitteeNames,'') AS ModCommitteeNames, ISNULL(m.JmdName,'') AS JmdName"
                    : ", '' AS ModCommitteeNames, '' AS JmdName";
                string rmCol = hasRmCol ? "ISNULL(m.RmName,'')       AS RmName" : "'' AS RmName";
                string hodCol = hasHodCol ? "ISNULL(m.HodName,'')       AS HodName" : "'' AS HodName";
                string uhCol = hasUhCol ? "ISNULL(m.UnitHeadName,'')  AS UnitHeadName" : "'' AS UnitHeadName";

                // ── NEW: EmpId columns in the SELECT ──
                string rmEmpIdCol = hasRmEmpId ? "m.RmEmpId" : "NULL AS RmEmpId";
                string hodEmpIdCol = hasHodEmpId ? "m.HodEmpId" : "NULL AS HodEmpId";
                string uhEmpIdCol = hasUhEmpId ? "m.UnitHeadEmpId" : "NULL AS UnitHeadEmpId";
                string jmdEmpIdCol = hasJmdEmpId ? "m.JmdEmpId" : "NULL AS JmdEmpId";

                string sql = $@"
SELECT
    m.MappingId,
    m.EmpId,
    ISNULL(m.UserId,'')     AS UserId,
    m.AppraisalYear,
    m.AppraisalCycle,
    m.CycleFromDate,
    m.CycleToDate,
    m.[Status],
    m.CreatedBy,
    m.CreatedAt,
    {rmCol},
    {hodCol},
    {uhCol},
    {rmEmpIdCol},
    {hodEmpIdCol},
    {uhEmpIdCol},
    {jmdEmpIdCol}
    {modJmdCols}
FROM  EmpAppraisalMapping m
WHERE m.IsDeleted = 0
ORDER BY m.MappingId DESC";

                using (var cmd = new SqlCommand(sql, con))
                using (var dr = cmd.ExecuteReader())
                {
                    int i = 1;
                    while (dr.Read())
                    {
                        var m = new EmpKpiMappingModel
                        {
                            SrNo = i++,
                            MappingId = Convert.ToInt32(dr["MappingId"]),
                            EmpId = dr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["EmpId"]),
                            UserId = dr["UserId"]?.ToString() ?? "",
                            AppraisalYear = Convert.ToInt32(dr["AppraisalYear"]),
                            AppraisalCycle = dr["AppraisalCycle"]?.ToString() ?? "",
                            CycleFromDate = dr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleFromDate"]),
                            CycleToDate = dr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleToDate"]),
                            Status = dr["Status"]?.ToString() ?? "Draft",
                            CreatedBy = dr["CreatedBy"]?.ToString() ?? "",
                            CreatedAt = dr["CreatedAt"] == DBNull.Value ? null : Convert.ToDateTime(dr["CreatedAt"]),
                            RmName = dr["RmName"]?.ToString() ?? "",
                            HodName = dr["HodName"]?.ToString() ?? "",
                            UnitHeadName = dr["UnitHeadName"]?.ToString() ?? "",
                            ModCommitteeNames = dr["ModCommitteeNames"]?.ToString() ?? "",
                            JmdName = dr["JmdName"]?.ToString() ?? "",
                            // ── NEW: EmpId fields ──
                            RmEmpId = dr["RmEmpId"] == DBNull.Value ? (int?)null : Convert.ToInt32(dr["RmEmpId"]),
                            HodEmpId = dr["HodEmpId"] == DBNull.Value ? (int?)null : Convert.ToInt32(dr["HodEmpId"]),
                            UnitHeadEmpId = dr["UnitHeadEmpId"] == DBNull.Value ? (int?)null : Convert.ToInt32(dr["UnitHeadEmpId"]),
                            JmdEmpId = dr["JmdEmpId"] == DBNull.Value ? (int?)null : Convert.ToInt32(dr["JmdEmpId"]),
                            EmpCode = "",
                            EmpName = "",
                            Email = "",
                            Designation = "",
                            Department = "",
                        };
                        list.Add(m);
                    }
                }

                if (!list.Any()) return list;

                // ── NEW: batch-load ModCommittee EmpIds for all mappings ──
                var mappingIdsForMc = list.Select(m => m.MappingId).ToList();
                try
                {
                    using (var mcChk = new SqlCommand(
                        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EmpAppraisalModCommitteeMember'", con))
                    {
                        if ((int)mcChk.ExecuteScalar() > 0 && mappingIdsForMc.Any())
                        {
                            var mcMap = new Dictionary<int, List<int>>();
                            using var mcCmd = new SqlCommand($@"
SELECT MappingId, EmpId FROM EmpAppraisalModCommitteeMember
WHERE MappingId IN ({string.Join(",", mappingIdsForMc)})", con);
                            using var mcDr = mcCmd.ExecuteReader();
                            while (mcDr.Read())
                            {
                                int mid = Convert.ToInt32(mcDr["MappingId"]);
                                int eid = Convert.ToInt32(mcDr["EmpId"]);
                                if (!mcMap.ContainsKey(mid)) mcMap[mid] = new List<int>();
                                mcMap[mid].Add(eid);
                            }
                            foreach (var m in list)
                                if (mcMap.TryGetValue(m.MappingId, out var ids))
                                    m.ModCommitteeEmpIds = ids;
                        }
                    }
                }
                catch { /* ModCommittee EmpId enrichment is best-effort */ }

                // ── Batch-load KPI row counts ──
                var mappingIds = list.Select(m => m.MappingId).ToList();
                var kpiCounts = new Dictionary<int, int>();
                using (var kpiCmd = new SqlCommand($@"
SELECT MappingId, COUNT(*) AS KpiCount
FROM   EmpAppraisalKpiRows
WHERE  MappingId IN ({string.Join(",", mappingIds)}) AND IsDeleted = 0
GROUP BY MappingId", con))
                using (var kdr = kpiCmd.ExecuteReader())
                    while (kdr.Read())
                        kpiCounts[Convert.ToInt32(kdr["MappingId"])] = Convert.ToInt32(kdr["KpiCount"]);

                foreach (var m in list)
                {
                    int cnt = kpiCounts.TryGetValue(m.MappingId, out var c) ? c : 0;
                    for (int k = 0; k < cnt; k++)
                        m.KpiRows.Add(new EmpKpiRow());
                }

                // ── Enrich with HRMS via OrgConStr (unchanged from original) ──
                try
                {
                    var empIds = list.Select(m => m.EmpId)
                                     .Where(id => id > 0)
                                     .Distinct().ToList();

                    if (empIds.Any())
                    {
                        string orgConn = _config.GetConnectionString("OrgConStr");
                        using var orgCon = new SqlConnection(orgConn);
                        orgCon.Open();

                        using var hrCmd = new SqlCommand($@"
SELECT
    e.Emp_ID,
    ISNULL(e.Emp_code,'')    AS EMPCode,
    ISNULL(LTRIM(RTRIM(
        ISNULL(e.Emp_First_Name,'') + ' ' + 
        ISNULL(e.Emp_Last_Name,'')
    )),'')                    AS FullName,
    ISNULL(e.Work_Email,'')   AS Email,
    ISNULL(dept.Dept_Name,'') AS DepartmentName,
    ISNULL(des.Desig_Name,'') AS DesignationName,
    ISNULL(g.Grd_Name,'')     AS UnitName
FROM   T0080_EMP_MASTER e
LEFT JOIN T0040_DEPARTMENT_MASTER  dept ON dept.Dept_Id = e.Dept_ID
LEFT JOIN T0040_DESIGNATION_MASTER des  ON des.Desig_ID = e.Desig_Id
LEFT JOIN T0040_GRADE_MASTER       g    ON g.Grd_ID     = e.Grd_ID
WHERE  e.Emp_ID IN ({string.Join(",", empIds)})", orgCon);

                        var hrData = new Dictionary<int, (string code, string name,
                                                           string email, string dept,
                                                           string desig, string unit)>();

                        using var hrDr = hrCmd.ExecuteReader();
                        while (hrDr.Read())
                        {
                            int eid = Convert.ToInt32(hrDr["Emp_ID"]);
                            hrData[eid] = (
                                hrDr["EMPCode"]?.ToString() ?? "",
                                hrDr["FullName"]?.ToString() ?? "",
                                hrDr["Email"]?.ToString() ?? "",
                                hrDr["DepartmentName"]?.ToString() ?? "",
                                hrDr["DesignationName"]?.ToString() ?? "",
                                hrDr["UnitName"]?.ToString() ?? ""
                            );
                        }

                        foreach (var m in list)
                        {
                            if (hrData.TryGetValue(m.EmpId, out var h))
                            {
                                m.EmpCode = h.code;
                                m.EmpName = h.name;
                                m.Email = h.email;
                                m.Department = h.dept;
                                m.Designation = h.desig;
                                m.UnitName = h.unit;
                            }
                        }
                    }
                }
                catch (Exception hrEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GetAllEmpMappings] HRMS enrichment failed: {hrEx.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllEmpMappings error: {ex.Message}");
            }
            return list;
        }

        private List<EmpKpiMappingModel> GetAllMasterMappings()
        {
            var list = new List<EmpKpiMappingModel>();
            try
            {
                using var con = new SqlConnection(_conn);
                con.Open();

                using (var chk = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EmpMappingMaster'", con))
                    if ((int)chk.ExecuteScalar() == 0)
                        return GetAllEmpMappings();

                bool hasUpdatedAt = ColExistsInTable(con, "EmpMappingMaster", "UpdatedAt");
                bool hasUpdatedBy = ColExistsInTable(con, "EmpMappingMaster", "UpdatedBy");

                string lastUpdatedAtSel = hasUpdatedAt
                    ? "ISNULL(mm.UpdatedAt, mm.CreatedAt) AS LastUpdatedAt"
                    : "mm.CreatedAt AS LastUpdatedAt";
                string lastUpdatedBySel = hasUpdatedBy
                    ? "ISNULL(mm.UpdatedBy, mm.CreatedBy) AS LastUpdatedBy"
                    : "mm.CreatedBy AS LastUpdatedBy";

                // ── KEY FIX: NO cross-db join here — fetch master rows only ──
                string sql = $@"
SELECT
    mm.MasterMappingId,
    mm.EmpId,
    mm.UserId,
    mm.AppraisalYear,
    ISNULL(mm.RmName,'')            AS RmName,
    ISNULL(mm.HodName,'')           AS HodName,
    ISNULL(mm.UnitHeadName,'')      AS UnitHeadName,
    ISNULL(mm.ModCommitteeNames,'') AS ModCommitteeNames,
    ISNULL(mm.JmdName,'')           AS JmdName,
    mm.CreatedAt,
    {lastUpdatedAtSel},
    {lastUpdatedBySel},
    (SELECT COUNT(1) FROM EmpMappingMasterKpi mk
     WHERE mk.MasterMappingId = mm.MasterMappingId
       AND mk.IsDeleted = 0)        AS KpiCount
FROM EmpMappingMaster mm
WHERE mm.IsDeleted = 0
ORDER BY mm.MasterMappingId DESC";

                using var cmd = new SqlCommand(sql, con);
                using var dr = cmd.ExecuteReader();
                int i = 1;
                while (dr.Read())
                {
                    list.Add(new EmpKpiMappingModel
                    {
                        SrNo = i++,
                        MappingId = Convert.ToInt32(dr["MasterMappingId"]),
                        EmpId = dr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["EmpId"]),
                        UserId = dr["UserId"]?.ToString() ?? "",
                        // HRMS fields left empty — enriched below
                        EmpCode = "",
                        EmpName = "",
                        Email = "",
                        Designation = "",
                        Department = "",
                        AppraisalYear = Convert.ToInt32(dr["AppraisalYear"]),
                        AppraisalCycle = "",
                        Status = "Master",
                        RmName = dr["RmName"]?.ToString() ?? "",
                        HodName = dr["HodName"]?.ToString() ?? "",
                        UnitHeadName = dr["UnitHeadName"]?.ToString() ?? "",
                        ModCommitteeNames = dr["ModCommitteeNames"]?.ToString() ?? "",
                        JmdName = dr["JmdName"]?.ToString() ?? "",
                        CreatedAt = dr["CreatedAt"] == DBNull.Value ? (DateTime?)null
                                            : Convert.ToDateTime(dr["CreatedAt"]),
                        LastUpdatedAt = dr["LastUpdatedAt"] == DBNull.Value ? (DateTime?)null
                                            : (DateTime?)Convert.ToDateTime(dr["LastUpdatedAt"]),
                        LastUpdatedBy = dr["LastUpdatedBy"] == DBNull.Value ? null
                                            : dr["LastUpdatedBy"]?.ToString(),
                    });

                    int kpiCount = Convert.ToInt32(dr["KpiCount"]);
                    for (int k = 0; k < kpiCount; k++)
                        list.Last().KpiRows.Add(new EmpKpiRow());
                }

                // ── Enrich with HRMS data in a SEPARATE try/catch ──
                // If HRMS is unreachable, we still return the list with empty names
                if (list.Any())
                {
                    try
                    {
                        var empIds = list.Select(m => m.EmpId)
                                         .Where(id => id > 0)
                                         .Distinct().ToList();

                        if (empIds.Any())
                        {
                            // Use OrgConStr — the orange_hrms database on 192.168.10.27
                            string orgConn = _config.GetConnectionString("OrgConStr");

                            using var orgCon = new SqlConnection(orgConn);
                            orgCon.Open();

                            using var hrmsCmd = new SqlCommand($@"
SELECT
    e.Emp_ID,
    ISNULL(e.Emp_code,'')   AS EMPCode,
    ISNULL(LTRIM(RTRIM(
        ISNULL(e.Emp_First_Name,'') + ' ' + 
        ISNULL(e.Emp_Last_Name,'')
    )),'')                    AS FullName,
    ISNULL(e.Work_Email,'')   AS Email,
    ISNULL(dept.Dept_Name,'') AS DepartmentName,
    ISNULL(des.Desig_Name,'') AS DesignationName
FROM   T0080_EMP_MASTER e
LEFT JOIN T0040_DEPARTMENT_MASTER  dept ON dept.Dept_Id = e.Dept_ID
LEFT JOIN T0040_DESIGNATION_MASTER des  ON des.Desig_ID = e.Desig_Id
WHERE  e.Emp_ID IN ({string.Join(",", empIds)})", orgCon);

                            var hrmsData = new Dictionary<int, (string code, string name,
                                                                 string email, string dept,
                                                                 string desig)>();

                            using var hrDr = hrmsCmd.ExecuteReader();
                            while (hrDr.Read())
                            {
                                int eid = Convert.ToInt32(hrDr["Emp_ID"]);
                                hrmsData[eid] = (
                                    hrDr["EMPCode"]?.ToString() ?? "",
                                    hrDr["FullName"]?.ToString() ?? "",
                                    hrDr["Email"]?.ToString() ?? "",
                                    hrDr["DepartmentName"]?.ToString() ?? "",
                                    hrDr["DesignationName"]?.ToString() ?? ""
                                );
                            }

                            foreach (var m in list)
                            {
                                if (hrmsData.TryGetValue(m.EmpId, out var h))
                                {
                                    m.EmpCode = h.code;
                                    m.EmpName = h.name;
                                    m.Email = h.email;
                                    m.Department = h.dept;
                                    m.Designation = h.desig;
                                }
                            }
                        }
                    }
                    catch (Exception hrmsEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GetAllMasterMappings] HRMS enrichment failed: {hrmsEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllMasterMappings error: {ex.Message}");
                return GetAllEmpMappings();
            }
            return list;
        }
        private EmpKpiMappingModel GetMappingByIdInternal(string connStr, int mappingId)
        {
            try
            {
                using var con = new SqlConnection(connStr);
                con.Open();

                bool hasModJmd = ColExistsInTable(con, "EmpAppraisalMapping", "ModCommitteeNames");
                bool hasRmCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmName");
                bool hasHodCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodName");
                bool hasUhCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadName");

                // ── NEW: reviewer EmpId columns (mirrors Behaviour_Audit_Main.RM_ID/HOD_ID) ──
                bool hasRmEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "RmEmpId");
                bool hasHodEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "HodEmpId");
                bool hasUhEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "UnitHeadEmpId");
                bool hasJmdEmpIdCol = ColExistsInTable(con, "EmpAppraisalMapping", "JmdEmpId");

                string modJmdCols = hasModJmd
                    ? ", ISNULL(m.ModCommitteeNames,'') AS ModCommitteeNames, ISNULL(m.JmdName,'') AS JmdName"
                    : ", '' AS ModCommitteeNames, '' AS JmdName";
                string hodCol = hasHodCol ? "ISNULL(m.HodName,'')      AS HodName" : "'' AS HodName";
                string uhCol = hasUhCol ? "ISNULL(m.UnitHeadName,'') AS UnitHeadName" : "'' AS UnitHeadName";
                string rmCol = hasRmCol ? "ISNULL(m.RmName,'')       AS RmName"
                                          : "ISNULL(LTRIM(RTRIM(ISNULL(rm.Emp_First_Name,'')+' '+ISNULL(rm.Emp_Last_Name,''))),'') AS RmName";

                // ── NEW: EmpId column selects ──
                string rmEmpIdSel = hasRmEmpIdCol ? "m.RmEmpId" : "NULL AS RmEmpId";
                string hodEmpIdSel = hasHodEmpIdCol ? "m.HodEmpId" : "NULL AS HodEmpId";
                string uhEmpIdSel = hasUhEmpIdCol ? "m.UnitHeadEmpId" : "NULL AS UnitHeadEmpId";
                string jmdEmpIdSel = hasJmdEmpIdCol ? "m.JmdEmpId" : "NULL AS JmdEmpId";

                // ── Step 1: Load header from app DB only (no cross-db join) ─────────
                string headerSql = $@"
                    SELECT m.MappingId, m.EmpId, ISNULL(m.UserId,'') AS UserId,
                           m.AppraisalYear, m.AppraisalCycle, m.CycleFromDate, m.CycleToDate,
                           m.[Status], m.CreatedBy, m.CreatedAt,
                           ''  AS EMPCode,
                           ''  AS FullName,
                           ''  AS Email,
                           ''  AS ReportTo,
                           ''  AS DepartmentName,
                           ''  AS SubDepartmentName,
                           ''  AS DesignationName,
                           {rmCol}, {hodCol}, {uhCol} {modJmdCols},
                           {rmEmpIdSel}, {hodEmpIdSel}, {uhEmpIdSel}, {jmdEmpIdSel},
                           ''  AS UnitName
                    FROM EmpAppraisalMapping m
                    WHERE m.MappingId = @Id AND m.IsDeleted = 0";

                EmpKpiMappingModel mapping = null;

                using (var cmd = new SqlCommand(headerSql, con))
                {
                    cmd.Parameters.AddWithValue("@Id", mappingId);
                    using var dr = cmd.ExecuteReader();
                    if (dr.Read())
                        mapping = MapMappingFromReaderSafe(dr, hasModJmd);
                }

                if (mapping == null) return null;

                // ── Step 2: Enrich with HRMS employee details (separate connection) ─
                if (mapping.EmpId > 0)
                {
                    try
                    {
                        string hrmsConn = _config.GetConnectionString("OrgConStr") ?? connStr;
                        using var hrmsCon = new SqlConnection(hrmsConn);
                        hrmsCon.Open();

                        using var hrmsCmd = new SqlCommand(@"
                            SELECT
                                ISNULL(e.Emp_code,'')                                                          AS EMPCode,
                                ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
                                ISNULL(e.Work_Email,'')                                                        AS Email,
                                CAST(ISNULL(e.Emp_Superior,0) AS NVARCHAR(50))                               AS ReportTo,
                                ISNULL(dept.Dept_Name,'')                                                      AS DepartmentName,
                                ISNULL(des.Desig_Name,'')                                                      AS DesignationName,
                                ISNULL(g.Grd_Name,'')                                                          AS UnitName
                            FROM   T0080_EMP_MASTER e
                            LEFT JOIN T0040_DEPARTMENT_MASTER  dept ON dept.Dept_Id = e.Dept_ID
                            LEFT JOIN T0040_DESIGNATION_MASTER des  ON des.Desig_ID = e.Desig_Id
                            LEFT JOIN T0040_GRADE_MASTER       g    ON g.Grd_ID    = e.Grd_ID
                            WHERE  e.Emp_ID = @EmpId", hrmsCon);

                        hrmsCmd.Parameters.AddWithValue("@EmpId", mapping.EmpId);
                        using var hrDr = hrmsCmd.ExecuteReader();
                        if (hrDr.Read())
                        {
                            mapping.EmpCode = hrDr["EMPCode"]?.ToString() ?? "";
                            mapping.EmpName = hrDr["FullName"]?.ToString() ?? "";
                            mapping.Email = hrDr["Email"]?.ToString() ?? "";
                            mapping.ReportTo = hrDr["ReportTo"]?.ToString() ?? "";
                            mapping.Department = hrDr["DepartmentName"]?.ToString() ?? "";
                            mapping.Designation = hrDr["DesignationName"]?.ToString() ?? "";
                            mapping.UnitName = hrDr["UnitName"]?.ToString() ?? "";
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"GetMappingByIdInternal HRMS enrichment failed for EmpId={mapping.EmpId}: {ex.Message}");
                    }
                }

                // ── Step 3: Load KPI rows from app DB ───────────────────────────────
                var rowCols = GetKpiRowColumns(con);
                bool has(string c) => rowCols.Contains(c);

                string empActual = has("EmpActual")
                    ? "r.EmpActual, r.EmpRating,"
                    : "NULL AS EmpActual, NULL AS EmpRating,";
                string rmCols = has("RmActual")
                    ? "r.RmActual, r.RmRating, ISNULL(r.RmRemarks,'') AS RmRemarks,"
                    : "NULL AS RmActual, NULL AS RmRating, '' AS RmRemarks,";
                string hodCols2 = has("HodActual")
                    ? "r.HodActual, r.HodRating, ISNULL(r.HodRemarks,'') AS HodRemarks,"
                    : "NULL AS HodActual, NULL AS HodRating, '' AS HodRemarks,";
                string uhCols2 = has("UnitHeadActual")
                    ? "ISNULL(r.UnitHeadActual,NULL) AS UnitHeadActual, ISNULL(r.UnitHeadRating,NULL) AS UnitHeadRating, ISNULL(r.UnitHeadRemarks,'') AS UnitHeadRemarks,"
                    : "NULL AS UnitHeadActual, NULL AS UnitHeadRating, '' AS UnitHeadRemarks,";
                string mcCols2 = has("ModCommitteeRating")
                    ? (has("ModCommitteeActual")
                        ? "ISNULL(r.ModCommitteeActual,NULL) AS ModCommitteeActual, ISNULL(r.ModCommitteeRating,NULL) AS ModCommitteeRating, ISNULL(r.ModCommitteeRemarks,'') AS ModCommitteeRemarks,"
                        : "NULL AS ModCommitteeActual, ISNULL(r.ModCommitteeRating,NULL) AS ModCommitteeRating, ISNULL(r.ModCommitteeRemarks,'') AS ModCommitteeRemarks,")
                    : "NULL AS ModCommitteeActual, NULL AS ModCommitteeRating, '' AS ModCommitteeRemarks,";
                string jmdCols2 = has("JmdRating")
                    ? (has("JmdActual")
                        ? "ISNULL(r.JmdActual,NULL) AS JmdActual, ISNULL(r.JmdRating,NULL) AS JmdRating, ISNULL(r.JmdRemarks,'') AS JmdRemarks,"
                        : "NULL AS JmdActual, ISNULL(r.JmdRating,NULL) AS JmdRating, ISNULL(r.JmdRemarks,'') AS JmdRemarks,")
                    : "NULL AS JmdActual, NULL AS JmdRating, '' AS JmdRemarks,";
                string hrCols = has("HrFinalRating")
                    ? "r.HrFinalRating, r.WeightedRating"
                    : "NULL AS HrFinalRating, NULL AS WeightedRating";
                string kpiRowSql = $@"
    SELECT r.EmpKpiRowId, r.KpiId, r.PmpId, r.KraId,
           r.KpiName, r.UnitOfMeasure,
           r.IndividualWeightage,
           ISNULL(r.OverallWeightage,0) AS OverallWeightage,
           r.Target,
           {empActual}
           ISNULL(r.EmpJustification,'') AS EmpJustification,
           {rmCols}
           {hodCols2}
           {uhCols2}
           {mcCols2}
           {jmdCols2}
           {hrCols},
           ISNULL(k.KpiCode,'')  AS KpiCode,
           ISNULL(p.PmpName,'')  AS PmpName,
           ISNULL(ra.KraName,'') AS KraName
    FROM EmpAppraisalKpiRows r
                    INNER JOIN KpiMaster  k  ON k.KpiId  = r.KpiId
                    INNER JOIN PmpMaster  p  ON p.PmpId  = r.PmpId
                    INNER JOIN KraMaster  ra ON ra.KraId = r.KraId
                    WHERE r.MappingId = @Id AND r.IsDeleted = 0
                    ORDER BY r.PmpId, r.KraId, r.KpiId";

                using (var cmd = new SqlCommand(kpiRowSql, con))
                {
                    cmd.Parameters.AddWithValue("@Id", mappingId);
                    using var dr = cmd.ExecuteReader();
                    int i = 1;
                    while (dr.Read())
                        mapping.KpiRows.Add(MapKpiRowFromReader(dr, i++));
                }

                // ── Step 4: Load KPI documents ───────────────────────────────────────
                foreach (var kpiRow in mapping.KpiRows)
                    kpiRow.Documents = GetDocumentsForRow(con, kpiRow.EmpKpiRowId, mappingId);

                return mapping;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"GetMappingByIdInternal error (mappingId={mappingId}): {ex}");
                return null;
            }
        }

        private EmpKpiMappingModel GetMappingByUserId(string userId, string role)
        {
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                int empId = 0;
                using (var ec = new SqlCommand("SELECT ISNULL(Emp_Id,0) FROM UserDetails WHERE UserId=@U", con))
                { ec.Parameters.AddWithValue("@U", userId); var v = ec.ExecuteScalar(); if (v != null && v != DBNull.Value) empId = Convert.ToInt32(v); }
                if (empId == 0) return null;

                EmpKpiMappingModel mapping = null;
                using (var cmd = new SqlCommand(@"
                    SELECT TOP 1 
                           m.MappingId, m.EmpId, ISNULL(m.UserId,'') AS UserId,
                           m.AppraisalYear, m.AppraisalCycle, m.CycleFromDate, m.CycleToDate,
                           m.[Status], m.CreatedBy, m.CreatedAt,
                           ISNULL(e.Emp_code,'')  AS EMPCode,
                           ISNULL(LTRIM(RTRIM(ISNULL(e.Emp_First_Name,'')+' '+ISNULL(e.Emp_Last_Name,''))),'') AS FullName,
                           ISNULL(e.Work_Email,'') AS Email,
                           CAST(ISNULL(e.Emp_Superior,0) AS NVARCHAR(50)) AS ReportTo,
                           ISNULL(dept.Dept_Name,'')  AS DepartmentName,
                           ISNULL(des.Desig_Name,'')  AS DesignationName,
                           ISNULL(LTRIM(RTRIM(ISNULL(rm.Emp_First_Name,'')+' '+ISNULL(rm.Emp_Last_Name,''))),'') AS RmName,
                           '' AS HodName, '' AS UnitHeadName, '' AS ModCommitteeNames, '' AS JmdName
                    FROM EmpAppraisalMapping m
                    LEFT JOIN [Orange_Hrms].[dbo].[T0080_EMP_MASTER]        e    ON e.Emp_ID    = m.EmpId
                    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DEPARTMENT_MASTER]  dept ON dept.Dept_Id = e.Dept_ID
                    LEFT JOIN [Orange_Hrms].[dbo].[T0040_DESIGNATION_MASTER] des  ON des.Desig_ID = e.Desig_Id
                    LEFT JOIN [Orange_Hrms].[dbo].[T0080_EMP_MASTER]        rm   ON rm.Emp_ID   = e.Emp_Superior
                    WHERE m.EmpId = @EmpId AND m.IsDeleted = 0
                    ORDER BY m.AppraisalYear DESC, m.MappingId DESC", con))
                {
                    cmd.Parameters.AddWithValue("@EmpId", empId);
                    using var dr = cmd.ExecuteReader();
                    if (dr.Read()) mapping = MapMappingFromReaderSafe(dr, false);
                }
                if (mapping == null) return null;
                var existing = GetMappingByIdInternal(_conn, mapping.MappingId);
                if (existing != null) mapping.KpiRows = existing.KpiRows;
                return mapping;
            }
            catch { return null; }
        }

        private List<EmpKpiMappingModel> GetMappingsByRm(string rmUserId)
        {
            var list = new List<EmpKpiMappingModel>();
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                using var cmd = new SqlCommand(@"SELECT m.MappingId,m.EmpId,ISNULL(m.UserId,'') AS UserId,m.AppraisalYear,m.AppraisalCycle,m.CycleFromDate,m.CycleToDate,m.[Status],m.CreatedBy,m.CreatedAt,ISNULL(ud.EMPCode,'') AS EMPCode,ISNULL(ud.FullName,'') AS FullName,ISNULL(ud.Email,'') AS Email,ISNULL(ud.ReportTo,'') AS ReportTo,ISNULL(dep.DepartmentName,'') AS DepartmentName,ISNULL(des.DesignationName,'') AS DesignationName,ISNULL(rm.FullName,'') AS RmName,'' AS HodName,'' AS UnitHeadName,'' AS ModCommitteeNames,'' AS JmdName FROM EmpAppraisalMapping m INNER JOIN UserDetails ud ON ud.Emp_Id=m.EmpId AND ud.ReportTo=@RmId LEFT JOIN DepartmentMasters dep ON dep.Id=ud.DepartmentId LEFT JOIN DesignationMasters des ON des.Id=ud.DesignationId LEFT JOIN UserDetails rm ON rm.UserId=ud.ReportTo WHERE m.IsDeleted=0 AND m.[Status] IN ('EmployeeFilled','RMReview') ORDER BY m.MappingId DESC", con);
                cmd.Parameters.AddWithValue("@RmId", rmUserId);
                using var dr = cmd.ExecuteReader(); int i = 1;
                while (dr.Read()) { var m = MapMappingFromReaderSafe(dr, false); m.SrNo = i++; list.Add(m); }
            }
            catch { }
            return list;
        }

        private List<EmpKpiMappingModel> GetMappingsByHod(string hodUserId)
        {
            var list = new List<EmpKpiMappingModel>();
            try
            {
                using var con = new SqlConnection(_conn); con.Open();
                int deptId = 0;
                using (var dc = new SqlCommand("SELECT DepartmentId FROM UserDetails WHERE UserId=@Id", con))
                { dc.Parameters.AddWithValue("@Id", hodUserId); var v = dc.ExecuteScalar(); if (v != null && v != DBNull.Value) deptId = Convert.ToInt32(v); }
                if (deptId == 0) return list;
                using var cmd = new SqlCommand(@"SELECT m.MappingId,m.EmpId,ISNULL(m.UserId,'') AS UserId,m.AppraisalYear,m.AppraisalCycle,m.CycleFromDate,m.CycleToDate,m.[Status],m.CreatedBy,m.CreatedAt,ISNULL(ud.EMPCode,'') AS EMPCode,ISNULL(ud.FullName,'') AS FullName,ISNULL(ud.Email,'') AS Email,ISNULL(ud.ReportTo,'') AS ReportTo,ISNULL(dep.DepartmentName,'') AS DepartmentName,ISNULL(des.DesignationName,'') AS DesignationName,ISNULL(rm.FullName,'') AS RmName,'' AS HodName,'' AS UnitHeadName,'' AS ModCommitteeNames,'' AS JmdName FROM EmpAppraisalMapping m INNER JOIN UserDetails ud ON ud.Emp_Id=m.EmpId AND ud.DepartmentId=@DeptId LEFT JOIN DepartmentMasters dep ON dep.Id=ud.DepartmentId LEFT JOIN DesignationMasters des ON des.Id=ud.DesignationId LEFT JOIN UserDetails rm ON rm.UserId=ud.ReportTo WHERE m.IsDeleted=0 AND m.[Status] IN ('RMReview','HODReview') ORDER BY m.MappingId DESC", con);
                cmd.Parameters.AddWithValue("@DeptId", deptId);
                using var dr = cmd.ExecuteReader(); int i = 1;
                while (dr.Read()) { var m = MapMappingFromReaderSafe(dr, false); m.SrNo = i++; list.Add(m); }
            }
            catch { }
            return list;
        }

        private static EmpKpiMappingModel MapMappingFromReaderSafe(SqlDataReader dr, bool hasModJmd)
        {
            string SafeCol(string col) { try { int ord = dr.GetOrdinal(col); return dr.IsDBNull(ord) ? "" : dr.GetValue(ord).ToString(); } catch { return ""; } }
            int? SafeInt(string col) { try { int ord = dr.GetOrdinal(col); return dr.IsDBNull(ord) ? (int?)null : Convert.ToInt32(dr.GetValue(ord)); } catch { return null; } }
            return new EmpKpiMappingModel
            {
                MappingId = Convert.ToInt32(dr["MappingId"]),
                EmpId = dr["EmpId"] == DBNull.Value ? 0 : Convert.ToInt32(dr["EmpId"]),
                UserId = SafeCol("UserId"),
                EmpCode = SafeCol("EMPCode"),
                EmpName = SafeCol("FullName"),
                Email = SafeCol("Email"),
                ReportTo = SafeCol("ReportTo"),
                Designation = SafeCol("DesignationName"),
                Department = SafeCol("DepartmentName"),
                SubDepartment = SafeCol("SubDepartmentName"),
                AppraisalYear = dr["AppraisalYear"] == DBNull.Value ? 0 : Convert.ToInt32(dr["AppraisalYear"]),
                AppraisalCycle = SafeCol("AppraisalCycle"),
                CycleFromDate = dr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleFromDate"]),
                CycleToDate = dr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(dr["CycleToDate"]),
                Status = SafeCol("Status"),
                CreatedBy = SafeCol("CreatedBy"),
                CreatedAt = dr["CreatedAt"] == DBNull.Value ? null : Convert.ToDateTime(dr["CreatedAt"]),
                RmName = SafeCol("RmName"),
                HodName = SafeCol("HodName"),
                UnitHeadName = SafeCol("UnitHeadName"),
                ModCommitteeNames = SafeCol("ModCommitteeNames"),
                JmdName = SafeCol("JmdName"),
                UnitName = SafeCol("UnitName"),
                // ── NEW: reviewer EmpId columns (null-safe if SQL didn't select them) ──
                RmEmpId = SafeInt("RmEmpId"),
                HodEmpId = SafeInt("HodEmpId"),
                UnitHeadEmpId = SafeInt("UnitHeadEmpId"),
                JmdEmpId = SafeInt("JmdEmpId"),
            };
        }

        private static EmpKpiMappingModel MapMappingFromReader(SqlDataReader dr, bool hasModJmd)
            => MapMappingFromReaderSafe(dr, hasModJmd);

        private static EmpKpiRow MapKpiRowFromReader(SqlDataReader dr, int srNo)
        {
            decimal? SafeDecimal(string col) { try { int o = dr.GetOrdinal(col); return dr.IsDBNull(o) ? null : Convert.ToDecimal(dr.GetValue(o)); } catch { return null; } }
            string SafeStr(string col) { try { int o = dr.GetOrdinal(col); return dr.IsDBNull(o) ? null : dr.GetValue(o).ToString(); } catch { return null; } }
            return new EmpKpiRow
            {
                SrNo = srNo,
                EmpKpiRowId = Convert.ToInt32(dr["EmpKpiRowId"]),
                KpiId = Convert.ToInt32(dr["KpiId"]),
                KpiCode = dr["KpiCode"].ToString(),
                KpiName = dr["KpiName"].ToString(),
                PmpId = Convert.ToInt32(dr["PmpId"]),
                PmpName = dr["PmpName"].ToString(),
                KraId = Convert.ToInt32(dr["KraId"]),
                KraName = dr["KraName"].ToString(),
                UnitOfMeasure = dr["UnitOfMeasure"].ToString(),
                IndividualWeightage = Convert.ToDecimal(dr["IndividualWeightage"]),
                OverallWeightage = Convert.ToDecimal(dr["OverallWeightage"]),
                Target = Convert.ToDecimal(dr["Target"]),
                EmpActual = SafeDecimal("EmpActual"),
                EmpRating = SafeDecimal("EmpRating"),
                EmpJustification = SafeStr("EmpJustification"),
                RmActual = SafeDecimal("RmActual"),
                RmRating = SafeDecimal("RmRating"),
                RmRemarks = SafeStr("RmRemarks"),
                HodActual = SafeDecimal("HodActual"),
                HodRating = SafeDecimal("HodRating"),
                HodRemarks = SafeStr("HodRemarks"),
                UnitHeadActual = SafeDecimal("UnitHeadActual"),
                UnitHeadRating = SafeDecimal("UnitHeadRating"),
                UnitHeadRemarks = SafeStr("UnitHeadRemarks"),
                ModCommitteeActual = SafeDecimal("ModCommitteeActual"),
                ModCommitteeRating = SafeDecimal("ModCommitteeRating"),
                ModCommitteeRemarks = SafeStr("ModCommitteeRemarks"),
                JmdActual = SafeDecimal("JmdActual"),
                JmdRating = SafeDecimal("JmdRating"),
                JmdRemarks = SafeStr("JmdRemarks"),
                HrFinalRating = SafeDecimal("HrFinalRating"),
                WeightedRating = SafeDecimal("WeightedRating")
            };
        }
    }

    // ====================================================
    // DTOs / VMs  (outside PmsController class)
    // ====================================================
    public class AssignCycleRequest
    {
        public List<int> MappingIds { get; set; }
        public string AppraisalCycle { get; set; }
        public string CycleFromDate { get; set; }
        public string CycleToDate { get; set; }
        public int AppraisalYear { get; set; }
        public string ModCommitteeNames { get; set; }
        public string JmdName { get; set; }
        public string RmName { get; set; }
        public string HodName { get; set; }
        public string UnitHeadName { get; set; }
        public string NotifyEmail { get; set; }
        public bool IsEdit { get; set; }
    }

    public class ChangeLogRequest
    {
        public int MappingId { get; set; }
        public string ActionType { get; set; }
        public List<ChangeLogEntry> Changes { get; set; }
        public string SessionJson { get; set; }
    }

    public class ChangeLogEntry
    {
        public string FieldName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
    }

    public class AppraisalListVM
    {
        public List<vgl.Models.EmpKpiMappingModel> Mappings { get; set; } = new();
        public string Role { get; set; }
        public string PageTitle { get; set; }
        public string LoggedInUserId { get; set; }
    }

    public class PasBaseRowDto
    {
        public int Id { get; set; }
        public string RangeLabel { get; set; }
        public decimal? PATRevenueMin { get; set; }
        public decimal PATRevenueMax { get; set; }
        public decimal BasePercentage { get; set; }
        public int SortOrder { get; set; }
    }

    public class SaveRatingMatrixDto { public List<RatingRowDto> Rows { get; set; } }
    public class RatingRowDto { public int Id { get; set; } public string RatingLabel { get; set; } public decimal HikeOverBasePercent { get; set; } }

    public class SaveRmvVersionDto
    {
        public int Id { get; set; }
        public string VersionName { get; set; }
        public string EffectiveDate { get; set; }
        public decimal? BasePctSample { get; set; }
        public bool IsActive { get; set; }
        public string Remarks { get; set; }
        public List<RmvDetailRowDto> DetailRows { get; set; }
    }

    public class RmvDetailRowDto
    {
        public int Rating { get; set; }
        public string RatingLabel { get; set; }
        public decimal RatingRangeMin { get; set; }
        public decimal RatingRangeMax { get; set; }
        public decimal DistributionPercent { get; set; }
        public decimal HikeOverBasePercent { get; set; }
        public int SortOrder { get; set; }
    }

    public class PasYearlyEntryDto
    {
        public int Id { get; set; }
        public string FinancialYear { get; set; }
        public DateTime FYStartDate { get; set; }
        public DateTime FYEndDate { get; set; }
        public decimal? PATRevenuePercentDirect { get; set; }
        public decimal? PATAmount { get; set; }
        public decimal? RevenueAmount { get; set; }
        public decimal? PATRevenuePercent { get; set; }
        public int? BasePercentageMasterId { get; set; }
        public decimal? BasePercentage { get; set; }
        public string Remarks { get; set; }
        public bool IsFinalized { get; set; }
    }

    public class SaveMcJmdRequest
    {
        public int MappingId { get; set; }
        public decimal? McRating { get; set; }
        public decimal? JmdRating { get; set; }
    }
    public class UpdateReviewersRequest
    {
        public int MappingId { get; set; }
        public string RmName { get; set; }
        public string HodName { get; set; }
        public string UnitHeadName { get; set; }
        public string ModCommitteeNames { get; set; }
        public string JmdName { get; set; }
        // Previous values for change-notification diffing
        public string PrevRmName { get; set; }
        public string PrevHodName { get; set; }
        public string PrevUhName { get; set; }
        public string PrevModNames { get; set; }
        public string PrevJmdName { get; set; }
    }
    public class KpiDetailRequest
    {
        public int KpiId { get; set; }
        public int PmpId { get; set; }
        public int KraId { get; set; }
        public string KpiName { get; set; }
        public string UnitOfMeasure { get; set; }
        public double IndividualWeightage { get; set; }
        public double Target { get; set; }
    }
}    
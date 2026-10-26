using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using System.Data;
using vgl.Areas.Identity.Data;
using vgl.DAL;
using vgl.Helpers;
using vgl.Models;
using vgl.Services.MailService;


namespace vgl.Controllers
{
    [Route("pmsbluecollar")]
    [Authorize(Roles = "SuperAdmin,Admin,User")]
    public class PMS_BlueCollarController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly vglContext _context;
        private readonly UserManager<vglUser> _userManager;
        private readonly DataAccessLayer _dataAccessLayer;
        private readonly CommonHelper _commonHelper;
        private readonly IWebHostEnvironment _hostingEnvironment;
        private readonly string _module = "PMS Blue Collar";
        private readonly IEmailSender _emailSender;
        private readonly orgContext _contextOrg;

        public PMS_BlueCollarController(
            IConfiguration configuration,
            vglContext context,
            IEmailSender emailSender,
            UserManager<vglUser> userManager,
            SignInManager<vglUser> signInManager,
            IWebHostEnvironment hostingEnvironment,
            DataAccessLayer dataAccessLayer,
            CommonHelper commonHelper,
            orgContext contextOrg)
        {
            _configuration = configuration;
            _context = context;
            _userManager = userManager;
            _hostingEnvironment = hostingEnvironment;
            _dataAccessLayer = dataAccessLayer;
            _commonHelper = commonHelper;
            _emailSender = emailSender;
            _contextOrg = contextOrg;
        }

        // ══════════════════════════════════════════════════════════════
        // RATING SCALE
        // Percentage thresholds: ≥91→5, ≥76→4, ≥61→3, ≥46→2, else 1
        // ══════════════════════════════════════════════════════════════
        private static string CalculateRating(decimal totalScore, int totalMaxScore)
        {
            if (totalMaxScore <= 0) return "";
            decimal pct = (totalScore / totalMaxScore) * 100m;
            return pct switch
            {
                >= 85 => "5",
                >= 71 => "4",
                >= 51 => "3",
                >= 31 => "2",
                _ => "1"
            };
        }

        private static string RatingLabel(string rating) => rating switch
        {
            "5" => "Consistently Exceeds Expectations",
            "4" => "Exceeds Expectations",
            "3" => "Meets Expectations",
            "2" => "Below Expectations",
            "1" => "Not Meeting Expectations",
            _ => ""
        };

        // ══════════════════════════════════════════════════════════════
        // APPRAISAL CYCLE HELPER
        // Returns (fromDate, toDate, label) for a given cycle + year
        // ══════════════════════════════════════════════════════════════
        private static (DateTime from, DateTime to, string label) ResolveCycleDates(
            string cycleType, int year, DateTime? customFrom, DateTime? customTo)
        {
            DateTime f, t;
            switch (cycleType?.ToUpper())
            {
                case "ANNUAL":
                    f = new DateTime(year, 4, 1);
                    t = new DateTime(year + 1, 3, 31);
                    return (f, t, $"Annual {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                case "MID-YEAR":
                    f = new DateTime(year, 4, 1);
                    t = new DateTime(year, 9, 30);
                    return (f, t, $"Mid-Year {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                case "Q1":
                    f = new DateTime(year, 4, 1);
                    t = new DateTime(year, 6, 30);
                    return (f, t, $"Q1 {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                case "Q2":
                    f = new DateTime(year, 7, 1);
                    t = new DateTime(year, 9, 30);
                    return (f, t, $"Q2 {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                case "Q3":
                    f = new DateTime(year, 10, 1);
                    t = new DateTime(year, 12, 31);
                    return (f, t, $"Q3 {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                case "Q4":
                    f = new DateTime(year, 1, 1);
                    t = new DateTime(year, 3, 31);
                    return (f, t, $"Q4 {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                case "CUSTOM":
                    f = customFrom ?? DateTime.Today;
                    t = customTo ?? DateTime.Today;
                    return (f, t, $"Custom {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
                default:
                    f = new DateTime(year, 4, 1);
                    t = new DateTime(year + 1, 3, 31);
                    return (f, t, $"Annual {year} ({f:dd-MMM-yyyy} → {t:dd-MMM-yyyy})");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // GET PREVIOUS CYCLE INFO (JSON — called from HR Dashboard JS)
        // ══════════════════════════════════════════════════════════════
        [HttpGet("get-previous-cycle")]
        public async Task<IActionResult> GetPreviousCycle(int year)
        {
            string dbCon = _configuration.GetConnectionString("DbConStr");
            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT TOP 1 CycleType, CycleFromDate, CycleToDate, CycleLabel
                FROM PMS_BC_Main
                WHERE AppraisalYear = @Year AND CycleType IS NOT NULL
                ORDER BY CreatedOn DESC", con);
            cmd.Parameters.Add("@Year", SqlDbType.Int).Value = year;
            using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                return Json(new
                {
                    found = true,
                    cycleType = rdr["CycleType"]?.ToString() ?? "",
                    fromDate = rdr["CycleFromDate"] == DBNull.Value ? "" : Convert.ToDateTime(rdr["CycleFromDate"]).ToString("dd-MMM-yyyy"),
                    toDate = rdr["CycleToDate"] == DBNull.Value ? "" : Convert.ToDateTime(rdr["CycleToDate"]).ToString("dd-MMM-yyyy"),
                    label = rdr["CycleLabel"]?.ToString() ?? ""
                });
            }
            return Json(new { found = false });
        }


        // ══════════════════════════════════════════════════════════════
        // INDEX — list all PMS criteria
        // ══════════════════════════════════════════════════════════════
        [HttpGet("")]
        [HttpGet("index")]
        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            ViewData["Role"] = user.CurrentUserRole;

            if (User.IsInRole("SuperAdmin") || (per is not null && per.CanView)) goto load;
            return RedirectToAction("AccessDenied", "Auth");

        load:
            var list = new List<PMSCriteriaModel>();
            string dbCon = _configuration.GetConnectionString("DbConStr");

            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT Id, Category, MainSrNo, SubSrNo, Beh_Pers, Description,
                       MaxScore, IsDeleted, CreatedBy, CreatedOn
                FROM Behaviour_AuditList
                WHERE Category = 'PMS' AND IsDeleted = 0
                ORDER BY MainSrNo, SubSrNo", con);
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                list.Add(new PMSCriteriaModel
                {
                    Id = Convert.ToInt32(rdr["Id"]),
                    Category = "PMS",
                    MainSrNo = rdr["MainSrNo"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["MainSrNo"]),
                    SubSrNo = rdr["SubSrNo"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["SubSrNo"]),
                    Beh_Pers = rdr["Beh_Pers"]?.ToString() ?? "",
                    Description = rdr["Description"]?.ToString() ?? "",
                    MaxScore = Convert.ToInt32(rdr["MaxScore"]),
                    IsDeleted = Convert.ToBoolean(rdr["IsDeleted"]),
                    CreatedBy = rdr["CreatedBy"]?.ToString() ?? "",
                    CreatedOn = rdr["CreatedOn"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(rdr["CreatedOn"])
                });
            }

            return View(list);
        }

        // ══════════════════════════════════════════════════════════════
        // CREATE GET
        // ══════════════════════════════════════════════════════════════
        [HttpGet("create")]
        public IActionResult Create() => View(new PMSCriteriaModel());

        // ══════════════════════════════════════════════════════════════
        // CREATE POST
        // ══════════════════════════════════════════════════════════════
        [HttpPost("create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PMSCriteriaModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            model.Category = "PMS"; // always PMS

            if (string.IsNullOrWhiteSpace(model.Beh_Pers)) { model.ErrorMessage = "Behavioural Perspective is required."; return View(model); }
            if (string.IsNullOrWhiteSpace(model.Description)) { model.ErrorMessage = "Description is required."; return View(model); }
            if (model.MaxScore < 1) { model.ErrorMessage = "Max Score must be at least 1."; return View(model); }
            if (model.MainSrNo < 1) { model.ErrorMessage = "Main Sr. No is required."; return View(model); }
            if (model.SubSrNo < 1) { model.ErrorMessage = "Sub Sr. No is required."; return View(model); }

            string dbCon = _configuration.GetConnectionString("DbConStr");
            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();

            // Duplicate check
            using (var dup = new SqlCommand(@"
                SELECT COUNT(1) FROM Behaviour_AuditList
                WHERE Category='PMS' AND MainSrNo=@M AND SubSrNo=@S AND IsDeleted=0", con))
            {
                dup.Parameters.Add("@M", SqlDbType.Int).Value = model.MainSrNo;
                dup.Parameters.Add("@S", SqlDbType.Int).Value = model.SubSrNo;
                int exists = Convert.ToInt32(await dup.ExecuteScalarAsync());
                if (exists > 0)
                {
                    model.ErrorMessage = $"A PMS criteria with Main Sr. {model.MainSrNo} / Sub Sr. {model.SubSrNo} already exists.";
                    return View(model);
                }
            }
            using (var scoreCmd = new SqlCommand(@"
    SELECT ISNULL(SUM(MaxScore),0)
    FROM Behaviour_AuditList
    WHERE Category='PMS'
      AND IsDeleted=0", con))
            {
                int currentTotal = Convert.ToInt32(await scoreCmd.ExecuteScalarAsync());

                if ((currentTotal + model.MaxScore) > 100)
                {
                    model.ErrorMessage =
     "Total Value Cannot be Greater than 100.";
                    return View(model);
                }
            }
            using var cmd = new SqlCommand(@"
                INSERT INTO Behaviour_AuditList
                    (Category, MainSrNo, SubSrNo, Beh_Pers, Description, MaxScore, IsDeleted, CreatedBy, CreatedOn)
                VALUES
                    ('PMS', @Main, @Sub, @Beh, @Desc, @Max, 0, @By, GETDATE())", con);
            cmd.Parameters.Add("@Main", SqlDbType.Int).Value = model.MainSrNo;
            cmd.Parameters.Add("@Sub", SqlDbType.Int).Value = model.SubSrNo;
            cmd.Parameters.Add("@Beh", SqlDbType.NVarChar).Value = model.Beh_Pers.Trim();
            cmd.Parameters.Add("@Desc", SqlDbType.NVarChar).Value = model.Description.Trim();
            cmd.Parameters.Add("@Max", SqlDbType.Int).Value = model.MaxScore;
            cmd.Parameters.Add("@By", SqlDbType.NVarChar).Value = user.UserName ?? user.Id;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                if (ex.Message.Contains("Total Value Cannot be Greater than 100") ||
                    ex.Message.Contains("Total MaxScore for a category cannot be greater than 100"))
                {
                    model.ErrorMessage = "Total Value Cannot be Greater than 100.";
                    return View(model);
                }

                throw;
            }

            TempData["Success"] = "PMS criteria added successfully.";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════════════════════════════
        // EDIT GET
        // ══════════════════════════════════════════════════════════════
        [HttpGet("edit/{id:int}")]
        public async Task<IActionResult> Edit(int id)
        {
            string dbCon = _configuration.GetConnectionString("DbConStr");
            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT Id, Category, MainSrNo, SubSrNo, Beh_Pers, Description, MaxScore, CreatedBy, CreatedOn
                FROM Behaviour_AuditList WHERE Id=@ID AND Category='PMS' AND IsDeleted=0", con);
            cmd.Parameters.Add("@ID", SqlDbType.Int).Value = id;
            using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return NotFound();

            var model = new PMSCriteriaModel
            {
                Id = Convert.ToInt32(rdr["Id"]),
                Category = "PMS",
                MainSrNo = rdr["MainSrNo"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["MainSrNo"]),
                SubSrNo = rdr["SubSrNo"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["SubSrNo"]),
                Beh_Pers = rdr["Beh_Pers"]?.ToString() ?? "",
                Description = rdr["Description"]?.ToString() ?? "",
                MaxScore = Convert.ToInt32(rdr["MaxScore"]),
                CreatedBy = rdr["CreatedBy"]?.ToString() ?? "",
                CreatedOn = rdr["CreatedOn"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(rdr["CreatedOn"])
            };
            return View(model);
        }

        // ══════════════════════════════════════════════════════════════
        // EDIT POST
        // ══════════════════════════════════════════════════════════════
        [HttpPost("edit/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, PMSCriteriaModel model)
        {
            model.Id = id;
            model.Category = "PMS";

            if (string.IsNullOrWhiteSpace(model.Beh_Pers)) { model.ErrorMessage = "Behavioural Perspective is required."; return View(model); }
            if (string.IsNullOrWhiteSpace(model.Description)) { model.ErrorMessage = "Description is required."; return View(model); }
            if (model.MaxScore < 1) { model.ErrorMessage = "Max Score must be at least 1."; return View(model); }
            if (model.MainSrNo < 1) { model.ErrorMessage = "Main Sr. No is required."; return View(model); }
            if (model.SubSrNo < 1) { model.ErrorMessage = "Sub Sr. No is required."; return View(model); }

            string dbCon = _configuration.GetConnectionString("DbConStr");
            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();

            // Duplicate check (exclude self)
            using (var dup = new SqlCommand(@"
                SELECT COUNT(1) FROM Behaviour_AuditList
                WHERE Category='PMS' AND MainSrNo=@M AND SubSrNo=@S AND IsDeleted=0 AND Id<>@ID", con))
            {
                dup.Parameters.Add("@M", SqlDbType.Int).Value = model.MainSrNo;
                dup.Parameters.Add("@S", SqlDbType.Int).Value = model.SubSrNo;
                dup.Parameters.Add("@ID", SqlDbType.Int).Value = id;
                int exists = Convert.ToInt32(await dup.ExecuteScalarAsync());
                if (exists > 0)
                {
                    model.ErrorMessage = $"Another criteria with Main Sr. {model.MainSrNo} / Sub Sr. {model.SubSrNo} already exists.";
                    return View(model);
                }
            }
            using (var scoreCmd = new SqlCommand(@"
    SELECT ISNULL(SUM(MaxScore),0)
    FROM Behaviour_AuditList
    WHERE Category='PMS'
      AND IsDeleted=0
      AND Id<>@ID", con))
            {
                scoreCmd.Parameters.Add("@ID", SqlDbType.Int).Value = id;

                int otherTotal =
                    Convert.ToInt32(await scoreCmd.ExecuteScalarAsync());

                if ((otherTotal + model.MaxScore) > 100)
                {
                    model.ErrorMessage =
    "Total Value Cannot be Greater than 100.";
                    return View(model);
                }
            }

            using var cmd = new SqlCommand(@"
                UPDATE Behaviour_AuditList SET
                    MainSrNo    = @Main,
                    SubSrNo     = @Sub,
                    Beh_Pers    = @Beh,
                    Description = @Desc,
                    MaxScore    = @Max
                WHERE Id=@ID AND Category='PMS'", con);
            cmd.Parameters.Add("@Main", SqlDbType.Int).Value = model.MainSrNo;
            cmd.Parameters.Add("@Sub", SqlDbType.Int).Value = model.SubSrNo;
            cmd.Parameters.Add("@Beh", SqlDbType.NVarChar).Value = model.Beh_Pers.Trim();
            cmd.Parameters.Add("@Desc", SqlDbType.NVarChar).Value = model.Description.Trim();
            cmd.Parameters.Add("@Max", SqlDbType.Int).Value = model.MaxScore;
            cmd.Parameters.Add("@ID", SqlDbType.Int).Value = id;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                if (ex.Message.Contains("Total Value Cannot be Greater than 100") ||
                    ex.Message.Contains("Total MaxScore for a category cannot be greater than 100"))
                {
                    model.ErrorMessage = "Total Value Cannot be Greater than 100.";
                    return View(model);
                }

                throw;
            }

            TempData["Success"] = "PMS criteria updated successfully.";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════════════════════════════
        // DELETE (Soft Delete)
        // ══════════════════════════════════════════════════════════════
        [HttpGet("delete/{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return RedirectToPage("/Identity/Account/Login");

            string dbCon = _configuration.GetConnectionString("DbConStr");

            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();

            using var cmd = new SqlCommand(@"
        UPDATE Behaviour_AuditList
        SET
            IsDeleted = 1,
            DeletedBy = @DeletedBy,
            DeletedOn = GETDATE()
        WHERE Id = @ID
          AND Category = 'PMS'
          AND IsDeleted = 0", con);

            cmd.Parameters.Add("@ID", SqlDbType.Int).Value = id;
            cmd.Parameters.Add("@DeletedBy", SqlDbType.NVarChar).Value =
                user.UserName ?? user.Id;

            await cmd.ExecuteNonQueryAsync();

            TempData["Success"] = "Criteria deleted successfully.";

            return RedirectToAction("Index");
        }


        // ══════════════════════════════════════════════════════════════
        // HR DASHBOARD
        // ══════════════════════════════════════════════════════════════
        [HttpGet("hr-dashboard")]
        public async Task<IActionResult> HRDashboard(int? deptId, int? unitId, string? searchText, string? statusFilter)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            ViewData["Role"] = user.CurrentUserRole;

            if (User.IsInRole("SuperAdmin") || (per is not null && per.CanView)) goto codes;
            return RedirectToAction("AccessDenied", "Auth");

        codes:
            string orgCon = _configuration.GetConnectionString("OrgConStr");
            string dbCon = _configuration.GetConnectionString("DbConStr");
            var auditDb = new SqlConnectionStringBuilder(dbCon).InitialCatalog;

            var deptTask = LoadDropdownAsync(orgCon, @"
        SELECT DISTINCT d.Dept_ID AS Val, d.Dept_Name AS Txt
        FROM T0040_DEPARTMENT_MASTER d
        INNER JOIN T0080_EMP_MASTER e ON d.Dept_ID = e.Dept_ID
        INNER JOIN T0040_GRADE_MASTER grd ON e.Grd_ID = grd.Grd_ID
        WHERE e.Cmp_ID = 2 AND e.Emp_Left = 'N'
          AND grd.Grd_Name IN ('SS1','SS2','SS3','SS4','OS1','OS2','OS3','OS4','OS5','OS5T','OS6')
        ORDER BY d.Dept_Name");

            var unitTask = LoadDropdownAsync(orgCon, @"
        SELECT Cat_ID AS Val, Cat_Name AS Txt
        FROM T0030_CATEGORY_MASTER
        ORDER BY Cat_Name");

            const string allEmpSql = @"
        SELECT DISTINCT Emp_ID AS Val,
            CAST(Emp_Code AS VARCHAR(50)) + ' - ' + Emp_Full_Name AS Txt
        FROM T0080_EMP_MASTER
        WHERE Cmp_ID = 2 AND Emp_Left = 'N'
        ORDER BY Txt";

            var rmTask = LoadDropdownAsync(orgCon, allEmpSql);
            var hodTask = LoadDropdownAsync(orgCon, allEmpSql);

            var cycleTask = Task.Run(async () =>
            {
                var dict = new Dictionary<int, List<string>>();
                using var con = new SqlConnection(dbCon);
                await con.OpenAsync();
                const string sql = @"
            SELECT EmpID,
                   ISNULL(CycleType,'')    AS CycleType,
                   ISNULL(AppraisalYear,0) AS AppraisalYear
            FROM PMS_BC_Main
            WHERE ISNULL(CycleType,'') <> ''
              AND ISNULL(AppraisalYear,0) > 0";
                using var cmd = new SqlCommand(sql, con) { CommandTimeout = 30 };
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    int empId = Convert.ToInt32(rdr["EmpID"]);
                    string cycle = rdr["CycleType"].ToString()!;
                    int year = Convert.ToInt32(rdr["AppraisalYear"]);
                    string key = $"{cycle}-{year}";
                    if (!dict.ContainsKey(empId)) dict[empId] = new List<string>();
                    if (!dict[empId].Contains(key)) dict[empId].Add(key);
                }
                return dict;
            });

            var listTask = Task.Run(async () =>
            {
                var result = new List<PMSBCDashboardModel>();
                var query = $@"
SELECT * FROM (
    SELECT
        emp.Emp_ID,
        CAST(emp.Emp_Code AS NVARCHAR(50)) AS Emp_Code,
        emp.Emp_Full_Name,
        ISNULL(desig.Desig_Name,'') AS Designation,
        ISNULL(dept.Dept_Name,'')   AS DepartmentName,
        ISNULL(dept.Dept_ID,0)      AS DepartmentID,
        ISNULL(cat.Cat_Name,'')     AS UnitName,
        ISNULL(cat.Cat_ID,0)        AS UnitID,
        ISNULL(sub.SubDepartmentName,'') AS SubDepartment,
        ISNULL(rm.Emp_Full_Name,'') AS ReportingManager,
        ISNULL(NULLIF(LTRIM(RTRIM(ISNULL(p.RM_ID,''))),''
        ), CAST(emp.Emp_Superior AS NVARCHAR(50))) AS RMID,
        ISNULL(p.RM_Name,'')   AS [RM_Name_db],
        ISNULL(p.HOD_ID,'')    AS HODID,
        ISNULL(p.HOD_Name,'')  AS HOD,
        ISNULL(p.CurrentStage,'')  AS CurrentStage,
        ISNULL(p.PmsStatus,'')     AS AuditStatus,
        ISNULL(p.CycleType,'')     AS CycleType,
        ISNULL(p.CycleLabel,'')    AS CycleLabel,
        p.PmsID,
        p.CreatedOn,
        ROW_NUMBER() OVER (
            PARTITION BY emp.Emp_ID
            ORDER BY CASE WHEN p.PmsID IS NULL THEN 1 ELSE 0 END, p.PmsID DESC
        ) AS rn
    FROM T0080_EMP_MASTER emp WITH (NOLOCK)
    INNER JOIN T0040_GRADE_MASTER grd WITH (NOLOCK)
        ON emp.Grd_ID = grd.Grd_ID
        AND grd.Grd_Name IN ('SS1','SS2','SS3','SS4','OS1','OS2','OS3','OS4','OS5','OS5T','OS6')
    LEFT JOIN T0040_DESIGNATION_MASTER desig WITH (NOLOCK) ON emp.Desig_Id = desig.Desig_ID
    LEFT JOIN T0040_DEPARTMENT_MASTER  dept  WITH (NOLOCK) ON emp.Dept_ID  = dept.Dept_ID
    LEFT JOIN T0030_CATEGORY_MASTER    cat   WITH (NOLOCK) ON emp.Cat_ID   = cat.Cat_ID
    LEFT JOIN T0080_EMP_MASTER         rm    WITH (NOLOCK) ON emp.Emp_Superior = rm.Emp_ID
    LEFT JOIN [{auditDb}].dbo.PMS_BC_Main    p   ON emp.Emp_ID = p.EmpID
    LEFT JOIN [{auditDb}].dbo.UserDetails    ud  ON emp.Emp_Code = ud.EMPCode
    LEFT JOIN [{auditDb}].dbo.SubDepartmentMaster sub ON ud.SubDepartmentId = sub.Id
    WHERE emp.Cmp_ID = 2 AND emp.Emp_Left = 'N'
) ranked
WHERE rn = 1
ORDER BY Emp_Full_Name";

                using var con = new SqlConnection(orgCon);
                await con.OpenAsync();
                using var cmd = new SqlCommand(query, con) { CommandTimeout = 120 };
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    int pmsId = rdr["PmsID"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["PmsID"]);
                    string status = rdr["AuditStatus"].ToString();
                    string stage = rdr["CurrentStage"].ToString();
                    string hrStatus = BuildHRStatus(status, stage, pmsId > 0);

                    result.Add(new PMSBCDashboardModel
                    {
                        EmpID = Convert.ToInt32(rdr["Emp_ID"]),
                        EmpCode = rdr["Emp_Code"].ToString(),
                        EmpName = rdr["Emp_Full_Name"].ToString(),
                        Designation = rdr["Designation"].ToString(),
                        DepartmentName = rdr["DepartmentName"].ToString(),
                        DepartmentID = Convert.ToInt32(rdr["DepartmentID"]),
                        UnitName = rdr["UnitName"].ToString(),
                        UnitID = Convert.ToInt32(rdr["UnitID"]),
                        SubDepartment = rdr["SubDepartment"].ToString(),
                        ReportingManager = rdr["ReportingManager"].ToString(),
                        RMID = rdr["RMID"].ToString(),
                        HOD = rdr["HOD"].ToString(),
                        HODID = rdr["HODID"].ToString(),
                        AuditStatus = hrStatus,
                        CurrentStage = stage,
                        PmsID = pmsId,
                        CreatedOn = rdr["CreatedOn"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CreatedOn"])
                    });
                }
                return result;
            });

            await Task.WhenAll(deptTask, unitTask, rmTask, hodTask, listTask, cycleTask);

            // Merge completed cycles
            var cycleMap = cycleTask.Result;
            foreach (var emp in listTask.Result)
            {
                if (cycleMap.TryGetValue(emp.EmpID, out var cycles))
                    emp.CompletedCycles = cycles;
            }

            // Apply status filter AFTER HR labels are computed
            var finalList = listTask.Result;
            if (!string.IsNullOrEmpty(statusFilter))
            {
                // "In Progress" covers both Pending at RM and Pending at HOD
                if (statusFilter == "In Progress")
                    finalList = finalList.Where(e =>
                        e.AuditStatus == "Pending at RM" ||
                        e.AuditStatus == "Pending at HOD").ToList();
                else
                    finalList = finalList.Where(e => e.AuditStatus == statusFilter).ToList();
            }

            ViewBag.DepartmentList = deptTask.Result;
            ViewBag.UnitList = unitTask.Result;
            ViewBag.RMList = rmTask.Result;
            ViewBag.HODList = hodTask.Result;
            ViewBag.SelectedDeptID = deptId;
            ViewBag.SelectedUnitID = unitId;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = statusFilter;

            ViewBag.CompletedCyclesJson = System.Text.Json.JsonSerializer.Serialize(
                listTask.Result.ToDictionary(       // use full list, not filtered
                    e => e.EmpID,
                    e => e.CompletedCycles
                )
            );

            return View(finalList);
        }
        private static string BuildHRStatus(string status, string stage, bool hasRecord)
        {
            if (!hasRecord) return "Not Started";
            if (status == "Completed") return "Completed";
            if (stage == "HOD" || status == "Pending at HOD") return "Pending at HOD";
            if (stage == "RM" || status == "Pending at RM") return "Pending at RM";
            if (status == "In Process") return "In Progress";
            if (!string.IsNullOrEmpty(status)) return status;
            return "Not Started";
        }

        // ══════════════════════════════════════════════════════════════
        // SAVE PMS (HR initiates) — NOW INCLUDES APPRAISAL CYCLE
        // ══════════════════════════════════════════════════════════════
        [HttpPost("save-pms")]
        public async Task<IActionResult> SavePms([FromBody] PMSBCSavePayload payload)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Json(new { success = false, message = "Session expired." });

            var emailWarnings = new List<string>();

            try
            {
                var model = payload?.Employees;
                if (model == null || !model.Any())
                    return Json(new { success = false, message = "No data received." });

                // Validate cycle
                string cycleType = payload!.CycleType?.Trim() ?? "";
                int cycleYear = payload.CycleYear > 0 ? payload.CycleYear : DateTime.Today.Year;
                if (string.IsNullOrWhiteSpace(cycleType))
                    return Json(new { success = false, message = "Please select an appraisal cycle." });

                DateTime? customFrom = payload.CustomFromDate;
                DateTime? customTo = payload.CustomToDate;
                var (cycleFrom, cycleTo, cycleLabel) = ResolveCycleDates(cycleType, cycleYear, customFrom, customTo);

                string dbCon = _configuration.GetConnectionString("DbConStr");
                string orgCon = _configuration.GetConnectionString("OrgConStr");
                string baseUrl = $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}";

                // Normalise IDs
                foreach (var item in model)
                {
                    if (int.TryParse(item.RMID, out int rid)) item.RMID = rid > 0 ? rid.ToString() : "";
                    if (int.TryParse(item.HODID, out int hid)) item.HODID = hid > 0 ? hid.ToString() : "";
                }

                var empIds = model.Select(m => m.EmpID).Distinct().ToList();
                var personIds = model
                    .SelectMany(m => new[] { m.RMID, m.HODID })
                    .Select(s => int.TryParse(s, out int i) ? i : 0)
                    .Where(i => i > 0).Distinct().ToList();

                var orgPersonMap = await LoadOrgEmployeesByIdsAsync(orgCon, personIds);

                using var sqlCon = new SqlConnection(dbCon);
                await sqlCon.OpenAsync();

                var rmEmailMap = new Dictionary<string, (string name, string email, List<string> emps)>();

                foreach (var item in model)
                {
                    bool hodAssigned = int.TryParse(item.HODID, out int hodInt) && hodInt > 0;

                    DateTime? prevDate = null;
                    using (var chk = new SqlCommand("SELECT MAX(CreatedOn) FROM PMS_BC_Main WHERE EmpID=@EID", sqlCon))
                    {
                        chk.Parameters.Add("@EID", SqlDbType.Int).Value = item.EmpID;
                        var dr = await chk.ExecuteScalarAsync();
                        if (dr != null && dr != DBNull.Value) prevDate = Convert.ToDateTime(dr);
                    }
                    // ── Guard: block if this employee already has this cycle+year ──
                    using (var dupChk = new SqlCommand(@"
    SELECT COUNT(1) FROM PMS_BC_Main
    WHERE EmpID = @EID AND CycleType = @CT AND AppraisalYear = @YR", sqlCon))
                    {
                        dupChk.Parameters.Add("@EID", SqlDbType.Int).Value = item.EmpID;
                        dupChk.Parameters.Add("@CT", SqlDbType.NVarChar).Value = cycleType;
                        dupChk.Parameters.Add("@YR", SqlDbType.Int).Value = cycleYear;
                        int exists = Convert.ToInt32(await dupChk.ExecuteScalarAsync());
                        if (exists > 0)
                        {
                            emailWarnings.Add($"{item.EmpName} already has a {cycleType} PMS for {cycleYear} — skipped.");
                            continue; // skip this employee, don't insert
                        }
                    }

                    string sql = @"
                        INSERT INTO PMS_BC_Main
                            (EmpID,EmpCode,EmpName,DesigName,DeptName,SubDeptName,
                             DeptID,UnitID,RM_ID,RM_Name,HOD_ID,HOD_Name,
                             CurrentStage,PmsStatus,FinalStatus,HOD_Required,
                             AppraisalYear,CycleType,CycleFromDate,CycleToDate,CycleLabel,
                             CreatedBy,CreatedOn,PreviousPmsDate)
                        VALUES
                            (@EmpID,@EmpCode,@EmpName,@DesigName,@DeptName,@SubDeptName,
                             @DeptID,@UnitID,@RM_ID,@RM_Name,@HOD_ID,@HOD_Name,
                             'RM','Pending at RM','In Process',@HOD_Required,
                             @AppraisalYear,@CycleType,@CycleFromDate,@CycleToDate,@CycleLabel,
                             @CreatedBy,GETDATE(),@PrevDate)";

                    using var ins = new SqlCommand(sql, sqlCon);
                    ins.Parameters.Add("@EmpID", SqlDbType.Int).Value = item.EmpID;
                    ins.Parameters.Add("@EmpCode", SqlDbType.NVarChar).Value = item.EmpCode ?? "";
                    ins.Parameters.Add("@EmpName", SqlDbType.NVarChar).Value = item.EmpName ?? "";
                    ins.Parameters.Add("@DesigName", SqlDbType.NVarChar).Value = item.Designation ?? "";
                    ins.Parameters.Add("@DeptName", SqlDbType.NVarChar).Value = item.Department ?? "";
                    ins.Parameters.Add("@SubDeptName", SqlDbType.NVarChar).Value = item.SubDepartment ?? "";
                    ins.Parameters.Add("@DeptID", SqlDbType.Int).Value = item.DepartmentID;
                    ins.Parameters.Add("@UnitID", SqlDbType.Int).Value = item.UnitID;
                    ins.Parameters.Add("@RM_ID", SqlDbType.NVarChar).Value = item.RMID ?? "";
                    ins.Parameters.Add("@RM_Name", SqlDbType.NVarChar).Value = item.RMName ?? "";
                    ins.Parameters.Add("@HOD_ID", SqlDbType.NVarChar).Value = item.HODID ?? "";
                    ins.Parameters.Add("@HOD_Name", SqlDbType.NVarChar).Value = item.HODName ?? "";
                    ins.Parameters.Add("@HOD_Required", SqlDbType.Bit).Value = hodAssigned;
                    ins.Parameters.Add("@AppraisalYear", SqlDbType.Int).Value = cycleYear;
                    ins.Parameters.Add("@CycleType", SqlDbType.NVarChar).Value = cycleType;
                    ins.Parameters.Add("@CycleFromDate", SqlDbType.Date).Value = cycleFrom;
                    ins.Parameters.Add("@CycleToDate", SqlDbType.Date).Value = cycleTo;
                    ins.Parameters.Add("@CycleLabel", SqlDbType.NVarChar).Value = cycleLabel;
                    ins.Parameters.Add("@CreatedBy", SqlDbType.NVarChar).Value = user.Id;
                    ins.Parameters.Add("@PrevDate", SqlDbType.DateTime).Value = (object?)prevDate ?? DBNull.Value;
                    await ins.ExecuteNonQueryAsync();

                    // Notify RM
                    int rmIdInt = int.TryParse(item.RMID, out int r2) ? r2 : 0;
                    if (rmIdInt > 0)
                    {
                        try
                        {
                            string rmEmpCode = orgPersonMap.TryGetValue(rmIdInt, out var rmOrg) ? rmOrg.EmpCode : "";
                            string rmUserId = !string.IsNullOrWhiteSpace(rmEmpCode)
                                ? _context.UserDetails.Where(a => a.EMPCode == rmEmpCode).Select(a => a.UserId).FirstOrDefault()
                                : null;
                            if (!string.IsNullOrWhiteSpace(rmUserId))
                            {
                                CreateNotification(rmUserId, user.Id, user.Id,
                                    $"You have been assigned as Reporting Manager for Blue Collar PMS — {cycleLabel}",
                                    "HR_PMS_BC_Notification", _module,
                                    $"/pmsbluecollar/rm-dashboard?rmId={rmIdInt}");
                            }
                        }
                        catch { }

                        string rmEmail = orgPersonMap.TryGetValue(rmIdInt, out var rmInfo) ? rmInfo.WorkEmail : "";
                        string rmDisplayName = orgPersonMap.TryGetValue(rmIdInt, out var rmN) ? rmN.FullName : (item.RMName ?? "Reporting Manager");
                        if (string.IsNullOrWhiteSpace(rmEmail))
                        {
                            emailWarnings.Add($"RM {rmDisplayName}: no Work_Email found.");
                        }
                        else
                        {
                            string key = rmIdInt.ToString();
                            if (!rmEmailMap.ContainsKey(key)) rmEmailMap[key] = (rmDisplayName, rmEmail, new List<string>());
                            rmEmailMap[key].emps.Add(item.EmpName ?? "");
                        }
                    }
                }

                // Send RM emails
                if (rmEmailMap.Any())
                {
                    try
                    {
                        foreach (var kvp in rmEmailMap)
                        {
                            string link = $"{baseUrl}/pmsbluecollar/rm-dashboard?rmId={kvp.Key}";
                            string body = BuildAssignmentEmailBody(kvp.Value.name, "Reporting Manager (RM)", kvp.Value.emps, link, cycleLabel);
                            await SendEmailAsync(kvp.Value.email, $"[Action Required] PMS Blue Collar — RM Score Submission ({cycleLabel})", body);
                        }
                    }
                    catch (Exception ex) { emailWarnings.Add("Email send failed: " + ex.Message); }
                }

                return Json(new { success = true, emailWarnings = emailWarnings.Count > 0 ? emailWarnings : null });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // RM DASHBOARD
        // ══════════════════════════════════════════════════════════════
        [HttpGet("rm-dashboard")]
        public async Task<IActionResult> RMDashboard(string rmId, string searchText, string status)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            ViewData["Role"] = user.CurrentUserRole;

            if (User.IsInRole("SuperAdmin") || (per is not null && per.CanView)) goto codes;
            return RedirectToAction("AccessDenied", "Auth");

        codes:
            if (string.IsNullOrEmpty(rmId)) return Content("RM ID is required.");

            string dbCon = _configuration.GetConnectionString("DbConStr");
            string orgCon = _configuration.GetConnectionString("OrgConStr");

            string rmName = "";
            using (var con = new SqlConnection(orgCon))
            {
                await con.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 Emp_Full_Name FROM T0080_EMP_MASTER WHERE Emp_ID=@ID", con);
                cmd.Parameters.Add("@ID", SqlDbType.NVarChar).Value = rmId;
                rmName = (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
            }

            var list = new List<PMSBCRMDashboardModel>();
            using (var con = new SqlConnection(dbCon))
            {
                await con.OpenAsync();
                var sb = new System.Text.StringBuilder(@"
                    SELECT p.PmsID, p.EmpID, p.EmpCode, p.EmpName, p.DesigName, p.DeptName, p.SubDeptName,
                           p.PmsStatus AS AuditStatus,
                           CASE WHEN p.RM_SubmitDate IS NULL THEN 'Pending' ELSE 'Submitted' END AS RMStatus,
                           ISNULL(p.FinalStatus,'') AS FinalStatus,
                           ISNULL(p.CycleLabel,'') AS CycleLabel,
                           p.CreatedOn, p.RM_SubmitDate,
                           ISNULL(SUM(d.RM_Score),0) AS RM_TotalScore,
                           COUNT(d.DetailID) AS CriteriaCount
                    FROM PMS_BC_Main p
                    LEFT JOIN PMS_BC_Details d ON p.PmsID = d.PmsID
                    WHERE p.RM_ID = @RMID");
                if (!string.IsNullOrEmpty(searchText))
                    sb.Append(" AND (p.EmpCode LIKE @Search OR p.EmpName LIKE @Search)");
                if (!string.IsNullOrEmpty(status) && status != "All")
                    sb.Append(" AND p.PmsStatus = @Status");
                sb.Append(" GROUP BY p.PmsID,p.EmpID,p.EmpCode,p.EmpName,p.DesigName,p.DeptName,p.SubDeptName,p.PmsStatus,p.RM_SubmitDate,p.FinalStatus,p.CycleLabel,p.CreatedOn ORDER BY p.PmsID DESC");

                using var cmd = new SqlCommand(sb.ToString(), con) { CommandTimeout = 30 };
                cmd.Parameters.Add("@RMID", SqlDbType.NVarChar).Value = rmId;
                if (!string.IsNullOrEmpty(searchText)) cmd.Parameters.Add("@Search", SqlDbType.NVarChar).Value = "%" + searchText + "%";
                if (!string.IsNullOrEmpty(status) && status != "All") cmd.Parameters.Add("@Status", SqlDbType.NVarChar).Value = status;

                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    list.Add(new PMSBCRMDashboardModel
                    {
                        PmsID = Convert.ToInt32(rdr["PmsID"]),
                        EmpID = Convert.ToInt32(rdr["EmpID"]),
                        EmpCode = rdr["EmpCode"]?.ToString() ?? "",
                        EmpName = rdr["EmpName"]?.ToString() ?? "",
                        Designation = rdr["DesigName"]?.ToString() ?? "",
                        Department = rdr["DeptName"]?.ToString() ?? "",
                        SubDepartment = rdr["SubDeptName"]?.ToString() ?? "",
                        AuditStatus = rdr["AuditStatus"]?.ToString() ?? "",
                        RMStatus = rdr["RMStatus"]?.ToString() ?? "Pending",
                        FinalStatus = rdr["FinalStatus"]?.ToString() ?? "",
                        CreatedOn = rdr["CreatedOn"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CreatedOn"]),
                        RM_SubmitDate = rdr["RM_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["RM_SubmitDate"]),
                        RM_TotalScore = Convert.ToInt32(rdr["RM_TotalScore"]),
                        CriteriaCount = Convert.ToInt32(rdr["CriteriaCount"])
                    });
                }
            }

            ViewBag.RMID = rmId;
            ViewBag.RMName = rmName;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = status;

            return View(list);
        }

        // ══════════════════════════════════════════════════════════════
        // HOD DASHBOARD
        // ══════════════════════════════════════════════════════════════
        [HttpGet("hod-dashboard")]
        public async Task<IActionResult> HODDashboard(string hodId, string searchText, string status)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            ViewData["Role"] = user.CurrentUserRole;

            if (User.IsInRole("SuperAdmin") || (per is not null && per.CanView)) goto codes;
            return RedirectToAction("AccessDenied", "Auth");

        codes:
            if (string.IsNullOrEmpty(hodId)) return Content("HOD ID is required.");

            string dbCon = _configuration.GetConnectionString("DbConStr");
            string orgCon = _configuration.GetConnectionString("OrgConStr");

            string hodName = "";
            using (var con = new SqlConnection(orgCon))
            {
                await con.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 Emp_Full_Name FROM T0080_EMP_MASTER WHERE Emp_ID=@ID", con);
                cmd.Parameters.Add("@ID", SqlDbType.NVarChar).Value = hodId;
                hodName = (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
            }

            var list = new List<PMSBCHODDashboardModel>();
            using (var con = new SqlConnection(dbCon))
            {
                await con.OpenAsync();
                var sb = new System.Text.StringBuilder(@"
                    SELECT p.PmsID, p.EmpID, p.EmpCode, p.EmpName, p.DesigName, p.DeptName, p.SubDeptName,
                           p.RM_Name AS RMName,
                           p.PmsStatus AS AuditStatus,
                           CASE WHEN p.RM_SubmitDate IS NULL THEN 'Pending' ELSE 'Done' END AS RMStatus,
                           CASE WHEN p.HOD_SubmitDate IS NULL THEN 'Pending' ELSE 'Submitted' END AS HODStatus,
                           ISNULL(p.FinalStatus,'') AS FinalStatus,
                           ISNULL(p.CycleLabel,'') AS CycleLabel,
                           p.CreatedOn, p.RM_SubmitDate, p.HOD_SubmitDate,
                           ISNULL(SUM(d.RM_Score),0) AS RM_TotalScore,
                           ISNULL(SUM(d.HOD_Score),0) AS HOD_TotalScore,
                           COUNT(d.DetailID) AS CriteriaCount
                    FROM PMS_BC_Main p
                    LEFT JOIN PMS_BC_Details d ON p.PmsID = d.PmsID
                    WHERE p.HOD_ID = @HODID");
                if (!string.IsNullOrEmpty(searchText))
                    sb.Append(" AND (p.EmpCode LIKE @Search OR p.EmpName LIKE @Search)");
                if (!string.IsNullOrEmpty(status) && status != "All")
                    sb.Append(" AND p.PmsStatus = @Status");
                sb.Append(" GROUP BY p.PmsID,p.EmpID,p.EmpCode,p.EmpName,p.DesigName,p.DeptName,p.SubDeptName,p.RM_Name,p.PmsStatus,p.RM_SubmitDate,p.HOD_SubmitDate,p.FinalStatus,p.CycleLabel,p.CreatedOn ORDER BY p.PmsID DESC");

                using var cmd = new SqlCommand(sb.ToString(), con) { CommandTimeout = 30 };
                cmd.Parameters.Add("@HODID", SqlDbType.NVarChar).Value = hodId;
                if (!string.IsNullOrEmpty(searchText)) cmd.Parameters.Add("@Search", SqlDbType.NVarChar).Value = "%" + searchText + "%";
                if (!string.IsNullOrEmpty(status) && status != "All") cmd.Parameters.Add("@Status", SqlDbType.NVarChar).Value = status;

                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    list.Add(new PMSBCHODDashboardModel
                    {
                        PmsID = Convert.ToInt32(rdr["PmsID"]),
                        EmpID = Convert.ToInt32(rdr["EmpID"]),
                        EmpCode = rdr["EmpCode"]?.ToString() ?? "",
                        EmpName = rdr["EmpName"]?.ToString() ?? "",
                        Designation = rdr["DesigName"]?.ToString() ?? "",
                        Department = rdr["DeptName"]?.ToString() ?? "",
                        SubDepartment = rdr["SubDeptName"]?.ToString() ?? "",
                        RMName = rdr["RMName"]?.ToString() ?? "",
                        AuditStatus = rdr["AuditStatus"]?.ToString() ?? "",
                        RMStatus = rdr["RMStatus"]?.ToString() ?? "",
                        HODStatus = rdr["HODStatus"]?.ToString() ?? "",
                        FinalStatus = rdr["FinalStatus"]?.ToString() ?? "",
                        CreatedOn = rdr["CreatedOn"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CreatedOn"]),
                        RM_SubmitDate = rdr["RM_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["RM_SubmitDate"]),
                        HOD_SubmitDate = rdr["HOD_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["HOD_SubmitDate"]),
                        RM_TotalScore = Convert.ToInt32(rdr["RM_TotalScore"]),
                        HOD_TotalScore = Convert.ToInt32(rdr["HOD_TotalScore"]),
                        CriteriaCount = Convert.ToInt32(rdr["CriteriaCount"])
                    });
                }
            }

            ViewBag.HODID = hodId;
            ViewBag.HODName = hodName;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = status;

            return View(list);
        }



        // ══════════════════════════════════════════════════════════════
        // TRACKING DASHBOARD
        // ══════════════════════════════════════════════════════════════
        [HttpGet("tracking")]
        public async Task<IActionResult> Tracking(
    string? searchText, int? deptId, int? unitId, string? status,
    int? year, string? cycleType)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            ViewData["Role"] = user.CurrentUserRole;

            if (User.IsInRole("SuperAdmin") || (per is not null && per.CanView)) goto codes;
            return RedirectToAction("AccessDenied", "Auth");

        codes:
            string orgCon = _configuration.GetConnectionString("OrgConStr");
            string dbCon = _configuration.GetConnectionString("DbConStr");

            var deptTask = LoadDropdownAsync(orgCon,
                "SELECT Dept_ID AS Val, Dept_Name AS Txt FROM T0040_DEPARTMENT_MASTER ORDER BY Dept_Name");
            var unitTask = LoadDropdownAsync(orgCon,
                "SELECT Cat_ID AS Val, Cat_Name AS Txt FROM T0030_CATEGORY_MASTER ORDER BY Cat_Name");

            // ── Year list: only years that have Annual PMS records ──
            var yearList = new List<int>();
            using (var con = new SqlConnection(dbCon))
            {
                await con.OpenAsync();
                using var cmd = new SqlCommand(@"
            SELECT DISTINCT ISNULL(AppraisalYear, YEAR(CreatedOn)) AS Yr
            FROM PMS_BC_Main
            WHERE ISNULL(CycleType,'') = 'Annual'
            ORDER BY Yr DESC", con);
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                    yearList.Add(Convert.ToInt32(rdr["Yr"]));
            }

            // ── Shared fetch function ──
            async Task<List<PMSBCTrackingModel>> FetchList(bool applyAllFilters)
            {
                var result = new List<PMSBCTrackingModel>();
                using var con = new SqlConnection(dbCon);
                await con.OpenAsync();

                var sb = new System.Text.StringBuilder(@"
            SELECT p.PmsID, p.EmpID, p.EmpCode, p.EmpName, p.DesigName, p.DeptName, p.SubDeptName,
                   p.DeptID, p.UnitID, p.RM_Name AS RM, p.HOD_Name AS HOD,
                   ISNULL(p.HOD_ID,'')                   AS HODID,
                   ISNULL(p.PmsStatus,'')                AS [Status],
                   ISNULL(CAST(p.HOD_Required AS INT),0) AS HOD_Required,
                   ISNULL(p.Rating,'')                   AS Rating,
                   ISNULL(p.CycleType,'')                AS CycleType,
                   p.CycleFromDate, p.CycleToDate,
                   p.MC_SubmitDate, p.JMD_SubmitDate,
                   ISNULL(p.AppraisalYear, YEAR(p.CreatedOn)) AS AppraisalYear,
                   p.CreatedOn, p.PreviousPmsDate
            FROM PMS_BC_Main p
            WHERE 1=1");

                if (applyAllFilters)
                {
                    if (!string.IsNullOrEmpty(searchText))
                        sb.Append(" AND (p.EmpCode LIKE @Search OR p.EmpName LIKE @Search)");

                    if (deptId.HasValue)
                        sb.Append(" AND p.DeptID = @DeptID");

                    if (unitId.HasValue)
                        sb.Append(" AND p.UnitID = @UnitID");

                    // "Pending at MC/JMD" and "Completed" are handled in-memory after fetch.
                    // For these we still want PmsStatus='Completed' as the SQL base set.
                    if (!string.IsNullOrEmpty(status) &&
                        status != "Pending at MC/JMD" &&
                        status != "Completed")
                    {
                        sb.Append(" AND p.PmsStatus = @Status");
                    }
                    if (status == "Pending at MC/JMD" || status == "Completed")
                    {
                        sb.Append(" AND p.PmsStatus = 'Completed'");
                    }

                    if (year.HasValue && year.Value > 0)
                        sb.Append(" AND ISNULL(p.AppraisalYear, YEAR(p.CreatedOn)) = @Year");

                    if (!string.IsNullOrEmpty(cycleType))
                        sb.Append(" AND p.CycleType = @CycleType");
                }

                sb.Append(" ORDER BY p.CreatedOn DESC, p.PmsID DESC");

                using var cmd = new SqlCommand(sb.ToString(), con) { CommandTimeout = 30 };

                if (applyAllFilters)
                {
                    if (!string.IsNullOrEmpty(searchText))
                        cmd.Parameters.Add("@Search", SqlDbType.NVarChar).Value = "%" + searchText + "%";
                    if (deptId.HasValue)
                        cmd.Parameters.Add("@DeptID", SqlDbType.Int).Value = deptId.Value;
                    if (unitId.HasValue)
                        cmd.Parameters.Add("@UnitID", SqlDbType.Int).Value = unitId.Value;
                    // Only bind @Status for simple statuses (not the merged MC/JMD or Completed)
                    if (!string.IsNullOrEmpty(status) &&
                        status != "Pending at MC/JMD" &&
                        status != "Completed")
                        cmd.Parameters.Add("@Status", SqlDbType.NVarChar).Value = status;
                    if (year.HasValue && year.Value > 0)
                        cmd.Parameters.Add("@Year", SqlDbType.Int).Value = year.Value;
                    if (!string.IsNullOrEmpty(cycleType))
                        cmd.Parameters.Add("@CycleType", SqlDbType.NVarChar).Value = cycleType;
                }

                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    result.Add(new PMSBCTrackingModel
                    {
                        PmsID = Convert.ToInt32(rdr["PmsID"]),
                        EmpID = Convert.ToInt32(rdr["EmpID"]),
                        EmpCode = rdr["EmpCode"]?.ToString() ?? "",
                        EmpName = rdr["EmpName"]?.ToString() ?? "",
                        Designation = rdr["DesigName"]?.ToString() ?? "",
                        Department = rdr["DeptName"]?.ToString() ?? "",
                        SubDepartment = rdr["SubDeptName"]?.ToString() ?? "",
                        DeptID = Convert.ToInt32(rdr["DeptID"]),
                        UnitID = Convert.ToInt32(rdr["UnitID"]),
                        RM = rdr["RM"]?.ToString() ?? "",
                        HOD = rdr["HOD"]?.ToString() ?? "",
                        HODID = rdr["HODID"]?.ToString() ?? "",
                        Status = rdr["Status"]?.ToString() ?? "",
                        HOD_Required = Convert.ToInt32(rdr["HOD_Required"]) == 1,
                        Rating = rdr["Rating"]?.ToString() ?? "",
                        CycleType = rdr["CycleType"]?.ToString() ?? "",
                        FromDate = rdr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CycleFromDate"]),
                        ToDate = rdr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CycleToDate"]),
                        Year = rdr["AppraisalYear"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["AppraisalYear"]),
                        CreatedOn = rdr["CreatedOn"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CreatedOn"]),
                        PreviousAuditDate = rdr["PreviousPmsDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["PreviousPmsDate"]),
                        MC_SubmitDate = rdr["MC_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["MC_SubmitDate"]),
                        JMD_SubmitDate = rdr["JMD_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["JMD_SubmitDate"])
                    });
                }
                return result;
            }

            var allList = await FetchList(applyAllFilters: false);
            var filteredList = await FetchList(applyAllFilters: true);

            // ══════════════════════════════════════════════════════════════
            // In-memory status refinement
            //
            // "Pending at MC/JMD" (merged) — Annual, PmsStatus=Completed,
            //                                 MC or JMD not yet submitted
            // "Completed"                  — Non-Annual: PmsStatus=Completed
            //                                Annual: BOTH MC + JMD submitted
            // ══════════════════════════════════════════════════════════════
            if (status == "Pending at MC/JMD")
            {
                // Show ALL Annual records that are HOD-approved but not yet
                // fully through MC and JMD (i.e. either MC or JMD still pending)
                filteredList = filteredList
                    .Where(x =>
                        x.Status == "Completed" &&
                        string.Equals(x.CycleType, "Annual", StringComparison.OrdinalIgnoreCase) &&
                        (x.MC_SubmitDate == null || x.JMD_SubmitDate == null))
                    .ToList();
            }
            else if (status == "Completed")
            {
                // Annual records only count as Completed when BOTH MC and JMD are submitted
                filteredList = filteredList
                    .Where(x =>
                        x.Status == "Completed" &&
                        (
                            !string.Equals(x.CycleType, "Annual", StringComparison.OrdinalIgnoreCase) ||
                            (x.MC_SubmitDate != null && x.JMD_SubmitDate != null)
                        ))
                    .ToList();
            }

            await Task.WhenAll(deptTask, unitTask);

            // ── Stat counts (always ignore the active status filter so all cards stay accurate) ──
            var statSource = allList; // start from unfiltered

            if (!string.IsNullOrEmpty(status))
            {
                // Re-build statSource without status but with all other active filters
                statSource = (await FetchList(false))
                    .Where(x =>
                        (string.IsNullOrEmpty(searchText) ||
                         x.EmpCode.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                         x.EmpName.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
                    .Where(x => !deptId.HasValue || x.DeptID == deptId.Value)
                    .Where(x => !unitId.HasValue || x.UnitID == unitId.Value)
                    .Where(x => !year.HasValue || year == 0 || x.Year == year.Value)
                    .Where(x => string.IsNullOrEmpty(cycleType) || x.CycleType == cycleType)
                    .ToList();
            }

            ViewBag.StatTotal = statSource.Count;

            ViewBag.StatAtRM = statSource.Count(x =>
                x.Status == "Pending at RM");

            ViewBag.StatAtHOD = statSource.Count(x =>
                x.Status == "Pending at HOD");

            // MERGED STAT: Annual records where MC or JMD is still pending
            ViewBag.StatAtMCJMD = statSource.Count(x =>
                x.Status == "Completed" &&
                string.Equals(x.CycleType, "Annual", StringComparison.OrdinalIgnoreCase) &&
                (x.MC_SubmitDate == null || x.JMD_SubmitDate == null));

            // Truly completed:
            //   Non-Annual → PmsStatus = Completed
            //   Annual     → PmsStatus = Completed AND both MC + JMD submitted
            ViewBag.StatCompleted = statSource.Count(x =>
                x.Status == "Completed" &&
                (
                    !string.Equals(x.CycleType, "Annual", StringComparison.OrdinalIgnoreCase) ||
                    (x.MC_SubmitDate != null && x.JMD_SubmitDate != null)
                ));

            ViewBag.StatReAssessed = statSource.Count(x => x.PreviousAuditDate.HasValue);
            ViewBag.StatRated = statSource.Count(x => !string.IsNullOrEmpty(x.Rating));

            ViewBag.DepartmentList = deptTask.Result;
            ViewBag.UnitList = unitTask.Result;
            ViewBag.YearList = yearList;
            ViewBag.SelectedYear = year;
            ViewBag.SelectedCycle = cycleType;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = status;

            return View(filteredList);
        }

        // ══════════════════════════════════════════════════════════════
        // MC / JMD DASHBOARD
        // ══════════════════════════════════════════════════════════════
        [HttpGet("mc-jmd-dashboard")]
        public async Task<IActionResult> MCJMDDashboard(
    string? searchText, int? year, string? tab, int? deptId, int? unitId, string? statusFilter)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Identity/Account/Login");

            var per = _dataAccessLayer.GetUserModulePermission(user.Id, user.CurrentUserRole, _module);
            ViewData["Permissions"] = per;
            ViewData["Role"] = user.CurrentUserRole;

            if (User.IsInRole("SuperAdmin") || (per is not null && per.CanView)) goto codes;
            return RedirectToAction("AccessDenied", "Auth");

        codes:
            string dbCon = _configuration.GetConnectionString("DbConStr");
            string orgCon = _configuration.GetConnectionString("OrgConStr");

            // ── Year list for dropdown ──
            var yearList = new List<int>();
            using (var con = new SqlConnection(dbCon))
            {
                await con.OpenAsync();
                using var cmd = new SqlCommand(@"
            SELECT DISTINCT ISNULL(AppraisalYear, YEAR(CreatedOn)) AS Yr
            FROM PMS_BC_Main
            WHERE PmsStatus = 'Completed'
            ORDER BY Yr DESC", con);
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                    yearList.Add(Convert.ToInt32(rdr["Yr"]));
            }

            // ── Local helper: run the main query with optional filters ──
            async Task<List<PMSBCMCJMDModel>> QueryData(bool applySearch)
            {
                var result = new List<PMSBCMCJMDModel>();
                using var con = new SqlConnection(dbCon);
                await con.OpenAsync();

                var sb = new System.Text.StringBuilder(@"
            SELECT p.PmsID, p.EmpID, p.EmpCode, p.EmpName,
                   p.DesigName, p.DeptName, p.SubDeptName, p.DeptID, p.UnitID,
                   ISNULL(p.CycleType,'')                              AS CycleType,
                   ISNULL(p.CycleLabel,'')                             AS CycleLabel,
                   p.CycleFromDate, p.CycleToDate,
                   ISNULL(p.AppraisalYear, YEAR(p.CreatedOn))          AS AppraisalYear,
                   ISNULL(p.Rating,'')                                 AS FinalRating,
                   ISNULL(p.MC_Score,0)                                AS MC_Score,
                   ISNULL(p.MC_Remark,'')                              AS MC_Remark,
                   p.MC_SubmitDate,
                   ISNULL(p.JMD_Score,0)                               AS JMD_Score,
                   ISNULL(p.JMD_Remark,'')                             AS JMD_Remark,
                   p.JMD_SubmitDate,
                   ISNULL(SUM(CASE WHEN d.RM_Score  IS NOT NULL THEN d.RM_Score  ELSE 0 END),0) AS RM_TotalScore,
                   ISNULL(SUM(CASE WHEN d.HOD_Score IS NOT NULL THEN d.HOD_Score ELSE 0 END),0) AS HOD_TotalScore,
                   ISNULL(SUM(al.MaxScore),0)                          AS TotalMaxScore
            FROM PMS_BC_Main p
            LEFT JOIN PMS_BC_Details d   ON p.PmsID = d.PmsID
            LEFT JOIN Behaviour_AuditList al ON al.Id = d.CriteriaID AND al.Category = 'PMS'
            WHERE p.PmsStatus = 'Completed'");

                // Year filter — always applied (affects stat cards + table)
                if (year.HasValue && year.Value > 0)
                    sb.Append(" AND ISNULL(p.AppraisalYear, YEAR(p.CreatedOn)) = @Year");

                // Search filter — only for table rows
                if (applySearch && !string.IsNullOrEmpty(searchText))
                    sb.Append(" AND (p.EmpCode LIKE @Search OR p.EmpName LIKE @Search)");

                if (deptId.HasValue) sb.Append(" AND p.DeptID = @DeptID");
                if (unitId.HasValue) sb.Append(" AND p.UnitID = @UnitID");

                sb.Append(@" GROUP BY
               p.PmsID, p.EmpID, p.EmpCode, p.EmpName, p.DesigName,
               p.DeptName, p.SubDeptName, p.DeptID, p.UnitID,
               p.CycleType, p.CycleLabel, p.CycleFromDate, p.CycleToDate,
               p.AppraisalYear, p.Rating,
               p.MC_Score, p.MC_Remark, p.MC_SubmitDate,
               p.JMD_Score, p.JMD_Remark, p.JMD_SubmitDate, p.CreatedOn
               ORDER BY p.PmsID DESC");

                using var cmd = new SqlCommand(sb.ToString(), con) { CommandTimeout = 30 };
                if (year.HasValue && year.Value > 0)
                    cmd.Parameters.Add("@Year", SqlDbType.Int).Value = year.Value;
                if (applySearch && !string.IsNullOrEmpty(searchText))
                    cmd.Parameters.Add("@Search", SqlDbType.NVarChar).Value = "%" + searchText + "%";
                if (deptId.HasValue) cmd.Parameters.Add("@DeptID", SqlDbType.Int).Value = deptId.Value;
                if (unitId.HasValue) cmd.Parameters.Add("@UnitID", SqlDbType.Int).Value = unitId.Value;

                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    result.Add(new PMSBCMCJMDModel
                    {
                        PmsID = Convert.ToInt32(rdr["PmsID"]),
                        EmpID = Convert.ToInt32(rdr["EmpID"]),
                        EmpCode = rdr["EmpCode"]?.ToString() ?? "",
                        EmpName = rdr["EmpName"]?.ToString() ?? "",
                        Designation = rdr["DesigName"]?.ToString() ?? "",
                        Department = rdr["DeptName"]?.ToString() ?? "",
                        SubDepartment = rdr["SubDeptName"]?.ToString() ?? "",
                        CycleType = rdr["CycleType"]?.ToString() ?? "",
                        CycleLabel = rdr["CycleLabel"]?.ToString() ?? "",
                        FromDate = rdr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CycleFromDate"]),
                        ToDate = rdr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CycleToDate"]),
                        Year = rdr["AppraisalYear"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["AppraisalYear"]),
                        FinalRating = rdr["FinalRating"]?.ToString() ?? "",
                        RM_TotalScore = Convert.ToDecimal(rdr["RM_TotalScore"]),
                        HOD_TotalScore = Convert.ToDecimal(rdr["HOD_TotalScore"]),
                        TotalMaxScore = Convert.ToInt32(rdr["TotalMaxScore"]),
                        MC_Score = Convert.ToDecimal(rdr["MC_Score"]),
                        MC_Remark = rdr["MC_Remark"]?.ToString() ?? "",
                        MC_SubmitDate = rdr["MC_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["MC_SubmitDate"]),
                        JMD_Score = Convert.ToDecimal(rdr["JMD_Score"]),
                        JMD_Remark = rdr["JMD_Remark"]?.ToString() ?? "",
                        JMD_SubmitDate = rdr["JMD_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["JMD_SubmitDate"])
                    });
                }
                return result;
            }

            // ── Stat counts: year-filtered, NOT search-filtered ──
            // ── Stat counts: year-filtered, NOT search-filtered ──
            //var allData = await QueryData(applySearch: false);
            //ViewBag.PendingCount = allData.Count(m => !(m.MC_SubmitDate != null && m.JMD_SubmitDate != null));
            //ViewBag.CompletedCount = allData.Count(m => m.MC_SubmitDate != null && m.JMD_SubmitDate != null);

            // TotalCount: always ALL-TIME so the card is a reliable grand total.
            // Run a lightweight scalar query instead of re-using the year-filtered allData.
            var allData = await QueryData(applySearch: false);
            ViewBag.PendingCount =
                allData.Count(m => !(m.MC_SubmitDate != null &&
                                     m.JMD_SubmitDate != null));
            ViewBag.CompletedCount =
                allData.Count(m => m.MC_SubmitDate != null &&
                                   m.JMD_SubmitDate != null);

            ViewBag.TotalCount = allData.Count;

            // ── Table data: year + search filtered ──
            var list = string.IsNullOrEmpty(searchText)
                ? allData
                : await QueryData(applySearch: true);

            ViewBag.YearList = yearList;
            ViewBag.SelectedYear = year;
            ViewBag.SearchText = searchText;
            ViewBag.TabFilter = string.IsNullOrEmpty(tab) ? "all" : tab;
            ViewBag.DepartmentList = await LoadDropdownAsync(orgCon,
                "SELECT Dept_ID AS Val, Dept_Name AS Txt FROM T0040_DEPARTMENT_MASTER ORDER BY Dept_Name");
            ViewBag.UnitList = await LoadDropdownAsync(orgCon,
                "SELECT Cat_ID AS Val, Cat_Name AS Txt FROM T0030_CATEGORY_MASTER ORDER BY Cat_Name");
            ViewBag.SelectedDeptID = deptId;
            ViewBag.SelectedUnitID = unitId;
            ViewBag.StatusFilter = statusFilter;

            return View(list);
        }
        // ══════════════════════════════════════════════════════════════
        // SAVE MC / JMD SCORES
        // ══════════════════════════════════════════════════════════════
        [HttpPost("save-mc-jmd")]
        public async Task<IActionResult> SaveMCJMD([FromBody] List<PMSBCMCJMDSaveItem> payload)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Json(new { success = false, message = "Session expired." });

            if (payload == null || !payload.Any())
                return Json(new { success = false, message = "No data." });

            try
            {
                string dbCon = _configuration.GetConnectionString("DbConStr");
                using var con = new SqlConnection(dbCon);
                await con.OpenAsync();

                foreach (var item in payload)
                {
                    using var cmd = new SqlCommand(@"
                    UPDATE PMS_BC_Main SET
                        MC_Score        = CASE WHEN @MC  > 0 THEN @MC  ELSE MC_Score  END,
                        MC_Remark       = CASE WHEN @MC  > 0 THEN @MCR ELSE MC_Remark END,
                        MC_SubmitDate   = CASE WHEN @MC  > 0 THEN ISNULL(MC_SubmitDate,  GETDATE()) ELSE MC_SubmitDate  END,
                        MC_SubmittedBy  = CASE WHEN @MC  > 0 THEN ISNULL(MC_SubmittedBy, @User)     ELSE MC_SubmittedBy END,
                        JMD_Score       = CASE WHEN @JMD > 0 THEN @JMD  ELSE JMD_Score  END,
                        JMD_Remark      = CASE WHEN @JMD > 0 THEN @JMDR ELSE JMD_Remark END,
                        JMD_SubmitDate  = CASE WHEN @JMD > 0 THEN ISNULL(JMD_SubmitDate,  GETDATE()) ELSE JMD_SubmitDate  END,
                        JMD_SubmittedBy = CASE WHEN @JMD > 0 THEN ISNULL(JMD_SubmittedBy, @User)     ELSE JMD_SubmittedBy END
                    WHERE PmsID = @PID", con);
                    cmd.Parameters.Add("@MC", SqlDbType.Decimal).Value = item.MC_Score;
                    cmd.Parameters.Add("@MCR", SqlDbType.NVarChar).Value = item.MC_Remark ?? "";
                    cmd.Parameters.Add("@JMD", SqlDbType.Decimal).Value = item.JMD_Score;
                    cmd.Parameters.Add("@JMDR", SqlDbType.NVarChar).Value = item.JMD_Remark ?? "";
                    cmd.Parameters.Add("@PID", SqlDbType.Int).Value = item.PmsID;
                    cmd.Parameters.Add("@User", SqlDbType.NVarChar).Value = user.Id;
                    await cmd.ExecuteNonQueryAsync();
                }
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        // ══════════════════════════════════════════════════════════════
        // RM FORM GET
        // ══════════════════════════════════════════════════════════════
        [HttpGet("rm-form")]
        public IActionResult RMForm(int id) => View(LoadFormModel(id));

        // ══════════════════════════════════════════════════════════════
        // HOD FORM GET
        // ══════════════════════════════════════════════════════════════
        [HttpGet("hod-form")]
        public async Task<IActionResult> HODForm(int id)
        {
            var (allowed, reason) = await IsStageAllowed(id, "HOD");
            if (!allowed) return Content($"Access Denied: {reason}");
            return View(LoadFormModel(id));
        }

        // ══════════════════════════════════════════════════════════════
        // SUBMIT SCORE (RM or HOD)
        // ══════════════════════════════════════════════════════════════
        [HttpPost("submit-score")]
        public async Task<IActionResult> SubmitScore([FromBody] PMSBCScoreSubmitModel payload)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Json(new { success = false, redirectUrl = "/Identity/Account/Login", sessionExpired = true });

            if (payload.Stage != "RM")
            {
                var (ok, reason) = await IsStageAllowed(payload.PmsID, payload.Stage);
                if (!ok) return Json(new { success = false, message = reason });
            }

            try
            {
                string dbCon = _configuration.GetConnectionString("DbConStr");
                string orgCon = _configuration.GetConnectionString("OrgConStr");
                string baseUrl = $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}";

                string nextPersonId = "", nextPersonName = "", empName = "", auditCreatedBy = "";
                bool isCompleted = false;

                using var sqlCon = new SqlConnection(dbCon);
                await sqlCon.OpenAsync();

                foreach (var row in payload.Scores)
                {
                    if (payload.Stage == "RM")
                    {
                        using var cmd = new SqlCommand(@"
                            MERGE PMS_BC_Details AS target
                            USING (VALUES(@PID,@CID)) AS source(PmsID,CriteriaID)
                            ON target.PmsID=source.PmsID AND target.CriteriaID=source.CriteriaID
                            WHEN MATCHED     THEN UPDATE SET RM_Score=@S, RM_Remark=@R
                            WHEN NOT MATCHED THEN INSERT(PmsID,CriteriaID,RM_Score,RM_Remark)
                                                  VALUES(@PID,@CID,@S,@R);", sqlCon);
                        cmd.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                        cmd.Parameters.Add("@CID", SqlDbType.Int).Value = row.CriteriaID;
                        cmd.Parameters.Add("@S", SqlDbType.Int).Value = row.Score;
                        cmd.Parameters.Add("@R", SqlDbType.NVarChar).Value = row.Remark ?? "";
                        await cmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        using var cmd = new SqlCommand(
                            "UPDATE PMS_BC_Details SET HOD_Score=@S, HOD_Remark=@R WHERE PmsID=@PID AND CriteriaID=@CID", sqlCon);
                        cmd.Parameters.Add("@S", SqlDbType.Int).Value = row.Score;
                        cmd.Parameters.Add("@R", SqlDbType.NVarChar).Value = row.Remark ?? "";
                        cmd.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                        cmd.Parameters.Add("@CID", SqlDbType.Int).Value = row.CriteriaID;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                bool hodRequired = false; string hodId = "", hodName = "";
                using (var info = new SqlCommand(@"
                    SELECT EmpName,CreatedBy,
                           ISNULL(CAST(HOD_ID AS NVARCHAR(50)),'') AS HOD_ID,
                           ISNULL(HOD_Name,'') AS HOD_Name,
                           ISNULL(CAST(HOD_Required AS INT),0) AS HOD_Required
                    FROM PMS_BC_Main WHERE PmsID=@PID", sqlCon))
                {
                    info.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                    using var r = await info.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        empName = r["EmpName"].ToString();
                        auditCreatedBy = r["CreatedBy"].ToString();
                        hodId = r["HOD_ID"].ToString();
                        hodName = r["HOD_Name"].ToString();
                        hodRequired = Convert.ToInt32(r["HOD_Required"]) == 1;
                    }
                }

                string divisor = hodRequired ? "1+(CASE WHEN HOD_Score IS NOT NULL THEN 1 ELSE 0 END)" : "1";
                string numer = "CAST(ISNULL(RM_Score,0) AS DECIMAL(10,2))" + (hodRequired ? "+CAST(ISNULL(HOD_Score,0) AS DECIMAL(10,2))" : "");
                using (var avg = new SqlCommand($@"
                    UPDATE PMS_BC_Details
                    SET AvgScore = CASE WHEN ({divisor})>0
                        THEN ({numer})/CAST(({divisor}) AS DECIMAL(10,2)) ELSE NULL END
                    WHERE PmsID=@PID", sqlCon))
                {
                    avg.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                    await avg.ExecuteNonQueryAsync();
                }

                string nextStatus = "", dateCol = "";
                if (payload.Stage == "RM")
                {
                    if (hodRequired)
                    {
                        nextStatus = "Pending at HOD";
                        dateCol = "RM_SubmitDate=GETDATE(),CurrentStage='HOD'";
                        nextPersonId = hodId; nextPersonName = hodName;
                    }
                    else
                    {
                        nextStatus = "Completed";
                        dateCol = "RM_SubmitDate=GETDATE(),CurrentStage='Done',FinalCompletedDate=GETDATE(),FinalStatus='Completed'";
                        isCompleted = true;
                    }
                }
                else
                {
                    nextStatus = "Completed";
                    dateCol = "HOD_SubmitDate=GETDATE(),CurrentStage='Done',FinalCompletedDate=GETDATE(),FinalStatus='Completed'";
                    isCompleted = true;
                }

                if (!string.IsNullOrEmpty(dateCol))
                {
                    using var st = new SqlCommand($"UPDATE PMS_BC_Main SET PmsStatus=@Status,{dateCol} WHERE PmsID=@PID", sqlCon);
                    st.Parameters.Add("@Status", SqlDbType.NVarChar).Value = nextStatus;
                    st.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                    await st.ExecuteNonQueryAsync();
                }

                if (isCompleted)
                {
                    try
                    {
                        decimal totalAvg = 0; int totalMax = 0;
                        using (var rd = new SqlCommand(@"
                            SELECT SUM(d.AvgScore) AS TotalAvg, SUM(al.MaxScore) AS TotalMax
                            FROM PMS_BC_Details d
                            INNER JOIN Behaviour_AuditList al ON al.Id=d.CriteriaID AND al.Category='PMS'
                            WHERE d.PmsID=@PID", sqlCon))
                        {
                            rd.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                            using var rr = await rd.ExecuteReaderAsync();
                            if (await rr.ReadAsync())
                            {
                                totalAvg = rr["TotalAvg"] == DBNull.Value ? 0m : Convert.ToDecimal(rr["TotalAvg"]);
                                totalMax = rr["TotalMax"] == DBNull.Value ? 0 : Convert.ToInt32(rr["TotalMax"]);
                            }
                        }
                        string rating = CalculateRating(totalAvg, totalMax);
                        using var rv = new SqlCommand("UPDATE PMS_BC_Main SET Rating=@R WHERE PmsID=@PID", sqlCon);
                        rv.Parameters.Add("@R", SqlDbType.NVarChar).Value = rating;
                        rv.Parameters.Add("@PID", SqlDbType.Int).Value = payload.PmsID;
                        await rv.ExecuteNonQueryAsync();
                    }
                    catch { }
                }

                if (!isCompleted && !string.IsNullOrWhiteSpace(nextPersonId))
                {
                    try
                    {
                        string code = GetEmpCode(nextPersonId, orgCon);
                        string nextUserId = !string.IsNullOrWhiteSpace(code)
                            ? _context.UserDetails.Where(a => a.EMPCode == code).Select(a => a.UserId).FirstOrDefault() ?? ""
                            : "";
                        CreateNotification(nextUserId, user.Id, user.Id,
                            $"PMS Blue Collar for {empName} is pending your HOD review",
                            "HR_PMS_BC_Notification", _module,
                            $"/pmsbluecollar/hod-dashboard?hodId={nextPersonId}");

                        string nextEmail = GetWorkEmail(nextPersonId, orgCon);
                        if (!string.IsNullOrWhiteSpace(nextEmail))
                        {
                            string link = $"{baseUrl}/pmsbluecollar/hod-dashboard?hodId={nextPersonId}";
                            await SendEmailAsync(nextEmail,
                                $"[Action Required] PMS Blue Collar — HOD Score for {empName}",
                                BuildStageEmail(nextPersonName, "Reporting Manager", "HOD", empName, link));
                        }
                    }
                    catch { }
                }

                if (isCompleted)
                {
                    try
                    {
                        string hrEmail = GetWorkEmail(GetEmpIdByUser(user.UserName, orgCon), orgCon);
                        if (!string.IsNullOrWhiteSpace(hrEmail))
                            await SendEmailAsync(hrEmail, $"[PMS Completed] Blue Collar — {empName}", BuildCompletionEmail(empName, baseUrl));
                    }
                    catch { }
                }

                string redirect = payload.Stage switch
                {
                    "RM" => $"/pmsbluecollar/rm-dashboard?rmId={payload.SubmittedByID}",
                    "HOD" => $"/pmsbluecollar/hod-dashboard?hodId={payload.SubmittedByID}",
                    _ => "/pmsbluecollar/tracking"
                };

                return Json(new { success = true, redirectUrl = redirect });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // PRINT / REPORT VIEW
        // ══════════════════════════════════════════════════════════════
        [HttpGet("print")]
        public IActionResult Print(int id)
        {
            string dbCon = _configuration.GetConnectionString("DbConStr");

            var model = new PMSBCPrintModel();

            model.PmsID = id;

            // =========================================================
            // HEADER DETAILS
            // =========================================================

            using (var con = new SqlConnection(dbCon))
            {
                con.Open();

                using var cmd = new SqlCommand(@"
                 SELECT
                    p.*,
                    ISNULL(p.Rating,'') AS Rating,
                    p.MC_Score,
                    p.JMD_Score
                FROM PMS_BC_Main p
                WHERE p.PmsID = @PID", con);

                cmd.Parameters.Add("@PID", SqlDbType.Int).Value = id;

                using var rdr = cmd.ExecuteReader();

                if (rdr.Read())
                {
                    model.EmpName = rdr["EmpName"]?.ToString() ?? "";

                    model.EmpCode = rdr["EmpCode"]?.ToString() ?? "";

                    model.Designation = rdr["DesigName"]?.ToString() ?? "";

                    model.Department = rdr["DeptName"]?.ToString() ?? "";

                    model.Unit = rdr["UnitID"]?.ToString() ?? "";

                    model.RMName = rdr["RM_Name"]?.ToString() ?? "";

                    model.HODName = rdr["HOD_Name"]?.ToString() ?? "";

                    model.AppraisalDate = rdr["CreatedOn"] == DBNull.Value
                        ? null
                        : Convert.ToDateTime(rdr["CreatedOn"]);

                    model.FinalRating = rdr["Rating"]?.ToString() ?? "";

                    model.CycleType = rdr["CycleType"]?.ToString() ?? "";
                    model.MC_Score = rdr["MC_Score"] == DBNull.Value
     ? null
     : Convert.ToInt32(Convert.ToDecimal(rdr["MC_Score"]));

                    model.JMD_Score = rdr["JMD_Score"] == DBNull.Value
                        ? null
                        : Convert.ToInt32(Convert.ToDecimal(rdr["JMD_Score"]));

                    if (rdr["RM_SubmitDate"] != DBNull.Value)
                        model.RM_Rating = model.FinalRating;

                    if (rdr["HOD_SubmitDate"] != DBNull.Value)
                        model.HOD_Rating = model.FinalRating;


                }
            }

            // =========================================================
            // PRINT DETAILS
            // =========================================================

            using (var con = new SqlConnection(dbCon))
            {
                con.Open();

                using var cmd = new SqlCommand(@"
            SELECT
                al.Id AS CriteriaID,
                al.Category,
                al.MainSrNo,
                al.SubSrNo,
                al.Beh_Pers,
                al.Description,
                al.MaxScore,

                d.DetailID,
                d.RM_Score,
                d.RM_Remark,
                d.HOD_Score,
                d.HOD_Remark,
                d.AvgScore

            FROM Behaviour_AuditList al

            LEFT JOIN PMS_BC_Details d
                ON d.CriteriaID = al.Id
                AND d.PmsID = @PID

            WHERE al.Category = 'PMS'
              AND al.IsDeleted = 0

            ORDER BY
                al.MainSrNo,
                al.SubSrNo", con);

                cmd.Parameters.Add("@PID", SqlDbType.Int).Value = id;

                using var rdr = cmd.ExecuteReader();

                var groups = new Dictionary<string, PMSBCPrintCategory>();

                while (rdr.Read())
                {
                    string cat = rdr["Beh_Pers"]?.ToString() ?? "";

                    if (!groups.ContainsKey(cat))
                    {
                        groups[cat] = new PMSBCPrintCategory
                        {
                            CategoryName = cat
                        };
                    }

                    int? rmScore = rdr["RM_Score"] == DBNull.Value
                        ? null
                        : Convert.ToInt32(rdr["RM_Score"]);

                    int? hodScore = rdr["HOD_Score"] == DBNull.Value
                        ? null
                        : Convert.ToInt32(rdr["HOD_Score"]);

                    groups[cat].Rows.Add(new PMSBCPrintRow
                    {
                        MainSrNo = rdr["MainSrNo"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["MainSrNo"]),

                        SubSrNo = rdr["SubSrNo"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["SubSrNo"]),

                        Description = rdr["Description"]?.ToString() ?? "",

                        MaxScore = rdr["MaxScore"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["MaxScore"]),

                        RM_Score = rmScore,

                        RM_Remark = rdr["RM_Remark"] == DBNull.Value
                            ? ""
                            : rdr["RM_Remark"].ToString(),

                        HOD_Score = hodScore,

                        HOD_Remark = rdr["HOD_Remark"] == DBNull.Value
                            ? ""
                            : rdr["HOD_Remark"].ToString()
                    });

                    model.RM_TotalScore += rmScore ?? 0;

                    model.HOD_TotalScore += hodScore ?? 0;

                    model.TotalMaxScore += rdr["MaxScore"] == DBNull.Value
                        ? 0
                        : Convert.ToInt32(rdr["MaxScore"]);
                }

                model.Categories = groups.Values.ToList();
            }
            // =========================================================
            // FINAL RATINGS
            // =========================================================

            string rmRatingNo = model.RM_TotalScore > 0
                ? CalculateRating(model.RM_TotalScore, model.TotalMaxScore)
                : "";

            string hodRatingNo = model.HOD_TotalScore > 0
                ? CalculateRating(model.HOD_TotalScore, model.TotalMaxScore)
                : "";

            model.RM_Rating = !string.IsNullOrEmpty(rmRatingNo)
                ? $"{rmRatingNo} - {RatingLabel(rmRatingNo)}"
                : "";

            model.HOD_Rating = !string.IsNullOrEmpty(hodRatingNo)
                ? $"{hodRatingNo} - {RatingLabel(hodRatingNo)}"
                : "";

            // Default blank
            model.ModCommitteeRating = "";
            model.JMDRating = "";

            // MC/JMD only for Annual cycle
            if (string.Equals(model.CycleType, "Annual",
     StringComparison.OrdinalIgnoreCase))
            {
                // MC Rating
                model.ModCommitteeRating = "";

                if (model.MC_Score.HasValue)
                {
                    int mcScore = Convert.ToInt32(model.MC_Score);

                    if (mcScore == 0)
                    {
                        model.ModCommitteeRating = "0";
                    }
                    else
                    {
                        model.ModCommitteeRating =
                            $"{mcScore} - {RatingLabel(mcScore.ToString())}";
                    }
                }

                // JMD Rating
                model.JMDRating = "";

                if (model.JMD_Score.HasValue)
                {
                    int jmdScore = Convert.ToInt32(model.JMD_Score);

                    if (jmdScore == 0)
                    {
                        model.JMDRating = "0";
                    }
                    else
                    {
                        model.JMDRating =
                            $"{jmdScore} - {RatingLabel(jmdScore.ToString())}";
                    }
                }
            }

            // ============================================
            // ANNUAL CYCLE → JMD > MC > HOD > RM
            // OTHER CYCLES → AVG(RM,HOD)
            // ============================================

            // ============================================
            // FINAL RATING LOGIC
            // Annual:  JMD → MC → HOD → RM (priority order)
            // Others:  if both RM+HOD → avg; if only RM → RM; if only HOD → HOD
            // ============================================
            if (string.Equals(model.CycleType, "Annual", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(model.JMDRating) && model.JMDRating != "0")
                {
                    model.FinalRating = model.JMDRating;
                }
                else if (!string.IsNullOrWhiteSpace(model.ModCommitteeRating) && model.ModCommitteeRating != "0")
                {
                    model.FinalRating = model.ModCommitteeRating;
                }
                else if (model.HOD_TotalScore > 0 && model.RM_TotalScore > 0)
                {
                    decimal annualBothAvg = (model.RM_TotalScore + model.HOD_TotalScore) / 2m;
                    string annualBothRating = CalculateRating(annualBothAvg, model.TotalMaxScore);
                    model.FinalRating = $"{annualBothRating} - {RatingLabel(annualBothRating)}";
                }
                else if (model.HOD_TotalScore > 0)
                {
                    string annualHodRating = CalculateRating(model.HOD_TotalScore, model.TotalMaxScore);
                    model.FinalRating = $"{annualHodRating} - {RatingLabel(annualHodRating)}";
                }
                else if (model.RM_TotalScore > 0)
                {
                    string annualRmRating = CalculateRating(model.RM_TotalScore, model.TotalMaxScore);
                    model.FinalRating = $"{annualRmRating} - {RatingLabel(annualRmRating)}";
                }
                else
                {
                    model.FinalRating = "";
                }
            }
            else
            {
                if (model.RM_TotalScore > 0 && model.HOD_TotalScore > 0)
                {
                    decimal otherBothAvg = (model.RM_TotalScore + model.HOD_TotalScore) / 2m;
                    string otherBothRating = CalculateRating(otherBothAvg, model.TotalMaxScore);
                    model.FinalRating = $"{otherBothRating} - {RatingLabel(otherBothRating)}";
                }
                else if (model.RM_TotalScore > 0)
                {
                    string otherRmRating = CalculateRating(model.RM_TotalScore, model.TotalMaxScore);
                    model.FinalRating = $"{otherRmRating} - {RatingLabel(otherRmRating)}";
                }
                else if (model.HOD_TotalScore > 0)
                {
                    string otherHodRating = CalculateRating(model.HOD_TotalScore, model.TotalMaxScore);
                    model.FinalRating = $"{otherHodRating} - {RatingLabel(otherHodRating)}";
                }
                else
                {
                    model.FinalRating = "";
                }
            }

            ViewBag.PMS_ID = id;

            return View(model);
        }
        // ══════════════════════════════════════════════════════════════
        // GET CRITERIA JSON
        // ══════════════════════════════════════════════════════════════
        [HttpGet("get-criteria")]
        public async Task<IActionResult> GetCriteriaJson(int pmsId)
        {
            string dbCon = _configuration.GetConnectionString("DbConStr");
            var result = new List<object>();
            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT al.Id AS CriteriaID, al.Category, al.MainSrNo, al.SubSrNo, al.Beh_Pers,
                       al.MaxScore, d.RM_Score, d.RM_Remark, d.HOD_Score, d.HOD_Remark
                FROM Behaviour_AuditList al
                LEFT JOIN PMS_BC_Details d ON d.CriteriaID=al.Id AND d.PmsID=@PID
                WHERE al.Category='PMS' AND al.IsDeleted=0
                ORDER BY al.Category, al.MainSrNo, al.SubSrNo", con);
            cmd.Parameters.Add("@PID", SqlDbType.Int).Value = pmsId;
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                result.Add(new
                {
                    criteriaID = Convert.ToInt32(rdr["CriteriaID"]),
                    category = rdr["Category"].ToString(),
                    mainSrNo = rdr["MainSrNo"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["MainSrNo"]),
                    subSrNo = rdr["SubSrNo"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["SubSrNo"]),
                    beh_Pers = rdr["Beh_Pers"].ToString(),
                    maxScore = Convert.ToInt32(rdr["MaxScore"]),
                    rm_Score = rdr["RM_Score"] == DBNull.Value ? (int?)null : Convert.ToInt32(rdr["RM_Score"]),
                    rm_Remark = rdr["RM_Remark"] == DBNull.Value ? "" : rdr["RM_Remark"].ToString(),
                    hod_Score = rdr["HOD_Score"] == DBNull.Value ? (int?)null : Convert.ToInt32(rdr["HOD_Score"]),
                    hod_Remark = rdr["HOD_Remark"] == DBNull.Value ? "" : rdr["HOD_Remark"].ToString()
                });
            }
            return Json(result);
        }

        // ══════════════════════════════════════════════════════════════
        // LOAD FORM MODEL
        // ══════════════════════════════════════════════════════════════
        private PMSBCFormModel LoadFormModel(int pmsId)
        {
            var model = new PMSBCFormModel();

            string dbCon = _configuration.GetConnectionString("DbConStr");

            // =========================================================
            // EMPLOYEE DETAILS
            // =========================================================

            using (var con = new SqlConnection(dbCon))
            {
                con.Open();

                using var cmd = new SqlCommand(@"
            SELECT
                p.EmpID,
                p.EmpCode,
                p.EmpName,
                p.DesigName,
                p.DeptName,
                p.RM_ID,
                p.RM_Name,
                p.HOD_ID,
                p.HOD_Name
            FROM PMS_BC_Main p
            WHERE p.PmsID = @PID", con);

                cmd.Parameters.Add("@PID", SqlDbType.Int).Value = pmsId;

                using var rdr = cmd.ExecuteReader();

                if (rdr.Read())
                {
                    model.Emp_ID = Convert.ToInt32(rdr["EmpID"]);
                    model.Emp_code = rdr["EmpCode"]?.ToString() ?? "";
                    model.Emp_Full_Name = rdr["EmpName"]?.ToString() ?? "";
                    model.Desig_Name = rdr["DesigName"]?.ToString() ?? "";
                    model.Dept_Name = rdr["DeptName"]?.ToString() ?? "";
                    model.Manager_Name = rdr["RM_Name"]?.ToString() ?? "";
                    model.HOD_Name = rdr["HOD_Name"]?.ToString() ?? "";

                    // ── Safe parse — HOD_ID / RM_ID are stored as NVARCHAR, may be "" ──
                    model.RMId = int.TryParse(rdr["RM_ID"]?.ToString(), out int rmParsed) ? rmParsed : 0;
                    model.HODId = int.TryParse(rdr["HOD_ID"]?.ToString(), out int hodParsed) ? hodParsed : 0;
                }
            }

            // =========================================================
            // PMS DETAILS
            // =========================================================

            using (var con = new SqlConnection(dbCon))
            {
                con.Open();

                using var cmd = new SqlCommand(@"
            SELECT
                al.Id AS CriteriaID,
                al.Category,
                al.MainSrNo,
                al.SubSrNo,
                al.Beh_Pers,
                al.Description,
                al.MaxScore,

                d.DetailID,
                d.RM_Score,
                d.RM_Remark,
                d.HOD_Score,
                d.HOD_Remark,
                d.AvgScore

            FROM Behaviour_AuditList al

            LEFT JOIN PMS_BC_Details d
                ON d.CriteriaID = al.Id
                AND d.PmsID = @PID

            WHERE al.Category = 'PMS'
              AND al.IsDeleted = 0

            ORDER BY
                al.MainSrNo,
                al.SubSrNo", con);

                cmd.Parameters.Add("@PID", SqlDbType.Int).Value = pmsId;

                using var rdr = cmd.ExecuteReader();

                while (rdr.Read())
                {
                    model.AuditDetails.Add(new PMSBCDetailDisplayModel
                    {
                        CriteriaID = Convert.ToInt32(rdr["CriteriaID"]),

                        Category = rdr["Category"]?.ToString() ?? "",

                        MainSrNo = rdr["MainSrNo"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["MainSrNo"]),

                        SubSrNo = rdr["SubSrNo"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["SubSrNo"]),

                        Beh_Pers = rdr["Beh_Pers"]?.ToString() ?? "",

                        Description = rdr["Description"]?.ToString() ?? "",

                        MaxScore = rdr["MaxScore"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["MaxScore"]),

                        DetailID = rdr["DetailID"] == DBNull.Value
                            ? 0
                            : Convert.ToInt32(rdr["DetailID"]),

                        RM_Score = rdr["RM_Score"] == DBNull.Value
                            ? null
                            : (int?)Convert.ToInt32(rdr["RM_Score"]),

                        RM_Remark = rdr["RM_Remark"] == DBNull.Value
                            ? ""
                            : rdr["RM_Remark"].ToString(),

                        HOD_Score = rdr["HOD_Score"] == DBNull.Value
                            ? null
                            : (int?)Convert.ToInt32(rdr["HOD_Score"]),

                        HOD_Remark = rdr["HOD_Remark"] == DBNull.Value
                            ? ""
                            : rdr["HOD_Remark"].ToString(),

                        AvgScore = rdr["AvgScore"] == DBNull.Value
                            ? null
                            : (decimal?)Convert.ToDecimal(rdr["AvgScore"])
                    });
                }
            }

            ViewBag.PmsID = pmsId;

            return model;
        }

        // ══════════════════════════════════════════════════════════════
        // STAGE GATE
        // ══════════════════════════════════════════════════════════════
        private async Task<(bool allowed, string reason)> IsStageAllowed(int pmsId, string stage)
        {
            string dbCon = _configuration.GetConnectionString("DbConStr");
            using var con = new SqlConnection(dbCon);
            await con.OpenAsync();
            using var cmd = new SqlCommand("SELECT RM_SubmitDate, HOD_Required FROM PMS_BC_Main WHERE PmsID=@PID", con);
            cmd.Parameters.Add("@PID", SqlDbType.Int).Value = pmsId;
            using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return (false, "PMS record not found.");
            bool rmSubmitted = rdr["RM_SubmitDate"] != DBNull.Value;
            return stage switch
            {
                "HOD" => rmSubmitted ? (true, "") : (false, "RM has not submitted scores yet."),
                _ => (true, "")
            };
        }

        // ══════════════════════════════════════════════════════════════
        // EXPORT MC / JMD EXCEL  —  Pending only (MC or JMD missing)
        // ══════════════════════════════════════════════════════════════
        [HttpGet("export-mc-jmd-excel")]
        public async Task<IActionResult> ExportMCJMDExcel(int? year)
        {
            string dbCon = _configuration.GetConnectionString("DbConStr");
            var list = new List<PMSBCMCJMDModel>();

            using (var con = new SqlConnection(dbCon))
            {
                await con.OpenAsync();
                var sb = new System.Text.StringBuilder(@"
    SELECT p.PmsID, p.EmpID, p.EmpCode, p.EmpName,
           p.DesigName, p.DeptName, p.SubDeptName,
           ISNULL(p.CycleType,'')                     AS CycleType,
           ISNULL(p.CycleLabel,'')                    AS CycleLabel,
           p.CycleFromDate, p.CycleToDate,
           ISNULL(p.AppraisalYear, YEAR(p.CreatedOn)) AS AppraisalYear,
           ISNULL(p.MC_Score,0)                       AS MC_Score,
           ISNULL(p.MC_Remark,'')                     AS MC_Remark,
           p.MC_SubmitDate,
           ISNULL(p.JMD_Score,0)                      AS JMD_Score,
           ISNULL(p.JMD_Remark,'')                    AS JMD_Remark,
           p.JMD_SubmitDate,
           ISNULL(SUM(CASE WHEN d.RM_Score  IS NOT NULL THEN d.RM_Score  ELSE 0 END),0) AS RM_TotalScore,
           ISNULL(SUM(CASE WHEN d.HOD_Score IS NOT NULL THEN d.HOD_Score ELSE 0 END),0) AS HOD_TotalScore,
           ISNULL((SELECT SUM(al2.MaxScore) FROM Behaviour_AuditList al2
                    WHERE al2.Category='PMS' AND al2.IsDeleted=0),0) AS TotalMaxScore
    FROM PMS_BC_Main p
    LEFT JOIN PMS_BC_Details d ON p.PmsID = d.PmsID
    WHERE p.PmsStatus = 'Completed'
      AND UPPER(ISNULL(p.CycleType,'')) = 'ANNUAL'
      AND p.RM_SubmitDate IS NOT NULL
      AND (p.MC_SubmitDate IS NULL OR p.JMD_SubmitDate IS NULL)");

                if (year.HasValue && year.Value > 0)
                    sb.Append(" AND ISNULL(p.AppraisalYear, YEAR(p.CreatedOn)) = @Year");

                sb.Append(@" GROUP BY p.PmsID,p.EmpID,p.EmpCode,p.EmpName,p.DesigName,
               p.DeptName,p.SubDeptName,p.CycleType,p.CycleLabel,
               p.CycleFromDate,p.CycleToDate,p.AppraisalYear,
               p.MC_Score,p.MC_Remark,p.MC_SubmitDate,
               p.JMD_Score,p.JMD_Remark,p.JMD_SubmitDate,p.CreatedOn
           ORDER BY p.EmpName ASC");

                using var cmd = new SqlCommand(sb.ToString(), con) { CommandTimeout = 30 };
                if (year.HasValue && year.Value > 0)
                    cmd.Parameters.Add("@Year", SqlDbType.Int).Value = year.Value;

                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    list.Add(new PMSBCMCJMDModel
                    {
                        PmsID = Convert.ToInt32(rdr["PmsID"]),
                        EmpCode = rdr["EmpCode"]?.ToString() ?? "",
                        EmpName = rdr["EmpName"]?.ToString() ?? "",
                        Designation = rdr["DesigName"]?.ToString() ?? "",
                        Department = rdr["DeptName"]?.ToString() ?? "",
                        SubDepartment = rdr["SubDeptName"]?.ToString() ?? "",
                        CycleType = rdr["CycleType"]?.ToString() ?? "",
                        FromDate = rdr["CycleFromDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CycleFromDate"]),
                        ToDate = rdr["CycleToDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["CycleToDate"]),
                        Year = rdr["AppraisalYear"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["AppraisalYear"]),
                        RM_TotalScore = Convert.ToDecimal(rdr["RM_TotalScore"]),
                        HOD_TotalScore = Convert.ToDecimal(rdr["HOD_TotalScore"]),
                        TotalMaxScore = Convert.ToInt32(rdr["TotalMaxScore"]),
                        MC_Score = Convert.ToDecimal(rdr["MC_Score"]),
                        MC_Remark = rdr["MC_Remark"]?.ToString() ?? "",
                        MC_SubmitDate = rdr["MC_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["MC_SubmitDate"]),
                        JMD_Score = Convert.ToDecimal(rdr["JMD_Score"]),
                        JMD_Remark = rdr["JMD_Remark"]?.ToString() ?? "",
                        JMD_SubmitDate = rdr["JMD_SubmitDate"] == DBNull.Value ? null : Convert.ToDateTime(rdr["JMD_SubmitDate"])
                    });
                }
            }

            string CalcRatingLocal(decimal score, int max)
            {
                if (max <= 0 || score <= 0) return "";
                decimal pct = score / max * 100m;
                if (pct >= 85) return "5"; if (pct >= 71) return "4";
                if (pct >= 51) return "3"; if (pct >= 31) return "2";
                return "1";
            }

            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("MC JMD Pending");

            // ── Colour palette ──
            var clrNavy = ClosedXML.Excel.XLColor.FromHtml("0F1C3F");
            var clrNavyLt = ClosedXML.Excel.XLColor.FromHtml("1A2D5A");
            var clrRM = ClosedXML.Excel.XLColor.FromHtml("0D4F38");
            var clrHOD = ClosedXML.Excel.XLColor.FromHtml("3B2278");
            var clrMC = ClosedXML.Excel.XLColor.FromHtml("7C3400");
            var clrJMD = ClosedXML.Excel.XLColor.FromHtml("0E3E5C");
            var clrWhite = ClosedXML.Excel.XLColor.White;
            var clrRowAlt = ClosedXML.Excel.XLColor.FromHtml("F0F4FF");
            var clrRowMain = ClosedXML.Excel.XLColor.White;
            var clrMCEntry = ClosedXML.Excel.XLColor.FromHtml("EAF4EA");
            var clrJMDEntry = ClosedXML.Excel.XLColor.FromHtml("EAF0FB");
            var clrBorder = ClosedXML.Excel.XLColor.FromHtml("B0BEC5");
            var clrGold = ClosedXML.Excel.XLColor.FromHtml("C9A84C");

            void ApplyBorder(ClosedXML.Excel.IXLRange range)
            {
                range.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                range.Style.Border.OutsideBorderColor = clrBorder;
                range.Style.Border.InsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                range.Style.Border.InsideBorderColor = clrBorder;
            }

            int COLS = 12; // added Status column

            // ══════════════════════════════════════════
            // ROW 1 — Logo (col 1) + Company title (cols 2–COLS)
            // ══════════════════════════════════════════
            ws.Row(1).Height = 42;

            // Col 1 — logo background only (no merge, so text in col 2 is independent)
            ws.Cell(1, 1).Style.Fill.BackgroundColor = clrNavy;

            // Try to add logo image
            string logoPath = System.IO.Path.Combine(
                _hostingEnvironment.WebRootPath, "admin", "assets", "images", "logo.png");
            if (System.IO.File.Exists(logoPath))
            {
                var img = ws.AddPicture(logoPath);
                img.MoveTo(ws.Cell(1, 1), new System.Drawing.Point(4, 4));
                img.Height = 34;
                img.Width = 34;
            }

            // Cols 2–COLS — title text (merged separately from col 1)
            ws.Range(1, 2, 1, COLS).Merge();
            ws.Cell(1, 2).Value = "    Vandana Global Limited  |  PMS – Blue Collar  |  MC / JMD Annual Review  |  Pending Scores";
            ws.Cell(1, 2).Style.Font.Bold = true;
            ws.Cell(1, 2).Style.Font.FontSize = 14;
            ws.Cell(1, 2).Style.Font.FontColor = clrGold;
            ws.Cell(1, 2).Style.Fill.BackgroundColor = clrNavy;
            ws.Cell(1, 2).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
            ws.Cell(1, 2).Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;

            // ══════════════════════════════════════════
            // ROW 2 — Generated date + filter info
            // ══════════════════════════════════════════
            ws.Row(2).Height = 18;
            ws.Range(2, 1, 2, COLS).Merge();
            ws.Cell(2, 1).Value =
                $"Generated: {DateTime.Now:dd MMM yyyy, hh:mm tt}   |   " +
                $"Year Filter: {(year.HasValue ? year.Value.ToString() : "All Years")}   |   " +
                $"Pending Records: {list.Count}";
            ws.Cell(2, 1).Style.Font.FontSize = 9;
            ws.Cell(2, 1).Style.Font.Italic = true;
            ws.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("A0AEC0");
            ws.Cell(2, 1).Style.Fill.BackgroundColor = clrNavyLt;
            ws.Cell(2, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
            ws.Cell(2, 1).Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;

            // ══════════════════════════════════════════
            // ROW 3 — Group headers
            // ══════════════════════════════════════════
            ws.Row(3).Height = 20;

            void GroupHeader(int c1, int c2, string text, ClosedXML.Excel.XLColor bg)
            {
                var r = ws.Range(3, c1, 3, c2);
                if (c1 != c2) r.Merge();
                r.Style.Fill.BackgroundColor = bg;
                r.Style.Font.FontColor = clrWhite;
                r.Style.Font.Bold = true;
                r.Style.Font.FontSize = 9;
                r.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                r.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                ws.Cell(3, c1).Value = text;
                ApplyBorder(r);
            }

            GroupHeader(1, 6, "", clrNavy);
            GroupHeader(7, 7, "RM", clrRM);
            GroupHeader(8, 8, "HOD", clrHOD);
            GroupHeader(9, 10, "MC SCORE", clrMC);
            GroupHeader(11, 12, "JMD SCORE", clrJMD);


            // ══════════════════════════════════════════
            // ROW 4 — Column headers
            // ══════════════════════════════════════════
            ws.Row(4).Height = 28;
            var colHeaders = new (string Text, ClosedXML.Excel.XLColor Bg)[]
            {
        ("#",           clrNavy),
        ("Employee",    clrNavy),
        ("Emp Code",    clrNavy),
        ("Department",  clrNavy),
        ("Designation", clrNavy),
        ("Period",      clrNavy),
        ("RM Rating",   clrRM),
        ("HOD Rating",  clrHOD),
        ("MC Score",    clrMC),
        ("MC Remark",   clrMC),
        ("JMD Score",   clrJMD),
        ("JMD Remark",  clrJMD),

            };

            for (int i = 0; i < colHeaders.Length; i++)
            {
                var cell = ws.Cell(4, i + 1);
                cell.Value = colHeaders[i].Text;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9;
                cell.Style.Font.FontColor = clrWhite;
                cell.Style.Fill.BackgroundColor = colHeaders[i].Bg;
                cell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = true;
            }
            ApplyBorder(ws.Range(4, 1, 4, COLS));

            // ══════════════════════════════════════════
            // DATA ROWS — start at row 5
            // ══════════════════════════════════════════
            int dataRow = 5;
            int rowNum = 0;

            foreach (var item in list)
            {
                rowNum++;
                string rmRating = CalcRatingLocal(item.RM_TotalScore, item.TotalMaxScore);
                string hodRating = CalcRatingLocal(item.HOD_TotalScore, item.TotalMaxScore);
                string period = item.FromDate.HasValue && item.ToDate.HasValue
                    ? $"{item.FromDate.Value:dd-MMM-yy} – {item.ToDate.Value:dd-MMM-yy}"
                    : (item.Year > 0 ? $"Annual {item.Year}" : "—");

                bool mcDone = item.MC_SubmitDate != null;
                bool jmdDone = item.JMD_SubmitDate != null;

                var rowBg = rowNum % 2 == 0 ? clrRowAlt : clrRowMain;

                // ── Cols 1–6: Employee info ──
                ws.Cell(dataRow, 1).Value = rowNum;
                ws.Cell(dataRow, 2).Value = item.EmpName;
                ws.Cell(dataRow, 3).Value = item.EmpCode;
                ws.Cell(dataRow, 4).Value = item.Department;
                ws.Cell(dataRow, 5).Value = item.Designation;
                ws.Cell(dataRow, 6).Value = period;

                for (int c = 1; c <= 6; c++)
                {
                    ws.Cell(dataRow, c).Style.Fill.BackgroundColor = rowBg;
                    ws.Cell(dataRow, c).Style.Font.FontSize = 9;
                    ws.Cell(dataRow, c).Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                }
                ws.Cell(dataRow, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(dataRow, 3).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(dataRow, 6).Style.Font.FontSize = 8;

                // ── Col 7: RM Rating ──
                ws.Cell(dataRow, 7).Value = rmRating;
                ws.Cell(dataRow, 7).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("E8F5E9");
                ws.Cell(dataRow, 7).Style.Font.Bold = true;
                ws.Cell(dataRow, 7).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("1B5E20");
                ws.Cell(dataRow, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(dataRow, 7).Style.Font.FontSize = 10;

                // ── Col 8: HOD Rating ──
                ws.Cell(dataRow, 8).Value = hodRating;
                ws.Cell(dataRow, 8).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("EDE7F6");
                ws.Cell(dataRow, 8).Style.Font.Bold = true;
                ws.Cell(dataRow, 8).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("4527A0");
                ws.Cell(dataRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(dataRow, 8).Style.Font.FontSize = 10;

                // ── Cols 9–10: MC Score / Remark ──
                if (mcDone && item.MC_Score > 0)
                {
                    ws.Cell(dataRow, 9).Value = item.MC_Score;
                    ws.Cell(dataRow, 9).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("FFF3E0");
                    ws.Cell(dataRow, 9).Style.Font.Bold = true;
                    ws.Cell(dataRow, 9).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("7C3400");
                    ws.Cell(dataRow, 10).Value = item.MC_Remark;
                    ws.Cell(dataRow, 10).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("FFF8F0");
                    ws.Cell(dataRow, 10).Style.Font.FontSize = 8;
                }
                else
                {
                    // Pending MC — soft green with dashed border to invite input
                    ws.Cell(dataRow, 9).Style.Fill.BackgroundColor = clrMCEntry;
                    ws.Cell(dataRow, 10).Style.Fill.BackgroundColor = clrMCEntry;
                    ws.Cell(dataRow, 9).Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Dashed;
                    ws.Cell(dataRow, 9).Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("66BB6A");
                    ws.Cell(dataRow, 10).Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Dashed;
                    ws.Cell(dataRow, 10).Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("66BB6A");
                }
                ws.Cell(dataRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(dataRow, 9).Style.Font.FontSize = 10;

                // ── Cols 11–12: JMD Score / Remark ──
                if (jmdDone && item.JMD_Score > 0)
                {
                    ws.Cell(dataRow, 11).Value = item.JMD_Score;
                    ws.Cell(dataRow, 11).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("E3F2FD");
                    ws.Cell(dataRow, 11).Style.Font.Bold = true;
                    ws.Cell(dataRow, 11).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("0E3E5C");
                    ws.Cell(dataRow, 12).Value = item.JMD_Remark;
                    ws.Cell(dataRow, 12).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("F0F8FF");
                    ws.Cell(dataRow, 12).Style.Font.FontSize = 8;
                }
                else
                {
                    // Pending JMD — soft blue with dashed border
                    ws.Cell(dataRow, 11).Style.Fill.BackgroundColor = clrJMDEntry;
                    ws.Cell(dataRow, 12).Style.Fill.BackgroundColor = clrJMDEntry;
                    ws.Cell(dataRow, 11).Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Dashed;
                    ws.Cell(dataRow, 11).Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("42A5F5");
                    ws.Cell(dataRow, 12).Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Dashed;
                    ws.Cell(dataRow, 12).Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("42A5F5");
                }
                ws.Cell(dataRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(dataRow, 11).Style.Font.FontSize = 10;



                // ── Row border ──
                ApplyBorder(ws.Range(dataRow, 1, dataRow, COLS));
                ws.Row(dataRow).Height = 20;

                dataRow++;
            }

            // ══════════════════════════════════════════
            // FOOTER ROW
            // ══════════════════════════════════════════
            ws.Range(dataRow, 1, dataRow, COLS).Merge();
            ws.Cell(dataRow, 1).Value =
                $"Total Pending: {list.Count} employees   |   " +
                $"MC Missing: {list.Count(x => x.MC_SubmitDate == null)}   |   " +
                $"JMD Missing: {list.Count(x => x.JMD_SubmitDate == null)}   |   " +
                $"Both Missing: {list.Count(x => x.MC_SubmitDate == null && x.JMD_SubmitDate == null)}";
            ws.Cell(dataRow, 1).Style.Font.Bold = true;
            ws.Cell(dataRow, 1).Style.Font.FontSize = 9;
            ws.Cell(dataRow, 1).Style.Font.FontColor = clrWhite;
            ws.Cell(dataRow, 1).Style.Fill.BackgroundColor = clrNavy;
            ws.Cell(dataRow, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
            ws.Cell(dataRow, 1).Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
            ws.Row(dataRow).Height = 22;

            // ══════════════════════════════════════════
            // COLUMN WIDTHS
            // ══════════════════════════════════════════
            ws.Column(1).Width = 5;
            ws.Column(2).Width = 26;
            ws.Column(3).Width = 11;
            ws.Column(4).Width = 28;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 28;
            ws.Column(7).Width = 11;
            ws.Column(8).Width = 11;
            ws.Column(9).Width = 11;
            ws.Column(10).Width = 26;
            ws.Column(11).Width = 11;
            ws.Column(12).Width = 26;


            // ══════════════════════════════════════════
            // FREEZE, FILTER, PRINT SETTINGS
            // ══════════════════════════════════════════
            ws.SheetView.FreezeRows(4);
            ws.Range(4, 1, 4, COLS).SetAutoFilter();

            ws.PageSetup.PageOrientation = ClosedXML.Excel.XLPageOrientation.Landscape;
            ws.PageSetup.PaperSize = ClosedXML.Excel.XLPaperSize.A4Paper;
            ws.PageSetup.FitToPages(1, 0);
            ws.PageSetup.SetRowsToRepeatAtTop(1, 4);
            ws.PageSetup.CenterHorizontally = true;

            ws.PageSetup.Header.Left.AddText("Vandana Global Limited – PMS Blue Collar  |  MC / JMD Pending");
            ws.PageSetup.Footer.Left.AddText($"Generated: {DateTime.Now:dd MMM yyyy}");
            ws.PageSetup.Footer.Right.AddText("Page &[Page]");

            using var stream = new System.IO.MemoryStream();
            wb.SaveAs(stream);

            string fileName = $"MC_JMD_Pending_{(year.HasValue ? year.Value.ToString() : "All")}_{DateTime.Now:yyyyMMdd}.xlsx";
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

        // ══════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════
        private sealed record OrgEmpInfo(int EmpId, string FullName, string WorkEmail, string EmpCode);

        private async Task<Dictionary<int, OrgEmpInfo>> LoadOrgEmployeesByIdsAsync(string orgCon, IEnumerable<int> ids)
        {
            var map = new Dictionary<int, OrgEmpInfo>();
            var list = ids.Where(i => i > 0).Distinct().ToList();
            if (!list.Any()) return map;
            var pNames = list.Select((_, i) => "@e" + i).ToList();
            using var con = new SqlConnection(orgCon);
            await con.OpenAsync();
            using var cmd = new SqlCommand($"SELECT Emp_ID,Emp_Full_Name,ISNULL(Work_Email,'') AS Work_Email,CAST(Emp_Code AS NVARCHAR(50)) AS Emp_Code FROM T0080_EMP_MASTER WHERE Emp_ID IN ({string.Join(",", pNames)})", con);
            for (int i = 0; i < list.Count; i++) cmd.Parameters.Add(pNames[i], SqlDbType.Int).Value = list[i];
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                int id = Convert.ToInt32(rdr["Emp_ID"]);
                map[id] = new OrgEmpInfo(id, rdr["Emp_Full_Name"]?.ToString()?.Trim() ?? "", rdr["Work_Email"]?.ToString()?.Trim() ?? "", rdr["Emp_Code"]?.ToString()?.Trim() ?? "");
            }
            return map;
        }

        private string GetEmpCode(string empId, string orgCon)
        {
            if (!int.TryParse(empId, out int id)) return "";
            try
            {
                using var con = new SqlConnection(orgCon); con.Open();
                using var cmd = new SqlCommand("SELECT TOP 1 CAST(Emp_Code AS NVARCHAR(50)) FROM T0080_EMP_MASTER WHERE Emp_ID=@ID", con);
                cmd.Parameters.Add("@ID", SqlDbType.Int).Value = id;
                return cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private string GetWorkEmail(string empId, string orgCon)
        {
            if (!int.TryParse(empId, out int id)) return "";
            try
            {
                using var con = new SqlConnection(orgCon); con.Open();
                using var cmd = new SqlCommand("SELECT TOP 1 ISNULL(Work_Email,'') FROM T0080_EMP_MASTER WHERE Emp_ID=@ID", con);
                cmd.Parameters.Add("@ID", SqlDbType.Int).Value = id;
                return cmd.ExecuteScalar()?.ToString()?.Trim() ?? "";
            }
            catch { return ""; }
        }

        private string GetEmpIdByUser(string userName, string orgCon)
        {
            if (string.IsNullOrWhiteSpace(userName)) return "";
            try
            {
                using var con = new SqlConnection(orgCon); con.Open();
                using var cmd = new SqlCommand("SELECT TOP 1 CAST(Emp_ID AS NVARCHAR(50)) FROM T0080_EMP_MASTER WHERE CAST(Emp_Code AS NVARCHAR(50))=@Code", con);
                cmd.Parameters.Add("@Code", SqlDbType.NVarChar).Value = userName;
                return cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static async Task<List<SelectListItem>> LoadDropdownAsync(string connStr, string query)
        {
            var list = new List<SelectListItem>();
            using var con = new SqlConnection(connStr);
            await con.OpenAsync();
            using var cmd = new SqlCommand(query, con) { CommandTimeout = 30 };
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                list.Add(new SelectListItem { Value = rdr[0].ToString(), Text = rdr[1].ToString() });
            return list;
        }

        private void CreateNotification(string to, string by, string created, string msg, string type, string module, string link)
        {
            try
            {
                _dataAccessLayer.AddNotification(new Notifications
                {
                    NotificationType = type,
                    NotificationMessage = msg,
                    NotificationModule = module,
                    NotificationLink = link,
                    AssignedTo = to,
                    AssignedBy = by,
                    CreatedBy = created,
                    CreatedOn = DateTime.Now,
                    IsAccessed = false,
                    ViewCount = 0,
                    ViewDate = DateTime.Now
                });
            }
            catch { }
        }

        private async Task SendEmailAsync(string to, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(to)) return;
            try { BackgroundJob.Enqueue(() => _emailSender.SendEmailAsync(subject, body, new List<string> { to }, new List<string>(), new List<string>())); }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════
        // EMAIL TEMPLATES — VGL PMS Blue Collar
        // ══════════════════════════════════════════════════════════════

        // ── Shared shell ──
        private static string EmailShell(string accentColor, string title, string bodyContent)
        {
            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"" />
  <meta name=""viewport"" content=""width=device-width,initial-scale=1.0"" />
  <title>{System.Net.WebUtility.HtmlEncode(title)}</title>
</head>
<body style=""margin:0;padding:0;background:#F4F7FC;font-family:'Segoe UI',Arial,sans-serif;"">

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background:#F4F7FC;padding:40px 15px;"">
<tr><td align=""center"">

<table width=""650"" cellpadding=""0"" cellspacing=""0"" border=""0""
       style=""background:#FFFFFF;border-radius:18px;overflow:hidden;
               box-shadow:0 8px 30px rgba(11,28,63,0.10);"">

  <!-- TOP STRIP -->
  <tr><td style=""height:6px;background:{accentColor};""></td></tr>

  <!-- HEADER -->
  <tr>
    <td style=""background:#0B1C3F;padding:30px 40px;"">
      <table width=""100%"" cellpadding=""0"" cellspacing=""0"">
        <tr>
          <td width=""70"">
            <div style=""width:55px;height:55px;border-radius:12px;background:{accentColor};
                         color:#FFFFFF;text-align:center;line-height:55px;
                         font-size:24px;font-weight:bold;"">V</div>
          </td>
          <td>
            <div style=""color:#FFFFFF;font-size:22px;font-weight:700;"">Vandana Global Limited</div>
            <div style=""color:rgba(255,255,255,.70);font-size:12px;margin-top:3px;letter-spacing:.5px;"">
              PMS – NON MANAGEMENT CADRE
            </div>
          </td>
        </tr>
      </table>
    </td>
  </tr>

  {bodyContent}

  <!-- FOOTER -->
  <tr>
    <td style=""background:#F8FAFC;border-top:1px solid #E2E8F0;padding:25px 40px;"">
      <div style=""font-size:12px;color:#64748B;line-height:1.8;"">
        This is an automated notification generated by the
        <strong>VGL PMS System</strong>. Please do not reply to this email.
      </div>
      <div style=""margin-top:10px;color:#94A3B8;font-size:11px;"">
        © {DateTime.Now.Year} Vandana Global Limited. All Rights Reserved.
      </div>
    </td>
  </tr>

</table>
</td></tr></table>
</body></html>";
        }

        // ── Shared CTA button ──
        private static string EmailButton(string url, string label, string bg, string fg = "#FFFFFF")
        {
            return $@"<tr>
  <td align=""center"" style=""padding:35px 40px;"">
    <a href=""{url}""
       style=""display:inline-block;background:{bg};color:{fg};text-decoration:none;
               padding:16px 38px;border-radius:10px;font-size:15px;font-weight:700;"">
      {System.Net.WebUtility.HtmlEncode(label)} →
    </a>
  </td>
</tr>";
        }

        // ── Shared info detail row ──
        private static string DetailRow(string label, string value, string bg = "#F8FAFC")
        {
            return $@"<tr>
  <td style=""padding:12px 18px;background:{bg};border-bottom:1px solid #E2E8F0;"">
    <span style=""font-size:11px;font-weight:700;color:#64748B;
                  text-transform:uppercase;letter-spacing:.8px;"">{System.Net.WebUtility.HtmlEncode(label)}&nbsp;&nbsp;</span>
    <span style=""font-size:14px;font-weight:600;color:#1E293B;"">{System.Net.WebUtility.HtmlEncode(value)}</span>
  </td>
</tr>";
        }

        // ══════════════════════════════════════════════════════════════
        // 1. ASSIGNMENT EMAIL — RM / HOD assigned to employee(s)
        // ══════════════════════════════════════════════════════════════
        private string BuildAssignmentEmailBody(
            string name, string role, List<string> emps, string link, string cycleLabel)
        {
            // Employee card rows
            string empRows = string.Join(
                @"<tr><td height=""8""></td></tr>",
                emps.Select(e => $@"<tr>
  <td style=""padding:14px 18px;background:#F8FAFC;border:1px solid #E2E8F0;
              border-radius:8px;font-size:14px;color:#1E293B;"">
    👤 {System.Net.WebUtility.HtmlEncode(e)}
  </td>
</tr>"));

            string body = $@"
  <!-- TITLE + GREETING -->
  <tr>
    <td style=""padding:40px 40px 20px 40px;"">
      <div style=""color:#0B1C3F;font-size:26px;font-weight:700;margin-bottom:15px;"">
        PMS Assignment Notification
      </div>
      <div style=""color:#475569;font-size:15px;line-height:1.8;"">
        Dear <strong>{System.Net.WebUtility.HtmlEncode(name)}</strong>,<br><br>
        You have been assigned as
        <strong style=""color:#2F56D8;"">{System.Net.WebUtility.HtmlEncode(role)}</strong>
        for the employees listed below. Kindly complete your review within the PMS system.
      </div>
    </td>
  </tr>

  <!-- CYCLE CARD -->
  <tr>
    <td style=""padding:0 40px 25px 40px;"">
      <table width=""100%"" cellpadding=""0"" cellspacing=""0""
             style=""background:#EEF4FF;border-left:5px solid #2F56D8;border-radius:10px;"">
        <tr>
          <td style=""padding:18px;"">
            <div style=""color:#2F56D8;font-size:11px;font-weight:700;
                        text-transform:uppercase;letter-spacing:1px;"">Appraisal Cycle</div>
            <div style=""color:#0B1C3F;font-size:18px;font-weight:700;margin-top:5px;"">
              {System.Net.WebUtility.HtmlEncode(cycleLabel)}
            </div>
          </td>
        </tr>
      </table>
    </td>
  </tr>

  <!-- EMPLOYEE LIST -->
  <tr>
    <td style=""padding:0 40px;"">
      <div style=""font-size:12px;font-weight:700;text-transform:uppercase;color:#64748B;
                  letter-spacing:1px;margin-bottom:12px;"">Assigned Employees</div>
      <table width=""100%"" cellpadding=""0"" cellspacing=""0"">
        {empRows}
      </table>
    </td>
  </tr>

  {EmailButton(link, "Open PMS Dashboard", "#2F56D8")}";

            return EmailShell("#2F56D8", "PMS Assignment Notification", body);
        }

        // ══════════════════════════════════════════════════════════════
        // 2. STAGE ADVANCE EMAIL — next reviewer notified
        // ══════════════════════════════════════════════════════════════
        private string BuildStageEmail(
     string toName, string fromStage, string nextStage, string empName, string link)
        {
            // Stage pipeline builder
            string StageDot(string label, bool done, bool active)
            {
                string bg = done ? "#059669" : active ? "#2F56D8" : "#E2E8F0";
                string fg = (done || active) ? "#FFFFFF" : "#94A3B8";
                string txt = done ? "✓" : label;
                return $@"<td align=""center"">
      <div style=""width:42px;height:42px;border-radius:50%;background:{bg};
                   color:{fg};font-size:12px;font-weight:700;
                   text-align:center;line-height:42px;"">{txt}</div>
      <div style=""font-size:10px;color:#64748B;margin-top:5px;font-weight:600;
                   text-transform:uppercase;letter-spacing:.5px;"">{label}</div>
    </td>";
            }

            string ConnectorLine(bool done) =>
                $@"<td style=""padding-bottom:18px;"">
         <div style=""height:2px;background:{(done ? "#059669" : "#E2E8F0")};width:40px;""></div>
       </td>";

            bool rmDone = fromStage == "RM" || fromStage == "HOD";
            bool hodDone = fromStage == "HOD";

            string body = $@"
  <!-- TITLE + GREETING -->
  <tr>
    <td style=""padding:40px 40px 20px 40px;"">
      <div style=""color:#0B1C3F;font-size:26px;font-weight:700;margin-bottom:15px;"">
        Action Required — {System.Net.WebUtility.HtmlEncode(nextStage)} Review
      </div>
      <div style=""color:#475569;font-size:15px;line-height:1.8;"">
        Dear <strong>{System.Net.WebUtility.HtmlEncode(toName)}</strong>,<br><br>
        The <strong style=""color:#7C3AED;"">{System.Net.WebUtility.HtmlEncode(fromStage)}</strong>
        has submitted scores for the employee listed below. Kindly complete your review
        as <strong style=""color:#7C3AED;"">{System.Net.WebUtility.HtmlEncode(nextStage)}</strong>
        within the PMS system.
      </div>
    </td>
  </tr>

  <!-- ROLE CARD (mirrors Appraisal Cycle card in RM email) -->
  <tr>
    <td style=""padding:0 40px 25px 40px;"">
      <table width=""100%"" cellpadding=""0"" cellspacing=""0""
             style=""background:#F3EEFF;border-left:5px solid #7C3AED;border-radius:10px;"">
        <tr>
          <td style=""padding:18px;"">
            <div style=""color:#7C3AED;font-size:11px;font-weight:700;
                        text-transform:uppercase;letter-spacing:1px;"">Your Role</div>
            <div style=""color:#0B1C3F;font-size:18px;font-weight:700;margin-top:5px;"">
              {System.Net.WebUtility.HtmlEncode(nextStage)} — Review &amp; Submit Score
            </div>
          </td>
        </tr>
      </table>
    </td>
  </tr>

  <!-- EMPLOYEE CARD (mirrors Assigned Employees list in RM email) -->
  <tr>
    <td style=""padding:0 40px;"">
      <div style=""font-size:12px;font-weight:700;text-transform:uppercase;color:#64748B;
                  letter-spacing:1px;margin-bottom:12px;"">Assigned Employee</div>
      <table width=""100%"" cellpadding=""0"" cellspacing=""0"">
        <tr>
          <td style=""padding:14px 18px;background:#F8FAFC;border:1px solid #E2E8F0;
                      border-radius:8px;font-size:14px;color:#1E293B;"">
            👤 {System.Net.WebUtility.HtmlEncode(empName)}
          </td>
        </tr>
      </table>
    </td>
  </tr>

  <!-- STAGE PIPELINE -->
  <tr>
    <td style=""padding:30px 40px 10px 40px;"">
      <div style=""font-size:12px;font-weight:700;text-transform:uppercase;color:#64748B;
                  letter-spacing:1px;margin-bottom:14px;"">Stage Progress</div>
      <table cellpadding=""0"" cellspacing=""0"">
        <tr>
          {StageDot("RM", rmDone, false)}
          {ConnectorLine(rmDone)}
          {StageDot("HOD", hodDone, nextStage == "HOD")}
          {ConnectorLine(hodDone)}
          {StageDot("MC", false, nextStage == "MC")}
          {ConnectorLine(false)}
          {StageDot("JMD", false, nextStage == "JMD")}
        </tr>
      </table>
    </td>
  </tr>

  {EmailButton(link, "Open My Dashboard", "#7C3AED")}";

            return EmailShell("#7C3AED", $"Action Required — {nextStage} Review", body);
        }

        // ══════════════════════════════════════════════════════════════
        // 3. COMPLETION EMAIL — HR notified of full completion
        // ══════════════════════════════════════════════════════════════
        private string BuildCompletionEmail(string empName, string baseUrl)
        {
            string body = $@"
  <!-- TITLE + GREETING -->
  <tr>
    <td style=""padding:40px 40px 20px 40px;"">
      <div style=""color:#0B1C3F;font-size:26px;font-weight:700;margin-bottom:15px;"">
        PMS Fully Completed ✓
      </div>
      <div style=""color:#475569;font-size:15px;line-height:1.8;"">
        Dear <strong>HR Team</strong>,<br><br>
        The full appraisal cycle for the employee below has been
        <strong style=""color:#059669;"">successfully completed</strong>.
        All stages — RM, HOD, MC, and JMD — have submitted their scores.
      </div>
    </td>
  </tr>

  <!-- COMPLETION BANNER -->
  <tr>
    <td style=""padding:0 40px 25px 40px;"">
      <table width=""100%"" cellpadding=""0"" cellspacing=""0""
             style=""background:#F0FDF4;border-left:5px solid #059669;border-radius:10px;"">
        <tr>
          <td style=""padding:18px;"">
            <div style=""color:#059669;font-size:11px;font-weight:700;
                        text-transform:uppercase;letter-spacing:1px;"">PMS Completed</div>
            <div style=""color:#0B1C3F;font-size:18px;font-weight:700;margin-top:5px;"">
              {System.Net.WebUtility.HtmlEncode(empName)}
            </div>
          </td>
        </tr>
      </table>
    </td>
  </tr>

  <!-- STAGE SUMMARY -->
  <tr>
    <td style=""padding:0 40px;"">
      <div style=""font-size:12px;font-weight:700;text-transform:uppercase;color:#64748B;
                  letter-spacing:1px;margin-bottom:12px;"">All Stages Complete</div>
      <table width=""100%"" cellpadding=""0"" cellspacing=""0""
             style=""border:1px solid #E2E8F0;border-radius:10px;overflow:hidden;"">
        {DetailRow("RM Review", "✓  Submitted", "#FFFFFF")}
        {DetailRow("HOD Review", "✓  Submitted", "#F8FAFC")}
        {DetailRow("MC Score", "✓  Submitted", "#FFFFFF")}
        {DetailRow("JMD Score", "✓  Submitted", "#F8FAFC")}
      </table>
    </td>
  </tr>

  {EmailButton($"{baseUrl}/pmsbluecollar/tracking", "View Tracking Dashboard", "#059669")}";

            return EmailShell("#059669", "PMS Fully Completed", body);
        }
        // ══════════════════════════════════════════════════════════════
        // NEW PAYLOAD MODEL — wraps employees + cycle info
        // ══════════════════════════════════════════════════════════════
        public class PMSBCSavePayload
        {
            public string CycleType { get; set; } = "";
            public int CycleYear { get; set; }
            public DateTime? CustomFromDate { get; set; }
            public DateTime? CustomToDate { get; set; }
            public List<PMSBCSaveModel> Employees { get; set; } = new();
        }
    }
}
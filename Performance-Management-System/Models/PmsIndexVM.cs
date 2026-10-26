using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace vgl.Models
{
    // ──────────────────────────────────────────────────────────
    // PMP — Performance Management Perspective
    // ──────────────────────────────────────────────────────────
    public class PmpModel
    {
        public int PmpId { get; set; }
        public string PmpCode { get; set; }
        [Required(ErrorMessage = "Perspective Name is required.")]
        public string PmpName { get; set; }
        public bool IsActive { get; set; } = true;
        public string CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int SrNo { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // KRA — Key Result Area
    // ──────────────────────────────────────────────────────────
    public class KraModel
    {
        public int KraId { get; set; }
        public string KraCode { get; set; }
        public int PmpId { get; set; }
        public string PmpCode { get; set; }
        public string PmpName { get; set; }
        [Required(ErrorMessage = "KRA Name is required.")]
        public string KraName { get; set; }
        public bool IsActive { get; set; } = true;
        public string CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int SrNo { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // KPI — Key Performance Indicator
    // ──────────────────────────────────────────────────────────
    public class KpiModel
    {
        public int KpiId { get; set; }
        public string KpiCode { get; set; }
        public int PmpId { get; set; }
        public int KraId { get; set; }
        [Required(ErrorMessage = "KPI Name is required.")]
        public string KpiName { get; set; }
        public string UnitOfMeasure { get; set; } = "%";
        [Range(0, 999999, ErrorMessage = "Weightage must be 0 or above.")]
        public decimal IndividualWeightage { get; set; }
        public decimal OverallWeightage { get; set; }
        [Range(0, 999999, ErrorMessage = "Target must be 0 or above.")]
        public decimal Target { get; set; }
        public bool IsActive { get; set; } = true;
        public string CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int SrNo { get; set; }
        // Joined display
        public string PmpName { get; set; }
        public string PmpCode { get; set; }
        public string KraName { get; set; }
        public string KraCode { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // KPI AJAX Save Request
    // ──────────────────────────────────────────────────────────
    public class KpiSaveRequest
    {
        public int PmpId { get; set; }
        public int KraId { get; set; }
        public List<KpiEntryRow> Rows { get; set; } = new();
    }

    public class KpiEntryRow
    {
        public int KpiId { get; set; }
        public string KpiName { get; set; }
        public string UnitOfMeasure { get; set; } = "%";
        public decimal IndividualWeightage { get; set; }
        public decimal Target { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // EMPLOYEE — from UserDetails table
    // ──────────────────────────────────────────────────────────
    public class EmployeeModel
    {
        public string UserId { get; set; }
        public int Emp_Id { get; set; }
        public string EMPCode { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public string DesignationName { get; set; }
        public string DepartmentName { get; set; }
        public string ReportTo { get; set; }
        public int SrNo { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // EMPLOYEE KPI MAPPING
    // ──────────────────────────────────────────────────────────
    public class EmpKpiMappingModel
    {
        public int MappingId { get; set; }
        // Add these two properties to EmpKpiMappingModel
        public decimal? JmdFinalBscScore { get; set; }
        public string? JmdFinalBscRemarks { get; set; }

        public string UnitName { get; set; } = "";
        // In PmsIndexVM
       // public List<EmpKpiMappingModel> AnnualCompletedMappings { get; set; } = new();
        public string UserId { get; set; }
        public int EmpId { get; set; }
        public string EmpCode { get; set; }
        public string EmpName { get; set; }

        public string Designation { get; set; }
        public string Department { get; set; }
        public string SubDepartment { get; set; }
        public string ReportTo { get; set; }
        public string Email { get; set; }
        public int? RmEmpId { get; set; }
        public int? HodEmpId { get; set; }
        public int? UnitHeadEmpId { get; set; }
        public int? JmdEmpId { get; set; }
        public List<int> ModCommitteeEmpIds { get; set; } = new();

        // Reviewer names
        public string RmName { get; set; }
        public string HodName { get; set; }
        public string UnitHeadName { get; set; }
        public string ModCommitteeNames { get; set; }
        public string JmdName { get; set; }

        public int AppraisalYear { get; set; }
        public string AppraisalCycle { get; set; }
        public DateTime? CycleFromDate { get; set; }
        public DateTime? CycleToDate { get; set; }

        public List<EmpKpiRow> KpiRows { get; set; } = new();

        public int KpiCount => KpiRows?.Count ?? 0;

        public string Status { get; set; } = "Draft";
        public DateTime? LastUpdatedAt { get; set; }
        public string LastUpdatedBy { get; set; }

        public string CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int SrNo { get; set; }
        public decimal? McRating { get; set; }
        public decimal? JmdRating { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // Each KPI row in the employee appraisal sheet
    // ──────────────────────────────────────────────────────────
    public class EmpKpiRow
    {
        public List<KpiDocumentVM> Documents { get; set; } = new();
        public int EmpKpiRowId { get; set; }

        public int MappingId { get; set; }
        public int KpiId { get; set; }
        public string KpiCode { get; set; }
        public string KpiName { get; set; }
        public string EmpJustification { get; set; }
        public int PmpId { get; set; }
        public string PmpName { get; set; }
        public int KraId { get; set; }
        public string KraName { get; set; }
        public string UnitOfMeasure { get; set; }
        public decimal IndividualWeightage { get; set; }
        public decimal OverallWeightage { get; set; }
        public decimal Target { get; set; }

        // Employee self-appraisal
        public decimal? EmpActual { get; set; }
        public decimal? EmpRating { get; set; }

        // Reporting Manager
        public decimal? RmActual { get; set; }
        public decimal? RmRating { get; set; }
        public string RmRemarks { get; set; }

        // Head of Department
        public decimal? HodActual { get; set; }
        public decimal? HodRating { get; set; }
        public string HodRemarks { get; set; }

        // Unit Head
        public decimal? UnitHeadActual { get; set; }
        public decimal? UnitHeadRating { get; set; }
        public string UnitHeadRemarks { get; set; }

        // Moderation Committee
        public decimal? ModCommitteeActual { get; set; }
        public decimal? ModCommitteeRating { get; set; }
        public string ModCommitteeRemarks { get; set; }

        // Joint Managing Director
        public decimal? JmdActual { get; set; }
        public decimal? JmdRating { get; set; }
        public string JmdRemarks { get; set; }

        // HR Final
        public decimal? HrFinalRating { get; set; }

        // Computed persisted column
        public decimal? WeightedRating { get; set; }

        public int SrNo { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // AJAX: HR Save Employee Mapping Request
    // ──────────────────────────────────────────────────────────
    public class EmpMappingSaveRequest
    {
        public int MappingId { get; set; }
        public string UserId { get; set; }
        public int AppraisalYear { get; set; }
        public string AppraisalCycle { get; set; }
        public string CycleFromDate { get; set; }
        public string CycleToDate { get; set; }
        public List<int> KpiIds { get; set; } = new();
        public List<KpiDetailForSave> KpiDetails { get; set; } = new();

        public string RmName { get; set; }
        public string HodName { get; set; }
        public string UnitHeadName { get; set; }
        public string ModCommitteeNames { get; set; }
        public string JmdName { get; set; }

        public bool? CycleUpdateOnly { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // KPI detail row sent from the mapping form
    // ──────────────────────────────────────────────────────────
    public class KpiDetailForSave
    {
        public int KpiId { get; set; }
        public int PmpId { get; set; }        // ← ADD
        public int KraId { get; set; }        // ← ADD
        public string KpiName { get; set; }
        public string UnitOfMeasure { get; set; }
        public double IndividualWeightage { get; set; }
        public double Target { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // AJAX: Fill actual & rating
    // ──────────────────────────────────────────────────────────
    public class AppraisalFillRequest
    {
        public int MappingId { get; set; }
        public string FillerRole { get; set; }
        public string OverallRemarks { get; set; }
        public bool NotifyRm { get; set; }
        // JMD-only BSC final appraisal
        public decimal? JmdFinalBscScore { get; set; }
        public string? JmdFinalBscRemarks { get; set; }
        // MC
        public decimal? McOverallRating { get; set; }
        public string McOverallRemarks { get; set; }

        // JMD
        public decimal? JmdOverallRating { get; set; }
        public string JmdOverallRemarks { get; set; }
        public List<AppraisalFillRow> Rows { get; set; } = new();
    }

    public class AppraisalFillRow
    {
        public int EmpKpiRowId { get; set; }
        public decimal? Actual { get; set; }   // nullable
        public decimal? Rating { get; set; }   // nullable
        public string Remarks { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // ViewModel for Appraisal Fill pages
    // ──────────────────────────────────────────────────────────
    public class AppraisalFillVM
    {
        public EmpKpiMappingModel Mapping { get; set; }
        public string LoggedInUserId { get; set; }
        public string LoggedInName { get; set; }
        public string Role { get; set; }
        public bool CanEdit { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // PAS Master view-models
    // ──────────────────────────────────────────────────────────
    public class PasBasePercentageRow
    {
        public int Id { get; set; }
        public string RangeLabel { get; set; }
        public decimal? PATRevenueMin { get; set; }
        public decimal PATRevenueMax { get; set; }
        public decimal BasePercentage { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Header/version row for the rating matrix (one row per version).
    /// Replaces the old flat PasRatingHikeRow.
    /// </summary>
    public class PasRatingVersionRow
    {
        public int Id { get; set; }
        public string VersionName { get; set; }
        public DateTime EffectiveDate { get; set; }
        public decimal? BasePctSample { get; set; }
        public bool IsActive { get; set; }
        public string Remarks { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        /// <summary>Populated in controller: detail rows for this version.</summary>
        public List<PasRatingDetailRow> DetailRows { get; set; } = new();
    }

    /// <summary>One rating level row within a version.</summary>
    public class PasRatingDetailRow
    {
        public int Id { get; set; }
        public int VersionId { get; set; }
        public int Rating { get; set; }
        public string RatingLabel { get; set; }
        public decimal RatingRangeMin { get; set; }
        public decimal RatingRangeMax { get; set; }
        public decimal DistributionPercent { get; set; }
        public decimal HikeOverBasePercent { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Legacy flat row — kept for backward-compat with any existing SaveRatingMatrix logic.
    /// New code should use PasRatingVersionRow / PasRatingDetailRow.
    /// </summary>
    public class PasRatingHikeRow
    {
        public int Id { get; set; }
        public int Rating { get; set; }
        public string RatingLabel { get; set; }
        public decimal HikeOverBasePercent { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>Yearly PAT entry — now supports direct % input in addition to amount fields.</summary>
    public class PasYearlyPATEntryRow
    {
        public int Id { get; set; }
        public string FinancialYear { get; set; }
        public DateTime FYStartDate { get; set; }
        public DateTime FYEndDate { get; set; }
        /// <summary>NEW: direct PAT/Revenue % input by HR (replaces amount-derived calc).</summary>
        public decimal? PATRevenuePercentDirect { get; set; }
        /// <summary>Optional reference — no longer required.</summary>
        public decimal? PATAmount { get; set; }
        /// <summary>Optional reference — no longer required.</summary>
        public decimal? RevenueAmount { get; set; }
        /// <summary>Stored final % (equals PATRevenuePercentDirect when using direct mode).</summary>
        public decimal? PATRevenuePercent { get; set; }
        public decimal? BasePercentage { get; set; }
        public string Remarks { get; set; }
        public bool IsFinalized { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // Main PMS Index ViewModel
    // ──────────────────────────────────────────────────────────
    public class PmsIndexVM
    {
        public List<PmpModel> Pmps { get; set; } = new();
        public List<KraModel> Kras { get; set; } = new();
        public List<KpiModel> Kpis { get; set; } = new();
        public List<SelectListItem> PmpDropdown { get; set; } = new();
        public List<SelectListItem> KraDropdown { get; set; } = new();
        public int ActiveTab { get; set; } = 1;
        public List<SelectListItem> ReviewerDropdown { get; set; } = new();
        public List<EmpKpiMappingModel> EmpMappings { get; set; } = new();
        public List<SelectListItem> EmployeeDropdown { get; set; } = new();
        public int CurrentYear { get; set; } = DateTime.Now.Year;

        // ── PAS Configuration (Tab 0) ──
        public List<PasBasePercentageRow> PasBaseMaster { get; set; } = new();
        public List<EmpKpiMappingModel> AnnualCompletedMappings { get; set; } = new List<EmpKpiMappingModel>();
        /// <summary>
        /// All versioned rating matrices. Replaces the old flat PasRatingMaster list.
        /// </summary>
        public List<PasRatingVersionRow> PasRatingVersions { get; set; } = new();

        /// <summary>Convenience shortcut to the currently active version.</summary>
        public PasRatingVersionRow PasActiveVersion { get; set; }

        /// <summary>
        /// Legacy flat list — kept so existing Razor references to Model.PasRatingMaster
        /// do not break during migration. Remove once Razor is fully updated.
        /// </summary>
        public List<PasRatingHikeRow> PasRatingMaster { get; set; } = new();

        public List<PasYearlyPATEntryRow> PasYearlyPAT { get; set; } = new();
    }
   
public class KpiDocument
    {
        public int Id { get; set; }
        public int EmpKpiRowId { get; set; }
        public int MappingId { get; set; }
        public string UploaderRole { get; set; }
        public string FileName { get; set; }
        public string OriginalFileName { get; set; }
        public DateTime UploadedAt { get; set; }
    }

    public class KpiDocumentVM
    {
        public int Id { get; set; }
        public string FileName { get; set; }
        public string OriginalFileName { get; set; }
        public string UploaderRole { get; set; }
        public int EmpKpiRowId { get; set; }
    }

    public class AnnualReportRow
    {
        public int MappingId { get; set; }
        public string EmpCode { get; set; }
        public int EmpId { get; set; }
        public string EmpName { get; set; }
        public string Department { get; set; }
        public string Designation { get; set; }
        public string UnitName { get; set; }
        public string UnitHeadName { get; set; }
        public DateTime? DateOfJoining { get; set; }
        public int AppraisalYear { get; set; }
        public decimal FinalScore { get; set; }
        public int CalcRating { get; set; }
        public string RatingLabel { get; set; }
        public decimal BasePercent { get; set; }
        public decimal HikeOverBase { get; set; }
        public decimal FinalIncrPct { get; set; }
        public bool BehaviouralCompleted { get; set; }
        public string JmdRemark { get; set; }

        public decimal? McRating { get; set; }
        public decimal? JmdRating { get; set; }
        public decimal? BaRmScore { get; set; }
        public decimal? BaHodScore { get; set; }
        public decimal? BaAvgScore { get; set; }
    }
}  // closes namespace vgl.Models

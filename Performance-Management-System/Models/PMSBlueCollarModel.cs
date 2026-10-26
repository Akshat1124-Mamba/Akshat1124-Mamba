using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace vgl.Models
{
    // ══════════════════════════════════════════════════════════════
    // HR DASHBOARD — employee list with PMS status
    // ══════════════════════════════════════════════════════════════
    public class PMSBCDashboardModel
    {
        public int EmpID { get; set; }
        public string EmpCode { get; set; } = "";
        public string EmpName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string DepartmentName { get; set; } = "";
        public int DepartmentID { get; set; }
        public string UnitName { get; set; } = "";
        public int UnitID { get; set; }
        public string SubDepartment { get; set; } = "";
        public string ReportingManager { get; set; } = "";
        public string RMID { get; set; } = "";
        public string HOD { get; set; } = "";
        public string HODID { get; set; } = "";
        public string AuditStatus { get; set; } = "";   // HR-facing label
        public string CurrentStage { get; set; } = "";
        public int PmsID { get; set; }         // 0 = not started
        public DateTime? CreatedOn { get; set; }
        public List<string> CompletedCycles { get; set; } = new(); // e.g. ["Q1-2026", "Annual-2026"]
    }

    // ══════════════════════════════════════════════════════════════
    // RM DASHBOARD
    // ══════════════════════════════════════════════════════════════
    public class PMSBCRMDashboardModel
    {
        public int PmsID { get; set; }
        public int EmpID { get; set; }
        public string EmpCode { get; set; } = "";
        public string EmpName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Department { get; set; } = "";
        public string SubDepartment { get; set; } = "";
        public string AuditStatus { get; set; } = "";
        public string RMStatus { get; set; } = "";   // "Pending" | "Submitted"
        public string FinalStatus { get; set; } = "";
        public DateTime? CreatedOn { get; set; }
        public DateTime? RM_SubmitDate { get; set; }
        public int RM_TotalScore { get; set; }
        public int CriteriaCount { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // HOD DASHBOARD
    // ══════════════════════════════════════════════════════════════
    public class PMSBCHODDashboardModel
    {
        public int PmsID { get; set; }
        public int EmpID { get; set; }
        public string EmpCode { get; set; } = "";
        public string EmpName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Department { get; set; } = "";
        public string SubDepartment { get; set; } = "";
        public string RMName { get; set; } = "";
        public string AuditStatus { get; set; } = "";
        public string RMStatus { get; set; } = "";
        public string HODStatus { get; set; } = "";
        public string FinalStatus { get; set; } = "";
        public DateTime? CreatedOn { get; set; }
        public DateTime? RM_SubmitDate { get; set; }
        public DateTime? HOD_SubmitDate { get; set; }
        public int RM_TotalScore { get; set; }
        public int HOD_TotalScore { get; set; }
        public int CriteriaCount { get; set; }
       
    }

    // ══════════════════════════════════════════════════════════════
    // TRACKING MODEL (HR tracking dashboard)
    // ══════════════════════════════════════════════════════════════
    public class PMSBCTrackingModel
    {
        public int PmsID { get; set; }
        public int EmpID { get; set; }
        public string EmpCode { get; set; } = "";
        public string EmpName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Department { get; set; } = "";
        public string SubDepartment { get; set; } = "";
        public int DeptID { get; set; }
        public int UnitID { get; set; }
        public string RM { get; set; } = "";
        public string HOD { get; set; } = "";
        public string HODID { get; set; } = "";
        public string Status { get; set; } = "";
        public bool HOD_Required { get; set; }
        public string Rating { get; set; } = "";
        public DateTime? CreatedOn { get; set; }
        public DateTime? PreviousAuditDate { get; set; }
        public DateTime? MC_SubmitDate { get; set; }
        public DateTime? JMD_SubmitDate { get; set; }
        // ── NEW ──
        public string CycleType { get; set; } = "";
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int Year { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // SCORE FORM MODEL (what RM / HOD see when scoring)
    // ══════════════════════════════════════════════════════════════
    public class PMSBCFormModel
    {
        public int Emp_ID { get; set; }
        public string Emp_code { get; set; } = "";
        public string Emp_Full_Name { get; set; } = "";
        public string Desig_Name { get; set; } = "";
        public string Dept_Name { get; set; } = "";
        public string Grd_Name { get; set; } = "";
        public string Cat_Name { get; set; } = "";
        public string Manager_Name { get; set; } = "";
        public int RMId { get; set; }
        public int HODId { get; set; }
        public string HOD_Name { get; set; } = "";
        public List<PMSBCDetailDisplayModel> AuditDetails { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════
    // DETAIL ROW — one criterion with scores
    // ══════════════════════════════════════════════════════════════
    public class PMSBCDetailDisplayModel
    {
        public int CriteriaID { get; set; }
        public string Category { get; set; } = "";   // category heading (Job Knowledge, etc.)
        public int MainSrNo { get; set; }
        public int SubSrNo { get; set; }
        public string Beh_Pers { get; set; } = "";   // criterion description text
        public int MaxScore { get; set; }
        public int DetailID { get; set; }
        public string Description { get; set; } = "";
        public int? RM_Score { get; set; }
        public string RM_Remark { get; set; } = "";
        public int? HOD_Score { get; set; }
        public string HOD_Remark { get; set; } = "";
        public decimal? AvgScore { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // SAVE MODEL — payload HR posts to kick off a PMS
    // ══════════════════════════════════════════════════════════════
    public class PMSBCSaveModel
    {
        public int EmpID { get; set; }
        public string EmpCode { get; set; } = "";
        public string EmpName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Department { get; set; } = "";
        public int DepartmentID { get; set; }
        public string SubDepartment { get; set; } = "";
        public int UnitID { get; set; }
        public string RMID { get; set; } = "";
        public string RMName { get; set; } = "";
        public string HODID { get; set; } = "";
        public string HODName { get; set; } = "";
    }

    // ══════════════════════════════════════════════════════════════
    // SCORE SUBMIT — payload from RM / HOD score page
    // ══════════════════════════════════════════════════════════════
    public class PMSBCScoreSubmitModel
    {
        public int PmsID { get; set; }
        public string Stage { get; set; } = "";   // "RM" | "HOD"
        public string SubmittedByID { get; set; } = "";
        public List<PMSBCScoreRow> Scores { get; set; } = new();
    }

    public class PMSBCScoreRow
    {
        public int CriteriaID { get; set; }
        public int Score { get; set; }
        public string Remark { get; set; } = "";
    }

    public class PMSBCMCJMDModel
    {
        public int PmsID { get; set; }
        public int EmpID { get; set; }
        public string EmpCode { get; set; } = "";
        public string EmpName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Department { get; set; } = "";
        public string SubDepartment { get; set; } = "";
        public string CycleType { get; set; } = "";
        public string CycleLabel { get; set; } = "";
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int Year { get; set; }
        public string FinalRating { get; set; } = "";
        public decimal RM_TotalScore { get; set; }
        public decimal HOD_TotalScore { get; set; }
        public int TotalMaxScore { get; set; }
        public decimal MC_Score { get; set; }
        public string MC_Remark { get; set; } = "";
        public DateTime? MC_SubmitDate { get; set; }
        public decimal JMD_Score { get; set; }
        public string JMD_Remark { get; set; } = "";
        public DateTime? JMD_SubmitDate { get; set; }
    }

    public class PMSBCMCJMDSaveItem
    {
        public int PmsID { get; set; }
        public decimal MC_Score { get; set; }
        public string? MC_Remark { get; set; }
        public decimal JMD_Score { get; set; }
        public string? JMD_Remark { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // PRINT / REPORT VIEW MODEL
    // ══════════════════════════════════════════════════════════════
    public class PMSBCPrintModel
    {
        // Employee header
        public int PmsID { get; set; }
        public string EmpName { get; set; } = "";
        public string EmpCode { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Department { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Grade { get; set; } = "";
        public string RMName { get; set; } = "";
        public string HODName { get; set; } = "";
        public DateTime? AppraisalDate { get; set; }
        public string Beh_Pers { get; set; }
        public string CycleType { get; set; } = "";

        // Scores grouped by category
        public List<PMSBCPrintCategory> Categories { get; set; } = new();

        // Totals
        public int RM_TotalScore { get; set; }
        public int HOD_TotalScore { get; set; }
        public int TotalMaxScore { get; set; }


        public int? MC_Score { get; set; }
        public int? JMD_Score { get; set; }

        // Rating Summary
        public string RM_Rating { get; set; } = "";
        public string HOD_Rating { get; set; } = "";
        public string ModCommitteeRating { get; set; } = "";
        public string JMDRating { get; set; } = "";
        public string FinalRating { get; set; } = "";
    }

    public class PMSBCPrintCategory
    {
        public string CategoryName { get; set; } = "";
        public List<PMSBCPrintRow> Rows { get; set; } = new();
    }

    public class PMSBCPrintRow
    {
        public int MainSrNo { get; set; }
        public int SubSrNo { get; set; }
        public string Description { get; set; } = "";
        public int MaxScore { get; set; }
        public int? RM_Score { get; set; }
        public string RM_Remark { get; set; } = "";
        public int? HOD_Score { get; set; }
        public string HOD_Remark { get; set; } = "";
    }

    public class PMSCriteriaModel
    {
        public int Id { get; set; }

        [Required]
        public string Category { get; set; } = "PMS";

        [Required]
        public int MainSrNo { get; set; }

        [Required]
        public int SubSrNo { get; set; }

        [Required]
        public string Beh_Pers { get; set; } = "";

        [Required]
        public string Description { get; set; } = "";

        [Required]
        [Range(1, 100)]
        public int MaxScore { get; set; }

        public bool IsDeleted { get; set; } = false;
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedOn { get; set; } = DateTime.Now;

        // UI only
        public string? ErrorMessage { get; set; }
    }
}

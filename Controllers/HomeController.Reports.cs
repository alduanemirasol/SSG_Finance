using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using MyMvcApp.Models;
using MyMvcApp.Services;
using MyMvcApp.Data;

namespace MyMvcApp.Controllers;

public partial class HomeController : AppController
{
    // ----------------------------------------------------------------
    // REPORTS
    // ----------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> GetReports()
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var reports = await _context.Reports
                .Include(r => r.SchoolYear)
                .Include(r => r.Creator).ThenInclude(a => a.User)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new
                {
                    r.ReportId,
                    r.ReportType,
                    r.Title,
                    r.Status,
                    r.BeginningBalance,
                    r.TotalRevenue,
                    r.TotalExpenses,
                    r.RunningBalance,
                    r.Semester,
                    r.DateFrom,
                    r.DateTo,
                    r.CreatedAt,
                    schoolYear = r.SchoolYear != null ? $"{r.SchoolYear.YearStart}–{r.SchoolYear.YearEnd}" : null,
                    createdBy = r.Creator != null && r.Creator.User != null
                        ? $"{r.Creator.User.FirstName} {r.Creator.User.LastName}"
                        : "Unknown"
                })
                .ToListAsync();

            return Json(new { success = true, reports });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetReportDetail(int id)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var report = await _context.Reports
                .Include(r => r.SchoolYear)
                .Include(r => r.Creator).ThenInclude(a => a.User)
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.ReportId == id);

            if (report == null)
                return Json(new { success = false, message = "Report not found." });

            return Json(new
            {
                success = true,
                report = new
                {
                    report.ReportId,
                    report.ReportType,
                    report.Title,
                    report.Status,
                    report.BeginningBalance,
                    report.TotalRevenue,
                    report.TotalExpenses,
                    report.RunningBalance,
                    report.Semester,
                    report.DateFrom,
                    report.DateTo,
                    report.CreatedAt,
                    schoolYear = report.SchoolYear != null ? $"{report.SchoolYear.YearStart}–{report.SchoolYear.YearEnd}" : null,
                    createdBy = report.Creator?.User != null
                        ? $"{report.Creator.User.FirstName} {report.Creator.User.LastName}"
                        : "Unknown",
                    items = report.Items.Select(i => new
                    {
                        i.ItemId,
                        i.ItemType,
                        i.ItemRefId,
                        i.Description,
                        i.Amount
                    })
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateReport([FromBody] CreateReportRequest req)
    {
        var guard = RequireAnyRole("Treasurer");
        if (guard != null) return guard;

        try
        {
            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var accountId))
                return Json(new { success = false, message = "Session expired." });

            // Determine date range
            DateTime? dateFrom = req.DateFrom.HasValue ? req.DateFrom.Value.Date : null;
            DateTime? dateTo   = req.DateTo.HasValue   ? req.DateTo.Value.Date   : null;

            // Fetch selected expenses and funds
            var selectedExpenses = req.ExpenseIds != null && req.ExpenseIds.Count > 0
                ? await _context.Expenses.Where(e => req.ExpenseIds.Contains(e.ExpenseId)).ToListAsync()
                : new List<Expense>();

            var selectedFunds = req.FundIds != null && req.FundIds.Count > 0
                ? await _context.OtherFunds.Where(f => req.FundIds.Contains(f.FundId)).ToListAsync()
                : new List<OtherFund>();

            // Compute beginning balance = all income - all expenses BEFORE the earliest selected date
            var allDates = selectedExpenses.Select(e => e.ExpenseDate)
                .Concat(selectedFunds.Select(f => f.ReceivedDate))
                .ToList();

            decimal beginningBalance = 0;
            if (allDates.Any())
            {
                var cutoff = allDates.Min();
                var orgFeesBefore  = await _context.OrgFeePayments.Where(p => p.PaymentDate < cutoff).SumAsync(p => (decimal?)p.Amount) ?? 0;
                var fundsBefore    = await _context.OtherFunds.Where(f => f.ReceivedDate < cutoff).SumAsync(f => (decimal?)f.Amount) ?? 0;
                var expBefore      = await _context.Expenses.Where(e => e.ExpenseDate < cutoff).SumAsync(e => (decimal?)e.Amount) ?? 0;
                beginningBalance   = orgFeesBefore + fundsBefore - expBefore;
            }

            var totalRevenue  = selectedFunds.Sum(f => f.Amount);
            var totalExpenses = selectedExpenses.Sum(e => e.Amount);
            var runningBalance = beginningBalance + totalRevenue - totalExpenses;

            var report = new Report
            {
                ReportType        = req.ReportType,
                Title             = req.Title,
                SchoolYearId      = req.SchoolYearId,
                Semester          = req.Semester,
                DateFrom          = dateFrom,
                DateTo            = dateTo,
                BeginningBalance  = beginningBalance,
                TotalRevenue      = totalRevenue,
                TotalExpenses     = totalExpenses,
                RunningBalance    = runningBalance,
                Status            = "Final",
                CreatedBy         = accountId,
                CreatedAt         = DateTime.Now
            };

            _context.Reports.Add(report);
            await _context.SaveChangesAsync();

            var items = new List<ReportItem>();
            foreach (var f in selectedFunds)
                items.Add(new ReportItem { ReportId = report.ReportId, ItemType = "Fund",    ItemRefId = f.FundId,    Description = f.Description ?? f.Source, Amount = f.Amount });
            foreach (var e in selectedExpenses)
                items.Add(new ReportItem { ReportId = report.ReportId, ItemType = "Expense", ItemRefId = e.ExpenseId, Description = e.Description, Amount = e.Amount });

            _context.ReportItems.AddRange(items);
            await _context.SaveChangesAsync();

            return Json(new { success = true, reportId = report.ReportId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReport([FromBody] DeleteReportRequest req)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var report = await _context.Reports
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.ReportId == req.ReportId);

            if (report == null)
                return Json(new { success = false, message = "Report not found." });

            // Items are cascade-configured, but remove them explicitly to mirror the
            // delete pattern used elsewhere (e.g. DeleteAccount) and avoid relying on
            // the database-level cascade.
            if (report.Items.Count > 0)
                _context.ReportItems.RemoveRange(report.Items);

            _context.Reports.Remove(report);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Report deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }
}

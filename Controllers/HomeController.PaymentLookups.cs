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
    [HttpGet]
    public async Task<IActionResult> GetStudentsForPayment(string? q = null, string? semester = null, int? fullAmountId = null)
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {

            FullAmount? targetFee = null;

            if (fullAmountId.HasValue && fullAmountId.Value > 0)
            {
                targetFee = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .FirstOrDefaultAsync(f => f.FullAmountId == fullAmountId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(semester))
            {
                Semester? semFilter = semester.Equals("First", StringComparison.OrdinalIgnoreCase)
                    ? Semester.First
                    : semester.Equals("Second", StringComparison.OrdinalIgnoreCase)
                        ? Semester.Second
                        : null;

                var currentSchoolYear = await _context.SchoolYears
                    .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

                if (currentSchoolYear != null && semFilter.HasValue)
                {
                    targetFee = await _context.FullAmounts
                        .Include(f => f.SchoolYear)
                        .FirstOrDefaultAsync(f => f.SchoolYearId == currentSchoolYear.SchoolYearId
                                               && f.Semester     == semFilter.Value);
                }
            }

            var query = (q ?? string.Empty).Trim().ToLower();

            var users = await _context.Users
                .Include(u => u.Account)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.SchoolYear)
                .Where(u => u.Account != null
                         && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                         && u.Account.IsActive
                         && u.Account.RequestStatus == RequestStatus.Approved
                         && (string.IsNullOrEmpty(query)
                             || (u.Account.SchoolId != null && u.Account.SchoolId.ToLower().Contains(query))
                             || (u.FirstName != null && u.FirstName.ToLower().Contains(query))
                             || (u.LastName  != null && u.LastName.ToLower().Contains(query))))
                .ToListAsync();

            var paidUserIds = targetFee != null
                ? await _context.OrgFeePayments
                    .Where(p => p.FullAmountId == targetFee.FullAmountId
                             && p.PaymentStatus == PaymentStatus.Paid
                             && p.UserId != null)
                    .Select(p => p.UserId!.Value)
                    .ToListAsync()
                : new List<int>();

            var partialUserIds = targetFee != null
                ? await _context.OrgFeePayments
                    .Where(p => p.FullAmountId == targetFee.FullAmountId
                             && p.PaymentStatus == PaymentStatus.Partial
                             && p.UserId != null)
                    .Select(p => p.UserId!.Value)
                    .Distinct()
                    .ToListAsync()
                : new List<int>();

            var orgFeeExemptions = await GetAllExemptionsAsync();

            var students = users
                .Where(u => { orgFeeExemptions.TryGetValue(u.UserId, out var ex); return targetFee == null || FeeRules.IsFeeApplicableToStudent(u.AcademicProfile, targetFee, ex); })
                .Select(u => new {
                    userId      = u.UserId,
                    schoolId    = u.Account!.SchoolId,
                    name        = (u.LastName  ?? "") + ", " + (u.FirstName ?? "")
                                  + (!string.IsNullOrWhiteSpace(u.MiddleName)
                                      ? " " + u.MiddleName.Substring(0, 1) + "."
                                      : ""),
                    courseCode  = u.AcademicProfile != null && u.AcademicProfile.Course != null
                                  ? u.AcademicProfile.Course.CourseCode : "N/A",
                    yearSection = u.AcademicProfile != null
                                  ? (u.AcademicProfile.YearLevel != null
                                      ? u.AcademicProfile.YearLevel.ToString() : "")
                                + "-" + (u.AcademicProfile.Section ?? "")
                                  : "N/A",
                    // hasPaid is now specific to selected semester's fee
                    hasPaid = targetFee != null
                              && paidUserIds.Contains(u.UserId),
                    hasPartial = targetFee != null
                              && partialUserIds.Contains(u.UserId)
                              && !paidUserIds.Contains(u.UserId)
                })
                .OrderBy(s => s.name)
                .ToList();

            return Json(new { success = true, students });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentFeeStatus(int userId, int fullAmountId)
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var fee = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.FullAmountId == fullAmountId);

            if (fee == null)
                return Json(new { success = false, message = "Fee not found." });

            var totalPaid = await _context.OrgFeePayments
                .Where(p => p.UserId == userId && p.FullAmountId == fullAmountId)
                .SumAsync(p => p.Amount);

            var balance = Math.Max(0, fee.Amount - totalPaid);
            var status = totalPaid <= 0
                ? "Unpaid"
                : totalPaid >= fee.Amount
                    ? "Paid"
                    : "Partial";

            // Mirror the write-path rule: surface any unpaid PRIOR school-year balance so the
            // treasurer is blocked (and told why) before reaching Save, not after.
            var academicProfile = await _context.AcademicProfiles
                .Include(ap => ap.SchoolYear)
                .FirstOrDefaultAsync(ap => ap.UserId == userId);
            var exemptions = await GetExemptionsForUserAsync(userId);
            var unpaidPriorFee = await GetEarliestUnpaidPriorYearFeeAsync(userId, academicProfile, fee, exemptions);

            string? priorBlock = null;
            if (unpaidPriorFee != null)
            {
                var semLabel = unpaidPriorFee.Semester == Semester.First ? "1st" : "2nd";
                var yrLabel  = unpaidPriorFee.SchoolYear != null
                    ? $"{unpaidPriorFee.SchoolYear.YearStart}–{unpaidPriorFee.SchoolYear.YearEnd}"
                    : "a previous school year";
                priorBlock = $"{semLabel} Semester {yrLabel}";
            }

            return Json(new
            {
                success = true,
                totalPaid,
                balance,
                required = fee.Amount,
                status,
                priorBlock
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAvailableSemesters()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var fees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .OrderByDescending(f => f.SemesterStatus == SemesterStatus.Current) // Current first
                .ThenByDescending(f => f.SchoolYear.YearStart)
                .ThenByDescending(f => f.Semester)
                .Select(f => new {
                    fullAmountId   = f.FullAmountId,
                    schoolYearId   = f.SchoolYearId,
                    yearStart      = f.SchoolYear.YearStart,
                    yearEnd        = f.SchoolYear.YearEnd,
                    semester       = f.Semester.ToString(),
                    semesterStatus = f.SemesterStatus.ToString(),
                    amount         = f.Amount,
                    label          = $"{f.SchoolYear.YearStart}–{f.SchoolYear.YearEnd} · " +
                                     $"{(f.Semester == Semester.First ? "1st" : "2nd")} Semester · " +
                                     $"₱{f.Amount:N2} · {f.SemesterStatus}"
                })
                .ToListAsync();

            return Json(new { success = true, fees });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
public async Task<IActionResult> GetCollectableOrgFee(string? schoolYear = null)
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            // Determine which fees to total. If a school year is passed (e.g. "2025–2026"),
            // sum collectable across BOTH semesters of that year. Otherwise use the current active fee.
            List<FullAmount> targetFees;

            // In: GetCollectableOrgFee

            if (!string.IsNullOrWhiteSpace(schoolYear))
            {
                var parts = schoolYear.Replace("—", "–").Split('–');
                int.TryParse(parts.ElementAtOrDefault(0)?.Trim(), out var yStart);

                targetFees = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .Where(f => f.SchoolYear != null && f.SchoolYear.YearStart == yStart)
                    .ToListAsync();
            }
            else
            {
                // No year specified = "All School Years": total collectable across every year.
                targetFees = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .ToListAsync();
            }

            if (!targetFees.Any())
                return Json(new { success = true, collectable = 0 });

            // Only ENROLLED active students
            var students = await _context.Users
                .Include(u => u.Account)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.SchoolYear)
                .Where(u => u.Account != null
                         && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                         && u.Account.IsActive
                         && u.Account.RequestStatus == RequestStatus.Approved
                         && (u.AcademicProfile == null 
                             || u.AcademicProfile.AcademicStatus == AcademicStatus.Enrolled))
                .ToListAsync();

var feeIds = targetFees.Select(f => f.FullAmountId).ToList();

            // All payments toward any of the target fees
            var payments = await _context.OrgFeePayments
                .Where(p => feeIds.Contains(p.FullAmountId))
                .ToListAsync();

            var collectableExemptions = await GetAllExemptionsAsync();
            decimal totalCollectable = 0;
            var debtorIds = new HashSet<int>();

            // For each target fee, add up what each applicable student still owes on it.
            foreach (var fee in targetFees)
            {
                foreach (var student in students.Where(u => { collectableExemptions.TryGetValue(u.UserId, out var ex); return FeeRules.IsFeeApplicableToStudent(u.AcademicProfile, fee, ex); }))
                {
                    var totalPaid = payments
                        .Where(p => p.UserId == student.UserId && p.FullAmountId == fee.FullAmountId)
                        .Sum(p => p.Amount);

                    if (totalPaid < fee.Amount)
                    {
                        totalCollectable += fee.Amount - totalPaid;
                        debtorIds.Add(student.UserId);
                    }
                }
            }

            return Json(new { success = true, collectable = totalCollectable, membersCount = debtorIds.Count });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

}

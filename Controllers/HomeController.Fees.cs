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
    public async Task<IActionResult> GetSchoolYears()
    {
        try
        {
            var schoolYears = await _context.SchoolYears
                .OrderByDescending(sy => sy.YearStart)
                .ToListAsync();

            var feeRecords = await _context.FullAmounts
                .Where(f => f.Amount > 0)
                .Select(f => new { f.SchoolYearId, f.Semester })
                .ToListAsync();

            var result = schoolYears.Select(sy => new {
                sy.SchoolYearId,
                sy.YearStart,
                sy.YearEnd,
                yearStatus     = sy.YearStatus.ToString(),
                hasFirst       = sy.FirstSemStart != null || feeRecords.Any(f => f.SchoolYearId == sy.SchoolYearId && f.Semester == Semester.First),
                hasSecond      = sy.SecondSemStart != null || feeRecords.Any(f => f.SchoolYearId == sy.SchoolYearId && f.Semester == Semester.Second),
                firstSemStart  = (object?)sy.FirstSemStart,
                firstSemEnd    = (object?)sy.FirstSemEnd,
                secondSemStart = (object?)sy.SecondSemStart,
                secondSemEnd   = (object?)sy.SecondSemEnd,
            });

            return Json(new { success = true, schoolYears = result });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetSchoolYearDateRange(string schoolYear)
    {
        try
        {
            var parts = schoolYear.Replace("–", "-").Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var ys) || !int.TryParse(parts[1].Trim(), out var ye))
                return Json(new { success = false, message = "Invalid school year format." });

            var sy = await _context.SchoolYears
                .FirstOrDefaultAsync(s => s.YearStart == ys && s.YearEnd == ye);

            if (sy == null)
                return Json(new { success = false, message = "School year not found." });

            return Json(new
            {
                success = true,
                firstSemStart  = sy.FirstSemStart,
                firstSemEnd    = sy.FirstSemEnd,
                secondSemStart = sy.SecondSemStart,
                secondSemEnd   = sy.SecondSemEnd
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
    public async Task<IActionResult> AddSchoolYear([FromBody] AddSchoolYearRequest request)

    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            if (request.YearEnd != request.YearStart + 1)
                return Json(new { success = false, message = "Year end must be exactly year start + 1." });

            var duplicate = await _context.SchoolYears
                .AnyAsync(sy => sy.YearStart == request.YearStart && sy.YearEnd == request.YearEnd);
            if (duplicate)
                return Json(new { success = false, message = "This school year already exists." });

            var existing = await _context.SchoolYears.ToListAsync();
            foreach (var sy in existing)
                sy.YearStatus = YearStatus.Ended;

            _context.SchoolYears.Add(new SchoolYear
            {
                YearStart      = request.YearStart,
                YearEnd        = request.YearEnd,
                YearStatus     = YearStatus.Current,
                FirstSemStart  = request.FirstSemStart,
                FirstSemEnd    = request.FirstSemEnd,
                SecondSemStart = request.SecondSemStart,
                SecondSemEnd   = request.SecondSemEnd
            });

            // Auto-advance year level when a new school year starts.
            // Only currently ENROLLED students advance, up to year level 5, which
            // represents completion of the 4-year program (a level-5 student owes
            // no further fees — see FeeRules). Dropped students and those with no
            // year level are left untouched.
            const int completionYearLevel = 5;

            var enrolledStudents = await _context.AcademicProfiles
                .Where(ap => ap.AcademicStatus == AcademicStatus.Enrolled
                          && ap.YearLevel != null)
                .ToListAsync();

            foreach (var profile in enrolledStudents)
            {
                if (profile.YearLevel < completionYearLevel)
                    profile.YearLevel += 1;
            }

            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("school-years-changed");
            return Json(new { success = true, message = "School year added successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSchoolYear([FromBody] DeleteSchoolYearRequest request)

    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            var sy = await _context.SchoolYears
                .Include(s => s.FullAmounts)
                .FirstOrDefaultAsync(s => s.SchoolYearId == request.SchoolYearId);

            if (sy == null)
                return Json(new { success = false, message = "School year not found." });

            // Check for payments tied to this school year's fees
            var feeIds = sy.FullAmounts.Select(f => f.FullAmountId).ToList();
            var hasPayments = feeIds.Any() && await _context.OrgFeePayments
                .AnyAsync(p => feeIds.Contains(p.FullAmountId));

            if (hasPayments)
                return Json(new { success = false, message = "Cannot delete — this school year has existing payment records. Delete the payments first." });

            // Check for funds or expenses
            var hasFunds = await _context.OtherFunds
                .AnyAsync(f => f.SchoolYearId == request.SchoolYearId);

            var hasExpenses = await _context.Expenses
                .AnyAsync(e => e.SchoolYearId == request.SchoolYearId);

            if (hasFunds || hasExpenses)
                return Json(new { success = false, message = "Cannot delete — this school year has existing fund or expense records." });

            if (sy.FullAmounts.Any())
                _context.FullAmounts.RemoveRange(sy.FullAmounts);

            // If the CURRENT school year is being deleted, reverse the promotion that
            // happened when it was added: step enrolled students back one year level.
            // Old/ended years don't trigger this, since they didn't cause the latest promotion.
            if (sy.YearStatus == YearStatus.Current)
            {
                var profilesToRevert = await _context.AcademicProfiles
                    .Where(ap => ap.YearLevel != null
                              && ap.YearLevel > 1
                              && ap.AcademicStatus == AcademicStatus.Enrolled)
                    .ToListAsync();

                foreach (var profile in profilesToRevert)
                    profile.YearLevel -= 1;
            }

            _context.SchoolYears.Remove(sy);
            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("school-years-changed");

            return Json(new { success = true, message = "School year deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSchoolYearStatus([FromBody] SetSchoolYearStatusRequest request)

    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            if (request.Status == "Current")
            {
                var allYears = await _context.SchoolYears.ToListAsync();
                foreach (var sy in allYears)
                    sy.YearStatus = YearStatus.Ended;
            }

            var schoolYear = await _context.SchoolYears.FindAsync(request.SchoolYearId);
            if (schoolYear == null)
                return Json(new { success = false, message = "School year not found." });

            schoolYear.YearStatus = request.Status == "Current"
                ? YearStatus.Current
                : YearStatus.Ended;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = $"School year set as {request.Status}." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSchoolYearDates([FromBody] UpdateSchoolYearDatesRequest request)

    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            var sy = await _context.SchoolYears.FindAsync(request.SchoolYearId);
            if (sy == null)
                return Json(new { success = false, message = "School year not found." });

            sy.FirstSemStart  = request.FirstSemStart;
            sy.FirstSemEnd    = request.FirstSemEnd;
            sy.SecondSemStart = request.SecondSemStart;
            sy.SecondSemEnd   = request.SecondSemEnd;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Semester dates updated successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddCourse([FromBody] AddCourseRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            if (string.IsNullOrWhiteSpace(request.CourseCode))
                return Json(new { success = false, message = "Course code is required." });

            var existing = await _context.Courses
                .FirstOrDefaultAsync(c => c.CourseCode.ToLower() == request.CourseCode.ToLower());

            if (existing != null)
                return Json(new { success = false, message = "That course code already exists." });

            _context.Courses.Add(new Course {
                CourseCode = request.CourseCode.ToUpper(),
                CourseName = request.CourseName
            });

            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("courses-changed");
            return Json(new { success = true, message = "Course added successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteCourse([FromBody] DeleteCourseRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            var course = await _context.Courses
                .FirstOrDefaultAsync(c => c.CourseId == request.CourseId);

            if (course == null)
                return Json(new { success = false, message = "Course not found." });

            var inUse = await _context.AcademicProfiles
                .AnyAsync(ap => ap.CourseId == request.CourseId);

            if (inUse)
                return Json(new { success = false, message = "Cannot delete — students are currently assigned to this course." });

            _context.Courses.Remove(course);
            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("courses-changed");

            return Json(new { success = true, message = "Course deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetFees()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var fees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .Where(f => f.Amount > 0)
                .OrderByDescending(f => f.SchoolYear.YearStart)
                .ThenByDescending(f => f.Semester)
                .ToListAsync();

            var latestFirst = fees
                .Where(f => f.Semester == Semester.First)
                .OrderByDescending(f => f.SchoolYear.YearStart)
                .FirstOrDefault();

            var latestSecond = fees
                .Where(f => f.Semester == Semester.Second)
                .OrderByDescending(f => f.SchoolYear.YearStart)
                .FirstOrDefault();

            var result = fees.Select(f => new {
                f.FullAmountId,
                schoolYear     = f.SchoolYear.YearStart + " – " + f.SchoolYear.YearEnd,
                semester       = f.Semester.ToString(),
                amount         = f.Amount,
                semesterStatus = f.SemesterStatus.ToString(),
                // used by the summary cards: latest record per semester (not the "Current/Ended" status)
                isLatest       = f.FullAmountId == latestFirst?.FullAmountId ||
                                 f.FullAmountId == latestSecond?.FullAmountId
            });

            return Json(new { success = true, fees = result });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SetFeeAmount([FromBody] SetFeeAmountRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            if (request.Amount <= 0)
                return Json(new { success = false, message = "Amount must be greater than zero." });

            var semesterInput = (request.Semester ?? string.Empty).Trim();



            Semester semester;
            if (semesterInput.Equals("1st", StringComparison.OrdinalIgnoreCase) ||
                semesterInput.Equals("First", StringComparison.OrdinalIgnoreCase) ||
                semesterInput.Equals("1st Semester", StringComparison.OrdinalIgnoreCase))
            {
                semester = Semester.First;

            }
            else if (semesterInput.Equals("2nd", StringComparison.OrdinalIgnoreCase) ||
                     semesterInput.Equals("Second", StringComparison.OrdinalIgnoreCase) ||
                     semesterInput.Equals("2nd Semester", StringComparison.OrdinalIgnoreCase))
            {
                semester = Semester.Second;

            }
            else
            {

                return Json(new { success = false, message = $"Invalid semester value received: '{semesterInput}'" });
            }

            var sy = await _context.SchoolYears
                .FirstOrDefaultAsync(s => s.SchoolYearId == request.SchoolYearId);

            if (sy == null)
                return Json(new { success = false, message = "School year not found." });

            // Enforce exactly one Current semester overall.
            var currentFees = await _context.FullAmounts
                .Where(f => f.SemesterStatus == SemesterStatus.Current)
                .ToListAsync();
            foreach (var f in currentFees)
                f.SemesterStatus = SemesterStatus.Ended;

            // Upsert per (SchoolYearId, Semester) so we don't violate the unique key.
            var existing = await _context.FullAmounts.FirstOrDefaultAsync(f =>
                f.SchoolYearId == request.SchoolYearId && f.Semester == semester);

            if (existing != null)
            {
                existing.Amount = request.Amount;
                existing.SemesterStatus = SemesterStatus.Current;
            }
            else
            {
                _context.FullAmounts.Add(new FullAmount
                {
                    SchoolYearId   = request.SchoolYearId,
                    Semester       = semester,
                    Amount         = request.Amount,
                    SemesterStatus = SemesterStatus.Current
                });
            }

            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("fees-changed");
            return Json(new { success = true, message = "Fee amount set successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> ChangeAdminPassword([FromBody] ChangePasswordRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
                string.IsNullOrWhiteSpace(request.NewPassword) ||
                string.IsNullOrWhiteSpace(request.ConfirmPassword))
            {
                return Json(new { success = false, message = "All password fields are required." });
            }

            if (request.NewPassword != request.ConfirmPassword)
                return Json(new { success = false, message = "New password and confirmation do not match." });

            if (!IsPasswordCompliant(request.NewPassword, out var passwordPolicyError))
                return Json(new { success = false, message = passwordPolicyError ?? "Password does not meet policy requirements." });


            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var accountId))
                return Json(new { success = false, message = "Unable to determine your account. Please sign in again." });

            var account = await _context.Accounts.FindAsync(accountId);
            if (account == null)
                return Json(new { success = false, message = "Account not found." });

            if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, account.PasswordHash))
                return Json(new { success = false, message = "Current password is incorrect." });

            account.PasswordHash = AuthService.HashPassword(request.NewPassword);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Password changed successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFee([FromBody] DeleteFeeRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            var fee = await _context.FullAmounts
                .FirstOrDefaultAsync(f => f.FullAmountId == request.FullAmountId);

            if (fee == null)
                return Json(new { success = false, message = "Fee record not found." });

            // Block if payments exist
            var hasPayments = await _context.OrgFeePayments
                .AnyAsync(p => p.FullAmountId == request.FullAmountId);

            if (hasPayments)
                return Json(new { success = false, message = "Cannot delete — this semester has existing payment records." });

            var deletedWasCurrent = fee.SemesterStatus == SemesterStatus.Current;
            _context.FullAmounts.Remove(fee);
            await _context.SaveChangesAsync();

            if (deletedWasCurrent)
            {
                var nextCurrent = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .OrderByDescending(f => f.SchoolYear.YearStart)
                    .ThenByDescending(f => f.Semester)
                    .FirstOrDefaultAsync();

                if (nextCurrent != null)
                {
                    nextCurrent.SemesterStatus = SemesterStatus.Current;
                    await _context.SaveChangesAsync();
                }
            }

            await _sse.BroadcastAsync("fees-changed");
            return Json(new { success = true, message = "Fee record deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStudentPaymentStart([FromBody] SetStudentPaymentStartRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            if (!Enum.TryParse<Semester>(request.Semester, out var semester))
                return Json(new { success = false, message = "Invalid semester value." });

            var sy = await _context.SchoolYears.FindAsync(request.SchoolYearId);
            if (sy == null)
                return Json(new { success = false, message = "School year not found." });

            var profile = await _context.AcademicProfiles
                .FirstOrDefaultAsync(ap => ap.UserId == request.UserId);
            if (profile == null)
                return Json(new { success = false, message = "Student academic profile not found." });

            profile.SchoolYearId    = request.SchoolYearId;
            profile.SemesterEntered = semester;
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Payment start updated." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    // ----------------------------------------------------------------
    // STUDENT FEE EXEMPTIONS
    // ----------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> GetStudentExemptions(int userId)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var exemptions = await _context.StudentFeeExemptions
                .Include(e => e.SchoolYear)
                .Where(e => e.UserId == userId)
                .OrderBy(e => e.SchoolYear.YearStart)
                .ThenBy(e => e.Semester)
                .Select(e => new
                {
                    e.ExemptionId,
                    e.SchoolYearId,
                    yearLabel = $"{e.SchoolYear.YearStart}–{e.SchoolYear.YearEnd}",
                    semester  = e.Semester.ToString()
                })
                .ToListAsync();

            return Json(new { success = true, exemptions });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStudentExemption([FromBody] StudentExemptionRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            if (!Enum.TryParse<Semester>(request.Semester, out var semester))
                return Json(new { success = false, message = "Invalid semester." });

            var exists = await _context.StudentFeeExemptions
                .AnyAsync(e => e.UserId == request.UserId
                            && e.SchoolYearId == request.SchoolYearId
                            && e.Semester == semester);
            if (exists)
                return Json(new { success = false, message = "Exemption already exists for that semester." });

            _context.StudentFeeExemptions.Add(new StudentFeeExemption
            {
                UserId       = request.UserId,
                SchoolYearId = request.SchoolYearId,
                Semester     = semester
            });
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Exemption added." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveStudentExemption([FromBody] RemoveStudentExemptionRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var exemption = await _context.StudentFeeExemptions
                .FirstOrDefaultAsync(e => e.ExemptionId == request.ExemptionId);
            if (exemption == null)
                return Json(new { success = false, message = "Exemption not found." });

            _context.StudentFeeExemptions.Remove(exemption);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Exemption removed." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred." });
        }
    }

    // ----------------------------------------------------------------
    // RETURN FROM LEAVE
    // Re-enrols a Dropped student and, in one step, exempts every semester
    // between their last attended semester and the current term, so they are
    // not back-billed for the gap. See FeeRules: an Enrolled student owes every
    // semester from their entry point forward unless it carries an exemption.
    // ----------------------------------------------------------------

    /// <summary>
    /// All (SchoolYearId, Semester) slots ordered chronologically, used to
    /// figure out which semesters fall inside a leave window.
    /// </summary>
    private static List<(int schoolYearId, Semester semester)> EnumerateSemesterSlots(
        IEnumerable<SchoolYear> schoolYearsOrdered)
    {
        var slots = new List<(int, Semester)>();
        foreach (var sy in schoolYearsOrdered)
        {
            slots.Add((sy.SchoolYearId, Semester.First));
            slots.Add((sy.SchoolYearId, Semester.Second));
        }
        return slots;
    }

    [HttpGet]
    public async Task<IActionResult> GetReturnFromLeaveInfo(int userId)
    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        try
        {
            var profile = await _context.AcademicProfiles
                .FirstOrDefaultAsync(ap => ap.UserId == userId);
            if (profile == null)
                return Json(new { success = false, message = "Student not found." });

            var schoolYears = await _context.SchoolYears
                .OrderBy(sy => sy.YearStart)
                .Select(sy => new
                {
                    sy.SchoolYearId,
                    yearLabel = $"{sy.YearStart}–{sy.YearEnd}",
                    sy.YearStart
                })
                .ToListAsync();

            // The fixed "returns at" end: the canonical current term.
            var currentTerm = await _context.FullAmounts
                .Where(f => f.SemesterStatus == SemesterStatus.Current)
                .OrderByDescending(f => f.SchoolYear.YearStart)
                .ThenByDescending(f => f.Semester)
                .Select(f => new
                {
                    f.SchoolYearId,
                    semester  = f.Semester.ToString(),
                    yearLabel = $"{f.SchoolYear.YearStart}–{f.SchoolYear.YearEnd}"
                })
                .FirstOrDefaultAsync();

            // Default for the adjustable end: the most recent semester the
            // student actually paid for, falling back to their entry point.
            var lastAttended = await _context.OrgFeePayments
                .Where(p => p.UserId == userId)
                .OrderByDescending(p => p.PaymentDate)
                .Select(p => new
                {
                    schoolYearId = p.FullAmount.SchoolYearId,
                    semester     = p.FullAmount.Semester.ToString()
                })
                .FirstOrDefaultAsync();

            if (lastAttended == null && profile.SchoolYearId != null && profile.SemesterEntered != null)
                lastAttended = new
                {
                    schoolYearId = profile.SchoolYearId.Value,
                    semester     = profile.SemesterEntered.Value.ToString()
                };

            return Json(new
            {
                success = true,
                schoolYears,
                currentTerm,
                lastAttended
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnFromLeave([FromBody] ReturnFromLeaveRequest request)
    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        try
        {
            var profile = await _context.AcademicProfiles
                .FirstOrDefaultAsync(ap => ap.UserId == request.UserId);
            if (profile == null)
                return Json(new { success = false, message = "Student not found." });

            if (profile.AcademicStatus != AcademicStatus.Dropped)
                return Json(new { success = false, message = "Student is not on leave." });

            if (!Enum.TryParse<Semester>(request.LastAttendedSemester, out var lastSem))
                return Json(new { success = false, message = "Invalid semester." });

            // The server resolves the "returns at" end itself so it can't be tampered with.
            var currentTerm = await _context.FullAmounts
                .Where(f => f.SemesterStatus == SemesterStatus.Current)
                .OrderByDescending(f => f.SchoolYear.YearStart)
                .ThenByDescending(f => f.Semester)
                .Select(f => new { f.SchoolYearId, f.Semester })
                .FirstOrDefaultAsync();
            if (currentTerm == null)
                return Json(new { success = false, message = "No current term is set." });

            var schoolYears = await _context.SchoolYears
                .OrderBy(sy => sy.YearStart)
                .ToListAsync();
            var slots = EnumerateSemesterSlots(schoolYears);

            var lastIdx    = slots.IndexOf((request.LastAttendedSchoolYearId, lastSem));
            var currentIdx = slots.IndexOf((currentTerm.SchoolYearId, currentTerm.Semester));
            if (lastIdx < 0 || currentIdx < 0)
                return Json(new { success = false, message = "Selected semester is not a valid school year." });
            if (lastIdx >= currentIdx)
                return Json(new { success = false, message = "The last attended semester must come before the current term." });

            var existing = await _context.StudentFeeExemptions
                .Where(e => e.UserId == request.UserId)
                .Select(e => new { e.SchoolYearId, e.Semester })
                .ToListAsync();
            var existingSet = existing.Select(e => (e.SchoolYearId, e.Semester)).ToHashSet();

            // Exempt every semester strictly between the last attended one and
            // the current term (the gap). May be empty → just re-enrol.
            var created = 0;
            for (var i = lastIdx + 1; i <= currentIdx - 1; i++)
            {
                var (syId, sem) = slots[i];
                if (existingSet.Contains((syId, sem)))
                    continue;

                _context.StudentFeeExemptions.Add(new StudentFeeExemption
                {
                    UserId       = request.UserId,
                    SchoolYearId = syId,
                    Semester     = sem
                });
                created++;
            }

            profile.AcademicStatus = AcademicStatus.Enrolled;
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                exemptedCount = created,
                message = $"Welcomed back. {created} semester(s) exempted."
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred." });
        }
    }

}

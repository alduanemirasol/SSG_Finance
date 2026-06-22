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
    // TREASURER FINANCIAL MANAGEMENT
    // ----------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> GetCurrentSchoolYearAndSemester()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            var currentSemester = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.SemesterStatus == SemesterStatus.Current);

            if (currentSchoolYear == null)
                return Json(new { success = false, message = "No current school year found." });

            return Json(new
            {
                success = true,
                schoolYear = new
                {
                    currentSchoolYear.SchoolYearId,
                    currentSchoolYear.YearStart,
                    currentSchoolYear.YearEnd,
                    yearStatus = currentSchoolYear.YearStatus.ToString()
                },
                semester = currentSemester != null ? new
                {
                    currentSemester.FullAmountId,
                    currentSemester.SchoolYearId,
                    semester = currentSemester.Semester.ToString(),
                    amount = currentSemester.Amount,
                    semesterStatus = currentSemester.SemesterStatus.ToString()
                } : null
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetOrgFeePayments()
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            var payments = await _context.OrgFeePayments
                .Include(p => p.User)
                    .ThenInclude(u => u.AcademicProfile)
                        .ThenInclude(ap => ap.Course)
                .Include(p => p.User)
                    .ThenInclude(u => u.Account)
                .Include(p => p.FullAmount)
                    .ThenInclude(f => f.SchoolYear)
                .Include(p => p.Receipts)
                .OrderByDescending(p => p.PaymentDate)
                .Select(p => new
                {
                    p.PaymentId,
                    p.UserId,
                    studentName = p.User != null
                        ? $"{(p.User.LastName  != null ? p.User.LastName.ToUpper()  : "")}, " +
                          $"{(p.User.FirstName != null ? p.User.FirstName.ToUpper() : "")}"
                        : "Unknown",
                    schoolId = p.User != null && p.User.Account != null ? p.User.Account.SchoolId ?? "" : "",
                    courseCode = p.User != null && p.User.AcademicProfile != null && p.User.AcademicProfile.Course != null
                        ? p.User.AcademicProfile.Course.CourseCode : "N/A",
                    yearSection = p.User != null && p.User.AcademicProfile != null
                        ? $"{(p.User.AcademicProfile.YearLevel.HasValue ? p.User.AcademicProfile.YearLevel.Value.ToString() : "N/A")}" +
                          $"-{(p.User.AcademicProfile.Section ?? "N/A")}"
                        : "N/A",
                    // school year and semester now come from FullAmount
                    schoolYear     = p.FullAmount.SchoolYear != null
                        ? $"{p.FullAmount.SchoolYear.YearStart} – {p.FullAmount.SchoolYear.YearEnd}" : "N/A",
                    semester       = p.FullAmount.Semester.ToString(),
                    amountRequired = p.FullAmount.Amount,    // required amount lives in full_amount table
                    p.Amount,                                // amount actually paid
                    paymentStatus  = p.PaymentStatus.ToString(),
                    p.PaymentDate,
                    receiptNumber  = p.Receipts.FirstOrDefault() != null
                        ? p.Receipts.FirstOrDefault()!.ReceiptNumber : null
                })
                .ToListAsync();

            return Json(new { success = true, payments });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetRecentPayments()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {

            var recentPayments = await _context.OrgFeePayments
                .Include(p => p.User)
                    .ThenInclude(u => u.Account)
                .Include(p => p.User)
                    .ThenInclude(u => u.AcademicProfile)
                        .ThenInclude(ap => ap!.Course)
                .Where(p => p.PaymentStatus == PaymentStatus.Paid
                         || p.PaymentStatus == PaymentStatus.Partial)
                .OrderByDescending(p => p.PaymentDate)
                .Take(10)
                .ToListAsync();

            var result = recentPayments.Select(p => new
            {
                p.PaymentId,
                p.UserId,
                studentName = p.User != null
                    ? $"{(p.User.LastName ?? "").ToUpper()}, {(p.User.FirstName ?? "") }"
                      + (!string.IsNullOrWhiteSpace(p.User.MiddleName)
                          ? " " + p.User.MiddleName.Substring(0, 1) + "."
                          : "")
                    : "Unknown",
                schoolId = p.User?.Account?.SchoolId ?? "",
                courseCode = p.User?.AcademicProfile?.Course?.CourseCode ?? "N/A",
                yearSection = p.User?.AcademicProfile != null
                    ? $"{(p.User.AcademicProfile.YearLevel?.ToString() ?? "N/A")}-{(p.User.AcademicProfile.Section ?? "N/A")}"
                    : "N/A",
                amount = p.Amount,
                paymentStatus = p.PaymentStatus.ToString(),
                paymentDate = p.PaymentDate
            }).ToList();

            return Json(new { success = true, payments = result });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetProfessorStudentPayments()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {

            var users = await _context.Users
                .Include(u => u.Account)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.SchoolYear)
                .Where(u => u.Account != null
                         && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                         && u.Account.IsActive
                         && u.Account.RequestStatus == RequestStatus.Approved)
                .ToListAsync();

            var currentFee = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.SemesterStatus == SemesterStatus.Current);

            var payments = currentFee != null
                ? await _context.OrgFeePayments
                    .Where(p => p.FullAmountId == currentFee.FullAmountId)
                    .ToListAsync()
                : new List<OrgFeePayment>();

            var allExemptions = await GetAllExemptionsAsync();

            var result = users.Select(u =>
            {
                var studentPayments = payments
                    .Where(p => p.UserId == u.UserId)
                    .ToList();

                allExemptions.TryGetValue(u.UserId, out var userExemptions);
                var totalPaid = studentPayments.Sum(p => p.Amount);
                var isApplicable = currentFee != null && FeeRules.IsFeeApplicableToStudent(u.AcademicProfile, currentFee, userExemptions);
                var requiredAmount = isApplicable ? currentFee?.Amount ?? 0 : 0;
                var lastPayment = studentPayments.OrderByDescending(p => p.PaymentDate).FirstOrDefault();

                string status;
                if (!isApplicable)
                    status = "N/A";
                else if (totalPaid <= 0)
                    status = "Unpaid";
                else if (totalPaid >= requiredAmount)
                    status = "Paid";
                else
                    status = "Partial";

                return new
                {
                    userId = u.UserId,
                    schoolId = u.Account!.SchoolId,
                    name = $"{(u.LastName ?? "").ToUpper()}, {(u.FirstName ?? "") }"
                          + (!string.IsNullOrWhiteSpace(u.MiddleName)
                              ? " " + u.MiddleName.Substring(0, 1) + "."
                              : ""),
                    courseCode = u.AcademicProfile?.Course?.CourseCode ?? "N/A",
                    yearSection = u.AcademicProfile != null
                        ? $"{(u.AcademicProfile.YearLevel?.ToString() ?? "N/A")}-{(u.AcademicProfile.Section ?? "N/A")}"
                        : "N/A",
                    totalPaid,
                    requiredAmount,
                    status,
                    hasPaid = status == "Paid",
                    lastPaymentDate = lastPayment?.PaymentDate
                };
            })
            .OrderBy(s => s.name)
            .ToList();

            return Json(new { success = true, students = result });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    private static string ComputeOrgFeeStatus(decimal totalPaid, decimal requiredAmount)
    {
        if (requiredAmount <= 0) return "N/A";
        if (totalPaid <= 0) return "Unpaid";
        if (totalPaid >= requiredAmount) return "Paid";
        return "Partial";
    }

    private static object? BuildSemesterFeeSummary(FullAmount? fee, IEnumerable<OrgFeePayment> payments, bool isApplicable = true)
    {
        if (fee == null) return null;

        var totalPaid = payments.Sum(p => p.Amount);
        var requiredAmount = isApplicable ? fee.Amount : 0m;

        return new
        {
            requiredAmount,
            totalPaid,
            balance = Math.Max(0, requiredAmount - totalPaid),
            feeStatus = isApplicable ? ComputeOrgFeeStatus(totalPaid, requiredAmount) : "N/A",
            semesterStatus = fee.SemesterStatus.ToString()
        };
    }

    [HttpGet]
    public async Task<IActionResult> GetTreasurerStudentsWithFees(string? schoolYear = null)
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {

            var role     = HttpContext.Session.GetString("UserRole") ?? "";
            var allStudents = await GetStudentsAsync();
            // Non-admin staff (Treasurer, Professor) only see active students: enrolled
            // and not yet graduated. Year level 5 is the graduated/completed sentinel
            // (the year level is the part before the dash in "YearSection", e.g. "5-A"),
            // matching the isGraduated check used below for fee applicability.
            var students = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
                ? allStudents
                : allStudents
                    .Where(s => string.Equals(s.AcademicStatus, "Enrolled", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals((s.YearSection ?? "").Split('-')[0].Trim(), "5", StringComparison.Ordinal))
                    .ToList();

            SchoolYear? currentSchoolYear;

            if (!string.IsNullOrWhiteSpace(schoolYear))
            {
                // Parse "2025–2026" or "2025-2026"
                var parts = schoolYear.Replace("–", "-").Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var ys) && int.TryParse(parts[1].Trim(), out var ye))
                {
                    currentSchoolYear = await _context.SchoolYears
                        .FirstOrDefaultAsync(sy => sy.YearStart == ys && sy.YearEnd == ye);
                }
                else
                {
                    currentSchoolYear = await _context.SchoolYears
                        .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);
                }
            }
            else
            {
                currentSchoolYear = await _context.SchoolYears
                    .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);
            }

            var semesterFees = currentSchoolYear != null
                ? await _context.FullAmounts
                    .Where(f => f.SchoolYearId == currentSchoolYear.SchoolYearId)
                    .ToListAsync()
                : new List<FullAmount>();

            var firstSemFee  = semesterFees.FirstOrDefault(f => f.Semester == Semester.First);
            var secondSemFee = semesterFees.FirstOrDefault(f => f.Semester == Semester.Second);

            var feeIds = semesterFees.Select(f => f.FullAmountId).ToList();
            var allSemPayments = feeIds.Count > 0
                ? await _context.OrgFeePayments
                    .Include(p => p.Receipts)
                    .Where(p => feeIds.Contains(p.FullAmountId))
                    .ToListAsync()
                : new List<OrgFeePayment>();

            var schoolYearLabel = currentSchoolYear != null
                ? $"{currentSchoolYear.YearStart}–{currentSchoolYear.YearEnd}"
                : "N/A";

            var studentsPageExemptions = await GetAllExemptionsAsync();

            var result = students.Select(s =>
            {
                var studentPayments = allSemPayments.Where(p => p.UserId == s.StudentId).ToList();
                studentsPageExemptions.TryGetValue(s.StudentId, out var sExemptions);
                // Dropped and graduated (year level 5) students no longer owe any
                // organizational fees. The scalar IsFeeApplicableToStudent overload
                // used below only does the term math and doesn't know the student's
                // status or year level, so both are guarded here (this mirrors the
                // AcademicProfile overload in FeeRules, which checks YearLevel == 5).
                var isGraduated = string.Equals((s.YearSection ?? "").Split('-')[0].Trim(), "5", StringComparison.Ordinal);
                var isInactive  = isGraduated
                    || string.Equals(s.AcademicStatus, "Dropped", StringComparison.OrdinalIgnoreCase);
                var firstApplicable = !isInactive && firstSemFee != null
                    && FeeRules.IsFeeApplicableToStudent(s.SchoolYearId, s.SemesterEntered, firstSemFee, sExemptions);
                var secondApplicable = !isInactive && secondSemFee != null
                    && FeeRules.IsFeeApplicableToStudent(s.SchoolYearId, s.SemesterEntered, secondSemFee, sExemptions);

                var firstPayments = firstSemFee != null
                    ? studentPayments.Where(p => p.FullAmountId == firstSemFee.FullAmountId).ToList()
                    : new List<OrgFeePayment>();
                var secondPayments = secondSemFee != null
                    ? studentPayments.Where(p => p.FullAmountId == secondSemFee.FullAmountId).ToList()
                    : new List<OrgFeePayment>();

                var firstPaid = firstPayments.Sum(p => p.Amount);
                var secondPaid = secondPayments.Sum(p => p.Amount);

                // Most recent payment across both semesters, used by the UI to stack
                // the latest-paid students at the top of the list by default.
                DateTime? lastPaymentDate = studentPayments.Count > 0
                    ? studentPayments.Max(p => p.PaymentDate)
                    : (DateTime?)null;

                // A semester can carry more than one receipt (e.g. two partial payments),
                // mirroring the per-payment receipts shown in the org-fee modal.
                var firstReceipts = firstPayments
                    .SelectMany(p => p.Receipts
                        .Where(r => !string.IsNullOrWhiteSpace(r.ReceiptNumber))
                        .Select(r => new { paymentId = p.PaymentId, receiptNumber = r.ReceiptNumber }))
                    .ToList();
                var secondReceipts = secondPayments
                    .SelectMany(p => p.Receipts
                        .Where(r => !string.IsNullOrWhiteSpace(r.ReceiptNumber))
                        .Select(r => new { paymentId = p.PaymentId, receiptNumber = r.ReceiptNumber }))
                    .ToList();

                var firstLatest  = firstPayments .OrderByDescending(p => p.PaymentDate).FirstOrDefault();
                var secondLatest = secondPayments.OrderByDescending(p => p.PaymentDate).FirstOrDefault();

                return new
                {
                    userId = s.StudentId,
                    accountId = s.AccountId,
                    schoolId = s.SchoolId,
                    name = s.FullName,
                    courseCode = s.CourseCode,
                    yearSection = s.YearSection,
                    avatarPath = s.AvatarPath ?? "",
                    role = s.Role,
                    isActive = s.IsActive,
                    academicStatus = s.AcademicStatus,
                    schoolYear = schoolYearLabel,
                    firstSemStatus  = firstApplicable ? ComputeOrgFeeStatus(firstPaid,  firstSemFee?.Amount  ?? 0) : "N/A",
                    secondSemStatus = secondApplicable ? ComputeOrgFeeStatus(secondPaid, secondSemFee?.Amount ?? 0) : "N/A",
                    firstSemPaid    = firstPaid,
                    secondSemPaid   = secondPaid,
                    firstSemRequired  = firstApplicable ? firstSemFee?.Amount  ?? 0 : 0,
                    secondSemRequired = secondApplicable ? secondSemFee?.Amount ?? 0 : 0,
                    firstSemIsCurrent  = firstSemFee  != null && firstSemFee.SemesterStatus  == SemesterStatus.Current,
                    secondSemIsCurrent = secondSemFee != null && secondSemFee.SemesterStatus == SemesterStatus.Current,
                    firstSemReceipts  = firstReceipts,
                    secondSemReceipts = secondReceipts,
                    lastPaymentDate,
                    firstSemLastPayDate       = firstLatest?.PaymentDate,
                    secondSemLastPayDate      = secondLatest?.PaymentDate,
                    firstSemLatestPaymentId   = firstLatest?.PaymentId,
                    secondSemLatestPaymentId  = secondLatest?.PaymentId,
                    firstSemFullAmount        = firstApplicable  ? firstSemFee?.Amount  ?? 0 : 0,
                    secondSemFullAmount       = secondApplicable ? secondSemFee?.Amount ?? 0 : 0,
                    schoolYearId              = s.SchoolYearId,
                    semesterEntered           = s.SemesterEntered?.ToString(),
                };
            }).ToList();

            return Json(new { success = true, students = result, schoolYear = schoolYearLabel });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentOrgFeeDetails(int userId)
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var student = await _context.Users
                .Include(u => u.Account)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
                .FirstOrDefaultAsync(u => u.UserId == userId
                                       && u.Account != null
                                       && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                                       && u.Account.RequestStatus == RequestStatus.Approved);

            if (student == null)
                return Json(new { success = false, message = "Student not found." });

            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            // Pull ALL fees across every school year, then filter to the ones
            // that actually apply to this student (respecting SchoolYearId + SemesterEntered).
            var allFees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .ToListAsync();

            var exemptions     = await GetExemptionsForUserAsync(userId);
            var applicableFees = allFees
                .Where(f => FeeRules.IsFeeApplicableToStudent(student.AcademicProfile, f, exemptions))
                .OrderBy(f => f.SchoolYear != null ? f.SchoolYear.YearStart : 0)
                .ThenBy(f => FeeRules.GetSemesterOrder(f.Semester))
                .ToList();

            var payments = await _context.OrgFeePayments
                .Include(p => p.FullAmount)
                    .ThenInclude(f => f.SchoolYear)
                .Include(p => p.Receipts)
                .Where(p => p.UserId == userId)
                .OrderByDescending(p => p.PaymentDate)
                .ToListAsync();

            var schoolYearLabel = currentSchoolYear != null
                ? $"{currentSchoolYear.YearStart}–{currentSchoolYear.YearEnd}"
                : "N/A";

            // Build one row per applicable fee across all years.
            // Include fees that still have a balance OR belong to the current year,
            // so the table shows current-year status plus any prior-year unpaid carryover.
            var fees = applicableFees
                .Select(f =>
                {
                    var feePayments = payments.Where(p => p.FullAmountId == f.FullAmountId).ToList();
                    var totalPaid = feePayments.Sum(p => p.Amount);
                    var balance = Math.Max(0, f.Amount - totalPaid);
                    var isCurrentYear = currentSchoolYear != null && f.SchoolYearId == currentSchoolYear.SchoolYearId;
                    return new
                    {
                        schoolYear = f.SchoolYear != null
                            ? $"{f.SchoolYear.YearStart}–{f.SchoolYear.YearEnd}"
                            : "N/A",
                        semester = f.Semester.ToString(),
                        requiredAmount = f.Amount,
                        totalPaid,
                        balance,
                        feeStatus = ComputeOrgFeeStatus(totalPaid, f.Amount),
                        isCurrentYear,
                        // True only for the live school-year + live semester, so the modal
                        // can float the current term to the very top of the list.
                        isCurrent = isCurrentYear && f.SemesterStatus == SemesterStatus.Current
                    };
                })
                .Where(x => x.balance > 0 || x.isCurrentYear)
                .ToList();

            var transactions = payments.Select(p => new
            {
                paymentId = p.PaymentId,
                date = p.PaymentDate,
                amount = p.Amount,
                paymentStatus = p.PaymentStatus.ToString(),
                receiptNumber = p.Receipts.FirstOrDefault()?.ReceiptNumber,
                hasReceipt = p.Receipts.Any(r => !string.IsNullOrWhiteSpace(r.ReceiptNumber)),
                schoolYear = p.FullAmount.SchoolYear != null
                    ? $"{p.FullAmount.SchoolYear.YearStart}–{p.FullAmount.SchoolYear.YearEnd}"
                    : "N/A",
                semester = p.FullAmount.Semester.ToString(),
                amountRequired = p.FullAmount.Amount
            }).ToList();

            return Json(new
            {
                success = true,
                student = new
                {
                    userId = student.UserId,
                    schoolId = student.Account!.SchoolId,
                    name = student.LastName != null && student.FirstName != null
                        ? $"{student.LastName.ToUpper()}, {student.FirstName.ToUpper()}"
                          + (!string.IsNullOrWhiteSpace(student.MiddleName) ? " " + student.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                        : "N/A",
                    courseCode = student.AcademicProfile?.Course?.CourseCode ?? "N/A",
                    yearSection = student.AcademicProfile != null
                        ? $"{(student.AcademicProfile.YearLevel?.ToString() ?? "N/A")}-{(student.AcademicProfile.Section ?? "N/A")}"
                        : "N/A",
                    role = student.Account.Role.ToString()
                },
                schoolYear = schoolYearLabel,
                fees,
                transactions
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetOrgFeeReceipt(int paymentId)
    {
        try
        {
            var role = HttpContext.Session.GetString("UserRole");
            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (string.IsNullOrWhiteSpace(role) || !int.TryParse(accountIdStr, out var accountId))
                return Json(new { success = false, message = "Unauthorized." });

            var studentUserId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrWhiteSpace(studentUserId) || !int.TryParse(studentUserId, out var requesterUserId))
                return Json(new { success = false, message = "Unauthorized." });

            var payment = await _context.OrgFeePayments
                .Include(p => p.User)
                    .ThenInclude(u => u.Account)
                .Include(p => p.User)
                    .ThenInclude(u => u.AcademicProfile)
                        .ThenInclude(ap => ap.Course)
                .Include(p => p.FullAmount)
                    .ThenInclude(f => f.SchoolYear)
                .Include(p => p.Receipts)
                .FirstOrDefaultAsync(p => p.PaymentId == paymentId);

            if (payment == null)
                return Json(new { success = false, message = "Receipt not available." });

            // Access control: staff can view any; a student may only view their own.
            var isStaff =
                string.Equals(role, UserRole.Admin.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, UserRole.Treasurer.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, UserRole.Professor.ToString(), StringComparison.OrdinalIgnoreCase);

            if (!isStaff && payment.UserId != requesterUserId)
                return Json(new { success = false, message = "Receipt not available." });

            var receipt = payment.Receipts.OrderBy(r => r.ReceiptId).FirstOrDefault();
            if (receipt == null || string.IsNullOrWhiteSpace(receipt.ReceiptNumber))
                return Json(new { success = false, message = "Receipt not available." });

            var semester = payment.FullAmount?.Semester.ToString() ?? "";
            var schoolYear = payment.FullAmount?.SchoolYear != null
                ? $"{payment.FullAmount.SchoolYear.YearStart}–{payment.FullAmount.SchoolYear.YearEnd}"
                : "";

            // Students get the minimal payload.
            if (!isStaff)
            {
                return Json(new
                {
                    success = true,
                    receipt = new
                    {
                        receiptNumber = receipt.ReceiptNumber,
                        issueDate = payment.PaymentDate,
                        amount = payment.Amount,
                        status = payment.PaymentStatus.ToString(),
                        semester,
                        schoolYear
                    }
                });
            }

            // Staff get the full payload needed to render the printable receipt.
            var studentName = payment.User?.LastName != null && payment.User?.FirstName != null
                ? $"{payment.User.LastName}, {payment.User.FirstName}"
                : (payment.User?.FirstName ?? "");
            var studentSchoolId = payment.User?.Account?.SchoolId ?? "";
            var courseCode = payment.User?.AcademicProfile?.Course?.CourseCode ?? "";

            // Prefer the year level/section captured at the time of payment so the receipt
            // reflects the student's standing then, not now. Fall back to the current
            // profile only for old payments that have no snapshot. Level 5 = "4 Completed".
            int? frozenYear = payment.YearLevelAtPayment ?? payment.User?.AcademicProfile?.YearLevel;
            string frozenSection = payment.SectionAtPayment
                                   ?? payment.User?.AcademicProfile?.Section
                                   ?? "";
            string yearSection;
            if (frozenYear == 5)
                yearSection = "4 Completed";
            else
                yearSection = $"{(frozenYear?.ToString() ?? "")}-{frozenSection}".Trim('-');

            // IssuedBy stores the treasurer's account_id.
            string issuedByName = "Treasurer";
            string? signatureData = null;
            if (receipt.IssuedBy != 0)
            {
                var issuerUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.AccountId == receipt.IssuedBy);
                if (issuerUser != null)
                {
                    issuedByName = $"{issuerUser.FirstName ?? ""} {issuerUser.LastName ?? ""}".Trim();
                    if (string.IsNullOrWhiteSpace(issuedByName)) issuedByName = "Treasurer";
                }

                var signature = await GetActiveTreasurerSignatureAsync(receipt.IssuedBy);
                signatureData = signature?.SignatureData;
            }

            return Json(new
            {
                success = true,
                receipt = new
                {
                    receiptNumber = receipt.ReceiptNumber,
                    issueDate = payment.PaymentDate,
                    paymentDate = payment.PaymentDate,
                    amount = payment.Amount,
                    status = payment.PaymentStatus.ToString(),
                    semester,
                    schoolYear,
                    studentName,
                    studentId = studentSchoolId,
                    course = courseCode,
                    yearSection,
                    issuedByName,
                    signatureData
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "Receipt retrieval failed." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetOtherFunds()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var funds = await _context.OtherFunds
                .Include(f => f.Receiver)
                    .ThenInclude(r => r.User)
                .Include(f => f.SchoolYear)
                .OrderByDescending(f => f.ReceivedDate)
                .Select(f => new
                {
                    f.FundId,
                    f.Source,
                    f.Description,
                    f.Category,
                    f.Amount,
                    f.ReceivedDate,
                    schoolYear = f.SchoolYear != null
                        ? $"{f.SchoolYear.YearStart} – {f.SchoolYear.YearEnd}"
                        : null,
                    receivedBy = f.Receiver != null && f.Receiver.User != null
                        ? $"{(f.Receiver.User.LastName ?? "").ToUpper()}, {(f.Receiver.User.FirstName ?? "").ToUpper()}"
                          + (!string.IsNullOrWhiteSpace(f.Receiver.User.MiddleName) ? " " + f.Receiver.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                        : "Unknown"
                })
                .ToListAsync();

            return Json(new { success = true, funds });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetExpenses()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {
            var expenses = await _context.Expenses
                .Include(e => e.Recorder)
                    .ThenInclude(r => r.User)
                .Include(e => e.SchoolYear)
                .OrderByDescending(e => e.ExpenseDate)
                .Select(e => new
                {
                    e.ExpenseId,
                    e.Description,
                    e.Amount,
                    e.ExpenseDate,
                    schoolYear = e.SchoolYear != null
                        ? $"{e.SchoolYear.YearStart} – {e.SchoolYear.YearEnd}"
                        : null,
                    recordedBy = e.Recorder != null && e.Recorder.User != null
                        ? $"{(e.Recorder.User.LastName != null ? e.Recorder.User.LastName.ToUpper() : "")}, {(e.Recorder.User.FirstName != null ? e.Recorder.User.FirstName.ToUpper() : "")}"
                          + (!string.IsNullOrWhiteSpace(e.Recorder.User.MiddleName) ? " " + e.Recorder.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                        : "Unknown",
                    images = e.Images.OrderBy(i => i.ImageId).Select(i => i.ImagePath).ToList()
                })
                .ToListAsync();

            return Json(new { success = true, expenses });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetTreasurerDashboardStats()
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

        try
        {

            var orgFeeTotal = await _context.OrgFeePayments
                .SumAsync(p => p.Amount);

            var otherFundsTotal = await _context.OtherFunds
                .SumAsync(f => f.Amount);

            var expensesTotal = await _context.Expenses
                .SumAsync(e => e.Amount);

            var totalIncome = orgFeeTotal + otherFundsTotal;
            var balance = totalIncome - expensesTotal;

            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            var currentSemester = await _context.FullAmounts
                .FirstOrDefaultAsync(f => f.SemesterStatus == SemesterStatus.Current);

            var recentTransactions = new List<object>();

            var recentPayments = await _context.OrgFeePayments
                .Include(p => p.User)
                .OrderByDescending(p => p.PaymentDate)
                .Take(5)
                .Select(p => new
                {
                    type = "income",
                    category = "Org Fee",
                    description = p.User != null
                        ? $"Org Fee – {(p.User.LastName != null ? p.User.LastName.ToUpper() : "")}, {(p.User.FirstName != null ? p.User.FirstName.ToUpper() : "")}"
                          + (!string.IsNullOrWhiteSpace(p.User.MiddleName) ? " " + p.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                        : "Org Fee",
                    amount = p.Amount,
                    date = p.PaymentDate,
                    receipt = p.Receipts.FirstOrDefault() != null ? p.Receipts.FirstOrDefault().ReceiptNumber : "—"
                })
                .ToListAsync();

            var recentFunds = await _context.OtherFunds
                .OrderByDescending(f => f.ReceivedDate)
                .Take(5)
                .Select(f => new
                {
                    type = "income",
                    category = "Other Fund",
                    description = f.Description ?? f.Source ?? "Other Fund",
                    amount = f.Amount,
                    date = f.ReceivedDate,
                    receipt = "—"
                })
                .ToListAsync();

            var recentExpenses = await _context.Expenses
                .OrderByDescending(e => e.ExpenseDate)
                .Take(5)
                .Select(e => new
                {
                    type = "expense",
                    category = "Expense",
                    description = e.Description ?? "Expense",
                    amount = e.Amount,
                    date = e.ExpenseDate,
                    receipt = "—"
                })
                .ToListAsync();

            recentTransactions.AddRange(recentPayments);
            recentTransactions.AddRange(recentFunds);
            recentTransactions.AddRange(recentExpenses);
            recentTransactions = recentTransactions.OrderByDescending(t => ((DateTime)(t.GetType().GetProperty("date")?.GetValue(t) ?? DateTime.MinValue))).Take(6).ToList();

            return Json(new
            {
                success = true,
                stats = new
                {
                    totalIncome,
                    orgFeeTotal,
                    otherFundsTotal,
                    expensesTotal,
                    balance,
                    expenseCount = await _context.Expenses.CountAsync(),
                    largestExpense = await _context.Expenses.AnyAsync()
                        ? await _context.Expenses.MaxAsync(e => e.Amount)
                        : 0
                },
                schoolYear = currentSchoolYear != null
                    ? $"{currentSchoolYear.YearStart} – {currentSchoolYear.YearEnd}"
                    : "Not Set",
                semester = currentSemester != null
                    ? (currentSemester.Semester == Semester.First ? "1st" : "2nd")
                    : "Not Set",
                recentTransactions
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

}

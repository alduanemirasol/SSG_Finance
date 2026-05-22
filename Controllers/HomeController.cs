using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using MyMvcApp.Models;
using MyMvcApp.Services;
using MyMvcApp.Data;

namespace MyMvcApp.Controllers;

public class HomeController : Controller
{
    private readonly IAuthService _authService;
    private readonly IEmailService _emailService;
    private readonly ApplicationDbContext _context;

    public HomeController(IAuthService authService, IEmailService emailService, ApplicationDbContext context)
    {
        _authService = authService;
        _emailService = emailService;
        _context = context;
    }

    // ----------------------------------------------------------------
    // PUBLIC PAGES
    // ----------------------------------------------------------------

    public IActionResult Index()
    {
        // Redirect already-logged-in users away from the login page
        if (HttpContext.Session.GetString("UserId") != null)
        {
            return RedirectToDashboard(HttpContext.Session.GetString("UserRole"));
        }

        ViewBag.OpenLoginOnLoad = true;
        return View();
    }

    public IActionResult About()
    {
        return View();
    }

    public IActionResult Contacts()
    {
        return View();
    }

    public IActionResult Login()
    {
        if (HttpContext.Session.GetString("UserId") != null)
        {
            return RedirectToDashboard(HttpContext.Session.GetString("UserRole"));
        }
        // Login UI lives on Index (overlay); there is no separate Login.cshtml view.
        return RedirectToAction(nameof(Index), new { login = 1 });
    }

    public IActionResult Privacy()
    {
        return View();
    }

    private static int GetSemesterOrder(Semester semester)
    {
        return semester == Semester.First ? 1 : 2;
    }

    private static bool IsFeeApplicableToStudent(AcademicProfile? academicProfile, FullAmount fee)
    {
        if (academicProfile?.SchoolYearId == null || academicProfile.SemesterEntered == null)
            return true;

        var entrySchoolYearId = academicProfile.SchoolYearId.Value;

        if (fee.SchoolYearId == entrySchoolYearId)
        {
            return GetSemesterOrder(fee.Semester) >= GetSemesterOrder(academicProfile.SemesterEntered.Value);
        }

        if (academicProfile.SchoolYear != null && fee.SchoolYear != null)
        {
            if (fee.SchoolYear.YearStart != academicProfile.SchoolYear.YearStart)
                return fee.SchoolYear.YearStart > academicProfile.SchoolYear.YearStart;

            return fee.SchoolYear.YearEnd >= academicProfile.SchoolYear.YearEnd;
        }

        return fee.SchoolYearId > entrySchoolYearId;
    }

    private static bool IsFeeApplicableToStudent(int? schoolYearId, Semester? semesterEntered, FullAmount fee)
    {
        if (schoolYearId == null || semesterEntered == null)
            return true;

        if (fee.SchoolYearId == schoolYearId.Value)
            return GetSemesterOrder(fee.Semester) >= GetSemesterOrder(semesterEntered.Value);

        return fee.SchoolYearId > schoolYearId.Value;
    }

    // ----------------------------------------------------------------
    // DASHBOARDS — role-guarded
    // ----------------------------------------------------------------

    public async Task<IActionResult> Dashboard()
    {
        var role = HttpContext.Session.GetString("UserRole");
        if (string.IsNullOrEmpty(role))
            return RedirectToAction("Login", "Home");

        if (role == "Admin")     return RedirectToAction("AdminDashboard",     "Home");
        if (role == "Treasurer") return RedirectToAction("TreasurerDashboard", "Home");
        if (role == "Professor") return RedirectToAction("ProfessorDashboard", "Home");

        var userIdStr = HttpContext.Session.GetString("UserId");
        if (!int.TryParse(userIdStr, out var userId))
            return RedirectToAction("Login", "Home");

        var paymentHistory = new List<StudentPaymentHistoryViewModel>();

        try
        {
            var academicProfile = await _context.AcademicProfiles
                .Include(ap => ap.SchoolYear)
                .FirstOrDefaultAsync(ap => ap.UserId == userId);

            // 1. Get ALL FullAmount records (all school years and semesters)
            var allFees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .OrderBy(f => f.SchoolYear.YearStart)
                .ThenBy(f => f.Semester)
                .ToListAsync();

            allFees = allFees
                .Where(f => IsFeeApplicableToStudent(academicProfile, f))
                .ToList();

            // 2. Get all payments for this student across all fees
            var studentPayments = await _context.OrgFeePayments
                .Include(p => p.FullAmount)
                    .ThenInclude(fa => fa.SchoolYear)
                .Include(p => p.Receipts)
                .Where(p => p.UserId == userId)
                .ToListAsync();

            // 3. Create payment history for each fee
            foreach (var fee in allFees)
            {
                var feePayments = studentPayments
                    .Where(p => p.FullAmountId == fee.FullAmountId)
                    .OrderBy(p => p.PaymentDate)
                    .ThenBy(p => p.PaymentId)
                    .ToList();

                var amountPaid = feePayments.Sum(p => p.Amount);
                var balance = fee.Amount - amountPaid;

                var status = amountPaid == 0                  ? "Unpaid"
                           : amountPaid >= fee.Amount          ? "Paid"
                                                              : "Partial";

                // Create payment entries: one for the total fee plus each recorded transaction.
                var payments = new List<StudentPaymentViewModel>
                {
                    new StudentPaymentViewModel
                    {
                        Semester = fee.Semester.ToString(),
                        Course   = "Org Fee",
                        Amount   = fee.Amount,
                        Status   = "Total"
                    }
                };

                // Add each payment transaction so receipts can line up by payment id.
                foreach (var paymentRecord in feePayments)
                {
                    // Directly query the Users table using ReceivedBy (treasurer's UserId)
                    var treasurerUser = await _context.Users
                        .FirstOrDefaultAsync(u => u.UserId == paymentRecord.ReceivedBy);
                    var treasurerName = treasurerUser != null
                        ? $"{treasurerUser.FirstName} {treasurerUser.LastName}".Trim()
                        : "";

                    var receiptNumber = "";
                    var receiptIssueDate = (DateTime?)paymentRecord.PaymentDate;
                    var receiptIssuedBy = treasurerName;
                    var receiptIssuedByAccountId = paymentRecord.ReceivedBy;

                    if (paymentRecord.Receipts != null && paymentRecord.Receipts.Any())
                    {
                        var receipt = paymentRecord.Receipts.OrderBy(r => r.ReceiptId).FirstOrDefault();
                        receiptNumber = receipt?.ReceiptNumber ?? "";

                        // Directly query using IssuedBy (also a UserId pointing to the treasurer)
                        if (receipt != null)
                        {
                            receiptIssuedByAccountId = receipt.IssuedBy;
                            var issuerUser = await _context.Users
                                .FirstOrDefaultAsync(u => u.UserId == receipt.IssuedBy);
                            receiptIssuedBy = issuerUser != null
                                ? $"{issuerUser.FirstName ?? ""} {issuerUser.LastName ?? ""}".Trim()
                                : "";
                        }
                    }
                    
                    payments.Add(new StudentPaymentViewModel
                    {
                        PaymentId = paymentRecord.PaymentId,
                        Semester = fee.Semester.ToString(),
                        Course   = "Org Fee",
                        Amount   = paymentRecord.Amount,
                        Status   = paymentRecord.PaymentStatus.ToString(),
                        PaymentDate = paymentRecord.PaymentDate,
                        TreasurerName = treasurerName,
                        ReceiptNumber = receiptNumber,
                        ReceiptIssueDate = receiptIssueDate,
                        ReceiptIssuedBy = receiptIssuedBy,
                        ReceiptIssuedByAccountId = receiptIssuedByAccountId
                    });
                }

                paymentHistory.Add(new StudentPaymentHistoryViewModel
                {
                    Term          = $"{fee.SchoolYear.YearStart}–{fee.SchoolYear.YearEnd}",
                    Semester      = fee.Semester.ToString(),
                    Course        = "Org Fee",
                    OverallStatus = status,
                    Payments      = payments
                });
            }
        }
        catch
        {
            paymentHistory = new List<StudentPaymentHistoryViewModel>();
        }

        // Fetch receipt data for this student
        var receipts = new List<StudentReceiptViewModel>();
        try
        {
            receipts = await _context.Receipts
                .Include(r => r.Payment)
                    .ThenInclude(p => p.FullAmount)
                        .ThenInclude(fa => fa.SchoolYear)
                .Include(r => r.Payment)
                    .ThenInclude(p => p.User)
                .Where(r => r.Payment != null && r.Payment.UserId == userId)
                .Select(r => new StudentReceiptViewModel
                {
                    ReceiptId = r.ReceiptId,
                    ReceiptNumber = r.ReceiptNumber,
                    PaymentId = r.PaymentId.HasValue ? r.PaymentId.Value : 0,
                    IssueDate = r.Payment != null ? r.Payment.PaymentDate : null,
                    IssuedByAccountId = r.IssuedBy,
                    IssuedByName = _context.Users
                        .Where(u => u.UserId == r.IssuedBy)
                        .Select(u => u.FirstName + " " + u.LastName)
                        .FirstOrDefault() ?? "",
                    StudentName = r.Payment != null && r.Payment.User != null 
                        ? $"{r.Payment.User.FirstName} {r.Payment.User.LastName}".Trim() 
                        : "",
                    StudentId = r.Payment != null && r.Payment.User != null && r.Payment.User.Account != null
                        ? r.Payment.User.Account.SchoolId 
                        : "",
                    Amount = r.Payment != null ? r.Payment.Amount : 0,
                    Term = r.Payment != null && r.Payment.FullAmount != null && r.Payment.FullAmount.SchoolYear != null
                        ? $"{r.Payment.FullAmount.SchoolYear.YearStart}–{r.Payment.FullAmount.SchoolYear.YearEnd}"
                        : "",
                    Semester = r.Payment != null && r.Payment.FullAmount != null ? r.Payment.FullAmount.Semester.ToString() : "",
                    Course = "Org Fee",
                    Status = r.Payment != null ? r.Payment.PaymentStatus.ToString() : ""
                })
                .OrderByDescending(r => r.IssueDate)
                .ToListAsync();
        }
        catch
        {
            receipts = new List<StudentReceiptViewModel>();
            paymentHistory = new List<StudentPaymentHistoryViewModel>();
        }

        return View("~/Views/Dashboard/student_dashboard.cshtml",
            new DashboardViewModel { 
                PaymentHistory = paymentHistory,
                Receipts = receipts
            });
    }

    public async Task<IActionResult> AdminDashboard()
    {
        // Guard: only Admins can access this page
        var role = HttpContext.Session.GetString("UserRole");
        if (string.IsNullOrEmpty(role))
            return RedirectToAction("Login", "Home");

        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Dashboard", "Home");

        var pendingAccounts = await GetPendingAccountsAsync();
        var allAccountRequests = await GetAllAccountRequestsAsync();
        var students = await GetStudentsAsync();
        var admins = await GetAdminsAsync();
        var treasurers = await GetTreasurersAsync();
        var professors = await GetProfessorsAsync();

        var studentOnlyCount = students.Count(s => s.Role == "Student");

        var model = new DashboardViewModel
        {
            RequestedAccounts = pendingAccounts,
            AllAccountRequests = allAccountRequests,
            Students = students,
            Admins = admins,
            Treasurers = treasurers,
            Professors = professors,
            ApprovedAccountsCount = students.Count + professors.Count
        };

        return View("~/Views/Dashboard/admin_dashboard.cshtml", model);
    }

    public IActionResult TreasurerDashboard()
    {
        var role = HttpContext.Session.GetString("UserRole");
        if (string.IsNullOrEmpty(role))
            return RedirectToAction("Login", "Home");

        if (role != "Treasurer")
            return RedirectToDashboard(role);

        return View("~/Views/Dashboard/treasurer_dashboard.cshtml");
    }

    public IActionResult ProfessorDashboard()
    {
        var role = HttpContext.Session.GetString("UserRole");
        if (string.IsNullOrEmpty(role))
            return RedirectToAction("Login", "Home");

        if (role != "Professor")
            return RedirectToDashboard(role);

        return View("~/Views/Dashboard/professor_dashboard.cshtml");
    }

    // ----------------------------------------------------------------
    // LOGIN
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.SchoolId) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Json(new { success = false, message = "School ID and password are required." });
            }

            // Step 1: Find account by School ID
            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == request.SchoolId.ToLower());

            if (account == null)
            {
                return Json(new { success = false, message = "Invalid School ID or password." });
            }

            // Step 2: Check approval and active status
            if (account.RequestStatus != RequestStatus.Approved)
            {
                return Json(new { success = false, message = "Your account is not yet approved. Please wait for admin approval." });
            }

            if (!account.IsActive)
            {
                return Json(new { success = false, message = "Your account has been deactivated. Please contact the administrator." });
            }

            // Step 3: Verify password
            if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
            {
                return Json(new { success = false, message = "Invalid School ID or password." });
            }

            // Step 4: Load the user profile
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.AccountId == account.AccountId);

            // Step 5: Load academic profile for students
            string courseCode = "";
            string courseName = "";
            int? yearLevel = null;
            string section = "";
            
            if (account.Role == UserRole.Student && user != null)
            {
                var academicProfile = await _context.AcademicProfiles
                    .Include(ap => ap.Course)
                    .FirstOrDefaultAsync(ap => ap.UserId == user.UserId);
                
                if (academicProfile != null)
                {
                    courseCode = academicProfile.Course?.CourseCode ?? "";
                    courseName = academicProfile.Course?.CourseName ?? "";
                    yearLevel = academicProfile.YearLevel;
                    section = academicProfile.Section ?? "";
                }
            }

            // Step 6: Store session (use actual UserId for payment lookups)
            HttpContext.Session.SetString("UserId",    user?.UserId.ToString() ?? account.AccountId.ToString());
            HttpContext.Session.SetString("AccountId", account.AccountId.ToString());
            HttpContext.Session.SetString("UserRole",  account.Role.ToString());
            HttpContext.Session.SetString("SchoolId",  account.SchoolId);
            HttpContext.Session.SetString("Email",     account.Email ?? "");
            HttpContext.Session.SetString("FirstName", user?.FirstName ?? "");
            HttpContext.Session.SetString("LastName",  user?.LastName ?? "");
            HttpContext.Session.SetString("MiddleName", user?.MiddleName ?? "");
            HttpContext.Session.SetString("CourseCode", courseCode);
            HttpContext.Session.SetString("CourseName", courseName);
            HttpContext.Session.SetString("YearLevel", yearLevel?.ToString() ?? "");
            HttpContext.Session.SetString("Section", section);

            // Step 7: Determine redirect by role
            string redirectUrl = account.Role switch
            {
                UserRole.Admin => Url.Action("AdminDashboard", "Home")!,
                UserRole.Treasurer => Url.Action("TreasurerDashboard", "Home")!,
                UserRole.Professor => Url.Action("ProfessorDashboard", "Home")!,
                _ => Url.Action("Dashboard", "Home")!
            };

            return Json(new
            {
                success = true,
                message = "Login successful.",
                redirectUrl,
                user = new
                {
                    id         = account.AccountId,
                    schoolId   = account.SchoolId,
                    email      = account.Email,
                    role       = account.Role.ToString(),
                    firstName  = user?.FirstName,
                    lastName   = user?.LastName,
                    middleName = user?.MiddleName
                }
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Login failed: {ex.Message}" });
        }
    }

    // ----------------------------------------------------------------
    // LOGOUT
    // ----------------------------------------------------------------

    [HttpGet]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Login", "Home");
    }

    // ----------------------------------------------------------------
    // REGISTER
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegistrationRequest request)
    {
        try
        {
            if (request == null)
                return Json(new { success = false, message = "Registration request is null." });

            if (string.IsNullOrWhiteSpace(request.SchoolId) || string.IsNullOrWhiteSpace(request.Password))
                return Json(new { success = false, message = "School ID and password are required." });

            if (!string.IsNullOrWhiteSpace(request.Email) && !IsValidEmail(request.Email))
                return Json(new { success = false, message = "Invalid email format." });

            if (request.Password.Length < 6)
                return Json(new { success = false, message = "Password must be at least 6 characters long." });

            var result = await _authService.RegisterAccountAsync(request);

            if (result.Success)
            {
                return Json(new
                {
                    success = true,
                    message = result.Message,
                    account = new
                    {
                        id       = result.Account!.AccountId,
                        schoolId = result.Account.SchoolId,
                        email    = result.Account.Email,
                        role     = result.Account.Role.ToString(),
                        status   = result.Account.RequestStatus.ToString()
                    }
                });
            }

            return Json(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Registration failed: {ex.Message}" });
        }
    }

    // ----------------------------------------------------------------
    // FORGOT PASSWORD
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.StudentId))
                return Json(new { success = false, message = "Student ID is required." });

            var account = await _context.Accounts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == request.StudentId.ToLower());

            // Always return the same message — don't reveal if ID exists or not
            var genericMessage = $"If that ID exists, a verification code has been sent to the registered email.";

            if (account == null || account.User == null)
                return Json(new { success = true, message = genericMessage });

            // Generate 6-digit code
        var verificationCode    = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            var expirationTime      = DateTime.UtcNow.AddMinutes(15);

            account.PasswordResetToken        = verificationCode;
            account.PasswordResetTokenExpires = expirationTime;
            await _context.SaveChangesAsync();

            var emailBody = $@"Hello {account.User.FirstName ?? account.SchoolId},<br><br>
Your password reset verification code is:<br><br>
<strong>{verificationCode}</strong><br><br>
This code expires in 15 minutes.<br><br>
If you did not request this, please ignore this email.<br><br>
Best regards,<br>SSG Financial Management System";

            await _emailService.SendEmailAsync(account.Email ?? "", "Password Reset Code", emailBody);

            return Json(new { success = true, message = genericMessage, studentId = request.StudentId });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to process request: {ex.Message}" });
        }
    }

    // ----------------------------------------------------------------
    // VERIFY RESET CODE
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> VerifyResetCode([FromBody] VerifyCodeRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.StudentId) || string.IsNullOrWhiteSpace(request.Code))
                return Json(new { success = false, message = "Student ID and verification code are required." });

            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == request.StudentId.ToLower());

            if (account == null
                || account.PasswordResetToken != request.Code
                || account.PasswordResetTokenExpires == null
                || DateTime.UtcNow > account.PasswordResetTokenExpires)
            {
                return Json(new { success = false, message = "Invalid or expired verification code." });
            }

            // Mark as verified
            account.PasswordResetToken        = "verified";
            account.PasswordResetTokenExpires = DateTime.UtcNow.AddMinutes(30);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Code verified. You can now reset your password." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to verify code: {ex.Message}" });
        }
    }

    // ----------------------------------------------------------------
    // RESET PASSWORD
    // ----------------------------------------------------------------

    [HttpGet]
    public IActionResult ResetPassword(string token, string studentId)
    {
        var model = new ResetPasswordViewModel { Token = token, StudentId = studentId };
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.StudentId))
                return Json(new { success = false, message = "Student ID is required." });

            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
                return Json(new { success = false, message = "Password must be at least 6 characters long." });

            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == request.StudentId.ToLower());

            if (account == null
                || account.PasswordResetToken != "verified"
                || account.PasswordResetTokenExpires == null
                || account.PasswordResetTokenExpires <= DateTime.UtcNow)
            {
                return Json(new { success = false, message = "Invalid or expired reset token." });
            }

            account.PasswordHash              = AuthService.HashPassword(request.NewPassword);
            account.PasswordResetToken        = null;
            account.PasswordResetTokenExpires = null;
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Password reset successfully. You can now log in." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to reset password: {ex.Message}" });
        }
    }

    // ----------------------------------------------------------------
    // ACCOUNT MANAGEMENT
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> UpdateAccountStatus([FromBody] UpdateAccountStatusRequest request)
    {
                try
                {
                        var account = await _context.Accounts
                                .Include(a => a.User)
                                .FirstOrDefaultAsync(a => a.AccountId == request.AccountId);

                        if (account == null)
                                return Json(new { success = false, message = "Account not found." });

                        account.RequestStatus = request.Status;
                        await _context.SaveChangesAsync();

                        // Send email notification if the account has an email
                        if (!string.IsNullOrWhiteSpace(account.Email))
                        {
                                var firstName = account.User?.FirstName ?? account.SchoolId;
                                var lastName  = account.User?.LastName  ?? "";
                                var fullName  = $"{firstName} {lastName}".Trim();

                                string subject, body;

                                if (request.Status == RequestStatus.Approved)
                                {
                                        subject = "Your SSG Finance Account Has Been Approved";
                                        body = $@"
<html>
<body style='font-family:Arial,sans-serif;background:#f4f4f4;padding:30px;'>
    <div style='max-width:500px;margin:auto;background:#ffffff;border-radius:12px;
                            padding:32px;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>

        <div style='text-align:center;margin-bottom:24px;'>
            <div style='display:inline-block;background:#1a7a4a;border-radius:10px;padding:10px 18px;'>
                <span style='color:#fff;font-size:18px;font-weight:700;'>SSG Finance</span>
            </div>
        </div>

        <h2 style='color:#1a1a1a;margin-bottom:6px;'>Hello, {fullName}!</h2>
        <p style='color:#555;margin-bottom:24px;'>
            Great news — your account request has been <strong style='color:#1a7a4a;'>approved</strong>!
            You can now log in to the SSG Finance system using your School ID and password.
        </p>

        <table style='width:100%;border-collapse:collapse;margin-bottom:24px;'>
            <tr style='background:#f0f9f4;'>
                <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;width:40%;'>School ID</td>
                <td style='padding:12px 16px;color:#1a1a1a;'>{account.SchoolId}</td>
            </tr>
            <tr>
                <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;'>Status</td>
                <td style='padding:12px 16px;'>
                    <span style='background:#dcfce7;color:#166534;padding:3px 10px;border-radius:20px;
                                             font-size:13px;font-weight:600;'>Approved</span>
                </td>
            </tr>
        </table>

        <p style='color:#888;font-size:12px;text-align:center;'>
            If you have any concerns, please contact your SSG administrator.
        </p>

        <div style='text-align:center;margin-top:20px;'>
            <span style='color:#1a7a4a;font-weight:600;font-size:13px;'>SSG Finance System</span>
        </div>
    </div>
</body>
</html>";
                                }
                                else // Rejected
                                {
                                        subject = "Your SSG Finance Account Request Was Not Approved";
                                        body = $@"
<html>
<body style='font-family:Arial,sans-serif;background:#f4f4f4;padding:30px;'>
    <div style='max-width:500px;margin:auto;background:#ffffff;border-radius:12px;
                            padding:32px;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>

        <div style='text-align:center;margin-bottom:24px;'>
            <div style='display:inline-block;background:#1a7a4a;border-radius:10px;padding:10px 18px;'>
                <span style='color:#fff;font-size:18px;font-weight:700;'>SSG Finance</span>
            </div>
        </div>

        <h2 style='color:#1a1a1a;margin-bottom:6px;'>Hello, {fullName}!</h2>
        <p style='color:#555;margin-bottom:24px;'>
            Unfortunately, your account request has been <strong style='color:#dc2626;'>rejected</strong>.
            Please contact your SSG administrator for more information or to re-apply.
        </p>

        <table style='width:100%;border-collapse:collapse;margin-bottom:24px;'>
            <tr style='background:#fff5f5;'>
                <td style='padding:12px 16px;font-weight:600;color:#dc2626;width:40%;'>School ID</td>
                <td style='padding:12px 16px;color:#1a1a1a;'>{account.SchoolId}</td>
            </tr>
            <tr>
                <td style='padding:12px 16px;font-weight:600;color:#dc2626;'>Status</td>
                <td style='padding:12px 16px;'>
                    <span style='background:#fee2e2;color:#991b1b;padding:3px 10px;border-radius:20px;
                                             font-size:13px;font-weight:600;'>Not Approved</span>
                </td>
            </tr>
        </table>

        <p style='color:#888;font-size:12px;text-align:center;'>
            If you believe this is a mistake, please reach out to your SSG administrator.
        </p>

        <div style='text-align:center;margin-top:20px;'>
            <span style='color:#1a7a4a;font-weight:600;font-size:13px;'>SSG Finance System</span>
        </div>
    </div>
</body>
</html>";
                                }

                                try
                                {
                                        await _emailService.SendEmailAsync(account.Email, subject, body);
                                }
                                catch
                                {
                                        // Don't fail the whole request if email fails — status was already saved
                                        return Json(new { success = true, message = $"Account {request.Status} successfully, but email notification could not be sent." });
                                }
                        }

                        return Json(new { success = true, message = $"Account {request.Status} successfully. Email notification sent." });
                }
                catch (Exception ex)
                {
                        return Json(new { success = false, message = $"Failed to update account status: {ex.Message}" });
                }
    }

    [HttpPost]
    public async Task<IActionResult> ToggleAccountActivation([FromBody] DeactivateRequest request)
    {
        try
        {
            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.AccountId == request.AccountId);

            if (account == null)
                return Json(new { success = false, message = "Account not found." });

            account.IsActive = !account.IsActive;
            await _context.SaveChangesAsync();

            return Json(new { 
                success = true, 
                isActive = account.IsActive,
                message = account.IsActive ? "Account reactivated." : "Account deactivated."
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteAccount([FromBody] DeactivateRequest request)
    {
        try
        {
            var account = await _context.Accounts
                .Include(a => a.User)
                    .ThenInclude(u => u!.AcademicProfile)
                .FirstOrDefaultAsync(a => a.AccountId == request.AccountId);

            if (account == null)
                return Json(new { success = false, message = "Account not found." });

            // 1. delete academic profile first
            if (account.User?.AcademicProfile != null)
                _context.AcademicProfiles.Remove(account.User.AcademicProfile);

            // 2. delete user
            if (account.User != null)
                _context.Users.Remove(account.User);

            // 3. delete account
            _context.Accounts.Remove(account);

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Account deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Delete failed: " + ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetStudent(int accountId)
    {
        var user = await _context.Users
            .Include(u => u.Account)
            .Include(u => u.AcademicProfile)
                .ThenInclude(ap => ap!.Course)
            .FirstOrDefaultAsync(u => u.AccountId == accountId);

        if (user == null)
            return Json(new { success = false, message = "Student not found." });

        return Json(new {
            success        = true,
            firstName      = user.FirstName,
            lastName       = user.LastName,
            middleName     = user.MiddleName,
            email          = user.Account?.Email,
            courseId       = user.AcademicProfile?.CourseId,
            yearLevel      = user.AcademicProfile?.YearLevel,
            section        = user.AcademicProfile?.Section,
            academicStatus = user.AcademicProfile?.AcademicStatus.ToString() ?? "Enrolled"
        });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateStudent([FromBody] UpdateStudentRequest request)
    {
        try
        {
            var user = await _context.Users
                .Include(u => u.Account)
                .Include(u => u.AcademicProfile)
                .FirstOrDefaultAsync(u => u.AccountId == request.AccountId);

            if (user == null)
                return Json(new { success = false, message = "Student not found." });

            // update name
            user.FirstName  = request.FirstName;
            user.LastName   = request.LastName;
            user.MiddleName = request.MiddleName;

            // update email on the account
            if (user.Account != null)
                user.Account.Email = request.Email;

            // update academic profile
            if (user.AcademicProfile != null)
            {
                user.AcademicProfile.CourseId  = request.CourseId;
                user.AcademicProfile.YearLevel = request.YearLevel;
                user.AcademicProfile.Section   = request.Section;

                if (!string.IsNullOrWhiteSpace(request.AcademicStatus)
                    && Enum.TryParse<AcademicStatus>(request.AcademicStatus, out var parsedStatus))
                {
                    user.AcademicProfile.AcademicStatus = parsedStatus;
                }
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Student updated successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Update failed: {ex.Message}" });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetProfessor(int accountId)
    {
        var user = await _context.Users
            .Include(u => u.Account)
            .FirstOrDefaultAsync(u => u.AccountId == accountId);

        if (user == null)
            return Json(new { success = false, message = "Professor not found." });

        return Json(new {
            success    = true,
            firstName  = user.FirstName,
            lastName   = user.LastName,
            middleName = user.MiddleName,
            email      = user.Account?.Email,
            schoolId   = user.Account?.SchoolId
        });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateProfessor([FromBody] UpdateProfessorRequest request)
    {
        try
        {
            var user = await _context.Users
                .Include(u => u.Account)
                .FirstOrDefaultAsync(u => u.AccountId == request.AccountId);

            if (user == null)
                return Json(new { success = false, message = "Professor not found." });

            // update name
            user.FirstName  = request.FirstName;
            user.LastName   = request.LastName;

            user.MiddleName = request.MiddleName;

            // update account info
            if (user.Account != null)
            {
                user.Account.Email = request.Email;
                user.Account.SchoolId = request.SchoolId ?? user.Account.SchoolId;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Professor updated successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Update failed: {ex.Message}" });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetCourses()
    {
        try
        {
            var courses = await _context.Courses
                .OrderBy(c => c.CourseCode)
                .Select(c => new { c.CourseId, c.CourseCode, c.CourseName })
                .ToListAsync();

            return Json(new { success = true, courses });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to get courses: {ex.Message}" });
        }
    }

    [HttpGet]
    public async Task<IActionResult> CheckEmail(string email, int excludeAccountId)
    {
        var account = await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Email != null 
                                   && a.Email.ToLower() == email.ToLower() 
                                   && a.AccountId != excludeAccountId);

        if (account == null)
            return Json(new { taken = false });

        var name = account.User != null
            ? $"{account.User.FirstName} {account.User.LastName}"
            : account.SchoolId;

        return Json(new { taken = true, usedBy = name });
    }

    [HttpPost]
    public async Task<IActionResult> ChangeRole([FromBody] ChangeRoleRequest request)
    {
        try
        {
            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.AccountId == request.AccountId);

            if (account == null)
                return Json(new { success = false, message = "Account not found." });

            if (!Enum.TryParse<UserRole>(request.Role, out var newRole))
                return Json(new { success = false, message = "Invalid role." });

            account.Role = newRole;
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Role changed to " + request.Role + " successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Failed to change role: " + ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetPendingRequests()
    {
        var requests = await GetPendingAccountsAsync();
        return Json(new { success = true, requests });
    }

    [HttpGet]
    public async Task<IActionResult> GetRejectedRequests()
    {
        try
        {
            var rejected = await _context.Accounts
                .Where(a => a.RequestStatus == RequestStatus.Rejected && a.Role == UserRole.Student)
                .Include(a => a.User)
                    .ThenInclude(u => u!.AcademicProfile)
                        .ThenInclude(ap => ap!.Course)
                .Select(a => new RequestedAccountViewModel
                {
                    AccountId  = a.AccountId,
                    SchoolId   = a.SchoolId,
                    Fullname   = a.User != null
                        ? $"{(a.User.LastName != null ? a.User.LastName.ToUpper() : "")}, {(a.User.FirstName != null ? a.User.FirstName.ToUpper() : "")}"
                        : a.SchoolId.ToUpper(),
                    CourseCode = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.Course != null
                        ? a.User.AcademicProfile.Course.CourseCode : null,
                    YearLevel  = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.YearLevel != null
                        ? a.User.AcademicProfile.YearLevel.ToString() : null,
                    Section    = a.User != null && a.User.AcademicProfile != null
                        ? a.User.AcademicProfile.Section : null,
                    CreatedAt  = a.CreatedAt,
                    Status     = a.RequestStatus
                })
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            return Json(new { success = true, requests = rejected });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentsList()
    {
        var students = await GetStudentsAsync();
        return Json(new { success = true, students });
    }

    [HttpGet]
    public async Task<IActionResult> GetTreasurersList()
    {
        var treasurers = await GetTreasurersAsync();
        return Json(new { success = true, treasurers });
    }

    [HttpGet]
    public async Task<IActionResult> GetProfessorsList()
    {
        var professors = await GetProfessorsAsync();
        return Json(new { success = true, professors });
    }

    [HttpGet]
    public async Task<IActionResult> GetAdminsList()
    {
        var admins = await GetAdminsAsync();
        return Json(new { success = true, admins });
    }

    [HttpGet]
    public async Task<IActionResult> GetDashboardStats()
    {
        var allStudents    = await GetStudentsAsync(); // includes both Student + Treasurer roles
        var professors     = await GetProfessorsAsync();
        var admins         = await GetAdminsAsync();
        var pending        = await GetPendingAccountsAsync();
        var allRequests    = await GetAllAccountRequestsAsync();

        var studentCount   = allStudents.Count(s => s.Role == "Student");
        var treasurerCount = allStudents.Count(s => s.Role == "Treasurer");

        return Json(new {
            success        = true,
            approvedCount  = (studentCount + treasurerCount) + professors.Count,
            pendingCount   = pending.Count,
            studentCount   = studentCount + treasurerCount, // students card shows both
            treasurerCount = treasurerCount,
            professorCount = professors.Count,
            adminCount     = admins.Count,
            recentRequests = allRequests
        });
    }

    // ----------------------------------------------------------------
    // DEBUG / TEST (remove before production)
    // ----------------------------------------------------------------

    [HttpGet]
    public IActionResult TestService()
    {
        try
        {
            if (_authService == null)
                return Json(new { success = false, message = "AuthService is null — DI issue." });

            var canConnect = _context.Database.CanConnect();
            return Json(new { success = true, message = $"DB can connect: {canConnect}" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Test failed: {ex.Message}" });
        }
    }

    // ----------------------------------------------------------------
    // ERROR
    // ----------------------------------------------------------------

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    // ----------------------------------------------------------------
    // PRIVATE HELPERS
    // ----------------------------------------------------------------

    private IActionResult RedirectToDashboard(string? role)
    {
        return role switch
        {
            "Admin" => RedirectToAction("AdminDashboard", "Home"),
            "Treasurer" => RedirectToAction("TreasurerDashboard", "Home"),
            "Professor" => RedirectToAction("ProfessorDashboard", "Home"),
            _ => RedirectToAction("Dashboard", "Home")
        };
    }

    private bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<RequestedAccountViewModel>> GetPendingAccountsAsync()
    {
        return await _context.Accounts
            .Where(a => a.RequestStatus == RequestStatus.Pending && a.Role == UserRole.Student)
            .Include(a => a.User)
                .ThenInclude(u => u!.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
            .Select(a => new RequestedAccountViewModel
            {
                AccountId  = a.AccountId,
                SchoolId   = a.SchoolId,
                Fullname   = a.User != null
                    ? $"{(a.User.LastName != null ? a.User.LastName.ToUpper() : "")}, {(a.User.FirstName != null ? a.User.FirstName.ToUpper() : "")}"
                    : a.SchoolId.ToUpper(),
                CourseCode = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.Course != null
                    ? a.User.AcademicProfile.Course.CourseCode : null,
                YearLevel  = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.YearLevel != null
                    ? a.User.AcademicProfile.YearLevel.ToString() : null,
                Section    = a.User != null && a.User.AcademicProfile != null
                    ? a.User.AcademicProfile.Section : null,
                CreatedAt  = a.CreatedAt,
                Status     = a.RequestStatus
            })
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    private async Task<List<RequestedAccountViewModel>> GetAllAccountRequestsAsync()
    {
        return await _context.Accounts
            .Where(a => a.Role == UserRole.Student || a.Role == UserRole.Treasurer)
            .Include(a => a.User)
                .ThenInclude(u => u!.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
            .Select(a => new RequestedAccountViewModel
            {
                AccountId  = a.AccountId,
                SchoolId   = a.SchoolId,
                Fullname   = a.User != null
                    ? $"{(a.User.LastName != null ? a.User.LastName.ToUpper() : "")}, {(a.User.FirstName != null ? a.User.FirstName.ToUpper() : "")}"
                    : a.SchoolId.ToUpper(),
                CourseCode = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.Course != null
                    ? a.User.AcademicProfile.Course.CourseCode : null,
                YearLevel  = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.YearLevel != null
                    ? a.User.AcademicProfile.YearLevel.ToString() : null,
                Section    = a.User != null && a.User.AcademicProfile != null
                    ? a.User.AcademicProfile.Section : null,
                CreatedAt  = a.CreatedAt,
                Status     = a.RequestStatus
            })
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    private async Task<List<StudentViewModel>> GetStudentsAsync()
    {
        var students = await _context.Users
            .Include(u => u.AcademicProfile)
                .ThenInclude(ap => ap!.Course)
            .Include(u => u.AcademicProfile)
                .ThenInclude(ap => ap!.SchoolYear)
            .Include(u => u.Account)
            .Where(u => u.Account != null 
                     && u.Account.RequestStatus == RequestStatus.Approved
                     && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer))
            .Select(u => new StudentViewModel
            {
                StudentId      = u.UserId,
                FullName       = u.LastName != null && u.FirstName != null
                    ? $"{u.LastName.ToUpper()}, {u.FirstName.ToUpper()}" : "N/A",
                CourseCode     = u.AcademicProfile != null && u.AcademicProfile.Course != null
                    ? u.AcademicProfile.Course.CourseCode : "N/A",
                YearSection    = u.AcademicProfile != null
                    ? $"{(u.AcademicProfile.YearLevel.HasValue ? u.AcademicProfile.YearLevel.Value.ToString() : "N/A")}-{(u.AcademicProfile.Section ?? "N/A")}"
                    : "N/A",
                AccountId      = u.AccountId,
                Role           = u.Account != null ? u.Account.Role.ToString() : "Student",
                IsActive       = u.Account != null ? u.Account.IsActive : false,
                SchoolId       = u.Account != null ? u.Account.SchoolId : "N/A",
                AcademicStatus = u.AcademicProfile != null ? u.AcademicProfile.AcademicStatus.ToString() : "Enrolled",
                SchoolYearId = u.AcademicProfile != null ? u.AcademicProfile.SchoolYearId : null,
                SemesterEntered = u.AcademicProfile != null ? u.AcademicProfile.SemesterEntered : null
            })
            .ToListAsync();

        return students.OrderBy(s => s.FullName).ToList();
    }

    private async Task<List<AdminViewModel>> GetAdminsAsync()
    {
        var admins = await _context.Accounts
            .Include(a => a.User)
            .Where(a => a.Role == UserRole.Admin && a.RequestStatus == RequestStatus.Approved)
            .Select(a => new AdminViewModel
            {
                AdminId = a.AccountId,
                FullName = a.User != null && a.User.LastName != null && a.User.FirstName != null
                    ? $"{a.User.LastName.ToUpper()}, {a.User.FirstName.ToUpper()}" : "N/A",
                Email = a.Email ?? "N/A",
                SchoolId = a.SchoolId ?? "N/A",
                Role = a.Role.ToString()
            })
            .ToListAsync();

        return admins.OrderBy(a => a.FullName).ToList();
    }

    private async Task<List<TreasurerViewModel>> GetTreasurersAsync()
    {
        var treasurers = await _context.Accounts
            .Include(a => a.User)
                .ThenInclude(u => u!.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
            .Where(a => a.Role == UserRole.Treasurer && a.RequestStatus == RequestStatus.Approved)
            .Select(a => new TreasurerViewModel
            {
                TreasurerId = a.AccountId,
                FullName = a.User != null && a.User.LastName != null && a.User.FirstName != null
                    ? $"{a.User.LastName.ToUpper()}, {a.User.FirstName.ToUpper()}" : "N/A",
                Email = a.Email ?? "N/A",
                SchoolId = a.SchoolId ?? "N/A",
                CourseCode = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.Course != null
                    ? a.User.AcademicProfile.Course.CourseCode : "N/A",
                YearSection = a.User != null && a.User.AcademicProfile != null
                    ? $"{(a.User.AcademicProfile.YearLevel.HasValue ? a.User.AcademicProfile.YearLevel.Value.ToString() : "N/A")}-{(a.User.AcademicProfile.Section ?? "N/A")}"
                    : "N/A",
                Role = a.Role.ToString()
            })
            .ToListAsync();

        return treasurers.OrderBy(t => t.FullName).ToList();
    }

    private async Task<List<ProfessorViewModel>> GetProfessorsAsync()
    {
        var professors = await _context.Accounts
            .Include(a => a.User)
            .Where(a => (a.Role == UserRole.Professor || a.Role == UserRole.Admin) 
                 && a.RequestStatus == RequestStatus.Approved)
            .Select(a => new ProfessorViewModel
            {
                ProfessorId = a.AccountId,
                AccountId = a.AccountId,
                FullName = a.User != null && a.User.LastName != null && a.User.FirstName != null
                    ? $"{a.User.LastName.ToUpper()}, {a.User.FirstName.ToUpper()}{(string.IsNullOrWhiteSpace(a.User.MiddleName) ? "" : " " + a.User.MiddleName.ToUpper())}" : "N/A",
                Email = a.Email ?? "N/A",
                SchoolId = a.SchoolId ?? "N/A",
                Role = a.Role.ToString()
            })
            .ToListAsync();

        return professors.OrderBy(p => p.FullName).ToList();
    }

    [HttpPost]
    public async Task<IActionResult> AddProfessor([FromBody] AddProfessorRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
                return Json(new { success = false, message = "First and last name are required." });

            if (string.IsNullOrWhiteSpace(request.SchoolId))
                return Json(new { success = false, message = "School ID is required." });

            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
                return Json(new { success = false, message = "Password must be at least 6 characters." });

            // check if school ID already exists
            var existing = await _context.Accounts
                .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == request.SchoolId.ToLower());

            if (existing != null)
                return Json(new { success = false, message = "School ID is already taken." });

            // create account
            var account = new Account
            {
                SchoolId      = request.SchoolId,
                Email         = request.Email,
                PasswordHash  = AuthService.HashPassword(request.Password),
                Role          = UserRole.Professor,
                RequestStatus = RequestStatus.Approved,
                IsActive      = true,
                CreatedAt     = DateTime.UtcNow
            };

            _context.Accounts.Add(account);
            await _context.SaveChangesAsync();

            // create user
            var user = new User
            {
                AccountId  = account.AccountId,
                FirstName  = request.FirstName,
                LastName   = request.LastName,
                MiddleName = request.MiddleName
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Send welcome email if an email address was provided
            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                var middlePart = string.IsNullOrWhiteSpace(request.MiddleName) ? "" : $" {request.MiddleName}";
                var fullName = $"{request.FirstName}{middlePart} {request.LastName}";

                var emailBody = $@"
<html>
<body style='font-family:Arial,sans-serif;background:#f4f4f4;padding:30px;'>
  <div style='max-width:500px;margin:auto;background:#ffffff;border-radius:12px;
              padding:32px;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>

    <div style='text-align:center;margin-bottom:24px;'>
      <div style='display:inline-block;background:#1a7a4a;border-radius:10px;padding:10px 18px;'>
        <span style='color:#fff;font-size:18px;font-weight:700;'>SSG Finance</span>
      </div>
    </div>

    <h2 style='color:#1a1a1a;margin-bottom:6px;'>Welcome, {fullName}!</h2>
    <p style='color:#555;margin-bottom:24px;'>
      Your professor account has been created. Here are your login credentials:
    </p>

    <table style='width:100%;border-collapse:collapse;margin-bottom:24px;'>
      <tr style='background:#f0f9f4;'>
        <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;width:40%;'>Full Name</td>
        <td style='padding:12px 16px;color:#1a1a1a;'>{fullName}</td>
      </tr>
      <tr>
        <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;'>School ID</td>
        <td style='padding:12px 16px;color:#1a1a1a;'>{request.SchoolId}</td>
      </tr>
      <tr style='background:#f0f9f4;'>
        <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;'>Email</td>
        <td style='padding:12px 16px;color:#1a1a1a;'>{request.Email}</td>
      </tr>
      <tr>
        <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;'>Password</td>
        <td style='padding:12px 16px;color:#1a1a1a;font-family:monospace;font-size:15px;'>
          {request.Password}
        </td>
      </tr>
    </table>

    <p style='color:#888;font-size:12px;text-align:center;'>
      Please keep your credentials secure. You can change your password after logging in.
    </p>

    <div style='text-align:center;margin-top:20px;'>
      <span style='color:#1a7a4a;font-weight:600;font-size:13px;'>SSG Finance Admin Panel</span>
    </div>
  </div>
</body>
</html>";

                try
                {
                    await _emailService.SendEmailAsync(
                        request.Email,
                        "Your SSG Finance Professor Account",
                        emailBody
                    );
                }
                catch
                {
                    return Json(new { success = true, message = "Professor added successfully, but welcome email could not be sent." });
                }
            }

            return Json(new { success = true, message = string.IsNullOrWhiteSpace(request.Email)
                ? "Professor added successfully."
                : "Professor added and welcome email sent successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Failed to add professor: " + ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetSchoolYears()
    {
        try
        {
            var schoolYears = await _context.SchoolYears
                .OrderByDescending(sy => sy.YearStart)
                .ToListAsync();

            var feeRecords = await _context.FullAmounts
                .Select(f => new { f.SchoolYearId, f.Semester })
                .ToListAsync();

            var result = schoolYears.Select(sy => new {
                sy.SchoolYearId,
                sy.YearStart,
                sy.YearEnd,
                yearStatus = sy.YearStatus.ToString(),
                hasFirst  = feeRecords.Any(f => f.SchoolYearId == sy.SchoolYearId && f.Semester == Semester.First),
                hasSecond = feeRecords.Any(f => f.SchoolYearId == sy.SchoolYearId && f.Semester == Semester.Second)
            });

            return Json(new { success = true, schoolYears = result });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddSchoolYear([FromBody] AddSchoolYearRequest request)
    {
        try
        {
            if (request.YearEnd != request.YearStart + 1)
                return Json(new { success = false, message = "Year end must be exactly year start + 1." });

            var duplicate = await _context.SchoolYears
                .AnyAsync(sy => sy.YearStart == request.YearStart && sy.YearEnd == request.YearEnd);
            if (duplicate)
                return Json(new { success = false, message = "This school year already exists." });

            // Mark all existing school years as Ended
            var existing = await _context.SchoolYears.ToListAsync();
            foreach (var sy in existing)
                sy.YearStatus = YearStatus.Ended;

            // Add new school year as Current
            _context.SchoolYears.Add(new SchoolYear
            {
                YearStart  = request.YearStart,
                YearEnd    = request.YearEnd,
                YearStatus = YearStatus.Current
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "School year added successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteSchoolYear([FromBody] DeleteSchoolYearRequest request)
    {
        try
        {
            var sy = await _context.SchoolYears
                .Include(s => s.FullAmounts)
                .FirstOrDefaultAsync(s => s.SchoolYearId == request.SchoolYearId);

            if (sy == null)
                return Json(new { success = false, message = "School year not found." });

            if (sy.FullAmounts.Any())
                _context.FullAmounts.RemoveRange(sy.FullAmounts);

            _context.SchoolYears.Remove(sy);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "School year deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddCourse([FromBody] AddCourseRequest request)
    {
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
            return Json(new { success = true, message = "Course added successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteCourse([FromBody] DeleteCourseRequest request)
    {
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

            return Json(new { success = true, message = "Course deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetFees()
    {
        try
        {
            var fees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SetFeeAmount([FromBody] SetFeeAmountRequest request)
    {
        try
        {
            if (request.Amount <= 0)
                return Json(new { success = false, message = "Amount must be greater than zero." });

            var semesterInput = (request.Semester ?? string.Empty).Trim();

            // Debug logging to see exactly what we received
            Console.WriteLine($"DEBUG: Received semester value: '{semesterInput}' (length: {semesterInput.Length})");

            Semester semester;
            if (semesterInput.Equals("1st", StringComparison.OrdinalIgnoreCase) ||
                semesterInput.Equals("First", StringComparison.OrdinalIgnoreCase) ||
                semesterInput.Equals("1st Semester", StringComparison.OrdinalIgnoreCase))
            {
                semester = Semester.First;
                Console.WriteLine($"DEBUG: Mapped to Semester.First");
            }
            else if (semesterInput.Equals("2nd", StringComparison.OrdinalIgnoreCase) ||
                     semesterInput.Equals("Second", StringComparison.OrdinalIgnoreCase) ||
                     semesterInput.Equals("2nd Semester", StringComparison.OrdinalIgnoreCase))
            {
                semester = Semester.Second;
                Console.WriteLine($"DEBUG: Mapped to Semester.Second");
            }
            else
            {
                Console.WriteLine($"DEBUG: Invalid semester value: '{semesterInput}'");
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
            return Json(new { success = true, message = "Fee amount set successfully." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Failed to set fee amount: {detail}" });
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

            if (request.NewPassword.Length < 8)
                return Json(new { success = false, message = "Password must be at least 8 characters long." });

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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> DebugFullAmounts()
    {
        try
        {
            var allFees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .ToListAsync();
            
            var result = allFees.Select(f => new {
                f.FullAmountId,
                SchoolYear = f.SchoolYear != null ? $"{f.SchoolYear.YearStart}-{f.SchoolYear.YearEnd}" : "N/A",
                f.Semester,
                f.Amount,
                SemesterStatus = f.SemesterStatus.ToString()
            }).ToList();

            return Json(new { success = true, count = result.Count, fees = result });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFee([FromBody] DeleteFeeRequest request)
    {
        try
        {
            var fee = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.FullAmountId == request.FullAmountId);

            if (fee == null)
                return Json(new { success = false, message = "Fee record not found." });

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

            return Json(new { success = true, message = "Fee record deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    // ----------------------------------------------------------------
    // TREASURER FINANCIAL MANAGEMENT
    // ----------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> GetCurrentSchoolYearAndSemester()
    {
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetOrgFeePayments()
    {
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
                    courseCode = p.User.AcademicProfile != null && p.User.AcademicProfile.Course != null
                        ? p.User.AcademicProfile.Course.CourseCode : "N/A",
                    yearSection = p.User.AcademicProfile != null
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetRecentPayments()
    {
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetProfessorStudentPayments()
    {
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

            var result = users.Select(u =>
            {
                var studentPayments = payments
                    .Where(p => p.UserId == u.UserId)
                    .ToList();

                var totalPaid = studentPayments.Sum(p => p.Amount);
                var isApplicable = currentFee != null && IsFeeApplicableToStudent(u.AcademicProfile, currentFee);
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
            return Json(new { success = false, message = ex.Message });
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
    public async Task<IActionResult> GetTreasurerStudentsWithFees()
    {
        try
        {
            var students = await GetStudentsAsync();

            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

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
                    .Where(p => feeIds.Contains(p.FullAmountId))
                    .ToListAsync()
                : new List<OrgFeePayment>();

            var schoolYearLabel = currentSchoolYear != null
                ? $"{currentSchoolYear.YearStart}–{currentSchoolYear.YearEnd}"
                : "N/A";

            var result = students.Select(s =>
            {
                var studentPayments = allSemPayments.Where(p => p.UserId == s.StudentId).ToList();
                var firstApplicable = firstSemFee != null
                    && IsFeeApplicableToStudent(s.SchoolYearId, s.SemesterEntered, firstSemFee);
                var secondApplicable = secondSemFee != null
                    && IsFeeApplicableToStudent(s.SchoolYearId, s.SemesterEntered, secondSemFee);

                var firstPaid = firstSemFee != null
                    ? studentPayments.Where(p => p.FullAmountId == firstSemFee.FullAmountId).Sum(p => p.Amount)
                    : 0m;
                var secondPaid = secondSemFee != null
                    ? studentPayments.Where(p => p.FullAmountId == secondSemFee.FullAmountId).Sum(p => p.Amount)
                    : 0m;

                return new
                {
                    userId = s.StudentId,
                    schoolId = s.SchoolId,
                    name = s.FullName,
                    courseCode = s.CourseCode,
                    yearSection = s.YearSection,
                    role = s.Role,
                    isActive = s.IsActive,
                    schoolYear = schoolYearLabel,
                    firstSemStatus  = firstApplicable ? ComputeOrgFeeStatus(firstPaid,  firstSemFee?.Amount  ?? 0) : "N/A",
                    secondSemStatus = secondApplicable ? ComputeOrgFeeStatus(secondPaid, secondSemFee?.Amount ?? 0) : "N/A",
                    firstSemPaid    = firstPaid,
                    secondSemPaid   = secondPaid,
                    firstSemRequired  = firstApplicable ? firstSemFee?.Amount  ?? 0 : 0,
                    secondSemRequired = secondApplicable ? secondSemFee?.Amount ?? 0 : 0
                };
            }).ToList();

            return Json(new { success = true, students = result, schoolYear = schoolYearLabel });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentOrgFeeDetails(int userId)
    {
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

            var semesterFees = currentSchoolYear != null
                ? await _context.FullAmounts
                    .Where(f => f.SchoolYearId == currentSchoolYear.SchoolYearId)
                    .ToListAsync()
                : new List<FullAmount>();

            var firstSemFee  = semesterFees.FirstOrDefault(f => f.Semester == Semester.First);
            var secondSemFee = semesterFees.FirstOrDefault(f => f.Semester == Semester.Second);

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

            var firstSemPayments  = firstSemFee  != null ? payments.Where(p => p.FullAmountId == firstSemFee.FullAmountId).ToList()  : new List<OrgFeePayment>();
            var secondSemPayments = secondSemFee != null ? payments.Where(p => p.FullAmountId == secondSemFee.FullAmountId).ToList() : new List<OrgFeePayment>();
            var firstApplicable = firstSemFee != null && IsFeeApplicableToStudent(student.AcademicProfile, firstSemFee);
            var secondApplicable = secondSemFee != null && IsFeeApplicableToStudent(student.AcademicProfile, secondSemFee);

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
                        : "N/A",
                    courseCode = student.AcademicProfile?.Course?.CourseCode ?? "N/A",
                    yearSection = student.AcademicProfile != null
                        ? $"{(student.AcademicProfile.YearLevel?.ToString() ?? "N/A")}-{(student.AcademicProfile.Section ?? "N/A")}"
                        : "N/A",
                    role = student.Account.Role.ToString()
                },
                schoolYear = schoolYearLabel,
                firstSemester  = BuildSemesterFeeSummary(firstSemFee,  firstSemPayments, firstApplicable),
                secondSemester = BuildSemesterFeeSummary(secondSemFee, secondSemPayments, secondApplicable),
                transactions
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetOrgFeeReceipt(int paymentId)
    {
        try
        {
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
                return Json(new { success = false, message = "Payment record not found." });

            var receipt = payment.Receipts
                .OrderBy(r => r.ReceiptId)
                .FirstOrDefault();

            if (receipt == null || string.IsNullOrWhiteSpace(receipt.ReceiptNumber))
                return Json(new { success = false, message = "No receipt number recorded for this payment." });

            var issuerUser = await _context.Users
                .FirstOrDefaultAsync(u => u.UserId == receipt.IssuedBy);

            issuerUser ??= await _context.Users
                .FirstOrDefaultAsync(u => u.AccountId == receipt.IssuedBy);

            var issuerName = issuerUser != null
                ? $"{issuerUser.FirstName ?? ""} {issuerUser.LastName ?? ""}".Trim()
                : "Treasurer";
            var issuerAccountId = issuerUser?.AccountId ?? receipt.IssuedBy;
            var signature = await GetActiveTreasurerSignatureAsync(issuerAccountId);

            var profile = payment.User?.AcademicProfile;
            var yearSection = profile != null
                ? $"{(profile.YearLevel.HasValue ? profile.YearLevel.Value.ToString() : "N/A")}-{(profile.Section ?? "N/A")}"
                : "N/A";

            return Json(new
            {
                success = true,
                receipt = new
                {
                    receipt.ReceiptId,
                    receipt.ReceiptNumber,
                    payment.PaymentId,
                    payment.PaymentDate,
                    payment.Amount,
                    status = payment.PaymentStatus.ToString(),
                    semester = payment.FullAmount.Semester.ToString(),
                    schoolYear = payment.FullAmount.SchoolYear != null
                        ? $"{payment.FullAmount.SchoolYear.YearStart}–{payment.FullAmount.SchoolYear.YearEnd}"
                        : "",
                    studentName = payment.User != null
                        ? $"{payment.User.FirstName ?? ""} {payment.User.LastName ?? ""}".Trim()
                        : "",
                    studentId = payment.User?.Account?.SchoolId ?? "",
                    course = payment.User?.AcademicProfile?.Course?.CourseCode ?? "",
                    yearSection,
                    issuedByName = string.IsNullOrWhiteSpace(issuerName) ? "Treasurer" : issuerName,
                    issuedByAccountId = issuerAccountId,
                    signatureData = signature?.SignatureData
                }
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetOtherFunds()
    {
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
                    f.Amount,
                    f.ReceivedDate,
                    schoolYear = f.SchoolYear != null
                        ? $"{f.SchoolYear.YearStart} – {f.SchoolYear.YearEnd}"
                        : null,
                    receivedBy = f.Receiver != null && f.Receiver.User != null
                        ? $"{(f.Receiver.User.LastName ?? "").ToUpper()}, {(f.Receiver.User.FirstName ?? "").ToUpper()}"
                        : "Unknown"
                })
                .ToListAsync();

            return Json(new { success = true, funds });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetExpenses()
    {
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
                        : "Unknown"
                })
                .ToListAsync();

            return Json(new { success = true, expenses });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetTreasurerDashboardStats()
    {
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddOrgFeePayment([FromBody] AddOrgFeePaymentRequest request)
    {
        try
        {
            // Parse semester from request
            var semesterInput = (request.Semester ?? string.Empty).Trim();
            Semester semester;
            if (semesterInput.Equals("First", StringComparison.OrdinalIgnoreCase) ||
                semesterInput.Equals("1st",   StringComparison.OrdinalIgnoreCase))
                semester = Semester.First;
            else if (semesterInput.Equals("Second", StringComparison.OrdinalIgnoreCase) ||
                     semesterInput.Equals("2nd",    StringComparison.OrdinalIgnoreCase))
                semester = Semester.Second;
            else
                return Json(new { success = false, message = "Invalid semester selected." });

            // Find current school year
            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            if (currentSchoolYear == null)
                return Json(new { success = false, message = "No active school year found. Please contact the administrator." });

            // Find fee for selected semester in current school year
            var targetFee = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.SchoolYearId == currentSchoolYear.SchoolYearId
                                       && f.Semester     == semester);

            if (targetFee == null)
                return Json(new { success = false, message = $"No fee set for {semesterInput} Semester of {currentSchoolYear.YearStart}–{currentSchoolYear.YearEnd}. Please set the fee in Settings first." });

            var student = await _context.Users
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.SchoolYear)
                .Include(u => u.Account)
                .FirstOrDefaultAsync(u => u.UserId == request.UserId
                                       && u.Account != null
                                       && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                                       && u.Account.RequestStatus == RequestStatus.Approved);

            if (student == null)
                return Json(new { success = false, message = "Student not found." });

            if (!IsFeeApplicableToStudent(student.AcademicProfile, targetFee))
                return Json(new { success = false, message = "This student is not charged for this semester because they entered after it." });

            var userIdStr = HttpContext.Session.GetString("UserId");
            if (!int.TryParse(userIdStr, out var receivedBy))
                return Json(new { success = false, message = "Invalid session." });

            // Check if student already has a Paid record for this fee
            var existingPayment = await _context.OrgFeePayments
                .FirstOrDefaultAsync(p => p.UserId       == request.UserId
                                       && p.FullAmountId == targetFee.FullAmountId
                                       && p.PaymentStatus == PaymentStatus.Paid);

            if (existingPayment != null)
                return Json(new { success = false, message = $"This student has already fully paid for the {semesterInput} Semester." });

            // ── KEY FIX: sum all previous partial payments first ──
            var previouslyPaid = await _context.OrgFeePayments
                .Where(p => p.UserId == request.UserId && p.FullAmountId == targetFee.FullAmountId)
                .SumAsync(p => p.Amount);

            var cumulativeTotal = previouslyPaid + request.Amount;

            var payment = new OrgFeePayment
            {
                UserId        = request.UserId,
                FullAmountId  = targetFee.FullAmountId,
                Amount        = request.Amount,
                PaymentStatus = cumulativeTotal >= targetFee.Amount
                                    ? PaymentStatus.Paid
                                    : PaymentStatus.Partial,
                ReceivedBy    = receivedBy,
                PaymentDate   = DateTime.Now
            };

            _context.OrgFeePayments.Add(payment);
            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber))
            {
                _context.Receipts.Add(new Receipt
                {
                    ReceiptNumber = request.ReceiptNumber,
                    PaymentId     = payment.PaymentId,
                    IssuedBy      = receivedBy
                });
                await _context.SaveChangesAsync();
            }

            return Json(new
            {
                success   = true,
                message   = "Payment recorded successfully.",
                paymentId = payment.PaymentId
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddOtherFund([FromBody] AddOtherFundRequest request)
    {
        try
        {
            var userIdStr = HttpContext.Session.GetString("UserId");
            if (!int.TryParse(userIdStr, out var receivedBy))
                return Json(new { success = false, message = "Invalid session." });

            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            var fund = new OtherFund
            {
                Source = request.Source,
                Description = request.Description,
                Amount = request.Amount,
                ReceivedBy = receivedBy,
                ReceivedDate = request.ReceivedDate ?? DateTime.Now,
                SchoolYearId = currentSchoolYear?.SchoolYearId
            };

            _context.OtherFunds.Add(fund);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Fund recorded successfully.", fundId = fund.FundId });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddExpense([FromBody] AddExpenseRequest request)
    {
        try
        {
            var userIdStr = HttpContext.Session.GetString("UserId");
            if (!int.TryParse(userIdStr, out var recordedBy))
                return Json(new { success = false, message = "Invalid session." });

            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            var expense = new Expense
            {
                Description = request.Description,
                Amount = request.Amount,
                RecordedBy = recordedBy,
                ExpenseDate = request.ExpenseDate ?? DateTime.Now,
                SchoolYearId = currentSchoolYear?.SchoolYearId
            };

            _context.Expenses.Add(expense);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Expense recorded successfully.", expenseId = expense.ExpenseId });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteOrgFeePayment([FromBody] DeleteTransactionRequest request)
    {
        try
        {
            var payment = await _context.OrgFeePayments
                .Include(p => p.Receipts)
                .FirstOrDefaultAsync(p => p.PaymentId == request.Id);

            if (payment == null)
                return Json(new { success = false, message = "Payment not found." });

            if (payment.Receipts.Any())
                _context.Receipts.RemoveRange(payment.Receipts);

            _context.OrgFeePayments.Remove(payment);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Payment deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteOtherFund([FromBody] DeleteTransactionRequest request)
    {
        try
        {
            var fund = await _context.OtherFunds.FindAsync(request.Id);
            if (fund == null)
                return Json(new { success = false, message = "Fund not found." });

            _context.OtherFunds.Remove(fund);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Fund deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> UpdateOtherFund([FromBody] UpdateOtherFundRequest request)
    {
        try
        {
            var fund = await _context.OtherFunds.FindAsync(request.Id);
            if (fund == null)
                return Json(new { success = false, message = "Fund not found." });

            fund.Source      = request.Source;
            fund.Description = request.Description;
            fund.Amount      = request.Amount;
            fund.ReceivedDate = request.ReceivedDate ?? fund.ReceivedDate;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Fund updated successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> UpdateOrgFeePayment([FromBody] UpdateOrgFeePaymentRequest request)
    {
        try
        {
            var payment = await _context.OrgFeePayments
                .Include(p => p.Receipts)
                .FirstOrDefaultAsync(p => p.PaymentId == request.Id);

            if (payment == null)
                return Json(new { success = false, message = "Payment not found." });

            payment.Amount = request.Amount;
            payment.PaymentStatus = request.Amount >= request.FullAmount
                ? PaymentStatus.Paid
                : PaymentStatus.Partial;

            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber))
            {
                var receipt = payment.Receipts.FirstOrDefault();
                if (receipt != null)
                    receipt.ReceiptNumber = request.ReceiptNumber;
                else
                {
                    var userIdStr = HttpContext.Session.GetString("UserId");
                    int.TryParse(userIdStr, out var issuedBy);
                    _context.Receipts.Add(new Receipt
                    {
                        ReceiptNumber = request.ReceiptNumber,
                        PaymentId     = payment.PaymentId,
                        IssuedBy      = issuedBy
                    });
                }
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Payment updated successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteExpense([FromBody] DeleteTransactionRequest request)
    {
        try
        {
            var expense = await _context.Expenses.FindAsync(request.Id);
            if (expense == null)
                return Json(new { success = false, message = "Expense not found." });

            _context.Expenses.Remove(expense);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Expense deleted successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> UpdateExpense([FromBody] UpdateExpenseRequest request)
    {
        try
        {
            var expense = await _context.Expenses.FindAsync(request.Id);
            if (expense == null)
                return Json(new { success = false, message = "Expense not found." });

            expense.Description = request.Description;
            expense.Amount      = request.Amount;
            expense.ExpenseDate = request.ExpenseDate ?? expense.ExpenseDate;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Expense updated successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentsForPayment(string? q = null, string? semester = null)
    {
        try
        {
            // Parse semester filter if provided
            Semester? semFilter = null;
            if (!string.IsNullOrWhiteSpace(semester))
            {
                if (semester.Equals("First", StringComparison.OrdinalIgnoreCase)) semFilter = Semester.First;
                if (semester.Equals("Second", StringComparison.OrdinalIgnoreCase)) semFilter = Semester.Second;
            }

            // Get current school year
            var currentSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            // Find fee for selected semester
            FullAmount? targetFee = null;
            if (currentSchoolYear != null && semFilter.HasValue)
            {
                targetFee = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .FirstOrDefaultAsync(f => f.SchoolYearId == currentSchoolYear.SchoolYearId
                                           && f.Semester     == semFilter.Value);
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
                             && p.PaymentStatus == PaymentStatus.Paid)
                    .Select(p => p.UserId)
                    .ToListAsync()
                : new List<int>();

            var students = users
                .Where(u => targetFee == null || IsFeeApplicableToStudent(u.AcademicProfile, targetFee))
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
                              && paidUserIds.Contains(u.UserId)
                })
                .OrderBy(s => s.name)
                .ToList();

            return Json(new { success = true, students });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAvailableSemesters()
    {
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetCollectableOrgFee()
    {
        try
        {
            // Only the current active semester fee
            var currentFee = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.SemesterStatus == SemesterStatus.Current);

            if (currentFee == null)
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

            // All payments for the current fee
            var payments = await _context.OrgFeePayments
                .Where(p => p.FullAmountId == currentFee.FullAmountId)
                .ToListAsync();

            decimal totalCollectable = 0;

            foreach (var student in students.Where(u => IsFeeApplicableToStudent(u.AcademicProfile, currentFee)))
            {
                var totalPaid = payments
                    .Where(p => p.UserId == student.UserId)
                    .Sum(p => p.Amount);

                // Only count if they haven't fully paid yet
                if (totalPaid < currentFee.Amount)
                    totalCollectable += currentFee.Amount - totalPaid;
            }

            return Json(new { success = true, collectable = totalCollectable });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message, collectable = 0 });
        }
    }

    [HttpGet]
    public async Task<IActionResult> DebugCollectable()
    {
        var fees = await _context.FullAmounts
            .Include(f => f.SchoolYear)
            .Select(f => new {
                f.FullAmountId,
                schoolYear = f.SchoolYear.YearStart + "-" + f.SchoolYear.YearEnd,
                f.Semester,
                f.Amount,
                status = f.SemesterStatus.ToString()
            })
            .ToListAsync();

        var studentCount = await _context.Users
            .Include(u => u.Account)
            .Include(u => u.AcademicProfile)
            .Where(u => u.Account != null
                     && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                     && u.Account.IsActive
                     && u.Account.RequestStatus == RequestStatus.Approved
                     && (u.AcademicProfile == null 
                         || u.AcademicProfile.AcademicStatus == AcademicStatus.Enrolled))
            .CountAsync();

        return Json(new { fees, enrolledStudentCount = studentCount });
    }

    // ----------------------------------------------------------------
    // EMAIL OTP FOR SIGNUP
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> SendEmailOTP([FromBody] SendOtpRequest req)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.Email))
                return Json(new { success = false, message = "Email is required." });

            if (!IsValidEmail(req.Email))
                return Json(new { success = false, message = "Invalid email format." });

            // Check if email already exists
            var existingAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Email != null && a.Email.ToLower() == req.Email.ToLower());

            if (existingAccount != null)
                return Json(new { success = false, message = "Email already registered." });

            // Generate 6-digit OTP
            var otp = new Random().Next(100000, 999999).ToString();
            var cacheKey = $"signup_otp_{req.Email.ToLower()}";
            
            // Store OTP in session
            HttpContext.Session.SetString(cacheKey, otp);
            HttpContext.Session.SetString($"{cacheKey}_expires", DateTime.UtcNow.AddMinutes(10).ToString("O"));

            // Send email
            var emailBody = $@"Hello,<br><br>
Your SSG verification code is:<br><br>
<strong>{otp}</strong><br><br>
This code expires in 10 minutes.<br><br>
If you did not request this, please ignore this email.<br><br>
Best regards,<br>SSG Financial Management System";

            await _emailService.SendEmailAsync(req.Email, "Your SSG Verification Code", emailBody);

            return Json(new { success = true, message = "Code sent to your email." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to send code: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> VerifyEmailOTP([FromBody] VerifyOtpRequest req)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Code))
                return Json(new { success = false, message = "Email and code are required." });

            var cacheKey = $"signup_otp_{req.Email.ToLower()}";
            var storedOtp = HttpContext.Session.GetString(cacheKey);
            var expiresStr = HttpContext.Session.GetString($"{cacheKey}_expires");

            if (string.IsNullOrEmpty(storedOtp) || string.IsNullOrEmpty(expiresStr))
                return Json(new { success = false, message = "No code found for this email." });

            if (DateTime.UtcNow > DateTime.Parse(expiresStr))
            {
                HttpContext.Session.Remove(cacheKey);
                HttpContext.Session.Remove($"{cacheKey}_expires");
                return Json(new { success = false, message = "Code has expired." });
            }

            if (storedOtp != req.Code)
                return Json(new { success = false, message = "Invalid code." });

            // Clear OTP after successful verification
            HttpContext.Session.Remove(cacheKey);
            HttpContext.Session.Remove($"{cacheKey}_expires");

            return Json(new { success = true, message = "Email verified successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Verification failed: {ex.Message}" });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetNextReceiptNumber()
    {
        try
        {
            var last = await _context.Receipts
                .OrderByDescending(r => r.ReceiptId)
                .FirstOrDefaultAsync();

            int nextNum = 1;
            if (last != null)
            {
                // Extract number from format "OR-2026-001"
                var parts = last.ReceiptNumber.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out int lastNum))
                    nextNum = lastNum + 1;
            }

            var year     = DateTime.Now.Year;
            var receipt  = $"OR-{year}-{nextNum:D3}";

            return Json(new { success = true, receiptNumber = receipt });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    private async Task<TreasurerSignature?> GetActiveTreasurerSignatureAsync(int accountOrUserId)
    {
        var signature = await _context.TreasurerSignatures
            .Where(s => s.AccountId == accountOrUserId && s.IsActive)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (signature != null)
            return signature;

        var mappedAccountId = await _context.Users
            .Where(u => u.UserId == accountOrUserId)
            .Select(u => (int?)u.AccountId)
            .FirstOrDefaultAsync();

        if (mappedAccountId == null || mappedAccountId.Value == accountOrUserId)
            return null;

        return await _context.TreasurerSignatures
            .Where(s => s.AccountId == mappedAccountId.Value && s.IsActive)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    // ----------------------------------------------------------------
    // TREASURER SIGNATURE
    // ----------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> SaveTreasurerSignature([FromBody] SaveSignatureRequest request)
    {
        try
        {
            var role = HttpContext.Session.GetString("UserRole");
            if (!string.Equals(role, UserRole.Treasurer.ToString(), StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = "Only treasurers can save signatures." });

            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var accountId))
                return Json(new { success = false, message = "Invalid session." });

            if (string.IsNullOrWhiteSpace(request.SignatureData))
                return Json(new { success = false, message = "Signature data is required." });

            if (!request.SignatureData.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = "Invalid signature image format." });

            var previous = await _context.TreasurerSignatures
                .Where(s => s.AccountId == accountId && s.IsActive)
                .ToListAsync();

            foreach (var signature in previous)
                signature.IsActive = false;

            _context.TreasurerSignatures.Add(new TreasurerSignature
            {
                AccountId = accountId,
                SignatureData = request.SignatureData,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            });

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Signature saved successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetMySignature()
    {
        try
        {
            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var accountId))
                return Json(new { success = false, message = "Invalid session." });

            var signature = await GetActiveTreasurerSignatureAsync(accountId);

            return Json(new
            {
                success = signature != null,
                signatureData = signature?.SignatureData,
                createdAt = signature?.CreatedAt
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetSignatureByAccountId(int accountId)
    {
        try
        {
            var signature = await GetActiveTreasurerSignatureAsync(accountId);

            return Json(new
            {
                success = signature != null,
                signatureData = signature?.SignatureData,
                createdAt = signature?.CreatedAt
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAllTreasurerSignatures()
    {
        try
        {
            var signatures = await _context.TreasurerSignatures
                .Include(s => s.Account)
                    .ThenInclude(a => a!.User)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.SignatureId,
                    s.AccountId,
                    s.SignatureData,
                    s.CreatedAt,
                    s.IsActive,
                    treasurerName = s.Account != null && s.Account.User != null
                        ? ((s.Account.User.FirstName ?? "") + " " + (s.Account.User.LastName ?? "")).Trim()
                        : "Unknown"
                })
                .ToListAsync();

            return Json(new { success = true, signatures });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> EditFee([FromBody] EditFeeRequest request)
    {
        try
        {
            var fee = await _context.FullAmounts.FindAsync(request.FullAmountId);
            if (fee == null)
                return Json(new { success = false, message = "Fee record not found." });

            fee.Amount = request.Amount;
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Fee amount updated successfully." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SetFeeStatus([FromBody] SetFeeStatusRequest request)
    {
        try
        {
            // If marking as Current, demote all others first
            if (request.Status == "Current")
            {
                var currentFees = await _context.FullAmounts
                    .Where(f => f.SemesterStatus == SemesterStatus.Current)
                    .ToListAsync();
                foreach (var f in currentFees)
                    f.SemesterStatus = SemesterStatus.Ended;
            }

            var fee = await _context.FullAmounts.FindAsync(request.FullAmountId);
            if (fee == null)
                return Json(new { success = false, message = "Fee record not found." });

            fee.SemesterStatus = request.Status == "Current"
                ? SemesterStatus.Current
                : SemesterStatus.Ended;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = $"Status changed to {request.Status}." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> SearchAllStudentsWithPaymentStatus(string? q = null)
    {
        try
        {
            var currentFee = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .FirstOrDefaultAsync(f => f.SemesterStatus == SemesterStatus.Current);

            var studentsQuery = _context.Users
                .Include(u => u.Account)
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
                .Where(u => u.Account != null
                         && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                         && u.Account.IsActive
                         && u.Account.RequestStatus == RequestStatus.Approved);

            var query = (q ?? string.Empty).Trim().ToLower();
            if (!string.IsNullOrEmpty(query))
            {
                studentsQuery = studentsQuery.Where(u =>
                    (u.FirstName != null && u.FirstName.ToLower().Contains(query)) ||
                    (u.LastName  != null && u.LastName.ToLower().Contains(query))  ||
                    (u.Account!.SchoolId != null && u.Account.SchoolId.ToLower().Contains(query)) ||
                    (u.AcademicProfile != null && u.AcademicProfile.Course != null &&
                     u.AcademicProfile.Course.CourseCode.ToLower().Contains(query)));
            }

            var students = await studentsQuery.ToListAsync();

            var payments = currentFee != null
                ? await _context.OrgFeePayments
                    .Include(p => p.Receipts)
                    .Where(p => p.FullAmountId == currentFee.FullAmountId)
                    .ToListAsync()
                : new List<OrgFeePayment>();

            var result = students.Select(u =>
            {
                var studentPayments = payments.Where(p => p.UserId == u.UserId).ToList();
                var totalPaid       = studentPayments.Sum(p => p.Amount);
                var isApplicable    = currentFee != null && IsFeeApplicableToStudent(u.AcademicProfile, currentFee);
                var required        = isApplicable ? currentFee?.Amount ?? 0 : 0;
                var lastPayment     = studentPayments.OrderByDescending(p => p.PaymentDate).FirstOrDefault();

                string status = !isApplicable        ? "N/A"
                              : totalPaid <= 0       ? "Unpaid"
                              : totalPaid >= required ? "Paid"
                                                      : "Partial";

                var receipt = lastPayment?.Receipts?.OrderBy(r => r.ReceiptId).FirstOrDefault();

                return new
                {
                    userId         = u.UserId,
                    schoolId       = u.Account!.SchoolId,
                    name           = $"{(u.LastName ?? "").ToUpper()}, {(u.FirstName ?? "").ToUpper()}",
                    courseCode     = u.AcademicProfile?.Course?.CourseCode ?? "N/A",
                    yearSection    = u.AcademicProfile != null
                        ? $"{u.AcademicProfile.YearLevel?.ToString() ?? "N/A"}-{u.AcademicProfile.Section ?? "N/A"}"
                        : "N/A",
                    role           = u.Account.Role.ToString(),
                    status,
                    totalPaid,
                    requiredAmount = required,
                    paymentDate    = lastPayment?.PaymentDate,
                    receiptNumber  = receipt?.ReceiptNumber,
                    schoolYear     = currentFee != null
                        ? $"{currentFee.SchoolYear.YearStart}–{currentFee.SchoolYear.YearEnd}" : null,
                    semester       = currentFee?.Semester.ToString()
                };
            })
            .OrderBy(s => s.name)
            .ToList();

            return Json(new { success = true, students = result });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    }

// ----------------------------------------------------------------
// REQUEST / RESPONSE MODELS
// ----------------------------------------------------------------

public class LoginRequest
{
    public string  SchoolId  { get; set; } = string.Empty;
    public string  Password  { get; set; } = string.Empty;
    public string? Email     { get; set; }
    public string? StudentId { get; set; }
    public string? Role      { get; set; }
}

public class ForgotPasswordRequest
{
    public string StudentId { get; set; } = string.Empty;
}

public class VerifyCodeRequest
{
    public string StudentId { get; set; } = string.Empty;
    public string Code      { get; set; } = string.Empty;
}

public class DeactivateRequest
{
    public int AccountId { get; set; }
}

public class ResetPasswordRequest
{
    public string Token       { get; set; } = string.Empty;
    public string StudentId   { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword     { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    public string Token     { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
}

public class UpdateAccountStatusRequest
{
    public int           AccountId { get; set; }
    public RequestStatus Status    { get; set; }
}

public class UpdateStudentRequest
{
    public int     AccountId      { get; set; }
    public string? FirstName      { get; set; }
    public string? LastName       { get; set; }
    public string? MiddleName     { get; set; }
    public string? Email          { get; set; }
    public int     CourseId       { get; set; }
    public int?    YearLevel      { get; set; }
    public string? Section        { get; set; }
    public string? AcademicStatus { get; set; }  // add this
}

public class UpdateProfessorRequest
{
    public int     AccountId  { get; set; }
    public string? FirstName  { get; set; }
    public string? LastName   { get; set; }
    public string? MiddleName { get; set; }
    public string? Email      { get; set; }
    public string? SchoolId   { get; set; }
}

public class ChangeRoleRequest
{
    public int    AccountId { get; set; }
    public string Role      { get; set; } = string.Empty;
}

public class AddProfessorRequest
{
    public string  FirstName  { get; set; } = string.Empty;
    public string  LastName   { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string  SchoolId   { get; set; } = string.Empty;
    public string? Email      { get; set; }
    public string  Password   { get; set; } = string.Empty;
}


    public class AddSchoolYearRequest
    {
        public int YearStart { get; set; }
        public int YearEnd   { get; set; }
    }

public class DeleteSchoolYearRequest
{
    public int SchoolYearId { get; set; }
}

public class AddCourseRequest
{
    public string  CourseCode { get; set; } = string.Empty;
    public string? CourseName { get; set; }
}

public class DeleteCourseRequest
{
    public int CourseId { get; set; }
}

public class SetFeeAmountRequest
{
    public int     SchoolYearId { get; set; }
    public string  Semester     { get; set; } = string.Empty;
    public decimal Amount       { get; set; }
}

public class DeleteFeeRequest
{
    public int FullAmountId { get; set; }
}

public class AddOrgFeePaymentRequest
{
    public int     UserId        { get; set; }
    public decimal Amount        { get; set; }
    public string? Semester      { get; set; }  // add this
    public string? ReceiptNumber { get; set; }
}


    public class AddOtherFundRequest
    {
        public string? Source { get; set; }
        public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTime? ReceivedDate { get; set; }
}

public class AddExpenseRequest
{
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTime? ExpenseDate { get; set; }
}

public class DeleteTransactionRequest
{
    public int Id { get; set; }
}

public class UpdateOtherFundRequest
{
    public int       Id           { get; set; }
    public string?   Source       { get; set; }
    public string?   Description  { get; set; }
    public decimal   Amount       { get; set; }
    public DateTime? ReceivedDate { get; set; }
}

public class UpdateOrgFeePaymentRequest
{
    public int     Id            { get; set; }
    public decimal Amount        { get; set; }
    public decimal FullAmount    { get; set; }
    public string? ReceiptNumber { get; set; }
}

public class UpdateExpenseRequest
{
    public int       Id          { get; set; }
    public string?   Description { get; set; }
    public decimal   Amount      { get; set; }
    public DateTime? ExpenseDate { get; set; }
}

public class SaveSignatureRequest
{
    public string SignatureData { get; set; } = string.Empty;
}

public record SendOtpRequest(string Email);
public record VerifyOtpRequest(string Email, string Code);

public class EditFeeRequest
{
    public int     FullAmountId { get; set; }
    public decimal Amount       { get; set; }
}

public class SetFeeStatusRequest
{
    public int    FullAmountId { get; set; }
    public string Status       { get; set; } = string.Empty;
}

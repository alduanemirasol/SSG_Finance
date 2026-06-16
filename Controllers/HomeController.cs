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
    // Authorization guards (GetSessionRole / RequireRole / RequireAnyRole) moved to AppController base.

    private readonly IAuthService _authService;
    private readonly IEmailService _emailService;
    private readonly ApplicationDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly SseService _sse;

    // Login-lockout helpers (IsTemporarilyLocked / RecordFailedAttempt / RecordSuccessfulLogin /
    // BuildLoginKey) moved to AppController base.

    public HomeController(IAuthService authService, IEmailService emailService, ApplicationDbContext context, IWebHostEnvironment env, SseService sse)
    {
        _authService = authService;
        _emailService = emailService;
        _context = context;
        _env = env;
        _sse = sse;
    }

    // ----------------------------------------------------------------
    // SERVER-SENT EVENTS
    // ----------------------------------------------------------------

    [HttpGet]
    public async Task Events(CancellationToken ct)
    {
        var role = GetSessionRole();
        if (string.IsNullOrEmpty(role))
        {
            Response.StatusCode = 401;
            return;
        }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Connection"] = "keep-alive";

        var clientId = _sse.Subscribe(Response);

        // initial ping so the browser knows the connection is live
        await Response.WriteAsync(": ping\n\n");
        await Response.Body.FlushAsync();

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        finally { _sse.Unsubscribe(clientId); }
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

        // Was login explicitly requested (e.g. the Login nav on About/Contacts -> ?login=1)?
        bool explicitLogin = Request.Query["login"].ToString() == "1";

        // Did the visitor arrive via internal navigation (same-site referer)?
        // Clicking Home/About/Contacts within the site sends a same-host Referer;
        // a fresh entry (typed URL, bookmark, external link) does not.
        var referer = Request.Headers["Referer"].ToString();
        bool cameFromSameSite =
            !string.IsNullOrEmpty(referer) &&
            Uri.TryCreate(referer, UriKind.Absolute, out var r) &&
            string.Equals(r.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase);

        // Pre-open the login panel at first paint ONLY on a genuine fresh entry to the site.
        //  - Fresh open (no internal referer, no ?login=1)  -> login shown on load.
        //  - Home from About/Contacts (internal referer)    -> homepage shown (bug fix).
        //  - Login from About/Contacts (?login=1)           -> landing paints, JS animates login in.
        ViewBag.OpenLoginOnLoad = !cameFromSameSite && !explicitLogin;
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

    public IActionResult ProfessorSignUp()
    {
        // Redirect already-logged-in users away from the signup page
        if (HttpContext.Session.GetString("UserId") != null)
        {
            return RedirectToDashboard(HttpContext.Session.GetString("UserRole"));
        }
        return View();
    }

    // Fee-applicability rules moved to MyMvcApp.Services.FeeRules (called as FeeRules.*).

    private async Task<HashSet<(int, Semester)>> GetExemptionsForUserAsync(int userId)
    {
        var rows = await _context.StudentFeeExemptions
            .Where(e => e.UserId == userId)
            .ToListAsync();
        return rows.Select(e => (e.SchoolYearId, e.Semester)).ToHashSet();
    }

    private async Task<Dictionary<int, HashSet<(int, Semester)>>> GetAllExemptionsAsync()
    {
        var all = await _context.StudentFeeExemptions.ToListAsync();
        return all.GroupBy(e => e.UserId)
                  .ToDictionary(g => g.Key,
                                g => g.Select(e => (e.SchoolYearId, e.Semester)).ToHashSet());
    }

    // RULE: a student can't pay for a school year until every APPLICABLE fee from
    // PREVIOUS school years is fully settled. Returns the earliest such unpaid
    // prior-year fee (or null if none). Only fees that actually apply to the student
    // (per FeeRules.IsFeeApplicableToStudent) count, so a student is never blocked by
    // years before they enrolled. Shared by the payment write path (AddOrgFeePayment)
    // and the read path that drives the treasurer UI (GetStudentFeeStatus) so the two
    // can never disagree about whether a payment is allowed.
    private async Task<FullAmount?> GetEarliestUnpaidPriorYearFeeAsync(
        int userId,
        AcademicProfile? academicProfile,
        FullAmount targetFee,
        HashSet<(int, Semester)> exemptions)
    {
        if (targetFee.SchoolYear == null)
            return null;

        var previousYearFees = await _context.FullAmounts
            .Include(f => f.SchoolYear)
            .Where(f => f.SchoolYear != null
                     && f.SchoolYear.YearStart < targetFee.SchoolYear.YearStart)
            .ToListAsync();

        var applicablePrevFees = previousYearFees
            .Where(f => FeeRules.IsFeeApplicableToStudent(academicProfile, f, exemptions))
            .OrderBy(f => f.SchoolYear!.YearStart)
            .ThenBy(f => FeeRules.GetSemesterOrder(f.Semester))
            .ToList();

        if (!applicablePrevFees.Any())
            return null;

        var prevFeeIds = applicablePrevFees.Select(f => f.FullAmountId).ToList();

        var prevPayments = await _context.OrgFeePayments
            .Where(p => p.UserId == userId && prevFeeIds.Contains(p.FullAmountId))
            .ToListAsync();

        foreach (var prevFee in applicablePrevFees)
        {
            var paidForPrevFee = prevPayments
                .Where(p => p.FullAmountId == prevFee.FullAmountId)
                .Sum(p => p.Amount);

            if (paidForPrevFee < prevFee.Amount)
                return prevFee;
        }

        return null;
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

            // 2. Get all payments for this student across all fees
            var studentPayments = await _context.OrgFeePayments
                .Include(p => p.FullAmount)
                    .ThenInclude(fa => fa.SchoolYear)
                .Include(p => p.Receipts)
                .Where(p => p.UserId == userId)
                .ToListAsync();

            // Show a fee in the history if it's still applicable to the student OR if the
            // student has already paid toward it. A graduated / year-5 student no longer
            // OWES fees (IsFeeApplicableToStudent returns false), but their past payments
            // are real records and must remain visible.
            var paidFeeIds  = studentPayments.Select(p => p.FullAmountId).ToHashSet();
            var exemptions  = await GetExemptionsForUserAsync(userId);

            allFees = allFees
                .Where(f => FeeRules.IsFeeApplicableToStudent(academicProfile, f, exemptions)
                         || paidFeeIds.Contains(f.FullAmountId))
                .ToList();

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
                    // ReceivedBy stores the treasurer's account_id.
                    var treasurerUser = await _context.Users
                        .FirstOrDefaultAsync(u => u.AccountId == paymentRecord.ReceivedBy);
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

                        // IssuedBy stores the treasurer's account_id.
                        if (receipt != null)
                        {
                            receiptIssuedByAccountId = receipt.IssuedBy;
                            var issuerUser = await _context.Users
                                .FirstOrDefaultAsync(u => u.AccountId == receipt.IssuedBy);
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
                        .Where(u => u.AccountId == r.IssuedBy)
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
                    Status = r.Payment != null ? r.Payment.PaymentStatus.ToString() : "",
                    YearLevelAtPayment = r.Payment != null ? r.Payment.YearLevelAtPayment : null,
                    SectionAtPayment = r.Payment != null ? r.Payment.SectionAtPayment : null
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
                return Json(new { success = false, message = "CTU ID and password are required." });
            }

            // Apply login attempt limiting (prevents brute force)
            var loginKey = BuildLoginKey(request.SchoolId, HttpContext.Connection.RemoteIpAddress?.ToString());
            if (IsTemporarilyLocked(loginKey))
            {
                return Json(new { success = false, message = "Account temporarily locked. Try again in 15 minutes." });
            }

            // Step 1: Find account by CTU ID
            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == request.SchoolId.ToLower());

            if (account == null)
            {
                // Record failed attempt against the login key even when account doesn't exist
                var loginKeyForFailure = BuildLoginKey(request.SchoolId, HttpContext.Connection.RemoteIpAddress?.ToString());
                RecordFailedAttempt(loginKeyForFailure);
                return Json(new { success = false, message = "Invalid CTU ID or password." });
            }


            // Step 2: Check approval and active status
            if (account.RequestStatus != RequestStatus.Approved)
            {
                // Not counting this as a failed password attempt (different failure mode)
                return Json(new { success = false, message = "Your account is not yet approved. Please wait for admin approval." });
            }

            if (!account.IsActive)
            {
                // Not counting this as a failed password attempt (different failure mode)
                return Json(new { success = false, message = "Your account has been deactivated. Please contact the administrator." });
            }


            // Step 3: Verify password
            if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
            {
                RecordFailedAttempt(loginKey);
                return Json(new { success = false, message = "Invalid CTU ID or password." });
            }


            // Step 4: Load the user profile
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.AccountId == account.AccountId);

            // Step 5: Load academic profile for students
            string courseCode = "";
            string courseName = "";
            int? yearLevel = null;
            string section = "";
            
            if (user != null)
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
            // Best-effort mitigation for session fixation: clear existing session before setting new values.
            RecordSuccessfulLogin(loginKey);
            HttpContext.Session.Clear();

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
            HttpContext.Session.SetString("AvatarPath", user?.AvatarPath ?? "");

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
                    id = account.AccountId,
                    role = account.Role.ToString()
                }
            });

        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Login failed." });

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
}

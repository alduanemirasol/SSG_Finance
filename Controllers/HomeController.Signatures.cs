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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetSignatureByAccountId(int accountId)
    {
        // Any authenticated user may read a signature (students need it to render the
        // signature on their own receipts), but anonymous access is not allowed.
        if (string.IsNullOrEmpty(GetSessionRole()))
            return Json(new { success = false, message = "Unauthorized." });

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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAllTreasurerSignatures()
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> EditFee([FromBody] EditFeeRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {

            var fee = await _context.FullAmounts.FindAsync(request.FullAmountId);
            if (fee == null)
                return Json(new { success = false, message = "Fee record not found." });

            fee.Amount = request.Amount;
            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("fees-changed");

            return Json(new { success = true, message = "Fee amount updated successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SetFeeStatus([FromBody] SetFeeStatusRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

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
            await _sse.BroadcastAsync("fees-changed");
            return Json(new { success = true, message = $"Status changed to {request.Status}." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> SearchAllStudentsWithPaymentStatus(string? q = null)
    {
        var guard = RequireAnyRole("Treasurer", "Admin", "Professor");
        if (guard != null) return guard;

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

            var profDashExemptions = await GetAllExemptionsAsync();

            var result = students.Select(u =>
            {
                var studentPayments = payments.Where(p => p.UserId == u.UserId).ToList();
                profDashExemptions.TryGetValue(u.UserId, out var uExemptions);
                var totalPaid       = studentPayments.Sum(p => p.Amount);
                var isApplicable    = currentFee != null && FeeRules.IsFeeApplicableToStudent(u.AcademicProfile, currentFee, uExemptions);
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
                    name           = $"{(u.LastName ?? "").ToUpper()}, {(u.FirstName ?? "").ToUpper()}"
                                   + (!string.IsNullOrWhiteSpace(u.MiddleName) ? " " + u.MiddleName.Substring(0, 1).ToUpper() + "." : ""),
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
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }
}

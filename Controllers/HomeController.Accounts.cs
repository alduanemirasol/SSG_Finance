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
                return Json(new { success = false, message = "CTU ID and password are required." });

            if (!string.IsNullOrWhiteSpace(request.Email) && !IsValidEmail(request.Email))
                return Json(new { success = false, message = "Invalid email format." });

            if (!IsPasswordCompliant(request.Password, out var passwordPolicyError))
                return Json(new { success = false, message = passwordPolicyError ?? "Password does not meet policy requirements." });

            // Rate limiting: limit signup attempts per IP+SchoolId (prevents registration flooding)
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var signupKey = BuildLoginKey(request.SchoolId, ip);
            if (IsTemporarilyLocked(signupKey))
            {
                return Json(new { success = false, message = "Too many signup attempts. Please wait 15 minutes before trying again." });
            }

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
            return Json(new { success = false, message = "Registration failed." });

        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteAvatar(int userId)
    {
        // Only staff manage student photos (the treasurer dashboard is the sole caller).
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
            return Json(new { success = false, message = "User not found." });

        if (!string.IsNullOrEmpty(user.AvatarPath))
        {
            var cleanPath = user.AvatarPath.Split('?')[0];
            var fullPath  = Path.Combine(_env.WebRootPath, cleanPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);
        }

        user.AvatarPath = null;
        await _context.SaveChangesAsync();

        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> UploadAvatar(int accountId, IFormFile avatar)
    {
        if (avatar == null || avatar.Length == 0)
            return Json(new { success = false, message = "No file provided." });

        // Map the validated content type to a fixed, safe extension. Never derive the
        // extension from the client-supplied filename (prevents writing arbitrary file
        // types such as .html/.svg into the web root).
        var extByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"]  = ".png",
            ["image/webp"] = ".webp"
        };
        if (avatar.ContentType == null || !extByType.TryGetValue(avatar.ContentType, out var ext))
            return Json(new { success = false, message = "Only JPG, PNG, or WEBP images are allowed." });

        if (avatar.Length > 2 * 1024 * 1024)
            return Json(new { success = false, message = "Image must be 2MB or smaller." });

        // Authorization (the target is the client-supplied accountId):
        //  - A logged-in user may only change their OWN avatar. Every dashboard uploads
        //    for the session's own account, so this never breaks the normal flow.
        //  - An anonymous caller is the sign-up flow, which may only set the photo of a
        //    freshly-created account that is still awaiting approval (Pending).
        var sessionAccountIdStr = HttpContext.Session.GetString("AccountId");
        if (!string.IsNullOrEmpty(sessionAccountIdStr))
        {
            if (!int.TryParse(sessionAccountIdStr, out var sessionAccountId) || sessionAccountId != accountId)
                return new ObjectResult(new { success = false, message = "You are not authorized to perform this action." })
                    { StatusCode = StatusCodes.Status403Forbidden };
        }
        else
        {
            var targetAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.AccountId == accountId);
            if (targetAccount == null || targetAccount.RequestStatus != RequestStatus.Pending)
                return new ObjectResult(new { success = false, message = "You are not authorized to perform this action." })
                    { StatusCode = StatusCodes.Status403Forbidden };
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.AccountId == accountId);
        if (user == null)
            return Json(new { success = false, message = "User not found." });

        var fileName = $"{accountId}{ext}";
        var avatarDir = Path.Combine(_env.WebRootPath, "uploads", "avatars");
        Directory.CreateDirectory(avatarDir);

        var filePath = Path.Combine(avatarDir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
            await avatar.CopyToAsync(stream);

        var versionedPath = $"/uploads/avatars/{fileName}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        user.AvatarPath = versionedPath;
        await _context.SaveChangesAsync();

        HttpContext.Session.SetString("AvatarPath", versionedPath);

        return Json(new { success = true, avatarPath = versionedPath });
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
            return Json(new { success = false, message = "Failed to process request." });

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
            return Json(new { success = false, message = "Failed to verify code." });

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

            if (!IsPasswordCompliant(request.NewPassword, out var passwordPolicyError))
                return Json(new { success = false, message = passwordPolicyError ?? "Password does not meet policy requirements." });


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
            return Json(new { success = false, message = "Failed to reset password." });

        }
    }

    // ----------------------------------------------------------------
    // ACCOUNT MANAGEMENT
    // ----------------------------------------------------------------

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccountStatus([FromBody] UpdateAccountStatusRequest request)

    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

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
            You can now log in to the SSG Finance system using your CTU ID and password.
        </p>

        <table style='width:100%;border-collapse:collapse;margin-bottom:24px;'>
            <tr style='background:#f0f9f4;'>
                <td style='padding:12px 16px;font-weight:600;color:#1a7a4a;width:40%;'>CTU ID</td>
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
                <td style='padding:12px 16px;font-weight:600;color:#dc2626;width:40%;'>CTU ID</td>
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
                                        await _sse.BroadcastAsync("accounts-changed");
                                        return Json(new { success = true, message = $"Account {request.Status} successfully, but email notification could not be sent." });
                                }
                        }

                        await _sse.BroadcastAsync("accounts-changed");
                        return Json(new { success = true, message = $"Account {request.Status} successfully. Email notification sent." });
                }
                catch (Exception ex)
                {
                        return Json(new { success = false, message = "Failed to update account status." });

                }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAccountActivation([FromBody] DeactivateRequest request)

    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        try
        {

            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.AccountId == request.AccountId);

            if (account == null)
                return Json(new { success = false, message = "Account not found." });

            account.IsActive = !account.IsActive;
            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("accounts-changed");

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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAccount([FromBody] DeactivateRequest request)

    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        try
        {

            var account = await _context.Accounts
                .Include(a => a.User)
                    .ThenInclude(u => u!.AcademicProfile)
                .FirstOrDefaultAsync(a => a.AccountId == request.AccountId);

            if (account == null)
                return Json(new { success = false, message = "Account not found." });

            // An approved student must be no longer active — either Dropped, or completed
            // (year level 5) — before they can be deleted, so a currently-enrolled student
            // can't be removed by mistake. Pending accounts (being rejected from the
            // requests list) and non-students are unaffected.
            if (account.RequestStatus == RequestStatus.Approved
                && account.User?.AcademicProfile != null
                && account.User.AcademicProfile.AcademicStatus != AcademicStatus.Dropped
                && account.User.AcademicProfile.YearLevel != 5)
            {
                return Json(new { success = false, message = "Only dropped or graduated students can be deleted. Set the student's status to Dropped first if they are still enrolled." });
            }

            // 1. delete academic profile first
            if (account.User?.AcademicProfile != null)
                _context.AcademicProfiles.Remove(account.User.AcademicProfile);

            // 2. delete user
            if (account.User != null)
                _context.Users.Remove(account.User);

            // 3. delete account
            _context.Accounts.Remove(account);

            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("accounts-changed");

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
        var guard = RequireRole("Admin");
        if (guard != null) return guard;
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStudent([FromBody] UpdateStudentRequest request)

    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

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
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfessor([FromBody] UpdateProfessorRequest request)

    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

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

    [HttpGet]
    public async Task<IActionResult> CheckSchoolId(string schoolId)
    {
        var taken = await _context.Accounts
            .AnyAsync(a => a.SchoolId.ToLower() == schoolId.ToLower());
        return Json(new { taken });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeRole([FromBody] ChangeRoleRequest request)

    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

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
            await _sse.BroadcastAsync("accounts-changed");

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
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        var requests = await GetPendingAccountsAsync();
        return Json(new { success = true, requests });
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentsList()
    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        var students = await GetStudentsAsync();
        return Json(new { success = true, students });
    }

    [HttpGet]
    public async Task<IActionResult> GetTreasurersList()
    {
        var guard = RequireRole("Admin");   // ← ADD THIS
        if (guard != null) return guard;    // ← AND THIS

        var treasurers = await GetTreasurersAsync();
        return Json(new { success = true, treasurers });
    }

    [HttpGet]
    public async Task<IActionResult> GetProfessorsList()
    {
        var guard = RequireRole("Admin");   // ← ADD THIS
        if (guard != null) return guard;    // ← AND THIS
        
        var professors = await GetProfessorsAsync();
        return Json(new { success = true, professors });
    }

    [HttpGet]
    public async Task<IActionResult> GetAdminsList()
    {
        var guard = RequireRole("Admin");
        if (guard != null) return guard;

        var admins = await GetAdminsAsync();
        return Json(new { success = true, admins });
    }

    [HttpGet]
    public async Task<IActionResult> GetDashboardStats()
    {
        var guard = RequireRole("Admin");   // ← ADD THIS
        if (guard != null) return guard;    // ← AND THIS

        var allStudents    = await GetStudentsAsync();
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
            studentCount   = studentCount + treasurerCount,
            treasurerCount = treasurerCount,
            professorCount = professors.Count,
            adminCount     = admins.Count,
            recentRequests = allRequests.Select(r => new {
                r.AccountId,
                r.SchoolId,
                r.Fullname,
                r.CourseCode,
                r.YearLevel,
                r.Section,
                r.Role,
                r.Status,
                createdAt = r.CreatedAt.ToString("MM-dd-yyyy")
            })
        });
    }

    // ----------------------------------------------------------------
    // STUDENT DASHBOARD POLLING (auto-refresh)
    // ----------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> GetStudentDashboardData()
    {
        var userIdStr = HttpContext.Session.GetString("UserId");
        if (!int.TryParse(userIdStr, out var userId))
            return Json(new { success = false });

        try
        {
            // Build totals from the same underlying sources used by Dashboard()
            // OrgFeePayments holds transactions; FullAmounts holds required fee amounts.

// Get the student's academic profile to know which fees apply to them
            var academicProfile = await _context.AcademicProfiles
                .Include(ap => ap.SchoolYear)
                .FirstOrDefaultAsync(ap => ap.UserId == userId);

            // Start from ALL fees, then filter to the ones applicable to this student
            var allFees = await _context.FullAmounts
                .Include(f => f.SchoolYear)
                .ToListAsync();

            var exemptions     = await GetExemptionsForUserAsync(userId);
            var applicableFees = allFees
                .Where(f => FeeRules.IsFeeApplicableToStudent(academicProfile, f, exemptions))
                .ToList();

            // Get all of this student's payments
            var paymentHistory = await _context.OrgFeePayments
                .Where(p => p.UserId == userId)
                .ToListAsync();

            decimal outstandingBalance = 0;
            int pendingCount = 0;

            foreach (var fee in applicableFees)
            {
                var paidForFee = paymentHistory
                    .Where(p => p.FullAmountId == fee.FullAmountId)
                    .Sum(p => p.Amount);

                var balance = fee.Amount - paidForFee;

                if (balance > 0)
                {
                    outstandingBalance += balance;
                    pendingCount++;
                }
            }

            var totalPaid = paymentHistory.Sum(p => p.Amount);
            var paymentCount = paymentHistory.Count();

            return Json(new {
                success = true,
                outstandingBalance,
                totalPaid,
                pendingCount,
                paymentCount
            });
        }
        catch
        {
            return Json(new { success = false });
        }
    }

    // ----------------------------------------------------------------
    // DEBUG / TEST (remove before production)
    // ----------------------------------------------------------------

   
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
            return string.Equals(addr.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPasswordCompliant(string? password, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(password))
        {
            errorMessage = "Password is required.";
            return false;
        }

        // Minimum length: 8
        if (password.Length < 8)
        {
            errorMessage = "Password must be at least 8 characters long.";
            return false;
        }

        // No whitespace-only passwords, and disallow spaces/tabs/newlines inside.
        if (password.Any(char.IsWhiteSpace))
        {
            errorMessage = "Password must not contain spaces.";
            return false;
        }

        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

        if (!hasUpper || !hasLower || !hasDigit || !hasSpecial)
        {
            errorMessage = "Password must include uppercase, lowercase, number, and special character.";
            return false;
        }

        return true;
    }

    private async Task<List<RequestedAccountViewModel>> GetPendingAccountsAsync()
    {
        return await _context.Accounts
            .Where(a => a.RequestStatus == RequestStatus.Pending 
         && (a.Role == UserRole.Student || a.Role == UserRole.Professor))
            .Include(a => a.User)
                .ThenInclude(u => u!.AcademicProfile)
                    .ThenInclude(ap => ap!.Course)
            .Select(a => new RequestedAccountViewModel
            {
                AccountId  = a.AccountId,
                SchoolId   = a.SchoolId,
                Fullname   = a.User != null
                    ? $"{(a.User.LastName != null ? a.User.LastName.ToUpper() : "")}, {(a.User.FirstName != null ? a.User.FirstName.ToUpper() : "")}"
                      + (a.User.MiddleName != null && a.User.MiddleName.Length > 0 ? " " + a.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                    : a.SchoolId.ToUpper(),
                CourseCode = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.Course != null
                    ? a.User.AcademicProfile.Course.CourseCode : null,
                YearLevel  = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.YearLevel != null
                    ? a.User.AcademicProfile.YearLevel.ToString() : null,
                Section    = a.User != null && a.User.AcademicProfile != null
                    ? a.User.AcademicProfile.Section : null,
                Role       = a.Role.ToString(),
                CreatedAt  = a.CreatedAt,
                Status     = a.RequestStatus,
                AvatarPath = a.User != null ? a.User.AvatarPath : null
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
                      + (a.User.MiddleName != null && a.User.MiddleName.Length > 0 ? " " + a.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                    : a.SchoolId.ToUpper(),
                CourseCode = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.Course != null
                    ? a.User.AcademicProfile.Course.CourseCode : null,
                YearLevel  = a.User != null && a.User.AcademicProfile != null && a.User.AcademicProfile.YearLevel != null
                    ? a.User.AcademicProfile.YearLevel.ToString() : null,
                Section    = a.User != null && a.User.AcademicProfile != null
                    ? a.User.AcademicProfile.Section : null,
                Role       = a.Role.ToString(),
                CreatedAt  = a.CreatedAt,
                Status     = a.RequestStatus,
                AvatarPath = a.User != null ? a.User.AvatarPath : null
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
                    ? $"{u.LastName.ToUpper()}, {u.FirstName.ToUpper()}"
                      + (u.MiddleName != null && u.MiddleName.Length > 0 ? " " + u.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                    : "N/A",
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
                SemesterEntered = u.AcademicProfile != null ? u.AcademicProfile.SemesterEntered : null,
                AvatarPath = u.AvatarPath
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
                    ? $"{a.User.LastName.ToUpper()}, {a.User.FirstName.ToUpper()}"
                      + (a.User.MiddleName != null && a.User.MiddleName.Length > 0 ? " " + a.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                    : "N/A",
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
                    ? $"{a.User.LastName.ToUpper()}, {a.User.FirstName.ToUpper()}"
                      + (a.User.MiddleName != null && a.User.MiddleName.Length > 0 ? " " + a.User.MiddleName.Substring(0, 1).ToUpper() + "." : "")
                    : "N/A",
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

}

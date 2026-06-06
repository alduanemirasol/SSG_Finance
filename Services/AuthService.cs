using Microsoft.EntityFrameworkCore;
using MyMvcApp.Data;
using MyMvcApp.Models;
 
namespace MyMvcApp.Services
{
    public class AuthService : IAuthService
    {
        private readonly ApplicationDbContext _context;
 
        public AuthService(ApplicationDbContext context)
        {
            _context = context;
        }
 
        public async Task<AuthResult> AuthenticateAsync(string email, string password, UserRole role)
        {
            try
            {
                // Find account by email and role
                var account = await _context.Accounts
                    .Include(a => a.User)
                    .ThenInclude(u => u!.AcademicProfile)
                    .FirstOrDefaultAsync(a => a.Email != null && a.Email.ToLower() == email.ToLower() && a.Role == role);
 
                if (account == null)
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = $"No {role} account found with this email address." 
                    };
                }
 
                // Check if account is approved
                if (account.RequestStatus != RequestStatus.Approved)
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = $"Account is {account.RequestStatus.ToString().ToLower()}. Please contact administrator." 
                    };
                }
 
                // Verify password
                if (!VerifyPassword(password, account.PasswordHash))
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = "Invalid password." 
                    };
                }
 
                // Update active status
                account.IsActive = true;
                await _context.SaveChangesAsync();
 
                return new AuthResult 
                { 
                    Success = true, 
                    Message = "Authentication successful.",
                    Account = account,
                    User = account.User
                };
            }
            catch (Exception ex)
            {
                return new AuthResult 
                { 
                    Success = false, 
                    Message = $"An error occurred during authentication: {ex.Message}" 
                };
            }
        }
 
        public async Task<AuthResult> AuthenticateBySchoolIdAsync(string schoolId, string password, UserRole role)
        {
            try
            {
                // Find account by school ID and role (Admin accounts may not have User record)
                var account = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == schoolId.ToLower() && a.Role == role);
                
                // Load User separately if account exists (for non-admin roles)
                User? user = null;
                if (account != null && role == UserRole.Student)
                {
                    user = await _context.Users
                        .Include(u => u.AcademicProfile)
                        .FirstOrDefaultAsync(u => u.AccountId == account.AccountId);
                }
 
                if (account == null)
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = $"No {role} account found with this school ID." 
                    };
                }
 
                // Check if account is approved
                if (account.RequestStatus != RequestStatus.Approved)
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = $"Account is {account.RequestStatus.ToString().ToLower()}. Please contact administrator." 
                    };
                }
 
                // Verify password
                if (!VerifyPassword(password, account.PasswordHash))
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = "Invalid password." 
                    };
                }
 
                // Update active status
                account.IsActive = true;
                await _context.SaveChangesAsync();
 
                return new AuthResult 
                { 
                    Success = true, 
                    Message = "Authentication successful.",
                    Account = account,
                    User = account.User ?? user
                };
            }
            catch (Exception ex)
            {
                return new AuthResult 
                { 
                    Success = false, 
                    Message = $"An error occurred during authentication: {ex.Message}" 
                };
            }
        }
 
        public async Task<AuthResult> AuthenticateByStudentIdAsync(string studentId, string password, UserRole role)
        {
            try
            {
                // Find user by student ID (only for students)
                var user = await _context.Users
                    .Include(u => u.Account)
                    .Include(u => u.AcademicProfile)
                        .ThenInclude(ap => ap!.Course)
                    .FirstOrDefaultAsync(u => u.AcademicProfile != null && u.Account != null && u.Account.Role == UserRole.Student);
 
                if (user == null || user.Account == null)
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = $"No student account found with this student ID." 
                    };
                }
 
                // Verify password
                if (!VerifyPassword(password, user.Account.PasswordHash))
                {
                    return new AuthResult 
                    { 
                        Success = false, 
                        Message = "Invalid password." 
                    };
                }
 
                // Update active status
                user.Account.IsActive = true;
                await _context.SaveChangesAsync();
 
                return new AuthResult 
                { 
                    Success = true, 
                    Message = "Authentication successful.",
                    Account = user.Account,
                    User = user
                };
            }
            catch (Exception ex)
            {
                return new AuthResult 
                { 
                    Success = false, 
                    Message = $"An error occurred during authentication: {ex.Message}" 
                };
            }
        }
 
        private bool VerifyPassword(string password, string passwordHash)
        {
            // Verify password using BCrypt
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
 
        public static string HashPassword(string password)
        {
            // Hash password using BCrypt
            return BCrypt.Net.BCrypt.HashPassword(password);
        }
 
        public async Task<RegistrationResult> RegisterAccountAsync(RegistrationRequest request)
        {
            try
            {
                // Intentionally no sensitive data logging here.
 
                // Test database connection
                if (_context == null)
 
                {
                    Console.WriteLine("Database context is null");
                    return new RegistrationResult 
                    { 
                        Success = false, 
                        Message = "Database context is null." 
                    };
                }
 
                // Add null check for request
 
                if (request == null)
                {
 
                    return new RegistrationResult 
                    { 
                        Success = false, 
                        Message = "Registration request is null." 
                    };
                }
 
                // Use local variable to avoid null reference warnings
 
                var regRequest = request;
 
                // Layer 4 — Duplicate Detection
                // Prevent the same person from spamming multiple accounts with the same School ID or email.
 
                // Already registered by School ID?
                var existingById = await _context.Accounts
                    .FirstOrDefaultAsync(a =>
                        a.SchoolId != null &&
                        regRequest.SchoolId != null &&
                        a.SchoolId.ToLower() == regRequest.SchoolId.ToLower());
 
                if (existingById != null)
                {
                    return new RegistrationResult
                    {
                        Success = false,
                        Message = "School ID is already registered."
                    };
                }
 
                // Already registered by Email? (only if provided)
                if (!string.IsNullOrWhiteSpace(regRequest.Email))
                {
                    var existingByEmail = await _context.Accounts
                        .FirstOrDefaultAsync(a =>
                            a.Email != null &&
                            regRequest.Email != null &&
                            a.Email.ToLower() == regRequest.Email.ToLower());
 
                    if (existingByEmail != null)
                    {
                        return new RegistrationResult
                        {
                            Success = false,
                            Message = "Email address is already registered."
                        };
                    }
                }
 
 
                // Validate user-specific fields
                if (string.IsNullOrWhiteSpace(regRequest.FirstName) || 
                    string.IsNullOrWhiteSpace(regRequest.LastName))
                {
                    return new RegistrationResult 
                    { 
                        Success = false, 
                        Message = "First name and last name are required." 
                    };
                }
 
                // Validate student-specific fields if role is Student.
                // NOTE: School year and semester are intentionally NOT required at sign-up.
                // They are assigned later by the treasurer via "Payment Start", so a new
                // student can register with just a course and year level.
                if (regRequest.Role == UserRole.Student)
                {
                    if (string.IsNullOrWhiteSpace(regRequest.CourseCode) ||
                        !regRequest.YearLevel.HasValue)
                    {
                        return new RegistrationResult 
                        { 
                            Success = false, 
                            Message = "Course and year level are required for student registration." 
                        };
                    }
                }
 
                // Create account
                var account = new Account
                {
                    SchoolId = request.SchoolId,
                    Email = request.Email,
                    PasswordHash = HashPassword(request.Password),
                    Role = request.Role,
                    RequestStatus = RequestStatus.Pending, // New accounts need approval
                    IsActive = true, // Set to true by default for approved accounts
                    CreatedAt = DateTime.UtcNow
                };
 
                _context.Accounts.Add(account);
                await _context.SaveChangesAsync();
 
 
                // Create user record
                var user = new User
                {
                    AccountId = account.AccountId,
                    FirstName = request.FirstName,
                    LastName = request.LastName,
                    MiddleName = request.MiddleName
                };
 
                _context.Users.Add(user);
                await _context.SaveChangesAsync();
 
 
                // Create academic profile if role is Student
                if (request.Role == UserRole.Student)
                {
                    // Find course
                    Course? course = null;
                    if (!string.IsNullOrWhiteSpace(request.CourseCode))
                    {
                        course = await _context.Courses
                            .FirstOrDefaultAsync(c => c.CourseCode.ToLower() == request.CourseCode.ToLower());
 
                        if (course == null)
                        {
                            // Create course if it doesn't exist
                            course = new Course
                            {
                                CourseCode = request.CourseCode,
                                CourseName = request.CourseCode // Use course code as name by default
                            };
                            _context.Courses.Add(course);
                            await _context.SaveChangesAsync();
 
                        }
                    }
                    else
                    {
                        // Create a default course if CourseCode is null
                        course = new Course
                        {
                            CourseCode = "DEFAULT",
                            CourseName = "Default Course"
                        };
                        _context.Courses.Add(course);
                        await _context.SaveChangesAsync();
 
                    }
 
                    // Create academic profile.
                    // SchoolYearId and SemesterEntered may be null here — the treasurer
                    // sets them later via "Payment Start".
                    var academicProfile = new AcademicProfile
                    {
                        UserId = user.UserId,
                        CourseId = course.CourseId,
                        SchoolYearId = request.SchoolYearId,
                        SemesterEntered = request.SemesterEntered,
                        YearLevel = request.YearLevel,
                        Section = request.Section ?? "A", // Default section if null
                        AcademicStatus = AcademicStatus.Enrolled
                    };
 
                    _context.AcademicProfiles.Add(academicProfile);
                    await _context.SaveChangesAsync();
 
                }
 
                return new RegistrationResult 
                { 
                    Success = true, 
                    Message = "Account created successfully. Please wait for admin approval.",
                    Account = account,
                    User = user
                };
            }
            catch (Exception ex)
            {
                // Log full details server-side for debugging, but never expose internal
                // error details (stack traces, DB messages) to the client.
                Console.WriteLine($"RegisterAccountAsync error: {ex}");
                return new RegistrationResult
                {
                    Success = false,
                    Message = "Registration failed. Please try again later."
                };
            }
        }
    }
}
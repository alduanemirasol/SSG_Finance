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
                Console.WriteLine("RegisterAccountAsync called");
                
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

                Console.WriteLine($"Starting registration for: {request?.SchoolId}");

                // Add null check for request
                if (request == null)
                {
                    Console.WriteLine("Registration request is null");
                    return new RegistrationResult 
                    { 
                        Success = false, 
                        Message = "Registration request is null." 
                    };
                }

                Console.WriteLine($"Request details: SchoolId={request.SchoolId}, Email={request.Email}, Role={request.Role}");
                Console.WriteLine($"User fields: FirstName={request.FirstName}, LastName={request.LastName}");

                // Use local variable to avoid null reference warnings
                var regRequest = request;

                // Check if school ID already exists
                var schoolIdLower = regRequest.SchoolId?.ToLower() ?? "";
                var existingSchoolId = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.SchoolId.ToLower() == schoolIdLower);
                
                if (existingSchoolId != null)
                {
                    return new RegistrationResult 
                    { 
                        Success = false, 
                        Message = "School ID already exists." 
                    };
                }

                // Check if email already exists (if provided)
                if (!string.IsNullOrWhiteSpace(regRequest.Email))
                {
                    var emailLower = regRequest.Email?.ToLower() ?? "";
                    var existingEmail = await _context.Accounts
                        .FirstOrDefaultAsync(a => a.Email != null && a.Email.ToLower() == emailLower);
                    
                    if (existingEmail != null)
                    {
                        return new RegistrationResult 
                        { 
                            Success = false, 
                            Message = "Email already exists." 
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

                // Validate student-specific fields if role is Student
                if (regRequest.Role == UserRole.Student)
                {
                    if (string.IsNullOrWhiteSpace(regRequest.CourseCode) ||
                        !regRequest.YearLevel.HasValue ||
                        !regRequest.SchoolYearId.HasValue ||
                        !regRequest.SemesterEntered.HasValue)
                    {
                        return new RegistrationResult 
                        { 
                            Success = false, 
                            Message = "Course, school year entered, semester entered, and year level are required for student registration." 
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
                Console.WriteLine($"✓ Account saved: {account.AccountId}");

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
                Console.WriteLine($"✓ User saved: {user.UserId}");

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
                            Console.WriteLine($"✓ Course saved: {course.CourseId}");
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
                        Console.WriteLine($"✓ Default course saved: {course.CourseId}");
                    }

                    // Create academic profile
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
                    Console.WriteLine($"✓ Academic profile saved: {academicProfile.UserId}");
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
                var inner = ex.InnerException?.Message ?? "No inner exception";
                var inner2 = ex.InnerException?.InnerException?.Message ?? "No second inner";
                var stackTrace = ex.StackTrace ?? "No stack trace";
                return new RegistrationResult 
                { 
                    Success = false, 
                    Message = $"Registration failed: {inner} | Inner2: {inner2} | Stack: {stackTrace}" 
                };
            }
        }
    }
}

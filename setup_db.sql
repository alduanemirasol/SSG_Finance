CREATE DATABASE IF NOT EXISTS ssgfinance_db
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

USE ssgfinance_db;

CREATE TABLE IF NOT EXISTS accounts (
  account_id                   INT             NOT NULL AUTO_INCREMENT,
  school_id                    VARCHAR(50)     NOT NULL,
  password_hash                VARCHAR(255)    NOT NULL,
  email                        VARCHAR(150)    NULL,
  roles                        ENUM('Student','Treasurer','Admin','Professor') NOT NULL,
  request_status               ENUM('Pending','Approved','Rejected') NOT NULL DEFAULT 'Pending',
  is_active                    BOOLEAN         NOT NULL DEFAULT TRUE,
  created_at                   TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP,
  password_reset_token         VARCHAR(255)    NULL,
  password_reset_token_expires DATETIME        NULL,
  PRIMARY KEY (account_id),
  UNIQUE KEY uq_accounts_school_id (school_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS users (
  user_id     INT          NOT NULL AUTO_INCREMENT,
  account_id  INT          NOT NULL,
  first_name  VARCHAR(100) NULL,
  last_name   VARCHAR(100) NULL,
  middle_name VARCHAR(100) NULL,
  avatar_path VARCHAR(500) NULL,
  PRIMARY KEY (user_id),
  UNIQUE KEY uq_users_account (account_id),
  CONSTRAINT fk_users_account
    FOREIGN KEY (account_id) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS courses (
  course_id   INT          NOT NULL AUTO_INCREMENT,
  course_code VARCHAR(20)  NOT NULL,
  course_name VARCHAR(100) NULL,
  PRIMARY KEY (course_id),
  UNIQUE KEY uq_course_code (course_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS school_years (
  school_year_id   INT                     NOT NULL AUTO_INCREMENT,
  year_start       INT                     NOT NULL,
  year_end         INT                     NOT NULL,
  year_status      ENUM('Current','Ended') NOT NULL DEFAULT 'Current',
  first_sem_start  DATE                    NULL,
  first_sem_end    DATE                    NULL,
  second_sem_start DATE                    NULL,
  second_sem_end   DATE                    NULL,
  PRIMARY KEY (school_year_id),
  CONSTRAINT chk_year_range CHECK (year_end = year_start + 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS academic_profile (
  academic_profile_id INT                    NOT NULL AUTO_INCREMENT,
  user_id             INT                    NOT NULL,
  course_id           INT                    NOT NULL,
  school_year_id      INT                    NULL,
  semester_entered    ENUM('First','Second') NULL,
  year_level          INT                    NULL,
  section             VARCHAR(50)            NULL,
  academic_status     ENUM('Enrolled','Dropped') NOT NULL DEFAULT 'Enrolled',
  PRIMARY KEY (academic_profile_id),
  UNIQUE KEY uq_academic_user (user_id),
  CONSTRAINT fk_academic_user
    FOREIGN KEY (user_id) REFERENCES users (user_id)
    ON UPDATE CASCADE ON DELETE RESTRICT,
  CONSTRAINT fk_academic_course
    FOREIGN KEY (course_id) REFERENCES courses (course_id)
    ON UPDATE CASCADE ON DELETE RESTRICT,
  CONSTRAINT fk_academic_school_year
    FOREIGN KEY (school_year_id) REFERENCES school_years (school_year_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS full_amount (
  full_amount_id  INT                     NOT NULL AUTO_INCREMENT,
  school_year_id  INT                     NOT NULL,
  semester        ENUM('First','Second')  NOT NULL,
  amount          DECIMAL(10,2)           NOT NULL,
  semester_status ENUM('Current','Ended') NOT NULL DEFAULT 'Current',
  PRIMARY KEY (full_amount_id),
  CONSTRAINT fk_full_amount_sy
    FOREIGN KEY (school_year_id) REFERENCES school_years (school_year_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS org_fee_payments (
  payment_id            INT           NOT NULL AUTO_INCREMENT,
  user_id               INT           NULL,
  full_amount_id        INT           NOT NULL,
  amount                DECIMAL(10,2) NOT NULL,
  payment_status        ENUM('Partial','Paid') NOT NULL,
  received_by           INT           NOT NULL,
  payment_date          TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  year_level_at_payment INT           NULL,
  section_at_payment    VARCHAR(50)   NULL,
  PRIMARY KEY (payment_id),
  CONSTRAINT fk_payment_user
    FOREIGN KEY (user_id) REFERENCES users (user_id)
    ON UPDATE CASCADE ON DELETE SET NULL,
  CONSTRAINT fk_payment_full_amount
    FOREIGN KEY (full_amount_id) REFERENCES full_amount (full_amount_id)
    ON UPDATE CASCADE ON DELETE RESTRICT,
  CONSTRAINT fk_payment_receiver
    FOREIGN KEY (received_by) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS other_funds (
  fund_id        INT           NOT NULL AUTO_INCREMENT,
  source         VARCHAR(200)  NULL,
  description    TEXT          NULL,
  amount         DECIMAL(10,2) NOT NULL,
  category       VARCHAR(50)   NULL,
  received_by    INT           NOT NULL,
  received_date  TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  school_year_id INT           NULL,
  PRIMARY KEY (fund_id),
  CONSTRAINT fk_fund_receiver
    FOREIGN KEY (received_by) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT,
  CONSTRAINT fk_fund_school_year
    FOREIGN KEY (school_year_id) REFERENCES school_years (school_year_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS expenses (
  expense_id     INT           NOT NULL AUTO_INCREMENT,
  description    TEXT          NULL,
  amount         DECIMAL(10,2) NOT NULL,
  recorded_by    INT           NOT NULL,
  expense_date   TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  school_year_id INT           NULL,
  PRIMARY KEY (expense_id),
  CONSTRAINT fk_expense_recorder
    FOREIGN KEY (recorded_by) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT,
  CONSTRAINT fk_expense_school_year
    FOREIGN KEY (school_year_id) REFERENCES school_years (school_year_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS expense_images (
  image_id    INT          NOT NULL AUTO_INCREMENT,
  expense_id  INT          NOT NULL,
  image_path  VARCHAR(255) NOT NULL,
  uploaded_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (image_id),
  CONSTRAINT fk_expense_image_expense
    FOREIGN KEY (expense_id) REFERENCES expenses (expense_id)
    ON UPDATE CASCADE ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS receipts (
  receipt_id     INT         NOT NULL AUTO_INCREMENT,
  receipt_number VARCHAR(50) NOT NULL,
  payment_id     INT         NULL,
  issued_by      INT         NOT NULL,
  PRIMARY KEY (receipt_id),
  UNIQUE KEY uq_receipt_number (receipt_number),
  CONSTRAINT fk_receipt_payment
    FOREIGN KEY (payment_id) REFERENCES org_fee_payments (payment_id)
    ON UPDATE CASCADE ON DELETE SET NULL,
  CONSTRAINT fk_receipt_issuer
    FOREIGN KEY (issued_by) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS treasurer_signatures (
  signature_id   INT        NOT NULL AUTO_INCREMENT,
  account_id     INT        NOT NULL,
  signature_data MEDIUMTEXT NOT NULL,
  created_at     TIMESTAMP  NOT NULL DEFAULT CURRENT_TIMESTAMP,
  is_active      BOOLEAN    NOT NULL DEFAULT TRUE,
  PRIMARY KEY (signature_id),
  CONSTRAINT fk_sig_account
    FOREIGN KEY (account_id) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS reports (
  report_id        INT           NOT NULL AUTO_INCREMENT,
  report_type      ENUM('Event','Semester','Annual') NOT NULL,
  title            VARCHAR(255)  NOT NULL,
  school_year_id   INT           NULL,
  semester         ENUM('First','Second') NULL,
  date_from        DATE          NULL,
  date_to          DATE          NULL,
  beginning_balance DECIMAL(10,2) NOT NULL DEFAULT 0.00,
  total_revenue    DECIMAL(10,2) NOT NULL DEFAULT 0.00,
  total_expenses   DECIMAL(10,2) NOT NULL DEFAULT 0.00,
  running_balance  DECIMAL(10,2) NOT NULL DEFAULT 0.00,
  status           ENUM('Draft','Final') NOT NULL DEFAULT 'Draft',
  created_by       INT           NOT NULL,
  created_at       TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (report_id),
  CONSTRAINT fk_report_school_year
    FOREIGN KEY (school_year_id) REFERENCES school_years (school_year_id)
    ON UPDATE CASCADE ON DELETE RESTRICT,
  CONSTRAINT fk_report_creator
    FOREIGN KEY (created_by) REFERENCES accounts (account_id)
    ON UPDATE CASCADE ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS report_items (
  item_id      INT           NOT NULL AUTO_INCREMENT,
  report_id    INT           NOT NULL,
  item_type    ENUM('Expense','Fund') NOT NULL,
  item_ref_id  INT           NOT NULL,
  description  TEXT          NULL,
  amount       DECIMAL(10,2) NOT NULL,
  PRIMARY KEY (item_id),
  CONSTRAINT fk_report_item_report
    FOREIGN KEY (report_id) REFERENCES reports (report_id)
    ON UPDATE CASCADE ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS student_fee_exemptions (
  exemption_id   INT NOT NULL AUTO_INCREMENT,
  user_id        INT NOT NULL,
  school_year_id INT NOT NULL,
  semester       VARCHAR(10) NOT NULL,
  PRIMARY KEY (exemption_id),
  UNIQUE KEY uq_exemption (user_id, school_year_id, semester),
  CONSTRAINT fk_exemption_user
    FOREIGN KEY (user_id) REFERENCES users(user_id)
    ON DELETE CASCADE,
  CONSTRAINT fk_exemption_school_year
    FOREIGN KEY (school_year_id) REFERENCES school_years(school_year_id)
    ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO accounts (school_id, password_hash, email, roles, request_status, is_active) VALUES
  ('ADMIN-001',   '$2b$10$yb/RowiDUqEV4yopXMLT7.XPvQu4I/qIZyZU485iTfiIsdFcXBtny', 'admin@ssg.com',       'Admin',     'Approved', TRUE),
  ('TREAS-001',   '$2b$10$DHAVeowe0OiJeys8IYX/7OoWfrIHiMB4qeVpaFP8Fpo.Z8xukqjcq', 'treas@ssg.com',       'Treasurer', 'Approved', TRUE),
  ('PROF-001',    '$2b$10$zQRMd9wPIEnJJNGjBky/eeTVekbpuod.MNk92bU.RnbXwvDT0wAne', 'prof.delacruz@ssg.com','Professor', 'Approved', TRUE),
  ('STU-001',     '$2b$10$xA24wevycLegwI3M1Pp95ueRmZ.54JZUUbOarWkAGdubL7RrsWIKK', 'juan.reyes@ssg.com',  'Student',   'Approved', TRUE),
  ('STU-002',     '$2b$10$xA24wevycLegwI3M1Pp95ueRmZ.54JZUUbOarWkAGdubL7RrsWIKK', 'maria.santos@ssg.com','Student',   'Approved', TRUE),
  ('STU-003',     '$2b$10$xA24wevycLegwI3M1Pp95ueRmZ.54JZUUbOarWkAGdubL7RrsWIKK', 'carlo.garcia@ssg.com','Student',   'Approved', TRUE),
  ('STU-PEND-001','$2b$10$xA24wevycLegwI3M1Pp95ueRmZ.54JZUUbOarWkAGdubL7RrsWIKK', 'pend.student@ssg.com','Student',   'Pending',  TRUE),
  ('PROF-PEND-001','$2b$10$zQRMd9wPIEnJJNGjBky/eeTVekbpuod.MNk92bU.RnbXwvDT0wAne','pend.prof@ssg.com',   'Professor', 'Pending',  TRUE);

INSERT INTO users (account_id, first_name, last_name, middle_name) VALUES
  (1, 'System',   'Admin',    NULL),
  (2, 'Maria',    'Santos',   'C.'),
  (3, 'Juan',     'Dela Cruz','R.'),
  (4, 'Juan',     'Reyes',    'M.'),
  (5, 'Maria',    'Santos',   'L.'),
  (6, 'Carlo',    'Garcia',   'D.');

INSERT INTO courses (course_code, course_name) VALUES
  ('BSCS', 'Bachelor of Science in Computer Science'),
  ('BSIT', 'Bachelor of Science in Information Technology'),
  ('BSA',  'Bachelor of Science in Accountancy'),
  ('BSBA', 'Bachelor of Science in Business Administration'),
  ('BSEd', 'Bachelor of Secondary Education');

INSERT INTO school_years (year_start, year_end, year_status, first_sem_start, first_sem_end, second_sem_start, second_sem_end) VALUES
  (2025, 2026, 'Current',
   '2025-08-01', '2025-12-20',
   '2026-01-10', '2026-05-30'),
  (2024, 2025, 'Ended',
   '2024-08-01', '2024-12-20',
   '2025-01-10', '2025-05-30');

INSERT INTO academic_profile (user_id, course_id, school_year_id, semester_entered, year_level, section, academic_status) VALUES
  (4, 1, 1, 'First', 3, 'A', 'Enrolled'),
  (5, 3, 1, 'First', 2, 'B', 'Enrolled'),
  (6, 2, 2, 'First', 1, 'A', 'Enrolled');

INSERT INTO full_amount (school_year_id, semester, amount, semester_status) VALUES
  (1, 'First',  500.00, 'Current'),
  (1, 'Second', 500.00, 'Current'),
  (2, 'First',  450.00, 'Ended'),
  (2, 'Second', 450.00, 'Ended');

INSERT INTO org_fee_payments (user_id, full_amount_id, amount, payment_status, received_by, payment_date, year_level_at_payment, section_at_payment) VALUES
  (4, 3, 450.00, 'Paid',    2, '2024-09-15 10:30:00', 2, 'A'),
  (4, 4, 450.00, 'Paid',    2, '2025-02-10 14:00:00', 2, 'A'),
  (5, 1, 250.00, 'Partial', 2, '2025-08-20 09:00:00', 2, 'B'),
  (6, 3, 450.00, 'Paid',    2, '2024-10-05 11:00:00', 1, 'A');

INSERT INTO other_funds (source, description, amount, category, received_by, received_date, school_year_id) VALUES
  ('School Donation', 'Donation from Alumni Association for SSG activities', 10000.00, 'Donation', 2, '2025-08-01 08:00:00', 1),
  ('Grant',           'Local Government Unit grant for leadership training',   5000.00, 'Grant',    2, '2025-09-01 10:00:00', 1);

INSERT INTO expenses (description, amount, recorded_by, expense_date, school_year_id) VALUES
  ('School Supplies for Freshman Orientation', 2500.00, 2, '2025-08-10 09:00:00', 1),
  ('Venue Rental for Leadership Seminar',       3500.00, 2, '2025-09-15 13:00:00', 1);

INSERT INTO treasurer_signatures (account_id, signature_data, is_active) VALUES
  (2, 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', TRUE);

INSERT INTO receipts (receipt_number, payment_id, issued_by) VALUES
  ('RCP-2024-0001', 1, 2),
  ('RCP-2024-0002', 2, 2);

INSERT INTO reports (report_type, title, school_year_id, semester, date_from, date_to, beginning_balance, total_revenue, total_expenses, running_balance, status, created_by) VALUES
  ('Semester', 'First Semester Report SY 2025-2026', 1, 'First', '2025-08-01', '2025-12-20', 0.00, 15000.00, 6000.00, 9000.00, 'Draft', 1);

INSERT INTO report_items (report_id, item_type, item_ref_id, description, amount) VALUES
  (1, 'Fund',    1, 'Alumni Association Donation',    10000.00),
  (1, 'Fund',    2, 'LGU Grant for Leadership Training', 5000.00);

INSERT INTO student_fee_exemptions (user_id, school_year_id, semester) VALUES
  (6, 1, 'First');

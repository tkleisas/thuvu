# Orchestration Test Scenario: Website Monitor Application

## Overview

This test scenario creates a complete ASP.NET Core web application for monitoring website uptime. It's designed to test the multi-agent orchestration system with a complex, real-world task.

## Task Description

```
Create an ASP.NET Core web application called "WebsiteMonitor" with the following features:

1. **Framework & Structure**
   - ASP.NET Core with C# and Razor Pages
   - SQLite database with Entity Framework Core
   - Clean project structure with separate folders for Models, Pages, Services, Data

2. **UI Stack**
   - Bulma CSS framework for styling
   - Font Awesome for icons
   - Htmx for dynamic content updates without full page reloads
   - Alpine.js for client-side interactivity

3. **Authentication & Authorization**
   - Custom cookie-based authentication (no ASP.NET Identity)
   - Password hashing with BCrypt or similar
   - Two roles: Admin and User
   - Default admin user on startup: username "admin", password "!$3cur3"

4. **User Management (Admin only)**
   - List all users
   - Create new users with role assignment
   - Edit user details and roles
   - Delete users (except self)

5. **Website Monitoring**
   - CRUD operations for monitored websites
   - Fields: Name, URL, Method (HEAD/GET), Headers (JSON), Interval (minutes)
   - Admin sees all websites, User sees only their own
   - Status tracking: Up/Down/Unknown

6. **Background Monitoring Service**
   - IHostedService that runs periodically
   - Checks each website based on its configured interval
   - Records datapoints: Timestamp, ResponseTime, StatusCode, Success
   - Handles timeouts and exceptions gracefully

7. **Notifications**
   - Email notification to admin when site goes down
   - Configurable SMTP settings in appsettings.json
   - Rate limiting to prevent spam

8. **Dashboard**
   - Overview of all monitored websites (filtered by role)
   - Shows: Name, URL, Status (with color), Last Check Time, Response Time
   - Auto-refresh using Htmx polling
   - Simple charts showing uptime history (optional)

9. **Security**
   - All pages require authentication except login
   - CSRF protection on forms
   - Input validation on all user inputs
   - Secure password storage
```

## Running the Test

### Step 1: Create the Plan

```bash
# Start thuvu
dotnet run

# Create the decomposition plan
/plan Create an ASP.NET Core web application called WebsiteMonitor for monitoring website uptime. Features: Razor Pages with Bulma/Font Awesome UI, Htmx and Alpine.js for dynamic updates, SQLite with EF Core, custom cookie authentication with Admin/User roles, default admin user (admin/!$3cur3), website CRUD with Name/URL/Method/Headers/Interval fields, background service for periodic checks storing datapoints, email notifications when sites go down, dashboard showing status with auto-refresh. Admin manages users and sees all sites, User sees only own sites.
```

### Step 2: Review the Plan

The system should decompose this into approximately 8-12 subtasks:

**Expected Phases:**

1. **Analysis** (1 task)
   - Analyze requirements and plan architecture

2. **Foundation** (2-3 parallel tasks)
   - Create project structure and configure EF Core + SQLite
   - Set up Bulma/Font Awesome/Htmx/Alpine.js
   - Create data models (User, Website, DataPoint)

3. **Authentication** (1-2 tasks)
   - Implement custom auth with cookie middleware
   - Create login page and auth service

4. **Core Features** (2-3 parallel tasks)
   - Website CRUD pages
   - User management pages (Admin)
   - Dashboard page

5. **Background Service** (1-2 tasks)
   - Monitoring service with datapoint recording
   - Email notification service

6. **Integration & Polish** (1-2 tasks)
   - Wire up Htmx for dynamic updates
   - Add Alpine.js interactivity
   - Final testing

### Step 3: Execute the Plan

```bash
# Execute with recommended agents
/orchestrate

# Or specify agent count
/orchestrate --agents 3

# Or keep branches separate for review
/orchestrate --no-merge
```

### Step 4: Verify Results

After orchestration completes:

```bash
# Check the generated project
cd work/WebsiteMonitor

# Restore and build
dotnet restore
dotnet build

# Run the application
dotnet run

# Open browser to https://localhost:5001 (or http://localhost:5000)
```

### Expected Output Structure

```
WebsiteMonitor/
├── WebsiteMonitor.csproj
├── Program.cs
├── appsettings.json
├── appsettings.Development.json
│
├── Data/
│   ├── AppDbContext.cs
│   └── DbInitializer.cs
│
├── Models/
│   ├── User.cs
│   ├── Website.cs
│   ├── DataPoint.cs
│   └── ViewModels/
│       ├── LoginViewModel.cs
│       └── WebsiteViewModel.cs
│
├── Services/
│   ├── AuthService.cs
│   ├── MonitoringService.cs
│   ├── EmailService.cs
│   └── WebsiteService.cs
│
├── Pages/
│   ├── _ViewImports.cshtml
│   ├── _ViewStart.cshtml
│   ├── Shared/
│   │   ├── _Layout.cshtml
│   │   └── _LoginPartial.cshtml
│   ├── Index.cshtml              # Dashboard
│   ├── Index.cshtml.cs
│   ├── Login.cshtml
│   ├── Login.cshtml.cs
│   ├── Logout.cshtml.cs
│   ├── Websites/
│   │   ├── Index.cshtml          # List
│   │   ├── Create.cshtml
│   │   ├── Edit.cshtml
│   │   └── Delete.cshtml
│   └── Admin/
│       └── Users/
│           ├── Index.cshtml
│           ├── Create.cshtml
│           ├── Edit.cshtml
│           └── Delete.cshtml
│
└── wwwroot/
    ├── css/
    │   └── site.css
    ├── js/
    │   └── site.js
    └── lib/
        └── (Bulma, htmx, alpine from CDN or local)
```

## Validation Checklist

After the orchestration completes, verify:

### Build & Run
- [ ] `dotnet build` succeeds without errors
- [ ] `dotnet run` starts the application
- [ ] Application is accessible in browser

### Authentication
- [ ] Unauthenticated users see login page only
- [ ] Can login with admin/!$3cur3
- [ ] Login redirects to dashboard
- [ ] Logout works correctly

### Dashboard
- [ ] Shows list of monitored websites
- [ ] Displays status, last check time, response time
- [ ] Auto-refreshes (Htmx polling)
- [ ] Bulma styling is applied

### Website Management
- [ ] Can add new website to monitor
- [ ] Can edit existing website
- [ ] Can delete website
- [ ] Form validation works
- [ ] Admin sees all, User sees own only

### User Management (Admin)
- [ ] Can list all users
- [ ] Can create new user with role
- [ ] Can edit user
- [ ] Cannot delete self

### Background Service
- [ ] Service starts with application
- [ ] Websites are checked periodically
- [ ] DataPoints are recorded in database
- [ ] Status updates reflect in dashboard

### Email Notifications
- [ ] Configuration in appsettings.json
- [ ] Email sent when site goes down (if SMTP configured)

## Troubleshooting

### Plan has too few subtasks
The LLM may oversimplify. Try rephrasing with more explicit requirements.

### Orchestration fails early
Check logs in `work/logs/`. Common issues:
- LLM timeout (increase AgentTimeoutMinutes)
- Model doesn't support tool calling
- Syntax errors in generated code

### Build fails after orchestration
- Check for missing using statements
- Verify NuGet packages are referenced
- Check for duplicate class definitions (if agents overlapped)

### Merge conflicts
Use `--no-merge` and manually review agent branches:
```bash
git branch -a  # List all branches
git diff main agent/...  # Compare changes
```

## Notes

- This is a complex task that tests all aspects of orchestration
- Expect 15-30 minutes for full execution with 2-3 agents
- The quality depends heavily on the LLM model used
- Consider running with a capable model (GPT-4, Claude, Qwen-72B)

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using thuvu.Models;
using thuvu.Web.Hubs;
using thuvu.Web.Services;

namespace thuvu.Web
{
    /// <summary>
    /// ASP.NET Core host for the web interface
    /// </summary>
    public static class WebHost
    {
        /// <summary>
        /// Start the web server with Blazor and SignalR
        /// </summary>
        public static async Task RunAsync(string[] args, CancellationToken ct = default)
        {
            // Find the wwwroot directory - check multiple locations
            var currentDir = Directory.GetCurrentDirectory();
            
            string? wwwrootPath = Path.Combine(currentDir, "wwwroot");
            if (!Directory.Exists(wwwrootPath))
            {
                // Try relative to executable
                var exeDir = AppContext.BaseDirectory;
                wwwrootPath = Path.Combine(exeDir, "wwwroot");
                if (!Directory.Exists(wwwrootPath))
                {
                    wwwrootPath = null;
                }
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = currentDir,
                WebRootPath = wwwrootPath,
                EnvironmentName = Environments.Development // Enable detailed errors
            });
            
            // Also set WebRootFileProvider explicitly
            if (wwwrootPath != null)
            {
                builder.Environment.WebRootFileProvider = new PhysicalFileProvider(wwwrootPath);
            }

            // Add services
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents(options =>
                {
                    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(1);
                    options.DisconnectedCircuitMaxRetained = 100;
                    options.DetailedErrors = true;
                });
            
            builder.Services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB for large tool results
                options.ClientTimeoutInterval = TimeSpan.FromMinutes(10); // Server considers client dead after 10 min
                options.KeepAliveInterval = TimeSpan.FromSeconds(15); // Server sends ping every 15 seconds
                options.HandshakeTimeout = TimeSpan.FromSeconds(30);
            });

            // Register our services
            builder.Services.AddSingleton<WebAgentService>();
            builder.Services.AddSingleton<HttpClient>(sp =>
            {
                var http = new HttpClient();
                AgentConfig.ApplyConfig(http);
                return http;
            });

            // Configure Kestrel - bind to localhost only for security
            var port = AgentApiConfig.Instance.Enabled ? AgentApiConfig.Instance.Port : 5000;
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(port); // Localhost only — no external access
            });

            var app = builder.Build();

            // Initialize TokenTracker with context length from config
            // Priority: 1) Model-specific config, 2) AgentConfig setting, 3) Default 32768
            try
            {
                var currentModel = ModelRegistry.Instance.GetModel(AgentConfig.Config.Model);
                if (currentModel?.MaxContextLength > 0)
                {
                    TokenTracker.Instance.MaxContextLength = currentModel.MaxContextLength;
                }
                else if (AgentConfig.Config.MaxContextLength > 0)
                {
                    TokenTracker.Instance.MaxContextLength = AgentConfig.Config.MaxContextLength;
                }
                // else keep default 32768
            }
            catch
            {
                // Keep default if config fails
            }

            // Log all requests for debugging
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                Console.WriteLine($"[Request] {context.Request.Method} {path}");
                await next();
                Console.WriteLine($"[Response] {context.Request.Method} {path} -> {context.Response.StatusCode}");
            });

            // Configure middleware
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            // Static files - use the simple version since WebRootPath is already set
            app.UseStaticFiles();
            
            app.UseAntiforgery();

            // Map SignalR hub
            app.MapHub<AgentHub>("/agenthub");

            // Map Agent API endpoints (if enabled)
            if (AgentApiConfig.Instance.Enabled)
            {
                app.MapAgentApi();
                app.MapConversationApi();
            }
            
            // Health check endpoint for Docker/Kubernetes
            app.MapGet("/health", () => Results.Ok(new 
            { 
                status = "healthy",
                timestamp = DateTime.UtcNow,
                version = "1.0.0"
            }));

            // Map Blazor
            app.MapRazorComponents<Components.App>()
                .AddInteractiveServerRenderMode();

            // Print startup info
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║           T.H.U.V.U. Web Interface           ║");
            Console.WriteLine("╠══════════════════════════════════════════════╣");
            Console.ResetColor();
            Console.Write("║  ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("http://localhost:5000");
            Console.ResetColor();
            Console.WriteLine("                       ║");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╚══════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"Content root: {app.Environment.ContentRootPath}");
            Console.WriteLine($"Web root: {app.Environment.WebRootPath}");
            Console.ResetColor();
            Console.WriteLine("Press Ctrl+C to stop the server...");

            await app.RunAsync(ct);
        }
    }
}

using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Middleware;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Signing;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;

// Bootstrap logger: records errors that happen before configuration is loaded (e.g. a broken appsettings file).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Replace the default logging with Serilog, configured from the "Serilog" section of appsettings.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddControllers(options => options.Filters.Add<IdempotencyKeyLoggingFilter>());
    // Lowercase route URLs in generated links and the Swagger docs.
    builder.Services.AddRouting(options => options.LowercaseUrls = true);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        // Adds an "Authorize" button to Swagger UI that sends the X-Api-Key header.
        options.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "API key issued to the calling service."
        });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyAuthenticationHandler.SchemeName }
            }] = []
        });
    });

    // Authentication: who is calling (API key -> client id + scopes). Same pattern as LedgerApi.
    builder.Services.AddOptions<ApiKeyOptions>()
        .Bind(builder.Configuration.GetSection("ApiKeys"))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<ApiKeyOptions>, ApiKeyOptionsValidator>();

    // Signing keys for the authorization grant (DESIGN.md Section 4.3.1/4.3.2, decisions #4/#26). See
    // SigningKeyOptionsValidator for why - unlike ApiKeyOptions - at least one key is required.
    builder.Services.AddOptions<SigningKeyOptions>()
        .Bind(builder.Configuration.GetSection("SigningKeys"))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<SigningKeyOptions>, SigningKeyOptionsValidator>();
    builder.Services.AddSingleton<IGrantSigner, GrantSigner>();

    builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

    // Authorization: one policy per scope, plus a fallback so an endpoint without [Authorize] is never public.
    builder.Services.AddAuthorization(options =>
    {
        foreach (var scope in IdentityScopes.All)
            options.AddPolicy(scope, policy => policy.RequireAuthenticatedUser().RequireClaim(IdentityScopes.ClaimType, scope));

        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    });

    builder.Services.AddDbContext<IdentityDbContext>(options => options
        .UseNpgsql(builder.Configuration.GetConnectionString("IdentityDb"))
        .UseSnakeCaseNamingConvention());

    // AuditLogger reads the actor, IP, user agent and correlation id of the current request.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentClient, HttpCurrentClient>();

    // Health checks for the hosting platform: "ready" includes the database, "live" checks only the process.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<IdentityDbContext>("database", tags: ["ready"]);

    builder.Services.AddScoped<ICustomerService, CustomerService>();
    builder.Services.AddScoped<IMandateService, MandateService>();
    builder.Services.AddScoped<ITransactionAuthorizationService, AuthorizationService>();
    builder.Services.AddScoped<IJointApprovalService, JointApprovalService>();
    builder.Services.AddScoped<IReversalAuthorizationService, ReversalAuthorizationService>();
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();

    builder.Services.AddScoped<CreateCustomerRequestValidator>();
    builder.Services.AddScoped<KycDecisionRequestValidator>();
    builder.Services.AddScoped<CustomerStatusChangeRequestValidator>();
    builder.Services.AddScoped<AttachMandateRequestValidator>();
    builder.Services.AddScoped<UpdateCustomerDetailsRequestValidator>();
    builder.Services.AddScoped<RevokeMandateRequestValidator>();
    builder.Services.AddScoped<AuthorizationRequestValidator>();
    builder.Services.AddScoped<ApproveRequestValidator>();
    builder.Services.AddScoped<ChangeApprovalPolicyRequestValidator>();
    builder.Services.AddScoped<RecordReversalOutcomeRequestValidator>();

    var app = builder.Build();

    // First in the pipeline, so every later log line (including the request summary) has the id.
    app.UseMiddleware<CorrelationIdMiddleware>();

    // One structured log line per request: method, path, status code and elapsed time, plus these extras.
    app.UseSerilogRequestLogging(options =>
    {
        // Platforms probe /health every few seconds; log those only when they fail.
        options.GetLevel = (httpContext, _, exception) =>
            exception is not null || httpContext.Response.StatusCode >= 500 ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
            : LogEventLevel.Information;

        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("UserId", httpContext.User.Identity?.Name ?? "anonymous");
            diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
        };
    });

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
    {
        app.UseSwagger();
        app.UseSwaggerUI(options => options.EnablePersistAuthorization());
    }

    app.UseMiddleware<ExceptionHandlingMiddleware>();

    // No UseHttpsRedirection: a redirect cannot protect an API key that was already sent over HTTP.
    // TLS belongs to the deployment (proxy/ingress or Kestrel), same as LedgerApi's decision #36.

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    // Anonymous on purpose: platforms probe without an API key. Live = the process responds; ready = it
    // can reach the database.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Identity API terminated unexpectedly");
    // Non-zero so Docker, Kubernetes and CI see a failed start rather than a clean stop.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

// Lets the test project start the app in memory with WebApplicationFactory<Program>.
public partial class Program;

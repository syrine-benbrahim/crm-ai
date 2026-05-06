using crm_ai.Data;       
using crm_ai.Services;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using crm_ai.Helpers;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:3000"
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// ── BLOCK 1: AI Options ──────────────────────────────────────────
builder.Services.Configure<GrokAiOptions>(
    builder.Configuration.GetSection(GrokAiOptions.SectionName));

// ── BLOCK 2: Resilience options ──────────────────────────────────
builder.Services.Configure<GroqResilienceOptions>(
    builder.Configuration.GetSection("GroqCircuitBreaker"));
var resilienceOpts = builder.Configuration
    .GetSection("GroqCircuitBreaker")
    .Get<GroqResilienceOptions>();

Console.WriteLine($"[RESILIENCE] Timeout={resilienceOpts?.TimeoutSeconds}s, " +
    $"Failures={resilienceOpts?.FailuresBeforeBreaking}, " +
    $"Retry={resilienceOpts?.RetryCount}");

// ── BLOCK 2b: Typed HttpClient for Groq with Polly ───────────────
builder.Services.AddHttpClient("GrokClient", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<GrokAiOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.ApiKey}");
    client.Timeout = TimeSpan.FromSeconds(120); // hard ceiling, Polly handles real timeout
})
.AddPolicyHandler((services, _) =>
{
    var options = services
        .GetRequiredService<IOptions<GroqResilienceOptions>>().Value;
    var logger = services
        .GetRequiredService<ILogger<AiService>>();
    return GroqCircuitBreaker.GetCombinedPolicy(options, logger);
});

// ── BLOCK 3: AI Service ──────────────────────────────────────────
builder.Services.AddScoped<IAiService, AiService>();
builder.Services.AddSingleton<IAiUsageService, AiUsageService>();
// ── BLOCK 4: Campaign Service ─────────────────────────────────────
builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<SegmentProfileBuilder>();
builder.Services.AddScoped<ICampaignWizardService, CampaignWizardService>();
builder.Services.AddScoped<ITemplateRenderingService, TemplateRenderingService>();
// Existing services
builder.Services.AddScoped<ITreeService, TreeService>();
builder.Services.AddScoped<ISelectionService, SelectionService>();
builder.Services.AddScoped<ISqlBuilderService, SqlBuilderService>();
builder.Services.AddScoped < ISelectionSuggestionService, SelectionSuggestionService>();
builder.Services.AddScoped<ITemplateRecommendationService,TemplateRecommendationService>();
builder.Services.AddMemoryCache();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseAuthorization();
app.MapControllers();
app.Run();

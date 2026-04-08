using crm_ai.Data;       
using crm_ai.Services;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;  

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
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

// ── BLOCK 2: Typed HttpClient for Groq ──────────────────────────
builder.Services.AddHttpClient("GrokClient", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<GrokAiOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.ApiKey}");
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ── BLOCK 3: AI Service ──────────────────────────────────────────
builder.Services.AddScoped<IAiService, AiService>();

// Existing services
builder.Services.AddScoped<ITreeService, TreeService>();
builder.Services.AddScoped<ISelectionService, SelectionService>();
builder.Services.AddScoped<ISqlBuilderService, SqlBuilderService>();
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

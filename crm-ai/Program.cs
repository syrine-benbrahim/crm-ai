using crm_ai.Data;
using crm_ai.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// ⭐ CRITICAL: Add CORS Policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
            "http://localhost:5173",  // Vite dev server default
            "http://localhost:5174",  // Alternative Vite port
            "http://localhost:3000"   // Alternative React port
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials();
    });
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// Services
builder.Services.AddScoped<TreeService>();
builder.Services.AddScoped<SelectionService>();
builder.Services.AddScoped<SqlBuilderService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ⭐ CRITICAL: Enable CORS (MUST be before UseAuthorization)
app.UseCors("AllowFrontend");

// Optional: Comment out HTTPS redirect for local development
// app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
// An ASP.NET Core app as people write them: minimal API routes, a controller, EF Core on
// PostgreSQL or SQL Server (DB=pg|mssql), and the one Zharp line.
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddZharp();
builder.Services.AddControllers();
string db = Environment.GetEnvironmentVariable("DB") ?? "pg";
builder.Services.AddDbContext<ShopDb>(o =>
{
    if (db == "mssql") o.UseSqlServer(Environment.GetEnvironmentVariable("DB_URL"));
    else o.UseNpgsql(Environment.GetEnvironmentVariable("DB_URL"));
});
builder.Services.AddHttpClient();
if (Environment.GetEnvironmentVariable("EXCEPTION_HANDLER") == "1")
{
    builder.Services.AddProblemDetails();
}

var app = builder.Build();
if (Environment.GetEnvironmentVariable("EXCEPTION_HANDLER") == "1") app.UseExceptionHandler();

using (var scope = app.Services.CreateScope())
{
    var shop = scope.ServiceProvider.GetRequiredService<ShopDb>();
    shop.Database.EnsureDeleted();
    shop.Database.EnsureCreated();
}

app.MapGet("/hello", (ILogger<Program> log) =>
{
    log.LogInformation("Saying hello");
    return Results.Ok(new { hello = "world" });
});

app.MapGet("/users/{id:int}", (int id) => Results.Ok(new { id }));

// Throws two calls deep, in a lambda inside a local function.
app.MapGet("/boom", () =>
{
    return Checkout.Run(0);
});

app.MapGet("/boom-async", async () => await Checkout.RunAsync(3));

// A unique violation EF Core can't save: unhandled, so the request answers 500.
app.MapPost("/users", async (ShopDb shop, string email) =>
{
    shop.Users.Add(new User { Email = email });
    await shop.SaveChangesAsync();
    return Results.Ok();
});

// A failed query the app catches: reported once, as handled.
app.MapGet("/db/caught", async (ShopDb shop, ILogger<Program> log) =>
{
    try
    {
        await shop.Database.ExecuteSqlRawAsync("SELECT * FROM no_such_table");
    }
    catch (Exception e)
    {
        log.LogWarning("Lookup failed, using defaults: {Reason}", e.Message);
    }
    return Results.Ok(new { fallback = true });
});

// A caught exception logged at Error: one handled error, the log line without it.
app.MapGet("/log", (ILogger<Program> log) =>
{
    try
    {
        Checkout.Validate(-1);
    }
    catch (ArgumentException e)
    {
        log.LogError(e, "Validation failed for {Quantity}", -1);
    }
    log.LogWarning("Stock is low");
    return Results.Ok();
});

// Calls this app back over HttpClient: the outgoing call joins the trace.
app.MapGet("/outbound", async (IHttpClientFactory http, HttpContext ctx) =>
{
    var client = http.CreateClient();
    client.DefaultRequestHeaders.Add("X-Api-Key", "secret-outbound-key");
    string body = await client.GetStringAsync($"http://localhost:{ctx.Connection.LocalPort}/users/7?token=abc");
    return Results.Ok(new { body });
});

app.MapPost("/echo", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    string text = await reader.ReadToEndAsync();
    return Results.Content(text, "application/json", statusCode: 422);
});

app.MapGet("/exit", (IHostApplicationLifetime life) =>
{
    life.StopApplication();
    return Results.Ok();
});

app.MapControllers();
app.Run();

namespace WebApp
{
    public class User
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
    }

    public class ShopDb : DbContext
    {
        public ShopDb(DbContextOptions<ShopDb> options) : base(options) { }
        public DbSet<User> Users => Set<User>();
        protected override void OnModelCreating(ModelBuilder b) => b.Entity<User>().HasIndex(u => u.Email).IsUnique();
    }

    public static class Checkout
    {
        public static IResult Run(int quantity)
        {
            int Total(int q)
            {
                Func<int, int> price = n => n > 0 ? n * 100 : throw new InvalidOperationException("Cart is empty");
                return price(q);
            }
            return Results.Ok(new { total = Total(quantity) });
        }

        public static async Task<IResult> RunAsync(int id)
        {
            await Task.Yield();
            await LoadAsync(id);
            return Results.Ok();
        }

        static async Task LoadAsync(int id)
        {
            await Task.Delay(1);
            throw new KeyNotFoundException($"Order {id} not found");
        }

        public static void Validate(int quantity)
        {
            if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive");
        }
    }

    [ApiController]
    [Route("api/orders")]
    public class OrdersController : ControllerBase
    {
        [HttpGet("{id}")]
        public IActionResult Get(int id)
        {
            if (id == 0) throw new InvalidOperationException("Order 0 is reserved");
            return Ok(new { id });
        }
    }
}

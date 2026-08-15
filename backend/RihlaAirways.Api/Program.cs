using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using RihlaAirways.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// SERVICES — everything here MUST sit above builder.Build().
// once the app is built the service collection is sealed and any
// AddX() throws "the service collection cannot be modified because
// it is read-only"
// ============================================================

// hook up our AppDbContext to the sql server container -
// reads the connection string from appsettings.Development.json,
// this is what lets every controller just ask for AppDbContext in its constructor
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // stops "A possible object cycle was detected" from 500-ing the API
        // when someone returns a raw entity with navigation properties.
        // output DTOs are still the right fix - this is just a seatbelt
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// swashbuckle only - we had AddOpenApi() running alongside this, which meant
// two different spec generators serving two different json files. they don't
// crash, they just drift apart, and later the JWT "Authorize" button only
// exists in swashbuckle. new fix - dropped AddOpenApi/MapOpenApi entirely
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // several controllers each define their own StatusUpdateDto, RoleUpdateDto,
    // FeeUpdateDto etc as nested records. swashbuckle keys schemas on the SHORT
    // type name by default, so FlightsController+StatusUpdateDto collides with
    // BookingsController+StatusUpdateDto and the whole spec fails to generate.
    // FullName keeps them distinct - the Replace is because nested types come
    // through as "Namespace.Controller+Dto" and + isn't legal in a schema id
    options.CustomSchemaIds(type => type.FullName?.Replace("+", "."));

    // this is what puts the "Authorize" button in the swagger UI. without it
    // there's no way to test a protected endpoint from the browser
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        // ApiKey + In.Header, NOT Http + Scheme="bearer". the Http form is
        // technically more correct but swashbuckle 10.x doesn't reliably
        // attach the header with it - you authorize, the padlock closes,
        // and the request goes out with no Authorization header at all.
        // this form means we type the "Bearer " prefix ourselves
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Type: Bearer {your token}  — include the word Bearer and a space."
    });

    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer"),
            new List<string>()
        }
    });
});

// TokenService is scoped, same lifetime as the DbContext it sits beside.
// without this line AuthController can't be constructed at all
builder.Services.AddScoped<TokenService>();

// same lifetime, same reason - EmailService takes a DbContext. a singleton
// would capture a disposed context and fail on the second request.
// this line went missing when the pipeline block got pasted over, which is
// what caused the read-only crash: it had ended up BELOW builder.Build()
builder.Services.AddScoped<IEmailService, EmailService>();

// THIS is what makes every [Authorize] in the 14 controllers actually work.
// without it the attribute fires and finds no scheme to authenticate against
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
            // default is 5 minutes of grace on expiry, which makes
            // "watch the token expire" impossible to demo. zero it
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// the frontend is a separate origin (http://localhost:8080 when served by
// `python3 -m http.server 8080` from /frontend), so the browser blocks every
// fetch without this.
//
// this used to be AllowAnyOrigin(), which let ANY website a user happens to
// be visiting call this API from their browser. it never leaked the token -
// that lives in localStorage, which cross-origin script can't read - but it
// did mean any page could fire requests that ride along with whatever the
// browser sends. naming the origins costs nothing.
//
// the list is config-driven so a teammate on a different port doesn't have to
// edit code - add to Cors:Origins in appsettings.Development.json
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:8080", "http://127.0.0.1:8080", "http://localhost:5500" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader());
});


// ---- rate limiting ----
// BCrypt makes each password guess slow, but nothing stopped an attacker
// making unlimited guesses. that matters more here than usual because the
// seeded accounts share one password.
//
// a FIXED window rather than a sliding one on purpose: it's the cheapest to
// reason about, and "5 tries a minute from this IP" is easy to explain and to
// demo. partitioned by remote IP so one attacker can't lock out everyone else.
// 429 rather than the default 503 - it's the status that actually means
// "you're going too fast"
// two SEPARATE policies rather than one shared "auth" budget. they defend
// against different things:
//   login    - guessing the password of a known account. tight, because a
//              legitimate human does not need 6 attempts a minute
//   register - bulk-creating junk accounts. looser, because one person
//              legitimately correcting a validation error several times in a
//              row is normal, and several people behind one office NAT share
//              this IP
// they were briefly one policy, and the consequence showed up immediately:
// the test suite's own registrations ate the login budget and everything
// after them 429'd
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static Func<HttpContext, RateLimitPartition<string>> PerIp(int permits, int seconds) =>
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromSeconds(seconds),
                QueueLimit = 0            // reject immediately, don't queue
            });

    options.AddPolicy("auth-login", PerIp(permits: 5, seconds: 60));
    options.AddPolicy("auth-register", PerIp(permits: 10, seconds: 60));
});


var app = builder.Build();

// ============================================================
// PIPELINE — everything below is app.UseX() / app.MapX().
// each of these appears EXACTLY ONCE. the file had UseCors,
// UseAuthorization and MapControllers twice from an old paste,
// which silently runs the middleware twice per request
// ============================================================

// seed on startup, dev only. the scope is required because AppDbContext is
// registered as Scoped and there's no request scope at startup - asking the
// root provider for a scoped service throws
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DbSeeder.SeedAsync(db);

    // swagger goes here, BEFORE MapControllers - it was after it in the
    // old version, which happens to work but reads backwards
    app.UseSwagger();
    app.UseSwaggerUI();
}

// commented out on purpose - the js frontend and postman both choke on the
// 307 redirect when we're running plain http locally. turn it back on
// if we ever deploy anywhere real
// app.UseHttpsRedirection();

// ---- security headers ----
// three cheap ones that can't break anything.
//
// deliberately NO Content-Security-Policy: the frontend has ~94 inline
// onclick/onchange handlers, and any useful CSP (script-src 'self') blocks
// every one of them - the pages would render and then do nothing when
// clicked. adding CSP means converting those to addEventListener first.
// XSS is already covered by escapeHtml() at ~99 call sites in the frontend
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    // stop the browser guessing a response is HTML/JS when we said it's JSON,
    // which is how a JSON endpoint gets turned into a script include
    h["X-Content-Type-Options"] = "nosniff";
    // this API has no UI of its own, so nothing should ever frame it
    h["X-Frame-Options"] = "DENY";
    // don't leak the full URL (which can carry ids) to third parties
    h["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseCors("AllowFrontend");

// before authentication: a request that's being throttled shouldn't cost us
// a token validation, and brute-force protection has to apply to anonymous
// callers too
app.UseRateLimiter();

// ORDER MATTERS, and this is the classic bug. authentication BEFORE
// authorization: authenticate = read the token, build User.Claims.
// authorize = check those claims against [Authorize].
// flip them and every protected endpoint 401s because the claims don't
// exist yet when the check runs
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// the frontend is a separate origin in week 2 (file:// or live server),
// so the browser blocks every fetch without this. wide open is fine for
// local dev - we tighten it if this ever leaves our machines
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
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

app.UseCors("AllowFrontend");

// ORDER MATTERS, and this is the classic bug. authentication BEFORE
// authorization: authenticate = read the token, build User.Claims.
// authorize = check those claims against [Authorize].
// flip them and every protected endpoint 401s because the claims don't
// exist yet when the check runs
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
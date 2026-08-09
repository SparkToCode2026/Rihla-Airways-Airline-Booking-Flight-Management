using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using RihlaAirways.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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
        // output DTOs are still the right fix - this is just a seatbelt.
        // note this MUST live above builder.Build() - once the app is built
        // the service collection is sealed and any AddX() throws
        // "the service collection cannot be modified because it is read-only"
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
    // through as "Namespace.Controller+Dto" and + isn't legal in a schema id.
    // new fix
    options.CustomSchemaIds(type => type.FullName?.Replace("+", "."));
    // this is what puts the "Authorize" button in the swagger UI. without it
    // there's no way to test a protected endpoint from the browser, and this
    // is literally the button we click during the week 3 demo
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        // ApiKey + In.Header, NOT Http + Scheme="bearer". the Http form is
        // technically more correct but swashbuckle 10.x doesn't reliably
        // attach the header with it - you authorize, the padlock closes,
        // and the request goes out with no Authorization header at all.
        // this form means we type the "Bearer " prefix ourselves.
        // new fix - server was always fine, this was purely a swagger bug
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

// THIS is what makes every [Authorize] in the 13 controllers actually work.
// right now they throw a 500 because there's no scheme registered - the
// attribute fires and finds nothing to authenticate against
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
app.UseCors("AllowFrontend");

// ORDER MATTERS, and this is the classic bug. authentication BEFORE
// authorization: authenticate = read the token, build User.Claims.
// authorize = check those claims against [Authorize].
// flip them and every protected endpoint 401s because the claims don't
// exist yet when the check runs
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// commented out on purpose - the js frontend and postman both choke on the
// 307 redirect when we're running plain http locally. turn it back on
// if we ever deploy anywhere real
// app.UseHttpsRedirection();

app.UseCors("AllowFrontend");

// UseAuthorization is here already but does nothing yet - there's no
// authentication scheme registered. when JWT lands, UseAuthentication()
// goes on the line ABOVE this one. order matters: authenticate first
// (who are you), then authorize (what are you allowed to do)
app.UseAuthorization();

app.MapControllers();
app.Run();
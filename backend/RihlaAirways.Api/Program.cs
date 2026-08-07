using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// hook up our AppDbContext to the sql server container -
// reads the connection string from appsettings.Development.json,
// this is what lets every controller just ask for AppDbContext in its constructor
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers();

// swashbuckle only - we had AddOpenApi() running alongside this, which meant
// two different spec generators serving two different json files. they don't
// crash, they just drift apart, and later the JWT "Authorize" button only
// exists in swashbuckle. new fix - dropped AddOpenApi/MapOpenApi entirely
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// the frontend is a separate origin in week 2 (file:// or live server),
// so the browser blocks every fetch without this. wide open is fine for
// local dev - we tighten it if this ever leaves our machines
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // stops "A possible object cycle was detected" from 500-ing the API
        // when someone returns a raw entity with navigation properties.
        // output DTOs are still the right fix - this is just a seatbelt
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

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
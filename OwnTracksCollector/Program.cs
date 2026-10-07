using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OwnTracksCollector;
using Microsoft.Extensions.Options;
using OwnTracksCollector.Services;
using OwnTracksCollector.Settings;

var builder = Host.CreateApplicationBuilder(args);

// ── Windows Service support ────────────────────────────────────────────────
// AddWindowsService is a no-op when the process is not running under the SCM,
// so the same binary works both as a console app and as a Windows Service.
// The ServiceName is the identifier shown in services.msc and the Event Log.
builder.Services.AddWindowsService(options =>
    options.ServiceName = "OwnTracksCollector");

// ── Configuration ──────────────────────────────────────────────────────────
// Host.CreateApplicationBuilder already loads appsettings.json and the default
// DOTNET_ / ASPNETCORE_ environment variables. The explicit prefix below adds
// a second environment variable layer scoped to this application, allowing
// sensitive values (e.g. broker passwords) to be injected at runtime without
// modifying appsettings.json.
// Naming convention: OWNTRACKS_Mqtt__Password maps to config key Mqtt:Password.
builder.Configuration.AddEnvironmentVariables(prefix: "OWNTRACKS_");

// ── Options ────────────────────────────────────────────────────────────────
// Bound once and validated at startup, so a bad port or empty topic stops the app
// with a clear message instead of failing later inside the MQTT client.
builder.Services.AddOptions<MqttOptions>()
    .Bind(builder.Configuration.GetSection(MqttOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MqttOptions>, MqttOptionsValidator>();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

// ── Services ───────────────────────────────────────────────────────────────
// Singletons: they are stateful (a long-lived DB connection, the MQTT client, the queue
// between them) and must survive for the application lifetime.
builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
builder.Services.AddSingleton<WriteQueue>();
builder.Services.AddSingleton<MqttService>();

// Hosted services start in registration order and stop in reverse. DatabaseWriter is
// registered first so it stops last, after Worker has disconnected from the broker and
// no more messages can arrive, and it can finish writing what is still queued.
builder.Services.AddHostedService<DatabaseWriter>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();



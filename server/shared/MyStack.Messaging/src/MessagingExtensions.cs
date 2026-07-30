using Cronos;
using JasperFx.CodeGeneration.Model;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace MyStack.Messaging;

public static class MessagingExtensions
{
    public const string BrokerConnectionStringName = "MessageBroker";

    /// <summary>
    /// Wolverine over RabbitMQ with the stack's conventions: the app listens on its own queue
    /// (named after it), envelopes persist durably in the app's own <c>wolverine_&lt;app&gt;</c>
    /// Postgres schema, handler failures retry on a cooldown schedule and then dead-letter, and
    /// traces/metrics flow through the names MyStack.Observability subscribes. Publishing rules
    /// are the app's own to declare in <paramref name="configure"/> — which messages an app emits
    /// is domain knowledge, and this library carries none.
    /// </summary>
    public static WebApplicationBuilder AddMessaging(
        this WebApplicationBuilder builder,
        string appName,
        string databaseConnectionStringName,
        Action<WolverineOptions>? configure = null
    )
    {
        var database =
            builder.Configuration.GetConnectionString(databaseConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{databaseConnectionStringName} is not configured."
            );
        var broker =
            builder.Configuration.GetConnectionString(BrokerConnectionStringName)
            // Same rule as the database: failing to boot beats silently dropping messages, and a
            // fallback would be infrastructure topology compiled into the binary.
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{BrokerConnectionStringName} is not configured."
            );

        var options = new MessagingOptions();
        builder.Configuration.GetSection(MessagingOptions.SectionName).Bind(options);

        builder.Host.UseWolverine(wolverine =>
        {
            // Names the Wolverine:<app> meter, so each app's messaging metrics stay its own.
            wolverine.ServiceName = appName;

            // Wolverine 6 forbids service location in its generated handler code by default,
            // which bans handlers from depending on anything factory-registered — and framework
            // services (OpenIddict's managers, Identity's) are exactly that. Allowed knowingly:
            // the price is a scoped-container lookup per message, irrelevant at message
            // frequency.
            wolverine.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

            wolverine.UseRabbitMq(new Uri(broker)).AutoProvision();

            // The durable inbox/outbox: envelopes are persisted before they ride the broker and
            // acknowledged after handling, in a schema per app — the same isolation split the
            // apps' own schemas use.
            wolverine.PersistMessagesWithPostgresql(database, SchemaFor(appName));
            wolverine.Policies.UseDurableInboxOnAllListeners();
            wolverine.Policies.UseDurableOutboxOnAllSendingEndpoints();

            // Every app consumes exactly its own queue. Cross-app messages are published *to*
            // another app's queue; nothing ever competes for another app's work.
            wolverine.ListenToRabbitQueue(appName);

            // A handler that throws retries on the cooldown schedule, then the message parks in
            // the dead-letter queue — visible in the broker UI, never silently dropped.
            double[] cooldowns = options.RetryCooldownsInSeconds ?? [1, 5, 30];
            wolverine
                .Policies.OnException<Exception>()
                .RetryWithCooldown([.. cooldowns.Select(TimeSpan.FromSeconds)]);

            // One node locally and in tests; clustered durability agents are a deployment
            // concern, not a default.
            if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
            {
                wolverine.Durability.Mode = DurabilityMode.Solo;
            }

            configure?.Invoke(wolverine);
        });

        // Creates the wolverine_<app> schema objects (and declared broker objects) on startup —
        // Wolverine owns its schema the way EF owns the app's.
        builder.Host.UseResourceSetupOnStartup();

        builder.Services.AddHostedService<MessageScheduler>();

        return builder;
    }

    /// <summary>
    /// Publishes a <typeparamref name="TMessage"/> at every occurrence of <paramref name="cron"/>
    /// (UTC; five fields, or six to include seconds). Scheduling is just a clock over the message
    /// pipeline — the handler's queue owns retries, dead-lettering and telemetry — so an instance
    /// that is down when a tick passes skips it, and two instances both publish: schedules carry
    /// idempotent maintenance-style work.
    /// </summary>
    public static IServiceCollection AddScheduledMessage<TMessage>(
        this IServiceCollection services,
        string cron
    )
        where TMessage : class, new() => services.AddScheduledMessage(cron, () => new TMessage());

    /// <summary>The factory overload, for scheduled messages that carry data.</summary>
    public static IServiceCollection AddScheduledMessage<TMessage>(
        this IServiceCollection services,
        string cron,
        Func<TMessage> factory
    )
        where TMessage : class
    {
        // Parsed here so a typo'd cron fails the boot, not silently at its first tick.
        var format =
            cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6
                ? CronFormat.IncludeSeconds
                : CronFormat.Standard;
        var expression = CronExpression.Parse(cron, format);

        services.AddSingleton(
            new ScheduledMessage(cron, expression, factory, typeof(TMessage).Name)
        );

        return services;
    }

    internal static string SchemaFor(string appName) => $"wolverine_{appName}";
}

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfiumRaster.Orchestration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers <see cref="PdfRenderOrchestrator" /> with the standard .NET dependency-injection, hosting, and health
/// check abstractions.
/// </summary>
public static class PdfiumRasterOrchestratorServiceCollectionExtensions
{
    private const string DefaultHealthCheckName = "pdfiumraster-orchestrator";

    /// <summary>
    /// Registers one process-wide <see cref="PdfRenderOrchestrator" /> singleton and its hosted shutdown service.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <returns>The same service collection so that additional registrations can be chained.</returns>
    /// <remarks>
    /// The orchestrator uses the host's <see cref="ILoggerFactory" /> when one is registered. Workers start
    /// asynchronously during host startup, accepted work drains during normal host shutdown, and queued work is
    /// canceled if the host shutdown token is canceled.
    /// </remarks>
    public static IServiceCollection AddPdfiumRasterOrchestrator(this IServiceCollection services)
    {
        return AddPdfiumRasterOrchestrator(services, static _ => { });
    }

    /// <summary>
    /// Registers and configures one process-wide <see cref="PdfRenderOrchestrator" /> singleton and its hosted
    /// shutdown service.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configure">Configures the options used when the singleton is first created.</param>
    /// <returns>The same service collection so that additional registrations can be chained.</returns>
    /// <remarks>
    /// Options are evaluated once when the singleton is first resolved. The host's <see cref="ILoggerFactory" /> is
    /// assigned before <paramref name="configure" /> runs, so the callback can explicitly replace or disable it.
    /// Repeated calls preserve the first orchestrator registration and add only one hosted shutdown service.
    /// </remarks>
    public static IServiceCollection AddPdfiumRasterOrchestrator(
        this IServiceCollection services,
        Action<PdfRenderOrchestratorOptions> configure)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        services.TryAddSingleton(serviceProvider =>
        {
            var options = new PdfRenderOrchestratorOptions
            {
                LoggerFactory = serviceProvider.GetService<ILoggerFactory>() ??
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            };
            configure(options);
            return PdfRenderOrchestrator.CreateForHost(options);
        });
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, PdfiumRasterOrchestratorHostedService>());
        return services;
    }

    /// <summary>
    /// Registers a readiness check for the singleton <see cref="PdfRenderOrchestrator" />.
    /// </summary>
    /// <param name="builder">The application's health-check builder.</param>
    /// <param name="name">The registration name. The default is <c>pdfiumraster-orchestrator</c>.</param>
    /// <param name="failureStatus">
    /// The status used by the health-check system if constructing or invoking the check throws. The default is
    /// <see cref="HealthStatus.Unhealthy" />.
    /// </param>
    /// <param name="tags">Optional tags used to select the check for an endpoint.</param>
    /// <returns>The same health-check builder so that additional checks can be chained.</returns>
    /// <remarks>
    /// The check is healthy when all workers are available, degraded during initial hosted startup or while a worker
    /// is unavailable or being replaced, and unhealthy after a terminal failure or after shutdown starts. It inspects
    /// in-memory state and never renders a document or starts a separate probe worker.
    /// </remarks>
    public static IHealthChecksBuilder AddPdfiumRasterOrchestrator(
        this IHealthChecksBuilder builder,
        string name = DefaultHealthCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Health-check name cannot be empty or whitespace.", nameof(name));
        }

        builder.Add(new HealthCheckRegistration(
            name,
            serviceProvider => new PdfiumRasterOrchestratorHealthCheck(
                serviceProvider.GetRequiredService<PdfRenderOrchestrator>()),
            failureStatus,
            tags));
        return builder;
    }
}

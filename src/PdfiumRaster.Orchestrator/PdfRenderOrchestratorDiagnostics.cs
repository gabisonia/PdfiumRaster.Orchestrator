namespace PdfiumRaster.Orchestration;

/// <summary>
/// Provides stable names used to configure orchestrator telemetry collection.
/// </summary>
public static class PdfRenderOrchestratorDiagnostics
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource" /> that emits request activities.
    /// </summary>
    public const string ActivitySourceName = "PdfiumRaster.Orchestrator";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.Metrics.Meter" /> that emits operational metrics.
    /// </summary>
    public const string MeterName = "PdfiumRaster.Orchestrator";
}

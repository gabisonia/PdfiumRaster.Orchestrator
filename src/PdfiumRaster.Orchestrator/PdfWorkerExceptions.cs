namespace PdfiumRaster.Orchestration;

/// <summary>
/// Base exception for failures involving an isolated PDFium worker process.
/// </summary>
public class PdfWorkerException : Exception
{
    /// <summary>
    /// Initializes a worker exception.
    /// </summary>
    /// <param name="message">Description of the worker failure.</param>
    public PdfWorkerException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a worker exception with an inner exception.
    /// </summary>
    /// <param name="message">Description of the worker failure.</param>
    /// <param name="innerException">Underlying failure.</param>
    public PdfWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception thrown when an isolated worker cannot start or complete its protocol handshake.
/// </summary>
public sealed class PdfWorkerStartupException : PdfWorkerException
{
    internal PdfWorkerStartupException(string message)
        : base(message)
    {
    }

    internal PdfWorkerStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception thrown when a worker exits unexpectedly while processing a request.
/// </summary>
public sealed class PdfWorkerCrashedException : PdfWorkerException
{
    internal PdfWorkerCrashedException(string message, int? exitCode, string standardError, Exception? innerException = null)
        : base(message, innerException ?? new EndOfStreamException(message))
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Gets the worker exit code when it was available.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Gets a bounded tail of the worker standard-error output.
    /// </summary>
    public string StandardError { get; }
}

/// <summary>
/// The exception thrown when a worker exceeds the configured hard request timeout.
/// </summary>
public sealed class PdfWorkerTimeoutException : PdfWorkerException
{
    internal PdfWorkerTimeoutException(TimeSpan timeout)
        : base($"The PDFium worker exceeded the active request timeout of {timeout}.")
    {
        Timeout = timeout;
    }

    /// <summary>
    /// Gets the configured active request timeout.
    /// </summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// The exception thrown when a worker reports a rendering, validation, or image-encoding failure.
/// </summary>
public sealed class PdfWorkerRemoteException : PdfWorkerException
{
    internal PdfWorkerRemoteException(string remoteExceptionType, string message)
        : base($"The PDFium worker reported {remoteExceptionType}: {message}")
    {
        RemoteExceptionType = remoteExceptionType;
    }

    /// <summary>
    /// Gets the full managed type name reported by the worker.
    /// </summary>
    public string RemoteExceptionType { get; }
}

/// <summary>
/// The exception thrown when communication with a worker violates the private orchestrator protocol.
/// </summary>
public sealed class PdfWorkerProtocolException : PdfWorkerException
{
    internal PdfWorkerProtocolException(string message)
        : base(message)
    {
    }

    internal PdfWorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

using core.domain.Auditing;

namespace core.Auditing;

// Thin wrapper so the audit Serilog logger can be registered/injected without
// colliding with Microsoft.Extensions.Logging.ILogger<T> or the app's default
// Serilog.ILogger in DI.
public class AuditLogger
{
    public Serilog.ILogger Logger { get; }

    public AuditLogger(Serilog.ILogger logger)
    {
        Logger = logger;
    }
}

public class SerilogAuditSink : IAuditSink
{
    private readonly AuditLogger _auditLogger;

    public SerilogAuditSink(AuditLogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    public Task WriteAsync(AuditEntry entry)
    {
        _auditLogger.Logger
            .ForContext("EntityName", entry.EntityName)
            .ForContext("EntityId", entry.EntityId)
            .ForContext("Feature", entry.Feature)
            .ForContext("Operation", entry.Operation)
            .ForContext("ChangedFields", entry.ChangedFields, destructureObjects: true)
            .ForContext("ChangedBy", entry.ChangedBy)
            .ForContext("ApprovedBy", entry.ApprovedBy)
            .ForContext("ApprovalStatus", entry.ApprovalStatus)
            .Information("Audit: {EntityName} {Operation} by {ChangedBy}", entry.EntityName, entry.Operation, entry.ChangedBy);

        return Task.CompletedTask;
    }
}

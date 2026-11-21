using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Transfers;

/// <summary>A finished (or interrupted) operation, persisted so it can be reviewed and retried after a restart.</summary>
public sealed class TransferRecord
{
    public Guid Id { get; set; }
    public OperationKind Kind { get; set; }
    public string SourceProviderId { get; set; } = string.Empty;
    public List<StorageItem> Items { get; set; } = new();
    public StorageLocation Destination { get; set; } = new(string.Empty, string.Empty);
    public string Title { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string DestinationName { get; set; } = string.Empty;
    public OperationStatus Status { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public int Files { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset? Finished { get; set; }

    public bool CanRetry => Status is OperationStatus.Failed or OperationStatus.CompletedWithErrors or OperationStatus.Canceled or OperationStatus.Interrupted;

    public static TransferRecord From(FileOperation op, OperationStatus? status = null) => new()
    {
        Id = op.Id,
        Kind = op.Kind,
        SourceProviderId = op.SourceProviderId,
        Items = op.Items.ToList(),
        Destination = op.Destination,
        Title = $"{(op.Kind == OperationKind.Copy ? "Copy" : "Move")} {op.What} to {op.DestinationName}",
        SourceName = op.SourceName,
        DestinationName = op.DestinationName,
        Status = status ?? op.Status,
        TotalBytes = op.TotalBytes,
        TransferredBytes = op.TransferredBytes,
        Files = op.TotalFiles,
        Skipped = op.SkippedFiles,
        Errors = op.Errors.Take(20).ToList(),
        Started = op.Created,
        Finished = op.FinishedAt ?? DateTimeOffset.Now,
    };
}

public interface ITransferHistory
{
    IReadOnlyList<TransferRecord> Records { get; }

    /// <summary>Raised (any thread) when records are added or removed.</summary>
    event EventHandler? Changed;

    void Add(TransferRecord record);

    void Remove(Guid id);

    void Clear();

    /// <summary>Writes pending changes synchronously (app exit).</summary>
    void Flush();
}

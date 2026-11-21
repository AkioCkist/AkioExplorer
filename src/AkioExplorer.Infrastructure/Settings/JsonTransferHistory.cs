using System.Text.Json;
using System.Text.Json.Serialization;
using AkioExplorer.Core.Transfers;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.Infrastructure.Settings;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<TransferRecord>))]
internal sealed partial class TransferHistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Transfer history in %LocalAppData%\AkioExplorer\transfers.json (newest first, capped). Records every visible
/// operation when it finishes; operations still running at exit are stored as "Interrupted" so they can be retried.
/// </summary>
public sealed class JsonTransferHistory : ITransferHistory, IDisposable
{
    private const int Capacity = 300;

    private readonly string _path;
    private readonly ILogger<JsonTransferHistory> _logger;
    private readonly IFileOperationService _operations;
    private readonly List<TransferRecord> _records;
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private bool _dirty;

    public JsonTransferHistory(IFileOperationService operations, ILogger<JsonTransferHistory> logger)
        : this(Path.Combine(AppPaths.DataFolder, "transfers.json"), operations, logger)
    {
    }

    public JsonTransferHistory(string path, IFileOperationService operations, ILogger<JsonTransferHistory> logger)
    {
        _path = path;
        _logger = logger;
        _operations = operations;
        _records = Load();
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        operations.OperationFinished += (_, op) =>
        {
            if (!op.Options.Internal) Add(TransferRecord.From(op));
        };
    }

    public IReadOnlyList<TransferRecord> Records
    {
        get { lock (_gate) return _records.ToArray(); }
    }

    public event EventHandler? Changed;

    public void Add(TransferRecord record)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => r.Id == record.Id);
            _records.Insert(0, record);
            if (_records.Count > Capacity) _records.RemoveRange(Capacity, _records.Count - Capacity);
            MarkDirty();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (_records.RemoveAll(r => r.Id == id) == 0) return;
            MarkDirty();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
            MarkDirty();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores operations that are still running as interrupted (called on exit), then flushes.</summary>
    public void RecordInterrupted()
    {
        foreach (var op in _operations.Operations.Where(o => !o.IsFinished && !o.Options.Internal))
            Add(TransferRecord.From(op, OperationStatus.Interrupted));
        Flush();
    }

    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_records, TransferHistoryJsonContext.Default.ListTransferRecord);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't save transfer history");
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _debounce?.Change(1000, Timeout.Infinite);
    }

    private List<TransferRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize(stream, TransferHistoryJsonContext.Default.ListTransferRecord) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transfer history is unreadable; starting a new one");
        }
        return new List<TransferRecord>();
    }

    public void Dispose()
    {
        _debounce.Dispose();
        Flush();
    }
}

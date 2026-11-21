namespace AkioExplorer.App.Services;

/// <summary>Ctrl+Z for file operations (rename, new folder, copy, move). Heuristic 3: user control and freedom.</summary>
public sealed class UndoService
{
    private const int Capacity = 50;
    private readonly LinkedList<(string Description, Func<Task> Undo)> _stack = new();

    public bool CanUndo => _stack.Count > 0;

    public string? NextDescription => _stack.First?.Value.Description;

    public event EventHandler? Changed;

    public void Push(string description, Func<Task> undo)
    {
        _stack.AddFirst((description, undo));
        while (_stack.Count > Capacity) _stack.RemoveLast();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the most recent action. Returns its description, or null if there was nothing to undo.</summary>
    public async Task<string?> UndoAsync()
    {
        if (_stack.First is not { } node) return null;
        _stack.RemoveFirst();
        Changed?.Invoke(this, EventArgs.Empty);
        await node.Value.Undo();
        return node.Value.Description;
    }
}

/// <summary>In-process drag payload: keeps full item metadata (and later cloud items) while dragging inside the app.</summary>
public sealed class DragDropBroker
{
    public const string PayloadProperty = "AkioDragId";

    public string? Id { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public IReadOnlyList<Core.Models.StorageItem> Items { get; private set; } = [];

    public string Begin(string providerId, IReadOnlyList<Core.Models.StorageItem> items)
    {
        Id = Guid.NewGuid().ToString("N");
        ProviderId = providerId;
        Items = items;
        return Id;
    }

    public void End()
    {
        Id = null;
        Items = [];
    }
}

namespace WinPreview.Session;

public sealed class UndoManager
{
    private sealed record Entry(string Name, Action Undo, Action Redo);

    private readonly Stack<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.TryPeek(out var e) ? e.Name : null;
    public string? RedoName => _redo.TryPeek(out var e) ? e.Name : null;

    /// <summary>Records an action that has already been performed.</summary>
    public void Push(string name, Action undo, Action redo)
    {
        _undo.Push(new Entry(name, undo, redo));
        _redo.Clear();
        if (_undo.Count > 200) Trim();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var e)) return;
        e.Undo();
        _redo.Push(e);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var e)) return;
        e.Redo();
        _undo.Push(e);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    private void Trim()
    {
        var keep = _undo.Take(150).Reverse().ToList();
        _undo.Clear();
        foreach (var e in keep) _undo.Push(e);
    }
}

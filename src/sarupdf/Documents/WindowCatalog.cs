namespace SaruPdf.Documents;

// UI-thread owned. Register before starting asynchronous rendering.
internal sealed class WindowCatalog<T> where T : class
{
    private readonly Dictionary<string, T> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<T> windows = [];
    public IReadOnlyList<T> Windows => windows;
    public T? EmptyWindow { get; private set; }
    public T? LastActive { get; private set; }

    public void Add(T window)
    {
        windows.Add(window);
        EmptyWindow = window;
        LastActive = window;
    }

    public bool TryFind(string path, out T? window) => documents.TryGetValue(path, out window);

    public void Assign(string path, T window)
    {
        documents.Add(path, window);
        if (ReferenceEquals(EmptyWindow, window)) EmptyWindow = null;
    }

    public void Activate(T window) => LastActive = window;

    public void Reassign(string path, T window)
    {
        if (documents.TryGetValue(path, out var existing) && !ReferenceEquals(existing, window))
            throw new InvalidOperationException("保存先は別のウィンドウで開かれています。");
        foreach (var key in documents.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
            documents.Remove(key);
        documents[path] = window;
    }

    public void Remove(T window)
    {
        windows.Remove(window);
        foreach (var key in documents.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
            documents.Remove(key);
        if (ReferenceEquals(EmptyWindow, window)) EmptyWindow = null;
        if (ReferenceEquals(LastActive, window)) LastActive = windows.LastOrDefault();
    }
}

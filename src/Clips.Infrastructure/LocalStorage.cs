using System.IO;
using Clips.Core;

namespace Clips.Infrastructure;

public sealed class AppPaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "clips.db");
    public string Captures => Path.Combine(Root, "captures");
    public string Logs => Path.Combine(Root, "logs");
    public AppPaths() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.StorageDirectoryName)) { }
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); Directory.CreateDirectory(Captures); Directory.CreateDirectory(Logs);
    }
}
public sealed class ImageStorage(AppPaths paths) : IImageStorage
{
    public string Resolve(string relativePath)
    {
        if (Path.GetFileName(relativePath) != relativePath || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(relativePath), "D", out _) || Path.GetExtension(relativePath) != ".png")
            throw new ArgumentException("Invalid capture image path.");
        return Path.Combine(paths.Captures, relativePath);
    }
    public async Task<string> SaveAsync(Guid id, byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new ArgumentException("Screenshot encoding failed.");
        var name = $"{id:D}.png"; var final = Resolve(name); var temp = final + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(png); await stream.FlushAsync(); }
            await Task.Run(() => File.Move(temp, final)); return name;
        }
        finally { await Task.Run(() => { if (File.Exists(temp)) File.Delete(temp); }); }
    }
    public Task DeleteAsync(string relativePath) => Task.Run(() => File.Delete(Resolve(relativePath)));
    public Task CleanupTemporaryAsync() => Task.Run(() => { foreach (var file in Directory.EnumerateFiles(paths.Captures, "*.png.tmp")) File.Delete(file); });
    public async Task ReconcileAsync(INotebookStore store)
    {
        await CleanupTemporaryAsync(); var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var notebook in (await store.GetNotebooksAsync()).Concat(await store.GetNotebooksAsync(true)))
            foreach (var item in await store.GetCapturesAsync(notebook.Id)) if (item.ImageRelativePath != null) referenced.Add(item.ImageRelativePath);
        await Task.Run(() => { foreach (var file in Directory.EnumerateFiles(paths.Captures, "*.png")) if (!referenced.Contains(Path.GetFileName(file))) File.Delete(file); });
    }
}
public sealed class LocalLog(AppPaths paths) : ILocalLog
{
    private readonly object gate = new();
    public void Event(string eventName, int? errorCode = null)
    {
        // Callers provide only constant event identifiers and numeric codes, never exception messages.
        _ = Task.Run(() =>
        {
            try
            {
                lock (gate)
                {
                    var path = Path.Combine(paths.Logs, "clips.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    {
                        for (var i = 2; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                        File.Move(path, path + ".1", true);
                    }
                    File.AppendAllText(path, $"{DateTime.UtcNow:O} {eventName} {errorCode}\n");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }
}

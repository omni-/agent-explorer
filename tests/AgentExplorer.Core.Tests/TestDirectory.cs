namespace AgentExplorer.Core.Tests;

internal sealed class TestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentExplorer.Tests." + Guid.NewGuid().ToString("N"));

    internal TestDirectory() => Directory.CreateDirectory(Path);

    internal string Write(string name, string text)
    {
        var file = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
        return file;
    }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var expected = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("AgentExplorer.Tests.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected test directory.");
        }

        Directory.Delete(resolved, true);
    }
}

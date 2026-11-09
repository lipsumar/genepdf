namespace GenePdf.Templates;

/// <summary>
/// Read access to the template tree. Paths are relative, '/'-separated, e.g.
/// <c>documents/invoice-fr/1.0/document.xhtml</c>.
/// </summary>
public interface ITemplateSource
{
    byte[]? ReadBytes(string path);

    /// <summary>Immediate sub-directory names of a directory.</summary>
    IReadOnlyList<string> ListDirectories(string path);

    /// <summary>File names (not paths) directly inside a directory.</summary>
    IReadOnlyList<string> ListFiles(string path);
}

public static class TemplateSourceExtensions
{
    public static string? ReadText(this ITemplateSource source, string path)
        => source.ReadBytes(path) is { } b ? System.Text.Encoding.UTF8.GetString(b).TrimStart('﻿') : null;
}

public static class TemplatePath
{
    /// <summary>Normalizes a relative path and rejects anything that would escape the template root.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new ArgumentException($"Path escapes the template root: {path}");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (part.Contains(':')) throw new ArgumentException($"Invalid template path: {path}");
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    public static string Combine(string dir, string relative) => Normalize(dir + "/" + relative);

    public static string Directory(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : "";
}

public sealed class FileSystemTemplateSource(string rootDirectory) : ITemplateSource
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public string FullPath(string path)
    {
        var full = Path.GetFullPath(Path.Combine(RootDirectory, TemplatePath.Normalize(path)));
        if (full != RootDirectory && !full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Path escapes the template root: {path}");
        return full;
    }

    public byte[]? ReadBytes(string path)
    {
        var full = FullPath(path);
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    public IReadOnlyList<string> ListDirectories(string path)
    {
        var full = FullPath(path);
        return System.IO.Directory.Exists(full)
            ? System.IO.Directory.GetDirectories(full).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.')).Order().ToList()
            : [];
    }

    public IReadOnlyList<string> ListFiles(string path)
    {
        var full = FullPath(path);
        return System.IO.Directory.Exists(full)
            ? System.IO.Directory.GetFiles(full).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.')).Order().ToList()
            : [];
    }
}

/// <summary>Unsaved edits (from the dev UI) layered over another source.</summary>
public sealed class OverlayTemplateSource(ITemplateSource inner, IReadOnlyDictionary<string, string> overrides) : ITemplateSource
{
    private readonly Dictionary<string, string> _overrides =
        overrides.ToDictionary(kv => TemplatePath.Normalize(kv.Key), kv => kv.Value);

    public byte[]? ReadBytes(string path)
        => _overrides.TryGetValue(TemplatePath.Normalize(path), out var text) ? System.Text.Encoding.UTF8.GetBytes(text) : inner.ReadBytes(path);

    public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);

    public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
}

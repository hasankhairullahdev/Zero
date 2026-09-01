using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using UglyToad.PdfPig;

namespace Zero.FileManager.Tools;

[McpServerToolType]
public sealed class FileManagerTools
{
    // ─── read_file ────────────────────────────────────────────────────────────

    [McpServerTool, Description("Read the text content of a file.")]
    public static string read_file(
        [Description("Absolute path to the file.")] string path)
    {
        if (!File.Exists(path))
            return $"Error: file not found — {path}";

        return File.ReadAllText(path, Encoding.UTF8);
    }

    // ─── write_file ───────────────────────────────────────────────────────────

    [McpServerTool, Description("Write or append text content to a file. Creates the file and any missing directories if they do not exist.")]
    public static string write_file(
        [Description("Absolute path to the file.")] string path,
        [Description("Text content to write.")] string content,
        [Description("If true, append to the file instead of overwriting. Default: false.")] bool append = false)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (append)
            File.AppendAllText(path, content, Encoding.UTF8);
        else
            File.WriteAllText(path, content, Encoding.UTF8);

        return $"OK: file written — {path}";
    }

    // ─── list_directory ───────────────────────────────────────────────────────

    [McpServerTool, Description("List files and subdirectories inside a directory.")]
    public static string list_directory(
        [Description("Absolute path to the directory.")] string path,
        [Description("If true, list recursively. Default: false.")] bool recursive = false)
    {
        if (!Directory.Exists(path))
            return $"Error: directory not found — {path}";

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var sb = new StringBuilder();

        foreach (var dir in Directory.GetDirectories(path, "*", option))
            sb.AppendLine($"[DIR]  {dir}");

        foreach (var file in Directory.GetFiles(path, "*", option))
        {
            var info = new FileInfo(file);
            sb.AppendLine($"[FILE] {file}  ({info.Length:N0} bytes)");
        }

        return sb.Length == 0 ? "(empty directory)" : sb.ToString();
    }

    // ─── search_files ─────────────────────────────────────────────────────────

    [McpServerTool, Description("Search for files by name pattern or text content within a directory.")]
    public static string search_files(
        [Description("Absolute path to the directory to search in.")] string path,
        [Description("File name pattern to match (e.g. '*.txt'). Leave empty to search all files.")] string namePattern = "*",
        [Description("Text to search for inside file contents. Leave empty to search by name only.")] string contentQuery = "",
        [Description("If true, search recursively. Default: true.")] bool recursive = true)
    {
        if (!Directory.Exists(path))
            return $"Error: directory not found — {path}";

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(namePattern) ? "*" : namePattern;
        var files = Directory.GetFiles(path, pattern, option);

        if (string.IsNullOrWhiteSpace(contentQuery))
        {
            return files.Length == 0
                ? "No files found."
                : string.Join(Environment.NewLine, files);
        }

        var matches = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                if (text.Contains(contentQuery, StringComparison.OrdinalIgnoreCase))
                    matches.Add(file);
            }
            catch
            {
                // skip unreadable files (binary, locked, etc.)
            }
        }

        return matches.Count == 0
            ? "No files matched the content query."
            : string.Join(Environment.NewLine, matches);
    }

    // ─── delete_file ──────────────────────────────────────────────────────────

    [McpServerTool, Description("Delete a file permanently.")]
    public static string delete_file(
        [Description("Absolute path to the file to delete.")] string path)
    {
        if (!File.Exists(path))
            return $"Error: file not found — {path}";

        File.Delete(path);
        return $"OK: file deleted — {path}";
    }

    // ─── read_pdf ─────────────────────────────────────────────────────────────

    [McpServerTool, Description("Extract plain text from a PDF file.")]
    public static string read_pdf(
        [Description("Absolute path to the PDF file.")] string path)
    {
        if (!File.Exists(path))
            return $"Error: file not found — {path}";

        try
        {
            var sb = new StringBuilder();
            using var document = PdfDocument.Open(path);

            foreach (var page in document.GetPages())
                sb.AppendLine(page.Text);

            return sb.Length == 0 ? "(no text extracted from PDF)" : sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: failed to read PDF — {ex.Message}";
        }
    }
}

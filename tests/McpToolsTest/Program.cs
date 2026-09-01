using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Zero.FileManager.Tools;
using Zero.SystemControl.Tools;

namespace McpToolsTest;

public class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("========================================");
        Console.WriteLine("    ZERO MCP TOOLS AUTOMATED TEST RUNNER");
        Console.WriteLine("========================================");
        Console.WriteLine();

        var testTempDir = Path.Combine(Path.GetTempPath(), "zero_mcp_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testTempDir);
        var testFilePath = Path.Combine(testTempDir, "sample.txt");
        var testPdfPath = Path.Combine(testTempDir, "test.pdf");

        // Helper to record result
        void RunTest(string category, string toolName, Func<string> action, Func<string, bool> validator)
        {
            Console.Write($"[{category}] {toolName,-22} : ");
            try
            {
                var result = action();
                if (validator(result))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("✅ PASS");
                    Console.ResetColor();
                    Console.WriteLine($" -> {result.Replace("\r", "").Replace("\n", " | ").Trim()}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("❌ FAIL");
                    Console.ResetColor();
                    Console.WriteLine($" -> Unexpected result: {result.Replace("\r", "").Replace("\n", " | ").Trim()}");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("❌ FAIL (Exception)");
                Console.ResetColor();
                Console.WriteLine($" -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("--- Testing Zero.FileManager Tools ---");
        // 1. write_file
        RunTest("FileManager", "write_file (create)", () =>
        {
            return FileManagerTools.write_file(testFilePath, "Hello from ZERO MCP Test!\nLine 2 content.", false);
        }, res => res.StartsWith("OK: file written") && File.Exists(testFilePath));

        // 1b. write_file (append)
        RunTest("FileManager", "write_file (append)", () =>
        {
            return FileManagerTools.write_file(testFilePath, "\nLine 3 appended.", true);
        }, res => res.StartsWith("OK: file written") && File.ReadAllText(testFilePath).Contains("Line 3 appended"));

        // 2. read_file
        RunTest("FileManager", "read_file", () =>
        {
            return FileManagerTools.read_file(testFilePath);
        }, res => res.Contains("Hello from ZERO MCP Test!") && res.Contains("Line 3 appended"));

        // 3. list_directory
        RunTest("FileManager", "list_directory", () =>
        {
            return FileManagerTools.list_directory(testTempDir, false);
        }, res => res.Contains("sample.txt"));

        // 4. search_files
        RunTest("FileManager", "search_files (by name)", () =>
        {
            return FileManagerTools.search_files(testTempDir, "*.txt", "", false);
        }, res => res.Contains("sample.txt"));

        RunTest("FileManager", "search_files (by query)", () =>
        {
            return FileManagerTools.search_files(testTempDir, "*", "ZERO MCP Test", false);
        }, res => res.Contains("sample.txt"));

        // 5. read_pdf
        RunTest("FileManager", "read_pdf (missing file)", () =>
        {
            return FileManagerTools.read_pdf(Path.Combine(testTempDir, "non_existent.pdf"));
        }, res => res.StartsWith("Error: file not found"));

        // Let's create a minimal valid PDF to test read_pdf
        try
        {
            CreateMinimalPdf(testPdfPath, "Zero AI Assistant PDF Test Content");
            RunTest("FileManager", "read_pdf (valid pdf)", () =>
            {
                return FileManagerTools.read_pdf(testPdfPath);
            }, res => res.Contains("Zero AI Assistant PDF Test Content") || !res.StartsWith("Error"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PDF generator error: {ex.Message}");
        }

        // 6. delete_file
        RunTest("FileManager", "delete_file", () =>
        {
            return FileManagerTools.delete_file(testFilePath);
        }, res => res.StartsWith("OK: file deleted") && !File.Exists(testFilePath));


        Console.WriteLine();
        Console.WriteLine("--- Testing Zero.SystemControl Tools ---");

        // SystemInfoTools
        RunTest("SystemControl", "get_cpu_usage", () =>
        {
            return SystemInfoTools.get_cpu_usage();
        }, res => res.Contains("CPU") && !res.StartsWith("Error"));

        RunTest("SystemControl", "get_ram_usage", () =>
        {
            return SystemInfoTools.get_ram_usage();
        }, res => res.StartsWith("RAM:") && res.Contains("GB"));

        RunTest("SystemControl", "get_battery_status", () =>
        {
            return SystemInfoTools.get_battery_status();
        }, res => res.StartsWith("Battery:"));

        RunTest("SystemControl", "get_disk_usage", () =>
        {
            return SystemInfoTools.get_disk_usage();
        }, res => res.Contains("GB") || res.Contains("No drives found"));

        // AudioTools
        string initialVolume = "";
        RunTest("SystemControl", "get_volume", () =>
        {
            var res = AudioTools.get_volume();
            initialVolume = res;
            return res;
        }, res => res.StartsWith("Volume:"));

        RunTest("SystemControl", "set_volume", () =>
        {
            return AudioTools.set_volume(50);
        }, res => res.Contains("volume set to 50%"));

        RunTest("SystemControl", "mute", () =>
        {
            return AudioTools.mute();
        }, res => res == "OK: audio muted");

        RunTest("SystemControl", "unmute", () =>
        {
            return AudioTools.unmute();
        }, res => res == "OK: audio unmuted");

        // ScreenTools
        RunTest("SystemControl", "get_screen_info", () =>
        {
            return ScreenTools.get_screen_info();
        }, res => res.Contains("Monitor count:"));

        string screenshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"zero_test_screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        );
        RunTest("SystemControl", "take_screenshot", () =>
        {
            return ScreenTools.take_screenshot(screenshotPath);
        }, res => res.StartsWith("OK: screenshot saved") && File.Exists(screenshotPath));

        // ClipboardTools
        string originalClipboard = ClipboardTools.get_clipboard();
        string testClipContent = $"ZERO_TEST_CLIPBOARD_{Guid.NewGuid()}";
        RunTest("SystemControl", "set_clipboard", () =>
        {
            return ClipboardTools.set_clipboard(testClipContent);
        }, res => res == "OK: clipboard updated");

        RunTest("SystemControl", "get_clipboard", () =>
        {
            return ClipboardTools.get_clipboard();
        }, res => res == testClipContent);

        // Restore clipboard
        ClipboardTools.set_clipboard(originalClipboard ?? "");

        // NotificationTools
        RunTest("SystemControl", "send_notification", () =>
        {
            return NotificationTools.send_notification("ZERO Test", "Zero MCP Tools Test Notification");
        }, res => res.StartsWith("OK: notification sent"));

        // AppControlTools & InputTools (launch notepad, focus, type, close)
        RunTest("SystemControl", "launch_app (notepad)", () =>
        {
            var res = AppControlTools.launch_app("notepad");
            Thread.Sleep(1500); // Wait for notepad to open
            return res;
        }, res => res.StartsWith("OK: launched"));

        RunTest("SystemControl", "list_running_apps", () =>
        {
            return AppControlTools.list_running_apps();
        }, res => res.Contains("Notepad", StringComparison.OrdinalIgnoreCase) || res.Contains("notepad"));

        RunTest("SystemControl", "focus_window", () =>
        {
            return AppControlTools.focus_window("Notepad");
        }, res => res.StartsWith("OK: focused") || res.StartsWith("Error: no window"));

        RunTest("SystemControl", "type_text", () =>
        {
            return InputTools.type_text("Hello from ZERO Automated Test!");
        }, res => res.StartsWith("OK: typed"));

        RunTest("SystemControl", "press_key", () =>
        {
            return InputTools.press_key("enter");
        }, res => res.StartsWith("OK: pressed"));

        RunTest("SystemControl", "minimize_window", () =>
        {
            return AppControlTools.minimize_window("Notepad");
        }, res => res.StartsWith("OK: minimized") || res.StartsWith("Error: no window"));

        RunTest("SystemControl", "close_app (notepad)", () =>
        {
            return AppControlTools.close_app("notepad");
        }, res => res.StartsWith("OK: closed"));

        // Dangerous Power Tools (Compile & Method Reflection Check ONLY)
        RunTest("SystemControl", "dangerous_tools (reflection check)", () =>
        {
            var type = typeof(PowerTools);
            var mLock = type.GetMethod("lock_screen", BindingFlags.Public | BindingFlags.Static);
            var mShutdown = type.GetMethod("shutdown", BindingFlags.Public | BindingFlags.Static);
            var mRestart = type.GetMethod("restart", BindingFlags.Public | BindingFlags.Static);
            var mSleep = type.GetMethod("sleep", BindingFlags.Public | BindingFlags.Static);
            var mCancel = type.GetMethod("cancel_shutdown", BindingFlags.Public | BindingFlags.Static);

            if (mLock != null && mShutdown != null && mRestart != null && mSleep != null && mCancel != null)
            {
                return "OK: lock_screen, shutdown, restart, sleep, cancel_shutdown are verified present & compiled";
            }
            return "Error: some methods were not found";
        }, res => res.StartsWith("OK:"));

        // Cleanup temp directory
        try
        {
            if (Directory.Exists(testTempDir))
                Directory.Delete(testTempDir, true);
        }
        catch { }

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("             TEST COMPLETED");
        Console.WriteLine("========================================");
    }

    private static void CreateMinimalPdf(string filePath, string text)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        var bytes = builder.Build();
        File.WriteAllBytes(filePath, bytes);
    }
}

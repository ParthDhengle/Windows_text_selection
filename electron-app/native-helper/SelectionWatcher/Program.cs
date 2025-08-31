using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using Newtonsoft.Json;

namespace SelectionWatcher
{
    class Program
    {
        const string PIPE_NAME = "ai_selection_pipe";
        static StreamWriter globalWriter = null;
        static object writerLock = new object();
        static string lastClipboardText = "";
        static DateTime lastCheckTime = DateTime.MinValue;

        // Low-level mouse hook
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelMouseProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("SelectionWatcher starting (mouse hook + clipboard monitoring)...");

            // Start the named-pipe server in background
            Task.Run(() => RunPipeServer());

            // Install global mouse hook
            _hookID = SetHook(_proc);

            Console.WriteLine("Monitoring text selections. Select text and the popup will appear!");
            Console.WriteLine("Press Enter to exit.");

            // Keep the application running
            Application.Run();

            UnhookWindowsHookEx(_hookID);
        }

        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_MOUSE_LL, proc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONUP))
            {
                // Mouse button released - check for text selection after a small delay
                Task.Run(async () =>
                {
                    await Task.Delay(100); // Small delay to let selection complete
                    CheckForTextSelection();
                });
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static void CheckForTextSelection()
        {
            try
            {
                // Prevent too frequent checks
                if (DateTime.Now.Subtract(lastCheckTime).TotalMilliseconds < 300)
                    return;

                lastCheckTime = DateTime.Now;

                // Use SendKeys to simulate Ctrl+C and check clipboard
                string originalClipboard = "";
                try
                {
                    if (Clipboard.ContainsText())
                        originalClipboard = Clipboard.GetText();
                }
                catch { }

                // Simulate Ctrl+C to copy selected text
                SendKeys.SendWait("^c");

                // Wait a bit for clipboard to update
                Thread.Sleep(50);

                if (Clipboard.ContainsText())
                {
                    string currentText = Clipboard.GetText();

                    // Check if we got new text that's different from what was there before
                    if (!string.IsNullOrWhiteSpace(currentText) &&
                        currentText != originalClipboard &&
                        currentText != lastClipboardText &&
                        currentText.Length > 2 &&
                        currentText.Length < 5000)
                    {
                        lastClipboardText = currentText;

                        // Get cursor position for popup placement
                        GetCursorPos(out POINT cursor);

                        var payload = new
                        {
                            type = "selection",
                            text = currentText,
                            rect = new { x = cursor.X, y = cursor.Y, width = 0, height = 0 },
                            process = "detected",
                            timestamp = DateTime.UtcNow.ToString("o")
                        };

                        string json = JsonConvert.SerializeObject(payload);
                        SendJson(json);
                        Console.WriteLine($"Selection detected: '{currentText.Substring(0, Math.Min(50, currentText.Length))}...'");
                    }
                }

                // Restore original clipboard if it was different
                if (originalClipboard != "" && Clipboard.ContainsText() && Clipboard.GetText() != originalClipboard)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        try { Clipboard.SetText(originalClipboard); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Selection check error: " + ex.Message);
            }
        }

        static void SendJson(string json)
        {
            lock (writerLock)
            {
                try
                {
                    if (globalWriter != null)
                    {
                        globalWriter.WriteLine(json);
                        globalWriter.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Pipe write failed: " + ex.Message);
                }
            }
        }

        static void RunPipeServer()
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PIPE_NAME, PipeDirection.Out, 1))
                    {
                        Console.WriteLine("Named pipe server waiting for connection...");
                        server.WaitForConnection();
                        Console.WriteLine("Electron app connected!");

                        using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
                        {
                            lock (writerLock)
                            {
                                globalWriter = writer;
                            }

                            // Keep connection alive
                            while (server.IsConnected)
                            {
                                Thread.Sleep(100);
                            }

                            lock (writerLock)
                            {
                                globalWriter = null;
                            }
                        }

                        Console.WriteLine("Electron app disconnected.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Pipe server error: " + ex.Message);
                }

                Thread.Sleep(1000);
            }
        }
    }
}
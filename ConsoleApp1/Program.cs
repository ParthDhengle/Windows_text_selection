// Program.cs
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Diagnostics;
using Newtonsoft.Json; // Install-Package Newtonsoft.Json

namespace SelectionWatcher
{
    class Program
    {
        // Pipe name (must match in Electron)
        const string PIPE_NAME = "ai_selection_pipe";

        // writer is set when a client connects
        static StreamWriter globalWriter = null;
        static object writerLock = new object();

        [STAThread] // important for UI Automation
        static void Main(string[] args)
        {
            Console.WriteLine("SelectionWatcher starting...");

            // Start the named-pipe server in background
            Task.Run(() => RunPipeServer());

            // Subscribe to TextSelectionChangedEvent for all descendants of desktop
            Automation.AddAutomationEventHandler(
                TextPatternIdentifiers.TextSelectionChangedEvent,
                AutomationElement.RootElement,
                TreeScope.Subtree,
                (sender, e) => OnTextSelectionChanged(sender, e)
            );

            // Also listen for focus changes — some controls only update selection on focus events
            Automation.AddAutomationEventHandler(
                AutomationElement.AutomationFocusChangedEvent,
                AutomationElement.RootElement,
                TreeScope.Subtree,
                (sender, e) => OnFocusChanged(sender, e)
            );

            Console.WriteLine("Listening for selection changes. Press Enter to exit.");
            Console.ReadLine();

            Automation.RemoveAllEventHandlers();
        }

        static void OnFocusChanged(AutomationElement sender, AutomationEventArgs e)
        {
            // optional: when focus changes, try to read any selection from the focused element
            TrySendSelectionFromElement(sender);
        }

        static void OnTextSelectionChanged(AutomationElement sender, AutomationEventArgs e)
        {
            TrySendSelectionFromElement(sender);
        }

        static void TrySendSelectionFromElement(AutomationElement element)
        {
            try
            {
                if (element == null) return;

                // Some controls won't support TextPattern
                object patternObj = null;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out patternObj)) return;

                var textPattern = patternObj as TextPattern;
                if (textPattern == null) return;

                var ranges = textPattern.GetSelection();
                if (ranges == null || ranges.Length == 0) return;

                // We take the first range
                var range = ranges[0];
                string selected = range.GetText(-1);
                if (string.IsNullOrWhiteSpace(selected)) return;

                // Try get bounding rectangles (may be empty)
                double[] rects = range.GetBoundingRectangles(); // array length 0 or multiples of 4
                double x = 0, y = 0, w = 0, h = 0;
                if (rects != null && rects.Length >= 4)
                {
                    // UIA returns rectangles as [l,t,r,b] sequence sometimes or [l,t,w,h] depending on control.
                    // We'll attempt to interpret as left,top,right,bottom if length >= 4.
                    x = rects[0];
                    y = rects[1];
                    // If the next two values look like right,bottom:
                    if (rects[2] >= x && rects[3] >= y)
                    {
                        double right = rects[2], bottom = rects[3];
                        w = Math.Max(1.0, right - x);
                        h = Math.Max(1.0, bottom - y);
                    }
                    else // fallback
                    {
                        w = rects[2];
                        h = rects[3];
                    }
                }

                int pid = element.Current.ProcessId;
                string procName = "unknown";
                try { procName = Process.GetProcessById(pid).ProcessName; } catch { }

                var payload = new
                {
                    type = "selection",
                    text = selected,
                    rect = new { x = x, y = y, width = w, height = h },
                    process = procName,
                    timestamp = DateTime.UtcNow.ToString("o")
                };

                string json = JsonConvert.SerializeObject(payload);

                SendJson(json);
                Console.WriteLine($"Sent selection ({selected.Length} chars) from {procName}");
            }
            catch (Exception ex)
            {
                // swallow errors but print to console for debugging
                Console.WriteLine("Error getting selection: " + ex.Message);
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
                    using (var server = new NamedPipeServerStream(PIPE_NAME, PipeDirection.Out, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
                    {
                        Console.WriteLine("Named pipe server waiting for client...");
                        server.WaitForConnection();
                        Console.WriteLine("Named pipe client connected.");

                        using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
                        {
                            lock (writerLock)
                            {
                                globalWriter = writer;
                            }

                            // Keep pipe open while connected. We don't expect reading from client in this simple example.
                            while (server.IsConnected)
                            {
                                Thread.Sleep(200);
                            }

                            lock (writerLock)
                            {
                                globalWriter = null;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Pipe server error: " + ex.Message);
                }

                // small delay before retrying accept
                Thread.Sleep(500);
            }
        }
    }
}

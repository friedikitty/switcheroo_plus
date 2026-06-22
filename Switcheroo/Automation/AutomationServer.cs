/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 *
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace Switcheroo.Automation
{
    /// <summary>
    /// Simple JSON-RPC style remote control server for automated testing.
    /// Listens on a named pipe and exposes actions that can drive the UI.
    /// </summary>
    public class AutomationServer : IDisposable
    {
        private const string PipeName = "switcheroo_plus_automation";
        private readonly MainWindow _mainWindow;
        private NamedPipeServerStream _pipeServer;
        private CancellationTokenSource _cts;
        private bool _disposed;

        public AutomationServer(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            var thread = new Thread(() => ListenLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "SwitcherooAutomationPipe"
            };
            thread.Start();
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            if (_pipeServer != null)
            {
                try { _pipeServer.Dispose(); }
                catch { }
                _pipeServer = null;
            }
        }

        private void ListenLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream localPipe = null;
                try
                {
                    localPipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    _pipeServer = localPipe;

                    localPipe.WaitForConnection();
                    System.Threading.Tasks.Task.Run(() => HandleClient(localPipe, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    localPipe?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Automation server error: " + ex);
                    try { localPipe?.Dispose(); }
                    catch { }
                }
            }
        }

        private async void HandleClient(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            try
            {
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), true, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                {
                    var requestJson = await reader.ReadLineAsync();
                    var response = new Dictionary<string, object>();

                    if (string.IsNullOrWhiteSpace(requestJson))
                    {
                        response["error"] = "empty request";
                        await WriteResponse(writer, response);
                        return;
                    }

                    Exception dispatchError = null;
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            Dispatch(requestJson, response);
                        }
                        catch (Exception ex)
                        {
                            dispatchError = ex;
                        }
                    });

                    if (dispatchError != null)
                    {
                        response["error"] = dispatchError.Message;
                        response["stack"] = dispatchError.StackTrace;
                    }

                    await WriteResponse(writer, response);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Automation client handler error: " + ex);
            }
            finally
            {
                try { pipe.Dispose(); }
                catch { }
            }
        }

        private void Dispatch(string requestJson, Dictionary<string, object> response)
        {
            var request = JsonHelper.Decode(requestJson);
            string action;
            request.TryGetValue("action", out action);
            action = (action ?? string.Empty).ToLowerInvariant();

            switch (action)
            {
                case "ping":
                    response["ok"] = true;
                    response["visible"] = _mainWindow.Visibility == Visibility.Visible;
                    break;

                case "show":
                    _mainWindow.ShowAndActivate();
                    response["ok"] = true;
                    break;

                case "hide":
                    _mainWindow.HideWindow();
                    response["ok"] = true;
                    break;

                case "setquery":
                    _mainWindow.SetQuery(request.ContainsKey("query") ? request["query"] : string.Empty);
                    response["ok"] = true;
                    break;

                case "getwindows":
                    response["windows"] = _mainWindow.GetWindowList();
                    break;

                case "select":
                    _mainWindow.SelectIndex(request.ContainsKey("index") ? int.Parse(request["index"]) : -1);
                    response["ok"] = true;
                    break;

                case "activate":
                    _mainWindow.ActivateSelectedWindow();
                    response["ok"] = true;
                    break;

                case "previousapp":
                    _mainWindow.TriggerPreviousApp();
                    response["ok"] = true;
                    break;

                case "installprevhook":
                    response["ok"] = _mainWindow.InstallPreviousAppHookForTest();
                    response["active"] = _mainWindow.IsPreviousAppHookActive;
                    break;

                case "installmainhotkey":
                    response["ok"] = _mainWindow.InstallActivationHotKeyForTest();
                    response["active"] = _mainWindow.IsActivationHotKeyActive;
                    break;

                case "resetprevwindow":
                    _mainWindow.ResetPreviousWindow();
                    response["ok"] = true;
                    break;

                case "getforeground":
                    response["foreground"] = _mainWindow.GetForegroundInfo();
                    break;

                case "setforeground":
                    {
                        long hv = 0;
                        string raw = request.ContainsKey("hwnd") ? request["hwnd"] ?? "" : "";
                        long.TryParse(raw.Trim(), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out hv);
                        _mainWindow.SetForegroundByHwnd(hv);
                        response["ok"] = true;
                    }
                    break;

                case "prevstate":
                    response["state"] = _mainWindow.GetPreviousAppState();
                    break;

                case "setconfig":
                    if (request.ContainsKey("config"))
                    {
                        _mainWindow.ApplyConfig(request["config"]);
                    }
                    response["ok"] = true;
                    break;

                case "getconfig":
                    response["config"] = _mainWindow.GetConfigJson();
                    break;

                case "exit":
                    Application.Current.Shutdown();
                    response["ok"] = true;
                    break;

                default:
                    response["error"] = "unknown action: " + action;
                    break;
            }
        }

        private static async System.Threading.Tasks.Task WriteResponse(TextWriter writer, Dictionary<string, object> response)
        {
            await writer.WriteLineAsync(JsonHelper.Encode(response));
            await writer.FlushAsync();
            writer.Dispose();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}

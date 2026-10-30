using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TokenIsland {
    public static class LocalSyncServer {
        private static TcpListener listener;
        private static Thread serverThread;
        private static volatile bool isRunning = false;
        private static Action<string> onDataSynced;

        public static void Start(Action<string> onSynced) {
            if (isRunning) return;
            onDataSynced = onSynced;

            try {
                listener = new TcpListener(IPAddress.Loopback, 19823);
                listener.Start();
                isRunning = true;

                serverThread = new Thread(ListenLoop);
                serverThread.IsBackground = true;
                serverThread.Name = "TokenIsland_LocalSyncServer";
                serverThread.Start();
            } catch (Exception ex) {
                try {
                    string errFile = Path.Combine(Storage.State, "sync-server-err.txt");
                    File.WriteAllText(errFile, ex.ToString());
                } catch { }
            }
        }

        public static void Stop() {
            isRunning = false;
            try {
                if (listener != null) {
                    listener.Stop();
                    listener = null;
                }
            } catch { }
        }

        private static void ListenLoop() {
            while (isRunning) {
                try {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                } catch {
                    if (!isRunning) break;
                }
            }
        }

        private static void HandleClient(object state) {
            TcpClient client = state as TcpClient;
            if (client == null) return;

            try {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) {
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(requestLine)) return;

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string path = parts[1];

                    int contentLength = 0;
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) {
                        int colon = line.IndexOf(':');
                        if (colon > 0) {
                            string headerName = line.Substring(0, colon).Trim().ToLowerInvariant();
                            string headerVal = line.Substring(colon + 1).Trim();
                            if (headerName == "content-length") {
                                int.TryParse(headerVal, out contentLength);
                            }
                        }
                    }

                    // CORS 預檢
                    if (method == "OPTIONS") {
                        SendResponse(stream, 204, "No Content", "application/json", "");
                        return;
                    }

                    if (method == "GET" && (path == "/ping" || path == "/status")) {
                        SendResponse(stream, 200, "OK", "application/json", "{\"status\":\"ok\",\"app\":\"TokenIsland\"}");
                        return;
                    }

                    if (method == "POST" && path.StartsWith("/sync/grok", StringComparison.OrdinalIgnoreCase)) {
                        string body = "";
                        if (contentLength > 0) {
                            char[] buffer = new char[contentLength];
                            int readTotal = 0;
                            while (readTotal < contentLength) {
                                int read = reader.Read(buffer, readTotal, contentLength - readTotal);
                                if (read <= 0) break;
                                readTotal += read;
                            }
                            body = new string(buffer, 0, readTotal);
                        }

                        if (!string.IsNullOrEmpty(body)) {
                            ProcessGrokSync(body);
                        }

                        SendResponse(stream, 200, "OK", "application/json", "{\"status\":\"ok\",\"provider\":\"grok\"}");
                        return;
                    }

                    // 未知請求
                    SendResponse(stream, 404, "Not Found", "application/json", "{\"error\":\"not_found\"}");
                }
            } catch {
            } finally {
                try { client.Close(); } catch { }
            }
        }

        private static void ProcessGrokSync(string json) {
            try {
                object parsed = Json.Parse(json);
                if (parsed == null) return;

                Storage.Write("grok-status.json", parsed);

                if (onDataSynced != null) {
                    onDataSynced("grok");
                }
            } catch { }
        }

        private static void SendResponse(Stream stream, int statusCode, string statusMsg, string contentType, string body) {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            string header = string.Format(
                "HTTP/1.1 {0} {1}\r\n" +
                "Server: TokenIsland-Sync\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type, Authorization, X-Requested-With\r\n" +
                "Content-Type: {2}; charset=utf-8\r\n" +
                "Content-Length: {3}\r\n" +
                "Connection: close\r\n\r\n",
                statusCode, statusMsg, contentType, bodyBytes.Length);

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0) {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }
            stream.Flush();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace ChatServer
{
    public class Client
    {
        public string Id { get; set; }
        public TcpClient TcpClient { get; set; }
        public string Username { get; set; }
        public string CurrentRoom { get; set; }
    }

    public class ChatRoom
    {
        public string Name { get; set; }
        public List<Client> Clients { get; } = new List<Client>();
        public string LogFilePath { get; set; }
    }

    public class ChatLogger
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();

        public ChatLogger()
        {
            _logFilePath = "chatlog.txt";
            if (!File.Exists(_logFilePath))
            {
                File.Create(_logFilePath).Close();
            }
        }

        public void LogMessage(string message)
        {
            lock (_lockObject)
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
        }

        public string[] GetLogs()
        {
            lock (_lockObject)
            {
                if (File.Exists(_logFilePath))
                {
                    return File.ReadAllLines(_logFilePath);
                }
                return new string[0];
            }
        }
    }

    public class Server
    {
        private TcpListener _server;
        private readonly List<Client> _clients = new List<Client>();
        private readonly Dictionary<string, ChatRoom> _rooms = new Dictionary<string, ChatRoom>();
        private readonly object _lock = new object();
        private readonly ChatLogger _logger;
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly string openaiApiUrl = "https://api.openai.com/v1/chat/completions";
      //  private static readonly string openaiApiKey = 
        public Server()
        {
            _logger = new ChatLogger();
            InitializeRooms();
        }

        private void InitializeRooms()
        {
            // Create default rooms
            string[] defaultRooms = { "Room1", "Room2", "Room3" };
            foreach (var roomName in defaultRooms)
            {
                var room = new ChatRoom { Name = roomName };
                _rooms[roomName] = room;
            }
        }

        public async Task StartServer()
        {
            _server = new TcpListener(IPAddress.Any, 5000); // Changed to port 5000
            _server.Start();
            Console.WriteLine("Server started on port 5000...");
            _logger.LogMessage("Server started");

            while (true)
            {
                TcpClient tcpClient = await _server.AcceptTcpClientAsync();
                var client = new Client
                {
                    Id = Guid.NewGuid().ToString(),
                    TcpClient = tcpClient
                };


                lock (_lock)
                {
                    _clients.Add(client);
                }

                _ = Task.Run(() => HandleClientAsync(client)); // Chạy trong Task để không block server
            }

        }

        private async Task HandleClientAsync(Client client)
        {
            try
            {
                NetworkStream stream = client.TcpClient.GetStream();
                byte[] buffer = new byte[8192]; // Changed buffer size
                int bytesRead;

                // First message is the username
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                string username = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                client.Username = username; // Assign username to client

                // Send room list to client
                await SendRoomList(client);

                // Send chat history when joining room
                if (client.CurrentRoom != null && _rooms.TryGetValue(client.CurrentRoom, out ChatRoom currentRoom))
                {
                    await SendChatHistory(client);
                }

                while (true)
                {
                    try
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;

                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        if (message.StartsWith("@JOIN:"))
                        {
                            string roomName = message.Substring(6);
                            await JoinRoom(client, roomName);
                        }
                        else if (message.StartsWith("@LEAVE:"))
                        {
                            await LeaveRoom(client);
                        }
                        else if (message.StartsWith("@HISTORY:"))
                        {
                            await SendChatHistory(client);
                        }
                        else
                        {
                            // Check message with AI
                            var aiResponse = await CheckMessageWithAI(message);
                            if (aiResponse == "VIOLATION")
                            {
                                await SendMessageToClient(client, "@WARNING: Tin nhắn này có nội dung không phù hợp.");
                                _logger.LogMessage($"{client.Username}: {message} (VIOLATION)");
                            }
                            else
                            {
                                await BroadcastToRoom(client, message);
                            }
                        }
                    }
                    catch (IOException) // Handle stream read exceptions
                    {
                        Console.WriteLine("Client disconnected unexpectedly.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Error: {ex.Message}");
            }
            finally
            {
                await LeaveRoom(client);
                lock (_lock)
                {
                    _clients.Remove(client);
                }
                client.TcpClient.Close();
            }
        }

        private async Task SendChatHistory(Client client)
        {
            var history = _logger.GetLogs();
            foreach (var message in history.TakeLast(50)) // Send last 50 messages
            {
                byte[] buffer = Encoding.UTF8.GetBytes($"@HISTORY:{message}");
                try
                {
                    await client.TcpClient.GetStream().WriteAsync(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    Console.WriteLine("Client disconnected unexpectedly.");
                    break;
                }
                await Task.Delay(50); // Small delay to prevent flooding
            }
        }

        private async Task JoinRoom(Client client, string roomName)
        {
            lock (_lock)
            {
                if (_rooms.TryGetValue(roomName, out ChatRoom room))
                {
                    // Leave current room if any
                    if (client.CurrentRoom != null && _rooms.TryGetValue(client.CurrentRoom, out ChatRoom currentRoom))
                    {
                        currentRoom.Clients.Remove(client);
                        _logger.LogMessage($"{client.Username} has left the room {currentRoom.Name}");
                    }

                    // Join new room
                    room.Clients.Add(client);
                    client.CurrentRoom = roomName;
                    _logger.LogMessage($"{client.Username} has joined the room {roomName}");
                }
            }

            string joinMessage = $"{client.Username} has joined the room.";
            await BroadcastToRoom(client, joinMessage);
            await SendRoomUserList(client.CurrentRoom);

            // Send chat history to new user
            await SendChatHistory(client);
        }

        private async Task LeaveRoom(Client client)
        {
            if (client.CurrentRoom != null)
            {
                lock (_lock)
                {
                    if (_rooms.TryGetValue(client.CurrentRoom, out ChatRoom room))
                    {
                        room.Clients.Remove(client);
                        _logger.LogMessage($"{client.Username} has left the room {room.Name}");
                    }
                }

                string leaveMessage = $"{client.Username} has left the room.";
                await BroadcastToRoom(client, leaveMessage);
                client.CurrentRoom = null;
                await SendRoomUserList(client.CurrentRoom);
            }
        }

        private async Task SendRoomList(Client client)
        {
            string roomList = "@ROOMLIST:" + string.Join(",", _rooms.Keys);
            byte[] buffer = Encoding.UTF8.GetBytes(roomList);
            try
            {
                await client.TcpClient.GetStream().WriteAsync(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                Console.WriteLine("Client disconnected unexpectedly.");
            }
        }

        private async Task SendRoomUserList(string roomName)
        {
            if (roomName == null) return;

            lock (_lock)
            {
                if (_rooms.TryGetValue(roomName, out ChatRoom room))
                {
                    string userList = "@USERLIST:" + string.Join(",", room.Clients.Select(c => c.Username));
                    byte[] buffer = Encoding.UTF8.GetBytes(userList);

                    foreach (var client in room.Clients)
                    {
                        try
                        {
                            client.TcpClient.GetStream().WriteAsync(buffer, 0, buffer.Length).Wait();
                        }
                        catch (IOException)
                        {
                            Console.WriteLine("Client disconnected unexpectedly.");
                            break;
                        }
                    }
                }
            }
        }

        private async Task BroadcastToRoom(Client sender, string message)
        {
            if (sender.CurrentRoom == null) return;

            string fullMessage = $"{sender.Username}: {message}";
            byte[] buffer = Encoding.UTF8.GetBytes(fullMessage);

            lock (_lock)
            {
                if (_rooms.TryGetValue(sender.CurrentRoom, out ChatRoom room))
                {
                    // Log the message
                    _logger.LogMessage(fullMessage);

                    // Broadcast to all clients in the room
                    foreach (var client in room.Clients)
                    {
                        try
                        {
                            client.TcpClient.GetStream().WriteAsync(buffer, 0, buffer.Length).Wait();
                        }
                        catch (IOException)
                        {
                            Console.WriteLine("Client disconnected unexpectedly.");
                            break;
                        }
                    }
                }
            }
        }

        private async Task<string> CheckMessageWithAI(string message)
        {
            try
            {
                var requestData = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                        new
                        {
                            role = "system",
                            content = "Bạn là AI kiểm duyệt nội dung. Nếu tin nhắn có nội dung xúc phạm, hãy trả lời 'VIOLATION'. Nếu không, trả lời 'OK'."
                        },
                        new { role = "user", content = message }
                    }
                };

                var json = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

              //  httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openaiApiKey);

                var response = await httpClient.PostAsync(openaiApiUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"OpenAI API Error: {response.StatusCode} - {error}");
                    return "OK"; // Hoặc xử lý lỗi theo cách phù hợp
                }

                var responseString = await response.Content.ReadAsStringAsync();
                dynamic responseObject = JsonConvert.DeserializeObject(responseString);

                return responseObject.choices[0].message.content.ToString().Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling OpenAI API: {ex.Message}");
                return "OK"; // Hoặc xử lý lỗi theo cách phù hợp
            }
        }

        private async Task SendMessageToClient(Client client, string message)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(message);
            try
            {
                await client.TcpClient.GetStream().WriteAsync(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                Console.WriteLine("Client disconnected unexpectedly.");
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            var server = new Server();
            await server.StartServer();
        }
    }
}
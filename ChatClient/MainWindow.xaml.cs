using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private TcpClient client;
        private NetworkStream stream;
        private bool isRunning = false;
        private string logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chatlog.txt");

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show("Please enter a username.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                client = new TcpClient("127.0.0.1", 5000);
                stream = client.GetStream();

                byte[] usernameBytes = Encoding.UTF8.GetBytes(username);
                stream.Write(usernameBytes, 0, usernameBytes.Length);

                isRunning = true;
                Thread receiveThread = new Thread(ReceiveMessages);
                receiveThread.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error connecting to server: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void ReceiveMessages()
        {
            byte[] buffer = new byte[8192];

            try
            {
                while (isRunning)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Bỏ qua nếu tin nhắn chứa @ROOMLIST
                    if (message.StartsWith("@ROOMLIST")) continue;

                    Dispatcher.Invoke(() =>
                    {
                        ChatTextBox.AppendText(message + "\n");
                        ChatTextBox.ScrollToEnd();
                    });
                }
            }
            catch (IOException)
            {
                Dispatcher.Invoke(() => ChatTextBox.AppendText("Disconnected from server.\n"));
            }
            finally
            {
                isRunning = false;
            }
        }




        private void MessageTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                SendMessage();
            }
        }

        private void SendMessage()
        {
            if (!isRunning || client == null) return;

            string message = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;

            byte[] buffer = Encoding.UTF8.GetBytes(message);
            stream.Write(buffer, 0, buffer.Length);

            // Hiển thị tin nhắn trong ChatTextBox
            ChatTextBox.AppendText($"Me: {message}\n");
            ChatTextBox.ScrollToEnd();

            MessageTextBox.Clear();
        }




            private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (client == null || !client.Connected)
            {
                MessageBox.Show("You are not connected to the server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string message = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                // Gửi tin nhắn dưới dạng "username: message"
                string formattedMessage = $"{UsernameTextBox.Text}: {message}\n";
                byte[] data = Encoding.UTF8.GetBytes(formattedMessage);
                stream.Write(data, 0, data.Length);

                // Hiển thị tin nhắn đã gửi trên chính client
                ChatTextBox.AppendText($"Me: {message}\n");
                ChatTextBox.ScrollToEnd();

                MessageTextBox.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to send message: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }

}

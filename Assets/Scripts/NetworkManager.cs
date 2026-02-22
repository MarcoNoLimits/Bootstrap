using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace WizardOfOz
{
    public class NetworkManager
    {
        private string _ipAddress;
        private int _port = 8080;
        private TcpClient _client;

        public NetworkManager(string ipAddress)
        {
            _ipAddress = ipAddress;
        }

        public async void SendTranslationRequest(string text, Action<string> onComplete)
        {
            try
            {
                Debug.Log($"[NetworkManager] Connecting to {_ipAddress}:{_port}...");
                
                using (_client = new TcpClient())
                {
                    // For UWP/HoloLens, Ensure "Private Networks" and "Internet (Client)" capabilities are enabled
                    await _client.ConnectAsync(_ipAddress, _port);
                    
                    if (!_client.Connected)
                    {
                        Debug.LogError("[NetworkManager] Failed to connect.");
                        onComplete?.Invoke("Error: Connection Failed");
                        return;
                    }

                    NetworkStream stream = _client.GetStream();
                    
                    // Send English Text
                    byte[] dataToSend = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(dataToSend, 0, dataToSend.Length);
                    Debug.Log($"[NetworkManager] Sent: {text}");

                    // Receive Turkish Translation
                    byte[] buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Debug.Log($"[NetworkManager] Received: {response}");

                    onComplete?.Invoke(response);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetworkManager] Exception: {e.Message}");
                onComplete?.Invoke($"Error: {e.Message}");
            }
        }
    }
}

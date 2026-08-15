using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class BridgeProtocolClientTests
    {
        [Test, Timeout(5000)]
        public void FirstHelpRequest_CompletesAfterConnecting()
        {
            JObject request = SendSingleRequest(
                new JObject { ["status"] = "done", ["server_version"] = "test" },
                (client, port) => client.GetHelpAsync("127.0.0.1", port, CancellationToken.None));

            Assert.That(request.Value<string>("cmd"), Is.EqualTo("help"));
        }

        [Test, Timeout(5000)]
        public void GenerateRequest_DoesNotSendLegacyOwnerPid()
        {
            JObject request = SendSingleRequest(
                new JObject { ["status"] = "done" },
                (client, port) => client.GenerateAsync(
                    "127.0.0.1",
                    port,
                    new KimodoGenerationRequestDto
                    {
                        prompt = "walk",
                        duration = 1f,
                        steps = 1,
                        model = KimodoMotionModelProfiles.DefaultModelName
                    },
                    null,
                    CancellationToken.None));

            Assert.That(request.Value<string>("cmd"), Is.EqualTo("generate"));
            Assert.That(request["owner_pid"], Is.Null);
        }

        private static JObject SendSingleRequest(
            JObject response,
            System.Func<BridgeProtocolClient, int, Task<BridgeProtocolResponse>> send)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<JObject> requestTask = ReadAndReplyAsync(listener, response);
            try
            {
                using (var client = new BridgeProtocolClient(connectTimeoutMs: 1000, ioTimeoutMs: 1000))
                {
                    BridgeProtocolResponse result = send(client, port).GetAwaiter().GetResult();
                    Assert.That(result?.Header?.Value<string>("status"), Is.EqualTo("done"));
                }
                return requestTask.GetAwaiter().GetResult();
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task<JObject> ReadAndReplyAsync(TcpListener listener, JObject response)
        {
            using (TcpClient connection = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
            using (NetworkStream stream = connection.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
            {
                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                JObject request = JObject.Parse(line ?? string.Empty);
                response["request_id"] = request.Value<string>("request_id") ?? string.Empty;
                await writer.WriteAsync(response.ToString(Formatting.None) + "\n").ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                return request;
            }
        }
    }
}

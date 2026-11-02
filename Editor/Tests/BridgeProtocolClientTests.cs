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

        [Test, Timeout(5000)]
        public void GenerateRequest_CapturesExactSerializedPayload()
        {
            string capturedPayload = null;
            KimodoBridgeService.GenerationRequestPayloadContext.Value = payload => capturedPayload = payload;
            try
            {
                JObject request = SendSingleRequest(
                    new JObject { ["status"] = "done" },
                    (client, port) => client.GenerateAsync(
                        "127.0.0.1",
                        port,
                        new KimodoGenerationRequestDto
                        {
                            prompt = "idle",
                            duration = 1f,
                            steps = 1,
                            model = KimodoMotionModelProfiles.DefaultModelName,
                            output_mp4 = true
                        },
                        null,
                        CancellationToken.None));

                Assert.That(capturedPayload, Is.Not.Null.And.Not.Empty);
                JObject payload = JObject.Parse(capturedPayload);
                Assert.That(payload["cmd"]?.Value<string>(), Is.EqualTo("generate"));
                Assert.That(payload["task_id"]?.Value<string>(), Is.EqualTo(request["task_id"]?.Value<string>()));
                Assert.That(payload["prompt"]?.Value<string>(), Is.EqualTo("idle"));
                Assert.That(payload["output_mp4"]?.Value<bool>(), Is.True);
            }
            finally
            {
                KimodoBridgeService.GenerationRequestPayloadContext.Value = null;
            }
        }

        [Test, Timeout(5000)]
        public void GenerateRequest_ReplaysRawInstructionWithFreshTaskId()
        {
            JObject request = SendSingleRequest(
                new JObject { ["status"] = "done", ["output_format"] = "kmb_v1" },
                (client, port) => client.GenerateAsync(
                    "127.0.0.1",
                    port,
                    new KimodoGenerationRequestDto
                    {
                        task_id = "current-command-task",
                        prompt = "ignored by replay",
                        duration = 9f,
                        steps = 99,
                        model = KimodoMotionModelProfiles.DefaultModelName,
                        raw_generate_instruction_json = "{\"cmd\":\"generate\",\"task_id\":\"old-task\",\"prompt\":\"captured walk\",\"duration\":2.0,\"diffusion_steps\":20,\"output_format\":\"kmb_v1\"}"
                    },
                    null,
                    CancellationToken.None));

            Assert.That(request.Value<string>("cmd"), Is.EqualTo("generate"));
            Assert.That(request.Value<string>("task_id"), Is.EqualTo("current-command-task"));
            Assert.That(request.Value<string>("task_id"), Is.Not.EqualTo("old-task"));
            Assert.That(request.Value<string>("prompt"), Is.EqualTo("captured walk"));
            Assert.That(request.Value<float?>("duration"), Is.EqualTo(2f));
            Assert.That(request.Value<int?>("diffusion_steps"), Is.EqualTo(20));
        }

        [Test, Timeout(5000)]
        public void GenerateRequest_ReadsMotionPayloadAndMp4PathFromJsonHeader()
        {
            byte[] expectedPayload = { 1, 2, 3 };
            byte[] actualPayload = null;
            string actualMp4Path = null;
            JObject request = SendSingleRequest(
                new JObject
                {
                    ["status"] = "done",
                    ["output_format"] = "kmb_v1",
                    ["mp4_path"] = "C:/quickserver/generated_animation_test.mp4"
                },
                (client, port) => client.GenerateAsync(
                    "127.0.0.1",
                    port,
                    new KimodoGenerationRequestDto
                    {
                        prompt = "walk",
                        duration = 1f,
                        steps = 1,
                        model = KimodoMotionModelProfiles.DefaultModelName,
                        output_mp4 = true
                    },
                    null,
                    CancellationToken.None),
                expectedPayload,
                result =>
                {
                    actualPayload = result.BinaryPayload;
                    actualMp4Path = result.Header.Value<string>("mp4_path");
                });

            Assert.That(request.Value<string>("cmd"), Is.EqualTo("generate"));
            Assert.That(actualPayload, Is.EqualTo(expectedPayload));
            Assert.That(actualMp4Path, Is.EqualTo("C:/quickserver/generated_animation_test.mp4"));
        }

        private static JObject SendSingleRequest(
            JObject response,
            System.Func<BridgeProtocolClient, int, Task<BridgeProtocolResponse>> send,
            byte[] binaryPayload = null,
            System.Action<BridgeProtocolResponse> inspectResponse = null)
        {
            if (binaryPayload != null)
            {
                response["byte_length"] = binaryPayload.Length;
            }
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<JObject> requestTask = ReadAndReplyAsync(listener, response, binaryPayload);
            try
            {
                using (var client = new BridgeProtocolClient(connectTimeoutMs: 1000, ioTimeoutMs: 1000))
                {
                    BridgeProtocolResponse result = send(client, port).GetAwaiter().GetResult();
                    Assert.That(result?.Header?.Value<string>("status"), Is.EqualTo("done"));
                    inspectResponse?.Invoke(result);
                }
                return requestTask.GetAwaiter().GetResult();
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task<JObject> ReadAndReplyAsync(
            TcpListener listener,
            JObject response,
            byte[] binaryPayload = null)
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
                if (binaryPayload != null && binaryPayload.Length > 0)
                {
                    await stream.WriteAsync(binaryPayload, 0, binaryPayload.Length).ConfigureAwait(false);
                }
                return request;
            }
        }
    }
}

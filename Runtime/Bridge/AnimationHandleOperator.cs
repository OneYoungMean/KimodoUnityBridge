using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public sealed class AnimationHandleInfo
    {
        public string Handle { get; internal set; } = string.Empty;
        public string Format { get; internal set; } = string.Empty;
        public string Description { get; internal set; } = string.Empty;
        public int ByteLength { get; internal set; }
        public int FrameCount { get; internal set; }
        public double DurationSeconds { get; internal set; }
        public float Fps { get; internal set; }
        public string CreatedUtc { get; internal set; } = string.Empty;
        public string ModelName { get; internal set; } = string.Empty;
        public string SkeletonId { get; internal set; } = string.Empty;
        public int JointCount { get; internal set; }
        public IReadOnlyList<string> JointNames { get; internal set; } = Array.Empty<string>();
        public string MotionRepFingerprint { get; internal set; } = string.Empty;
        public string ServerInstanceId { get; internal set; } = string.Empty;
        public string Sha256 { get; internal set; } = string.Empty;
        public bool IsStream { get; internal set; }
        public string TaskId { get; internal set; } = string.Empty;
        public string SessionId { get; internal set; } = string.Empty;
        public int CapacityFrames { get; internal set; }
        public int HorizonFrames { get; internal set; }
        public bool IsClosed { get; internal set; }

        internal static AnimationHandleInfo FromJson(JObject value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Value<string>("handle")))
            {
                throw new InvalidOperationException("Bridge response is missing handle_info.handle.");
            }

            return new AnimationHandleInfo
            {
                Handle = value.Value<string>("handle") ?? string.Empty,
                Format = value.Value<string>("format") ?? string.Empty,
                Description = value.Value<string>("description") ?? string.Empty,
                ByteLength = value.Value<int?>("byte_length") ?? 0,
                FrameCount = value.Value<int?>("num_frames") ?? 0,
                DurationSeconds = value.Value<double?>("duration_seconds") ?? 0.0,
                Fps = value.Value<float?>("fps") ?? 0f,
                CreatedUtc = value.Value<string>("created_utc") ?? string.Empty,
                ModelName = value.Value<string>("model_name") ?? string.Empty,
                SkeletonId = value.Value<string>("skeleton_id") ?? string.Empty,
                JointCount = value.Value<int?>("joint_count") ?? 0,
                JointNames = value["joint_names"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                MotionRepFingerprint = value.Value<string>("motion_rep_fingerprint") ?? string.Empty,
                ServerInstanceId = value.Value<string>("server_instance_id") ?? string.Empty,
                Sha256 = value.Value<string>("sha256") ?? string.Empty,
                IsStream = value.Value<bool?>("is_stream") ?? false,
                TaskId = value.Value<string>("task_id") ?? string.Empty,
                SessionId = value.Value<string>("session_id") ?? string.Empty,
                CapacityFrames = value.Value<int?>("capacity_frames") ?? 0,
                HorizonFrames = value.Value<int?>("horizon_frames") ?? 0,
                IsClosed = value.Value<bool?>("closed") ?? false
            };
        }
    }

    public sealed class AnimationHandleOperator : IDisposable
    {
        private int releaseStarted;
        private readonly KimodoBridgeService owner;

        internal AnimationHandleOperator(KimodoBridgeService owner, AnimationHandleInfo info)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }

        public AnimationHandleInfo Info { get; }
        public bool IsReleased => Volatile.Read(ref releaseStarted) != 0;

        public Task<byte[]> DownloadAsync(CancellationToken token = default)
        {
            if (IsReleased)
            {
                throw new ObjectDisposedException(nameof(AnimationHandleOperator));
            }
            return owner.DownloadAnimationAsync(Info.Handle, Info.ServerInstanceId, token);
        }

        public Task<bool> ReleaseAsync(CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref releaseStarted, 1) != 0)
            {
                return Task.FromResult(false);
            }
            _ = token;
            return Task.FromResult(Info.IsStream
                ? owner.QueueCancelTask(Info.TaskId)
                : owner.QueueReleaseAnimation(Info.Handle, Info.ServerInstanceId));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref releaseStarted, 1) == 0)
            {
                if (Info.IsStream)
                {
                    owner.QueueCancelTask(Info.TaskId);
                }
                else
                {
                    owner.QueueReleaseAnimation(Info.Handle, Info.ServerInstanceId);
                }
            }
        }
    }
}

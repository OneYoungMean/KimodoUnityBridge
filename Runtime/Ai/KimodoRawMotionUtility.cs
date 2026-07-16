using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using Google.FlatBuffers;

namespace KimodoBridge
{
    public sealed class KimodoRawMotionData
    {
        internal readonly string[] jointNames;
        internal readonly int[] jointParents;
        internal readonly Vector3[] rootPositions;
        internal readonly List<float> localRotQuats;
        internal readonly int rootJointIndex;
        internal readonly byte[] footContacts;

        internal KimodoRawMotionData(
            int frameCount,
            int jointCount,
            float frameRate,
            string[] jointNames,
            int[] jointParents,
            Vector3[] rootPositions,
            List<float> localRotQuats,
            int rootJointIndex,
            byte[] footContacts = null)
        {
            FrameCount = frameCount;
            JointCount = jointCount;
            FrameRate = frameRate > 0f ? frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
            this.jointNames = jointNames ?? Array.Empty<string>();
            this.jointParents = jointParents ?? Array.Empty<int>();
            this.rootPositions = rootPositions ?? Array.Empty<Vector3>();
            this.localRotQuats = localRotQuats;
            this.rootJointIndex = Mathf.Clamp(rootJointIndex, 0, Mathf.Max(0, jointCount - 1));
            this.footContacts = footContacts != null && footContacts.Length == frameCount * KimodoFootContactTrackUtility.ChannelCount
                ? footContacts
                : Array.Empty<byte>();
        }

        public int FrameCount { get; }
        public int JointCount { get; }
        public float FrameRate { get; }
        public float DurationSeconds => FrameCount > 0 ? FrameCount / FrameRate : 0f;
        public float LastFrameTimeSeconds => FrameCount > 1 ? (FrameCount - 1) / FrameRate : 0f;
        public int RootJointIndex => rootJointIndex;
        public IReadOnlyList<string> JointNames => jointNames;
        public bool HasFootContacts => footContacts.Length == FrameCount * KimodoFootContactTrackUtility.ChannelCount;

        internal bool TryReadUnityRootPosition(int frameIndex, out Vector3 value)
        {
            value = default;
            if (rootPositions == null ||
                frameIndex < 0 ||
                frameIndex >= rootPositions.Length)
            {
                return false;
            }

            value = rootPositions[frameIndex];
            return true;
        }

        internal bool TryReadUnityLocalRotation(int frameIndex, int jointIndex, int rotationJointCount, out Quaternion value)
        {
            value = Quaternion.identity;
            if (localRotQuats == null ||
                frameIndex < 0 ||
                frameIndex >= FrameCount ||
                jointIndex < 0 ||
                jointIndex >= rotationJointCount)
            {
                return false;
            }

            int baseIndex = (frameIndex * rotationJointCount + jointIndex) * 4;
            if (baseIndex < 0 || baseIndex + 3 >= localRotQuats.Count)
            {
                return false;
            }

            float w = localRotQuats[baseIndex + 0];
            float x = localRotQuats[baseIndex + 1];
            float y = localRotQuats[baseIndex + 2];
            float z = localRotQuats[baseIndex + 3];
            Quaternion source = new Quaternion(x, y, z, w).normalized;
            value = new Quaternion(source.x, -source.y, -source.z, source.w);
            return true;
        }

        internal bool TryReadFootContact(int frameIndex, int channel, out float value)
        {
            value = 0f;
            if (!HasFootContacts ||
                frameIndex < 0 || frameIndex >= FrameCount ||
                channel < 0 || channel >= KimodoFootContactTrackUtility.ChannelCount)
            {
                return false;
            }

            value = footContacts[frameIndex * KimodoFootContactTrackUtility.ChannelCount + channel] > 0 ? 1f : 0f;
            return true;
        }
    }

    public sealed class KimodoRawMotionPlaybackBinding
    {
        internal readonly KimodoRawMotionData motion;
        internal readonly Transform[] joints;
        internal readonly int[] motionJointIndices;

        internal KimodoRawMotionPlaybackBinding(
            KimodoRawMotionData motion,
            Transform[] joints,
            int[] motionJointIndices)
        {
            this.motion = motion;
            this.joints = joints ?? Array.Empty<Transform>();
            this.motionJointIndices = motionJointIndices ?? Array.Empty<int>();
        }

        public KimodoRawMotionData Motion => motion;
        public int JointCount => joints.Length;
        public float DurationSeconds => motion != null ? motion.DurationSeconds : 0f;
    }

    public sealed class KimodoRawMotionMetadata
    {
        internal KimodoRawMotionMetadata(
            KimodoRawMotionData motion,
            Vector3 firstRootPosition,
            Vector3 lastRootPosition,
            KimodoMarkerSampleResult tailPose)
        {
            Motion = motion;
            FirstRootPosition = firstRootPosition;
            LastRootPosition = lastRootPosition;
            TailPose = tailPose;
        }

        public KimodoRawMotionData Motion { get; }
        public Vector3 FirstRootPosition { get; }
        public Vector3 LastRootPosition { get; }
        public KimodoMarkerSampleResult TailPose { get; }
    }

    public static class KimodoRawMotionUtility
    {
        private const string FullBodyConstraintType = "fullbody";

        [Serializable]
        private sealed class MotionJsonData
        {
            public int num_frames;
            public int num_joints;
            public int fps;
            public string[] joint_names;
            public int[] joint_parents;
            public List<float> joints;
            public List<float> local_rot_quats;
            public List<float> foot_contacts;
        }

        public static bool TryParse(string motionJson, out KimodoRawMotionData motion, out string error)
        {
            motion = null;
            error = string.Empty;

            MotionJsonData data;
            try
            {
                data = ParseMotionJsonFlexible(motionJson);
            }
            catch (Exception ex)
            {
                error = $"Failed to parse motion json: {ex.Message}";
                return false;
            }

            if (!ValidateData(data, out error))
            {
                return false;
            }

            int frameCount = data.num_frames;
            int jointCount = Mathf.Min(data.joint_names.Length, data.num_joints);
            int rotationJointCount = ResolveRotationJointCount(data, frameCount, jointCount);
            jointCount = Mathf.Min(jointCount, rotationJointCount > 0 ? Mathf.Max(jointCount, rotationJointCount) : jointCount);
            int rootJoint = FindRootJointIndex(data, jointCount);
            if (!TryBuildRootPositions(data.joints, frameCount, jointCount, rootJoint, out Vector3[] rootPositions, out error))
            {
                motion = null;
                return false;
            }

            motion = new KimodoRawMotionData(
                frameCount,
                jointCount,
                data.fps > 0 ? data.fps : KimodoPlayableClip.FIXED_FRAME_RATE,
                data.joint_names,
                data.joint_parents,
                rootPositions,
                data.local_rot_quats,
                rootJoint,
                TryBuildFootContacts(data.foot_contacts, frameCount, out byte[] footContacts, out error)
                    ? footContacts
                    : null);
            if (!string.IsNullOrWhiteSpace(error))
            {
                motion = null;
                return false;
            }
            return true;
        }

        public static string ToCompactJson(KimodoRawMotionData motion)
        {
            if (motion == null)
            {
                return string.Empty;
            }

            int frameCount = Mathf.Max(0, motion.FrameCount);
            int jointCount = Mathf.Max(0, motion.JointCount);
            if (frameCount <= 0 || jointCount <= 0)
            {
                return string.Empty;
            }

            var joints = new List<float>(frameCount * jointCount * 3);
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                motion.TryReadUnityRootPosition(frameIndex, out Vector3 unityRootPosition);
                float srcX = -unityRootPosition.x;
                float srcY = unityRootPosition.y;
                float srcZ = unityRootPosition.z;

                for (int jointIndex = 0; jointIndex < jointCount; jointIndex++)
                {
                    if (jointIndex == motion.rootJointIndex)
                    {
                        joints.Add(srcX);
                        joints.Add(srcY);
                        joints.Add(srcZ);
                    }
                    else
                    {
                        joints.Add(0f);
                        joints.Add(0f);
                        joints.Add(0f);
                    }
                }
            }

            var payload = new MotionJsonData
            {
                num_frames = frameCount,
                num_joints = jointCount,
                fps = Mathf.RoundToInt(motion.FrameRate > 0f ? motion.FrameRate : KimodoPlayableClip.FIXED_FRAME_RATE),
                joint_names = (string[])motion.jointNames.Clone(),
                joint_parents = (int[])motion.jointParents.Clone(),
                joints = joints,
                local_rot_quats = motion.localRotQuats != null ? new List<float>(motion.localRotQuats) : new List<float>(),
                foot_contacts = ToFloatFootContacts(motion)
            };
            return JsonUtility.ToJson(payload);
        }

        public static byte[] ToFlatBuffer(KimodoRawMotionData motion, string modelName)
        {
            if (motion == null || motion.FrameCount <= 0 || motion.JointCount <= 0)
            {
                return Array.Empty<byte>();
            }

            int frameCount = motion.FrameCount;
            int jointCount = motion.JointCount;
            float[] roots = new float[frameCount * 3];
            for (int frame = 0; frame < frameCount; frame++)
            {
                motion.TryReadUnityRootPosition(frame, out Vector3 root);
                int index = frame * 3;
                roots[index + 0] = -root.x;
                roots[index + 1] = root.y;
                roots[index + 2] = root.z;
            }

            float[] rotations = motion.localRotQuats != null
                ? motion.localRotQuats.ToArray()
                : Array.Empty<float>();
            byte[] contacts = motion.HasFootContacts ? motion.footContacts : Array.Empty<byte>();
            var builder = new FlatBufferBuilder(Mathf.Max(1024, roots.Length * 4 + rotations.Length * 4 + contacts.Length + 512));
            var nameOffsets = new StringOffset[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                string jointName = i < motion.jointNames.Length ? motion.jointNames[i] : $"joint_{i}";
                nameOffsets[i] = builder.CreateString(jointName ?? string.Empty);
            }

            VectorOffset namesOffset = MotionPacket.CreateJointNamesVector(builder, nameOffsets);
            VectorOffset parentsOffset = MotionPacket.CreateJointParentsVector(builder, motion.jointParents);
            VectorOffset rootsOffset = MotionPacket.CreateRootPositionsVector(builder, roots);
            VectorOffset rotationsOffset = MotionPacket.CreateLocalRotQuatsVector(builder, rotations);
            StringOffset modelOffset = builder.CreateString(modelName ?? string.Empty);
            VectorOffset contactsOffset = contacts.Length > 0
                ? MotionPacket.CreateFootContactsVector(builder, contacts)
                : default;
            Offset<MotionPacket> packet = MotionPacket.CreateMotionPacket(
                builder,
                version: 1,
                fps: motion.FrameRate,
                num_frames: (uint)frameCount,
                num_joints: (uint)jointCount,
                joint_namesOffset: namesOffset,
                joint_parentsOffset: parentsOffset,
                root_positionsOffset: rootsOffset,
                local_rot_quatsOffset: rotationsOffset,
                model_nameOffset: modelOffset,
                foot_contactsOffset: contactsOffset);
            MotionPacket.FinishMotionPacketBuffer(builder, packet);
            return builder.SizedByteArray();
        }

        public static bool TryConcatenate(
            IReadOnlyList<KimodoRawMotionData> motions,
            int targetFrameCount,
            out KimodoRawMotionData combined,
            out string error)
        {
            combined = null;
            error = string.Empty;
            if (motions == null || motions.Count == 0 || motions[0] == null)
            {
                error = "Motion list is empty.";
                return false;
            }

            KimodoRawMotionData first = motions[0];
            int availableFrames = 0;
            for (int i = 0; i < motions.Count; i++)
            {
                KimodoRawMotionData motion = motions[i];
                if (motion == null ||
                    motion.JointCount != first.JointCount ||
                    Mathf.Abs(motion.FrameRate - first.FrameRate) > 1e-4f ||
                    !SameRig(first, motion))
                {
                    error = $"Motion {i} has incompatible FPS or rig metadata.";
                    return false;
                }
                availableFrames += motion.FrameCount;
            }

            int frameCount = Mathf.Clamp(targetFrameCount, 1, availableFrames);
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * first.JointCount * 4);
            bool keepFootContacts = true;
            var contacts = new byte[frameCount * KimodoFootContactTrackUtility.ChannelCount];
            int written = 0;
            for (int i = 0; i < motions.Count && written < frameCount; i++)
            {
                KimodoRawMotionData motion = motions[i];
                int copyFrames = Mathf.Min(motion.FrameCount, frameCount - written);
                Array.Copy(motion.rootPositions, 0, roots, written, copyFrames);
                int scalarCount = copyFrames * first.JointCount * 4;
                for (int scalar = 0; scalar < scalarCount; scalar++)
                {
                    rotations.Add(motion.localRotQuats[scalar]);
                }
                if (motion.HasFootContacts)
                {
                    Array.Copy(
                        motion.footContacts,
                        0,
                        contacts,
                        written * KimodoFootContactTrackUtility.ChannelCount,
                        copyFrames * KimodoFootContactTrackUtility.ChannelCount);
                }
                else
                {
                    keepFootContacts = false;
                }
                written += copyFrames;
            }

            combined = new KimodoRawMotionData(
                frameCount,
                first.JointCount,
                first.FrameRate,
                (string[])first.jointNames.Clone(),
                (int[])first.jointParents.Clone(),
                roots,
                rotations,
                first.rootJointIndex,
                keepFootContacts ? contacts : null);
            return true;
        }

        public static bool TryResample(
            KimodoRawMotionData source,
            float targetFrameRate,
            int targetFrameCount,
            out KimodoRawMotionData resampled,
            out string error)
        {
            resampled = null;
            error = string.Empty;
            if (source == null || source.FrameCount <= 0 || targetFrameRate <= 0f || targetFrameCount <= 0)
            {
                error = "Source motion or target sampling settings are invalid.";
                return false;
            }

            var roots = new Vector3[targetFrameCount];
            var rotations = new List<float>(targetFrameCount * source.JointCount * 4);
            byte[] contacts = source.HasFootContacts
                ? new byte[targetFrameCount * KimodoFootContactTrackUtility.ChannelCount]
                : null;
            for (int frame = 0; frame < targetFrameCount; frame++)
            {
                float sourceFrame = frame * source.FrameRate / targetFrameRate;
                int frame0 = Mathf.Clamp(Mathf.FloorToInt(sourceFrame), 0, source.FrameCount - 1);
                int frame1 = Mathf.Min(frame0 + 1, source.FrameCount - 1);
                float blend = Mathf.Clamp01(sourceFrame - frame0);
                source.TryReadUnityRootPosition(frame0, out Vector3 root0);
                source.TryReadUnityRootPosition(frame1, out Vector3 root1);
                roots[frame] = Vector3.LerpUnclamped(root0, root1, blend);

                for (int joint = 0; joint < source.JointCount; joint++)
                {
                    source.TryReadUnityLocalRotation(frame0, joint, source.JointCount, out Quaternion rotation0);
                    source.TryReadUnityLocalRotation(frame1, joint, source.JointCount, out Quaternion rotation1);
                    Quaternion unityRotation = Quaternion.SlerpUnclamped(rotation0, rotation1, blend).normalized;
                    rotations.Add(unityRotation.w);
                    rotations.Add(unityRotation.x);
                    rotations.Add(-unityRotation.y);
                    rotations.Add(-unityRotation.z);
                }
                if (contacts != null)
                {
                    int contactFrame = Mathf.Clamp(Mathf.RoundToInt(sourceFrame), 0, source.FrameCount - 1);
                    Array.Copy(
                        source.footContacts,
                        contactFrame * KimodoFootContactTrackUtility.ChannelCount,
                        contacts,
                        frame * KimodoFootContactTrackUtility.ChannelCount,
                        KimodoFootContactTrackUtility.ChannelCount);
                }
            }

            resampled = new KimodoRawMotionData(
                targetFrameCount,
                source.JointCount,
                targetFrameRate,
                (string[])source.jointNames.Clone(),
                (int[])source.jointParents.Clone(),
                roots,
                rotations,
                source.rootJointIndex,
                contacts);
            return true;
        }

        private static bool SameRig(KimodoRawMotionData a, KimodoRawMotionData b)
        {
            if (a.jointNames.Length != b.jointNames.Length || a.jointParents.Length != b.jointParents.Length)
            {
                return false;
            }
            for (int i = 0; i < a.jointNames.Length; i++)
            {
                if (!string.Equals(a.jointNames[i], b.jointNames[i], StringComparison.Ordinal) ||
                    a.jointParents[i] != b.jointParents[i])
                {
                    return false;
                }
            }
            return true;
        }

        public static bool TryParseFlatBuffer(byte[] motionBytes, out KimodoRawMotionData motion, out string error)
        {
            motion = null;
            error = string.Empty;

            if (motionBytes == null || motionBytes.Length == 0)
            {
                error = "FlatBuffer motion payload is empty.";
                return false;
            }

            MotionPacket.ValidateVersion();
            var buffer = new ByteBuffer(motionBytes);
            if (!MotionPacket.MotionPacketBufferHasIdentifier(buffer) || !MotionPacket.VerifyMotionPacket(buffer))
            {
                error = "FlatBuffer motion payload failed identifier or schema verification.";
                return false;
            }

            MotionPacket packet = MotionPacket.GetRootAsMotionPacket(buffer);
            if (packet.Version != 1)
            {
                error = $"Unsupported FlatBuffer motion version: {packet.Version}.";
                return false;
            }

            int frameCount = Mathf.Max(0, (int)packet.NumFrames);
            int jointCount = Mathf.Max(0, (int)packet.NumJoints);
            if (frameCount <= 0 || jointCount <= 0)
            {
                error = $"FlatBuffer motion has invalid frame/joint counts: frames={frameCount}, joints={jointCount}.";
                return false;
            }

            string[] jointNames = new string[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                string name = i < packet.JointNamesLength ? packet.JointNames(i) : null;
                jointNames[i] = string.IsNullOrWhiteSpace(name) ? $"joint_{i}" : name;
            }

            int[] jointParents = new int[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                jointParents[i] = i < packet.JointParentsLength ? packet.JointParents(i) : (i == 0 ? -1 : i - 1);
            }

            float[] rootPositionScalars = packet.GetRootPositionsArray();
            if (rootPositionScalars == null || rootPositionScalars.Length < frameCount * 3)
            {
                error = $"FlatBuffer root_positions is too small. Expected at least {frameCount * 3}, got {rootPositionScalars?.Length ?? 0}.";
                return false;
            }

            Vector3[] rootPositions = new Vector3[frameCount];
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int baseIndex = frameIndex * 3;
                rootPositions[frameIndex] = new Vector3(
                    -rootPositionScalars[baseIndex + 0],
                    rootPositionScalars[baseIndex + 1],
                    rootPositionScalars[baseIndex + 2]);
            }

            float[] localRotQuatArray = packet.GetLocalRotQuatsArray();
            if (localRotQuatArray == null || localRotQuatArray.Length < frameCount * jointCount * 4)
            {
                error = $"FlatBuffer local_rot_quats is too small. Expected at least {frameCount * jointCount * 4}, got {localRotQuatArray?.Length ?? 0}.";
                return false;
            }

            byte[] footContacts = packet.GetFootContactsArray();
            if (footContacts != null && footContacts.Length > 0 &&
                footContacts.Length != frameCount * KimodoFootContactTrackUtility.ChannelCount)
            {
                error = $"FlatBuffer foot_contacts is invalid. Expected {frameCount * KimodoFootContactTrackUtility.ChannelCount} values, got {footContacts.Length}.";
                return false;
            }

            int rootJoint = 0;
            for (int i = 0; i < jointParents.Length; i++)
            {
                if (jointParents[i] < 0)
                {
                    rootJoint = i;
                    break;
                }
            }

            motion = new KimodoRawMotionData(
                frameCount,
                jointCount,
                packet.Fps > 0f ? packet.Fps : KimodoPlayableClip.FIXED_FRAME_RATE,
                jointNames,
                jointParents,
                rootPositions,
                new List<float>(localRotQuatArray),
                rootJoint,
                footContacts);
            return true;
        }

        private static bool TryBuildFootContacts(
            List<float> source,
            int frameCount,
            out byte[] contacts,
            out string error)
        {
            contacts = Array.Empty<byte>();
            error = string.Empty;
            if (source == null || source.Count == 0)
            {
                return true;
            }

            int expected = frameCount * KimodoFootContactTrackUtility.ChannelCount;
            if (source.Count != expected)
            {
                error = $"foot_contacts length mismatch. Expected {expected}, got {source.Count}.";
                return false;
            }

            contacts = new byte[expected];
            for (int i = 0; i < expected; i++)
            {
                contacts[i] = source[i] >= 0.5f ? (byte)1 : (byte)0;
            }
            return true;
        }

        private static List<float> ToFloatFootContacts(KimodoRawMotionData motion)
        {
            var values = new List<float>();
            if (motion == null || !motion.HasFootContacts)
            {
                return values;
            }

            for (int i = 0; i < motion.footContacts.Length; i++)
            {
                values.Add(motion.footContacts[i] > 0 ? 1f : 0f);
            }
            return values;
        }

        public static bool TryParseAndAnalyze(
            string motionJson,
            string modelName,
            out KimodoRawMotionMetadata metadata,
            out string error,
            string constraintType = FullBodyConstraintType,
            double sampleTime = 0.0,
            bool allowPartialJoints = false)
        {
            metadata = null;
            if (!TryParse(motionJson, out KimodoRawMotionData motion, out error))
            {
                return false;
            }

            return TryAnalyze(
                motion,
                modelName,
                out metadata,
                out error,
                constraintType,
                sampleTime,
                allowPartialJoints);
        }

        public static bool TryAnalyze(
            KimodoRawMotionData motion,
            string modelName,
            out KimodoRawMotionMetadata metadata,
            out string error,
            string constraintType = FullBodyConstraintType,
            double sampleTime = 0.0,
            bool allowPartialJoints = false)
        {
            metadata = null;
            error = string.Empty;
            if (motion == null)
            {
                error = "Motion data is null.";
                return false;
            }

            if (!motion.TryReadUnityRootPosition(0, out Vector3 firstRootPosition))
            {
                error = "Failed to read first root position from motion data.";
                return false;
            }

            if (!motion.TryReadUnityRootPosition(Mathf.Max(0, motion.FrameCount - 1), out Vector3 lastRootPosition))
            {
                error = "Failed to read last root position from motion data.";
                return false;
            }

            if (!TryExtractTailMarkerSample(
                    motion,
                    modelName,
                    out KimodoMarkerSampleResult tailPose,
                    out error,
                    constraintType,
                    sampleTime,
                    allowPartialJoints))
            {
                return false;
            }

            metadata = new KimodoRawMotionMetadata(
                motion,
                firstRootPosition,
                lastRootPosition,
                tailPose);
            return true;
        }

        public static bool TryAnalyzeGenerationResult(
            KimodoGenerationResultDto result,
            string modelName,
            out KimodoRawMotionMetadata metadata,
            out string error,
            string constraintType = FullBodyConstraintType,
            double sampleTime = 0.0,
            bool allowPartialJoints = false)
        {
            metadata = null;
            error = string.Empty;
            if (result == null)
            {
                error = "Generation result is null.";
                return false;
            }

            if (result.motionData != null)
            {
                return TryAnalyze(
                    result.motionData,
                    modelName,
                    out metadata,
                    out error,
                    constraintType,
                    sampleTime,
                    allowPartialJoints);
            }

            if (!string.IsNullOrWhiteSpace(result.motionJsonCompact))
            {
                return TryParseAndAnalyze(
                    result.motionJsonCompact,
                    modelName,
                    out metadata,
                    out error,
                    constraintType,
                    sampleTime,
                    allowPartialJoints);
            }

            error = "Generation result does not contain motion data.";
            return false;
        }

        public static bool TryApplyFrame(
            KimodoRawMotionData motion,
            string modelName,
            Transform profileSkeletonRoot,
            int frameIndex,
            out string error,
            bool applyRootPosition = true,
            bool allowPartialJoints = false)
        {
            if (!TryCreatePlaybackBinding(motion, modelName, profileSkeletonRoot, out KimodoRawMotionPlaybackBinding binding, out error, allowPartialJoints))
            {
                return false;
            }

            return TryApplyFrame(binding, frameIndex, out error, applyRootPosition);
        }

        public static bool TryApplyTime(
            KimodoRawMotionData motion,
            string modelName,
            Transform profileSkeletonRoot,
            float timeSeconds,
            out string error,
            bool loop = false,
            bool applyRootPosition = true,
            bool allowPartialJoints = false)
        {
            if (!TryCreatePlaybackBinding(motion, modelName, profileSkeletonRoot, out KimodoRawMotionPlaybackBinding binding, out error, allowPartialJoints))
            {
                return false;
            }

            return TryApplyTime(binding, timeSeconds, out error, loop, applyRootPosition);
        }

        public static bool TryCreatePlaybackBinding(
            KimodoRawMotionData motion,
            string modelName,
            Transform profileSkeletonRoot,
            out KimodoRawMotionPlaybackBinding binding,
            out string error,
            bool allowPartialJoints = false)
        {
            binding = null;
            if (!TryResolvePlaybackTargets(motion, modelName, profileSkeletonRoot, allowPartialJoints, out Transform[] joints, out int[] motionJointIndices, out error))
            {
                return false;
            }

            binding = new KimodoRawMotionPlaybackBinding(motion, joints, motionJointIndices);
            return true;
        }

        public static bool TryApplyFrame(
            KimodoRawMotionPlaybackBinding binding,
            int frameIndex,
            out string error,
            bool applyRootPosition = true)
        {
            error = string.Empty;
            if (!ValidateBinding(binding, out error))
            {
                return false;
            }

            KimodoRawMotionData motion = binding.motion;
            int frame = Mathf.Clamp(frameIndex, 0, Mathf.Max(0, motion.FrameCount - 1));
            int rotationJointCount = ResolveRotationJointCount(motion);
            for (int i = 0; i < binding.joints.Length; i++)
            {
                Transform joint = binding.joints[i];
                int motionJoint = binding.motionJointIndices[i];
                if (joint == null || motionJoint < 0)
                {
                    continue;
                }

                if (motion.TryReadUnityLocalRotation(frame, motionJoint, rotationJointCount, out Quaternion localRotation))
                {
                    joint.localRotation = localRotation;
                }
            }

            if (applyRootPosition && binding.joints.Length > 0 && binding.joints[0] != null)
            {
                if (motion.TryReadUnityRootPosition(frame, out Vector3 rootPosition))
                {
                    binding.joints[0].localPosition = rootPosition;
                }
            }

            return true;
        }

        public static bool TryApplyTime(
            KimodoRawMotionPlaybackBinding binding,
            float timeSeconds,
            out string error,
            bool loop = false,
            bool applyRootPosition = true)
        {
            error = string.Empty;
            if (!ValidateBinding(binding, out error))
            {
                return false;
            }

            KimodoRawMotionData motion = binding.motion;
            ResolveSampleFrames(motion, timeSeconds, loop, out int frame0, out int frame1, out float blend);
            int rotationJointCount = ResolveRotationJointCount(motion);
            for (int i = 0; i < binding.joints.Length; i++)
            {
                Transform joint = binding.joints[i];
                int motionJoint = binding.motionJointIndices[i];
                if (joint == null || motionJoint < 0)
                {
                    continue;
                }

                if (!motion.TryReadUnityLocalRotation(frame0, motionJoint, rotationJointCount, out Quaternion q0))
                {
                    continue;
                }

                if (blend > 0f && motion.TryReadUnityLocalRotation(frame1, motionJoint, rotationJointCount, out Quaternion q1))
                {
                    joint.localRotation = Quaternion.Slerp(q0, q1, blend);
                }
                else
                {
                    joint.localRotation = q0;
                }
            }

            if (applyRootPosition && binding.joints.Length > 0 && binding.joints[0] != null)
            {
                if (motion.TryReadUnityRootPosition(frame0, out Vector3 p0))
                {
                    if (blend > 0f && motion.TryReadUnityRootPosition(frame1, out Vector3 p1))
                    {
                        binding.joints[0].localPosition = Vector3.Lerp(p0, p1, blend);
                    }
                    else
                    {
                        binding.joints[0].localPosition = p0;
                    }
                }
            }

            return true;
        }

        public static bool ResolveInterpolatedRootPosition(
            KimodoRawMotionData motion,
            float timeSeconds,
            bool loop,
            out Vector3 rootPosition)
        {
            rootPosition = Vector3.zero;
            if (motion == null)
            {
                return false;
            }

            ResolveSampleFrames(motion, timeSeconds, loop, out int frame0, out int frame1, out float blend);
            if (!motion.TryReadUnityRootPosition(frame0, out Vector3 p0))
            {
                return false;
            }

            if (blend > 0f && motion.TryReadUnityRootPosition(frame1, out Vector3 p1))
            {
                rootPosition = Vector3.Lerp(p0, p1, blend);
            }
            else
            {
                rootPosition = p0;
            }

            return true;
        }

        public static bool TryExtractTailMarkerSample(
            string motionJson,
            string modelName,
            out KimodoMarkerSampleResult sample,
            out string error,
            string constraintType = FullBodyConstraintType,
            double sampleTime = 0.0,
            bool allowPartialJoints = false)
        {
            sample = null;
            if (!TryParse(motionJson, out KimodoRawMotionData motion, out error))
            {
                return false;
            }

            return TryExtractMarkerSample(
                motion,
                modelName,
                Mathf.Max(0, motion.FrameCount - 1),
                out sample,
                out error,
                constraintType,
                sampleTime,
                allowPartialJoints);
        }

        public static bool TryExtractTailMarkerSample(
            KimodoRawMotionData motion,
            string modelName,
            out KimodoMarkerSampleResult sample,
            out string error,
            string constraintType = FullBodyConstraintType,
            double sampleTime = 0.0,
            bool allowPartialJoints = false)
        {
            return TryExtractMarkerSample(
                motion,
                modelName,
                motion != null ? Mathf.Max(0, motion.FrameCount - 1) : 0,
                out sample,
                out error,
                constraintType,
                sampleTime,
                allowPartialJoints);
        }

        public static bool TryExtractMarkerSample(
            KimodoRawMotionData motion,
            string modelName,
            int frameIndex,
            out KimodoMarkerSampleResult sample,
            out string error,
            string constraintType = FullBodyConstraintType,
            double sampleTime = 0.0,
            bool allowPartialJoints = false)
        {
            sample = null;
            error = string.Empty;
            if (motion == null)
            {
                error = "Motion data is null.";
                return false;
            }

            KimodoRigProfileDatabase.ResolveProfile(modelName, out KimodoConstraintRigType rigType, out string[] profileJointNames, out _);
            if (!TryResolveMotionJointIndices(motion, profileJointNames, allowPartialJoints, out int[] motionJointIndices, out error))
            {
                return false;
            }

            int frame = Mathf.Clamp(frameIndex, 0, Mathf.Max(0, motion.FrameCount - 1));
            int rotationJointCount = ResolveRotationJointCount(motion);
            var localAxisAngles = new List<Vector3>(profileJointNames.Length);
            var sampledJointIndices = new List<int>(profileJointNames.Length);
            for (int i = 0; i < profileJointNames.Length; i++)
            {
                int motionJoint = motionJointIndices[i];
                if (motionJoint >= 0 &&
                    motion.TryReadUnityLocalRotation(frame, motionJoint, rotationJointCount, out Quaternion localRotation))
                {
                    localAxisAngles.Add(KimodoRuntimeUtility.QuaternionToAxisAngleVector(localRotation));
                    sampledJointIndices.Add(i);
                }
                else
                {
                    localAxisAngles.Add(Vector3.zero);
                }
            }

            Vector3 rootPosition = Vector3.zero;
            _ = motion.TryReadUnityRootPosition(frame, out rootPosition);

            Vector2 heading = Vector2.right;
            int rootRotationJoint = motionJointIndices.Length > 0 && motionJointIndices[0] >= 0
                ? motionJointIndices[0]
                : motion.RootJointIndex;
            if (motion.TryReadUnityLocalRotation(frame, rootRotationJoint, rotationJointCount, out Quaternion rootRotation))
            {
                Vector3 forward = rootRotation * Vector3.forward;
                heading = new Vector2(forward.x, forward.z);
                if (heading.sqrMagnitude <= 1e-8f)
                {
                    heading = Vector2.right;
                }
                else
                {
                    heading.Normalize();
                }
            }

            sample = new KimodoMarkerSampleResult
            {
                constraintType = string.IsNullOrWhiteSpace(constraintType) ? FullBodyConstraintType : constraintType,
                sampleTime = sampleTime,
                rigType = rigType,
                hasRootHeading = true,
                kimodoRootPosition = rootPosition,
                rootHeading = heading,
                unityRootPos = rootPosition,
                unityRootRot = Quaternion.identity,
                jointNames = new List<string>(profileJointNames),
                localAxisAngles = localAxisAngles,
                sampledJointIndices = sampledJointIndices
            };
            return true;
        }

        private static bool ValidateBinding(KimodoRawMotionPlaybackBinding binding, out string error)
        {
            error = string.Empty;
            if (binding == null)
            {
                error = "Motion playback binding is null.";
                return false;
            }

            if (binding.motion == null)
            {
                error = "Motion playback binding has no motion data.";
                return false;
            }

            if (binding.joints == null || binding.motionJointIndices == null || binding.joints.Length != binding.motionJointIndices.Length)
            {
                error = "Motion playback binding joint mapping is invalid.";
                return false;
            }

            return true;
        }

        private static MotionJsonData ParseMotionJsonFlexible(string motionJson)
        {
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new Exception("motion json is empty.");
            }

            JToken token = JToken.Parse(motionJson);
            if (token is not JObject obj)
            {
                throw new Exception("motion json root is not an object.");
            }

            return obj.ToObject<MotionJsonData>() ?? new MotionJsonData();
        }

        private static bool ValidateData(MotionJsonData data, out string error)
        {
            error = string.Empty;
            if (data == null)
            {
                error = "Parsed motion data is null.";
                return false;
            }

            if (data.num_frames < 2)
            {
                error = "Need at least 2 frames in motion data.";
                return false;
            }

            if (data.num_joints <= 0)
            {
                error = "No num_joints in motion data.";
                return false;
            }

            if (data.joint_names == null || data.joint_names.Length == 0)
            {
                error = "No joint_names in motion data.";
                return false;
            }

            if (data.joint_names.Length < data.num_joints)
            {
                error = "joint_names count is smaller than num_joints.";
                return false;
            }

            if (data.joints == null || data.joints.Count == 0)
            {
                error = "No joints in compact motion data.";
                return false;
            }

            int requiredJointScalars = data.num_frames * data.num_joints * 3;
            if (data.joints.Count < requiredJointScalars)
            {
                error = $"Compact joints count is too small. Expected at least {requiredJointScalars}, got {data.joints.Count}.";
                return false;
            }

            if (data.local_rot_quats == null || data.local_rot_quats.Count == 0)
            {
                error = "No local_rot_quats in motion data.";
                return false;
            }

            return true;
        }

        private static bool TryResolvePlaybackTargets(
            KimodoRawMotionData motion,
            string modelName,
            Transform profileSkeletonRoot,
            bool allowPartialJoints,
            out Transform[] joints,
            out int[] motionJointIndices,
            out string error)
        {
            joints = Array.Empty<Transform>();
            motionJointIndices = Array.Empty<int>();
            error = string.Empty;
            if (motion == null)
            {
                error = "Motion data is null.";
                return false;
            }

            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName,
                    profileSkeletonRoot,
                    out string[] profileJointNames,
                    out _,
                    out joints,
                    out error))
            {
                return false;
            }

            return TryResolveMotionJointIndices(motion, profileJointNames, allowPartialJoints, out motionJointIndices, out error);
        }

        private static bool TryResolveMotionJointIndices(
            KimodoRawMotionData motion,
            string[] profileJointNames,
            bool allowPartialJoints,
            out int[] motionJointIndices,
            out string error)
        {
            motionJointIndices = Array.Empty<int>();
            error = string.Empty;
            if (motion == null)
            {
                error = "Motion data is null.";
                return false;
            }

            if (profileJointNames == null || profileJointNames.Length == 0)
            {
                error = "Profile joint names are empty.";
                return false;
            }

            var sourceByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < motion.jointNames.Length; i++)
            {
                AddJointLookup(sourceByName, motion.jointNames[i], i);
                AddJointLookup(sourceByName, KimodoRuntimeUtility.SanitizeName(motion.jointNames[i]), i);
            }

            motionJointIndices = new int[profileJointNames.Length];
            for (int i = 0; i < motionJointIndices.Length; i++)
            {
                motionJointIndices[i] = -1;
            }

            var missing = new List<string>();
            bool sameJointCount = motion.JointCount == profileJointNames.Length;
            for (int i = 0; i < profileJointNames.Length; i++)
            {
                string profileName = profileJointNames[i];
                if (!string.IsNullOrWhiteSpace(profileName) &&
                    sourceByName.TryGetValue(profileName, out int sourceIndex))
                {
                    motionJointIndices[i] = sourceIndex;
                    continue;
                }

                if (sameJointCount && i < motion.JointCount)
                {
                    motionJointIndices[i] = i;
                    continue;
                }

                missing.Add(profileName ?? $"Joint_{i}");
            }

            if (!allowPartialJoints && missing.Count > 0)
            {
                error = $"Motion json is missing profile joints for '{string.Join("', '", missing)}'.";
                motionJointIndices = Array.Empty<int>();
                return false;
            }

            return true;
        }

        private static void AddJointLookup(Dictionary<string, int> lookup, string name, int index)
        {
            if (lookup == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string key = name.Trim();
            if (!lookup.ContainsKey(key))
            {
                lookup[key] = index;
            }
        }

        private static void ResolveSampleFrames(KimodoRawMotionData motion, float timeSeconds, bool loop, out int frame0, out int frame1, out float blend)
        {
            float sampleTime = Mathf.Max(0f, timeSeconds);
            if (loop && motion.DurationSeconds > 1e-6f)
            {
                sampleTime = Mathf.Repeat(sampleTime, motion.DurationSeconds);
            }
            else
            {
                sampleTime = Mathf.Min(sampleTime, motion.LastFrameTimeSeconds);
            }

            float frameFloat = sampleTime * motion.FrameRate;
            frame0 = Mathf.Clamp(Mathf.FloorToInt(frameFloat), 0, Mathf.Max(0, motion.FrameCount - 1));
            frame1 = Mathf.Clamp(frame0 + 1, 0, Mathf.Max(0, motion.FrameCount - 1));
            blend = Mathf.Clamp01(frameFloat - frame0);
        }

        private static int ResolveRotationJointCount(KimodoRawMotionData motion)
        {
            if (motion == null || motion.localRotQuats == null || motion.FrameCount <= 0)
            {
                return 0;
            }

            return Mathf.Min(motion.JointCount, motion.localRotQuats.Count / (motion.FrameCount * 4));
        }

        private static int ResolveRotationJointCount(MotionJsonData data, int frameCount, int jointCount)
        {
            if (data == null || data.local_rot_quats == null || frameCount <= 0)
            {
                return 0;
            }

            return Mathf.Min(jointCount, data.local_rot_quats.Count / (frameCount * 4));
        }

        private static bool TryBuildRootPositions(
            List<float> joints,
            int frameCount,
            int jointCount,
            int rootJointIndex,
            out Vector3[] rootPositions,
            out string error)
        {
            rootPositions = null;
            error = string.Empty;
            if (joints == null)
            {
                error = "Compact joints data is null.";
                return false;
            }

            if (frameCount <= 0 || jointCount <= 0)
            {
                error = "Frame count or joint count is invalid while building root positions.";
                return false;
            }

            if (rootJointIndex < 0 || rootJointIndex >= jointCount)
            {
                error = $"Root joint index {rootJointIndex} is out of range for joint count {jointCount}.";
                return false;
            }

            rootPositions = new Vector3[frameCount];
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int baseIndex = (frameIndex * jointCount + rootJointIndex) * 3;
                if (baseIndex < 0 || baseIndex + 2 >= joints.Count)
                {
                    error = $"Compact joints data is truncated while reading root position for frame {frameIndex}.";
                    rootPositions = null;
                    return false;
                }

                rootPositions[frameIndex] = new Vector3(
                    -joints[baseIndex + 0],
                    joints[baseIndex + 1],
                    joints[baseIndex + 2]);
            }

            return true;
        }

        private static int FindRootJointIndex(MotionJsonData data, int jointCount)
        {
            if (jointCount <= 0)
            {
                return 0;
            }

            if (data.joint_parents != null && data.joint_parents.Length >= jointCount)
            {
                for (int i = 0; i < jointCount; i++)
                {
                    if (data.joint_parents[i] < 0)
                    {
                        return i;
                    }
                }
            }

            return 0;
        }
    }
}

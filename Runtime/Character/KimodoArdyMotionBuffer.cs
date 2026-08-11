using System;
using Unity.Collections;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoArdyMotionBuffer : IDisposable
    {
        internal const int DefaultCapacityFrames = 4096;

        private readonly int capacityFrames;
        private readonly int jointCount;
        private readonly float frameRate;
        private readonly string[] jointNames;
        private readonly int[] jointParents;
        private readonly int rootJointIndex;
        private NativeArray<Vector3> rootPositions;
        private NativeArray<Quaternion> localRotations;
        private NativeArray<byte> footContacts;

        internal KimodoArdyMotionBuffer(KimodoRawMotionData source, int capacityFrames = DefaultCapacityFrames)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            this.capacityFrames = Mathf.Max(2, capacityFrames);
            jointCount = source.JointCount;
            frameRate = source.FrameRate;
            jointNames = (string[])source.jointNames.Clone();
            jointParents = (int[])source.jointParents.Clone();
            rootJointIndex = source.RootJointIndex;
            rootPositions = new NativeArray<Vector3>(this.capacityFrames, Allocator.Persistent);
            localRotations = new NativeArray<Quaternion>(
                this.capacityFrames * jointCount,
                Allocator.Persistent);
            footContacts = new NativeArray<byte>(
                this.capacityFrames * KimodoFootContactTrackUtility.ChannelCount,
                Allocator.Persistent);
        }

        internal int StartFrame { get; private set; }
        internal int EndFrameExclusive { get; private set; }
        internal int JointCount => jointCount;
        internal float FrameRate => frameRate;
        internal int RootJointIndex => rootJointIndex;
        internal string[] JointNames => jointNames;
        internal int[] JointParents => jointParents;
        internal bool IsEmpty => EndFrameExclusive <= StartFrame;
        internal float EndTimeSeconds => EndFrameExclusive > 0
            ? (EndFrameExclusive - 1) / frameRate
            : 0f;

        internal bool TryReplace(
            KimodoRawMotionData segment,
            int responseStartFrame,
            int protectedFrameExclusive,
            out int writtenStartFrame,
            out string error)
        {
            writtenStartFrame = responseStartFrame;
            error = string.Empty;
            if (!IsCompatible(segment, out error))
            {
                return false;
            }

            int responseEndFrame = responseStartFrame + segment.FrameCount;
            int writeStartFrame = Mathf.Max(responseStartFrame, protectedFrameExclusive);
            if (!IsEmpty && writeStartFrame > EndFrameExclusive)
            {
                error = $"ARDY replacement leaves a gap: buffer ends at frame {EndFrameExclusive}, replacement starts at frame {writeStartFrame}.";
                return false;
            }
            if (responseEndFrame - writeStartFrame > capacityFrames)
            {
                error = $"ARDY replacement exceeds the {capacityFrames}-frame ring buffer capacity.";
                return false;
            }

            writtenStartFrame = writeStartFrame;
            if (writeStartFrame >= responseEndFrame)
            {
                return true;
            }
            if (writeStartFrame < responseEndFrame)
            {
                int sourceStartFrame = writeStartFrame - responseStartFrame;
                for (int sourceFrame = sourceStartFrame; sourceFrame < segment.FrameCount; sourceFrame++)
                {
                    int absoluteFrame = responseStartFrame + sourceFrame;
                    int slot = PositiveModulo(absoluteFrame, capacityFrames);
                    if (!segment.TryReadUnityRootPosition(sourceFrame, out Vector3 rootPosition))
                    {
                        error = $"Failed to read ARDY root position at response frame {sourceFrame}.";
                        return false;
                    }
                    rootPositions[slot] = rootPosition;

                    int rotationBase = slot * jointCount;
                    for (int joint = 0; joint < jointCount; joint++)
                    {
                        if (!segment.TryReadUnityLocalRotation(sourceFrame, joint, jointCount, out Quaternion rotation))
                        {
                            error = $"Failed to read ARDY local rotation at response frame {sourceFrame}, joint {joint}.";
                            return false;
                        }
                        localRotations[rotationBase + joint] = rotation;
                    }

                    int contactBase = slot * KimodoFootContactTrackUtility.ChannelCount;
                    for (int channel = 0; channel < KimodoFootContactTrackUtility.ChannelCount; channel++)
                    {
                        footContacts[contactBase + channel] =
                            segment.HasFootContacts && segment.TryReadFootContact(sourceFrame, channel, out float contact) && contact > 0.5f
                                ? (byte)1
                                : (byte)0;
                    }
                }
            }

            if (IsEmpty)
            {
                StartFrame = writeStartFrame;
            }
            EndFrameExclusive = responseEndFrame;
            StartFrame = Mathf.Max(StartFrame, EndFrameExclusive - capacityFrames);
            return true;
        }

        internal int ResolveProtectedFrameExclusive(float timeSeconds)
        {
            if (IsEmpty)
            {
                return 0;
            }

            int interpolationEnd = Mathf.FloorToInt(Mathf.Max(0f, timeSeconds) * frameRate) + 2;
            return Mathf.Clamp(interpolationEnd, StartFrame, EndFrameExclusive);
        }

        internal bool TryResolveSampleFrames(
            float timeSeconds,
            out int frame0,
            out int frame1,
            out float blend)
        {
            frame0 = frame1 = 0;
            blend = 0f;
            if (IsEmpty)
            {
                return false;
            }

            float frame = Mathf.Clamp(
                Mathf.Max(0f, timeSeconds) * frameRate,
                StartFrame,
                EndFrameExclusive - 1);
            frame0 = Mathf.FloorToInt(frame);
            frame1 = Mathf.Min(frame0 + 1, EndFrameExclusive - 1);
            blend = Mathf.Clamp01(frame - frame0);
            return true;
        }

        internal bool TryReadRootPosition(int absoluteFrame, out Vector3 value)
        {
            value = default;
            if (!Contains(absoluteFrame))
            {
                return false;
            }

            value = rootPositions[PositiveModulo(absoluteFrame, capacityFrames)];
            return true;
        }

        internal bool TryReadLocalRotation(int absoluteFrame, int jointIndex, out Quaternion value)
        {
            value = Quaternion.identity;
            if (!Contains(absoluteFrame) || jointIndex < 0 || jointIndex >= jointCount)
            {
                return false;
            }

            int slot = PositiveModulo(absoluteFrame, capacityFrames);
            value = localRotations[slot * jointCount + jointIndex];
            return true;
        }

        internal bool TryReadFootContact(int absoluteFrame, int channel, out float value)
        {
            value = 0f;
            if (!Contains(absoluteFrame) ||
                channel < 0 || channel >= KimodoFootContactTrackUtility.ChannelCount)
            {
                return false;
            }

            int slot = PositiveModulo(absoluteFrame, capacityFrames);
            value = footContacts[slot * KimodoFootContactTrackUtility.ChannelCount + channel] > 0 ? 1f : 0f;
            return true;
        }

        public void Dispose()
        {
            if (rootPositions.IsCreated) rootPositions.Dispose();
            if (localRotations.IsCreated) localRotations.Dispose();
            if (footContacts.IsCreated) footContacts.Dispose();
        }

        private bool Contains(int absoluteFrame)
        {
            return absoluteFrame >= StartFrame && absoluteFrame < EndFrameExclusive;
        }

        private bool IsCompatible(KimodoRawMotionData segment, out string error)
        {
            error = string.Empty;
            if (segment == null || segment.FrameCount <= 0)
            {
                error = "ARDY KMB replacement is empty.";
                return false;
            }
            if (segment.JointCount != jointCount || Mathf.Abs(segment.FrameRate - frameRate) > 1e-4f ||
                segment.jointNames.Length != jointNames.Length || segment.jointParents.Length != jointParents.Length)
            {
                error = "ARDY KMB replacement FPS or rig metadata changed.";
                return false;
            }
            for (int joint = 0; joint < jointCount; joint++)
            {
                if (!string.Equals(segment.jointNames[joint], jointNames[joint], StringComparison.Ordinal) ||
                    segment.jointParents[joint] != jointParents[joint])
                {
                    error = "ARDY KMB replacement rig metadata changed.";
                    return false;
                }
            }
            return true;
        }

        private static int PositiveModulo(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}

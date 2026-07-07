using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoPlayableClipGenerationExecutionService
    {
        internal static bool TryStartGenerate(
            KimodoPlayableClip clip,
            out EditorGenerateSession session,
            out string error)
        {
            session = null;
            error = string.Empty;

            if (clip == null)
            {
                error = "KimodoPlayableClip is null.";
                return false;
            }

            return EditorGenerateSessionRunner.Start(
                clip,
                $"clip:{clip.GetInstanceID()}",
                KimodoEditorCommandKind.GeneratePlayableClip,
                async (handle, token) =>
                {
                    return await GenerateAndFinalizeAsync(
                        clip,
                        externalConstraint: null,
                        (stage, message) => EditorGenerateSessionRunner.UpdateProgress(clip, handle.RequestId, stage, message),
                        token);
                },
                out session,
                out error);
        }

        internal static async Task<KimodoEditorGenerateResult> GenerateAndFinalizeAsync(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            Action<KimodoBridgeCommandStage, string> progress,
            CancellationToken token)
        {
            if (clip == null)
            {
                throw new InvalidOperationException("KimodoPlayableClip is null.");
            }

            string prompt = clip.motionPrompt ?? string.Empty;
            KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                clip,
                prompt,
                externalConstraint,
                token);

            try
            {
                request.Progress = progress;
                KimodoEditorGenerateResult result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
                token.ThrowIfCancellationRequested();
                KimodoPlayableClipGenerationHostService.FinalizeGeneration(clip, request, result);
                return result;
            }
            catch
            {
                KimodoPlayableClipGenerationHostService.CleanupFailedGeneration(request);
                throw;
            }
        }
    }
}

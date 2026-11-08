using System;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        internal static bool TryAnalyzePlayableClipFromEditor(
            KimodoPlayableClip playableClip,
            out string analysisJson,
            out string error)
        {
            analysisJson = string.Empty;
            error = string.Empty;

            try
            {
                if (playableClip == null)
                {
                    throw new InvalidOperationException("The selected KimodoPlayableClip is missing.");
                }

                AnimationClip animationClip = playableClip.clip;
                if (animationClip == null)
                {
                    throw new InvalidOperationException("This KimodoPlayableClip does not contain a generated AnimationClip yet.");
                }

                TimelineClip sourceTimelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(playableClip);
                if (sourceTimelineClip == null)
                {
                    throw new InvalidOperationException("Could not locate this KimodoPlayableClip in an open or scene Timeline.");
                }

                AnimationTrack sourceTrack = sourceTimelineClip.GetParentTrack() as AnimationTrack;
                if (sourceTrack == null)
                {
                    throw new InvalidOperationException("The selected KimodoPlayableClip is not on an AnimationTrack.");
                }

                TimelineAsset sourceTimeline = sourceTrack.timelineAsset;
                if (!KimodoDirectorResolver.TryResolve(sourceTimeline, out PlayableDirector sourceDirector, out string directorError))
                {
                    throw new InvalidOperationException($"Could not resolve the scene Director for this Timeline: {directorError}");
                }

                Animator animator = ResolveBoundAnimator(sourceDirector.GetGenericBinding(sourceTrack));
                if (animator == null)
                {
                    throw new InvalidOperationException("The selected Timeline track is not bound to a scene Animator.");
                }

                GameObject characterRoot = animator.transform.root.gameObject;
                Scene activeScene = SceneManager.GetActiveScene();
                if (characterRoot == null || !characterRoot.scene.IsValid() || characterRoot.scene != activeScene ||
                    EditorUtility.IsPersistent(characterRoot) || IsSessionObject(characterRoot))
                {
                    throw new InvalidOperationException("The selected Timeline must be bound to a character in the active scene.");
                }

                if (!TryGetExistingEditorAnalysisSession(
                        sourceTimeline,
                        out TimelineSessionRecord session,
                        out string sessionError))
                {
                    throw new InvalidOperationException(sessionError);
                }
                TimelineCharacterRecord character = ResolveSessionCharacter(session, characterRoot, characterRoot.name);
                if (character == null)
                {
                    if (!AddCharacterTrack(session, characterRoot, animator, tryResolveAvatar: true, out error))
                    {
                        throw new InvalidOperationException($"Could not register the scene character in the current context: {error}");
                    }

                    character = ResolveSessionCharacter(session, characterRoot, characterRoot.name);
                }
                if (character == null)
                {
                    throw new InvalidOperationException("The scene character was not registered in the current context.");
                }

                TimelineAnimationRecord animation = character.Animations.FirstOrDefault(item =>
                    item.TimelineClip == sourceTimelineClip ||
                    item.TimelineClip?.asset == playableClip ||
                    ReferenceEquals(item.Clip, animationClip));
                if (animation == null)
                {
                    string requestedName = string.IsNullOrWhiteSpace(sourceTimelineClip.displayName)
                        ? animationClip.name
                        : sourceTimelineClip.displayName;
                    animation = AppendAnimationClip(
                        session,
                        character,
                        animationClip,
                        "editor_generated",
                        null,
                        requestedName);
                }

                SaveTimelineSession(session);

                JObject result;
                try
                {
                    result = AnimationAnalysisCoreInternal(new JObject
                    {
                        ["clips"] = new JArray(new JObject
                        {
                            ["character"] = character.Name,
                            ["clip"] = animation.Name
                        })
                    }, allowActiveGenerationRange: false);
                }
                finally
                {
                    AnalysisPoseSamples.Clear();
                }

                analysisJson = result.ToString(Formatting.Indented);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static Animator ResolveBoundAnimator(UnityEngine.Object binding)
        {
            if (binding is Animator animator)
            {
                return animator;
            }

            if (binding is GameObject gameObject)
            {
                return gameObject.GetComponent<Animator>() ?? gameObject.GetComponentInChildren<Animator>(true);
            }

            if (binding is Component component)
            {
                return component.GetComponent<Animator>() ?? component.GetComponentInChildren<Animator>(true);
            }

            return null;
        }

        private static bool TryGetExistingEditorAnalysisSession(
            TimelineAsset sourceTimeline,
            out TimelineSessionRecord session,
            out string error)
        {
            session = null;
            error = string.Empty;
            session = BuildTimelineSessionView(SceneManager.GetActiveScene());
            if (!IsTimelineSessionValid(session))
            {
                error = "Post-generation analysis skipped because no existing scene Timeline Session is available; an automatic context was not created.";
                return false;
            }
            if (sourceTimeline == null || session.TimelineAsset != sourceTimeline)
            {
                error = "Post-generation analysis skipped because the existing scene Session does not own the source Timeline.";
                return false;
            }
            return true;
        }
    }

    [InitializeOnLoad]
    internal static class KimodoEditorFullAnalysisCommandRegistration
    {
        static KimodoEditorFullAnalysisCommandRegistration()
        {
            KimodoEditorFullAnalysisBridge.Register(command_context.TryAnalyzePlayableClipFromEditor);
        }
    }
}

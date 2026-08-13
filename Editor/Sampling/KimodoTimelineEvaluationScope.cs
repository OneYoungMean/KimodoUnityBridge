using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoTimelineEvaluationScope : IDisposable
    {
        private readonly PlayableDirector director;
        private readonly PlayState originalState;
        private readonly bool originalGraphValid;
        private readonly double originalTime;
        private readonly List<AnimatorEventState> animatorStates;
        private bool disposed;

        private KimodoTimelineEvaluationScope(PlayableDirector director)
        {
            this.director = director != null
                ? director
                : throw new ArgumentNullException(nameof(director));
            originalState = director.state;
            originalGraphValid = director.playableGraph.IsValid();
            originalTime = director.time;
            animatorStates = DisableBoundAnimatorEvents(director);
            try
            {
                if (originalState == PlayState.Playing)
                {
                    director.Pause();
                }
            }
            catch
            {
                RestoreAnimatorEvents(animatorStates);
                throw;
            }
        }

        internal static KimodoTimelineEvaluationScope Begin(PlayableDirector director) =>
            new KimodoTimelineEvaluationScope(director);

        internal void EvaluateAt(double time)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(KimodoTimelineEvaluationScope));
            }
            director.time = time;
            director.Evaluate();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try
            {
                if (director == null)
                {
                    return;
                }

                director.time = originalTime;
                director.Evaluate();
                if (originalState == PlayState.Playing)
                {
                    director.Play();
                }
                else if (!originalGraphValid)
                {
                    director.Stop();
                    director.time = originalTime;
                }
                else
                {
                    director.Pause();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][TimelineSample] Failed to restore Director state: {ex.Message}");
            }
            finally
            {
                RestoreAnimatorEvents(animatorStates);
            }
        }

        private static List<AnimatorEventState> DisableBoundAnimatorEvents(PlayableDirector director)
        {
            var states = new List<AnimatorEventState>();
            var seen = new HashSet<Animator>();
            if (director.playableAsset == null)
            {
                return states;
            }

            foreach (PlayableBinding output in director.playableAsset.outputs)
            {
                UnityEngine.Object binding = director.GetGenericBinding(output.sourceObject);
                Animator animator = binding as Animator;
                if (animator == null && binding is GameObject gameObject)
                {
                    animator = gameObject.GetComponentInChildren<Animator>(true);
                }
                else if (animator == null && binding is Component component)
                {
                    animator = component.GetComponentInChildren<Animator>(true);
                }
                if (animator == null || !seen.Add(animator))
                {
                    continue;
                }

                states.Add(new AnimatorEventState(animator, animator.fireEvents));
                animator.fireEvents = false;
            }
            return states;
        }

        private static void RestoreAnimatorEvents(IReadOnlyList<AnimatorEventState> states)
        {
            for (int i = 0; i < states.Count; i++)
            {
                Animator animator = states[i].Animator;
                if (animator != null)
                {
                    animator.fireEvents = states[i].FireEvents;
                }
            }
        }

        private readonly struct AnimatorEventState
        {
            internal AnimatorEventState(Animator animator, bool fireEvents)
            {
                Animator = animator;
                FireEvents = fireEvents;
            }

            internal Animator Animator { get; }
            internal bool FireEvents { get; }
        }
    }
}

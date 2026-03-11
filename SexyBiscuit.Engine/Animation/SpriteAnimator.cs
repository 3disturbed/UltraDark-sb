using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Animation;

// ---------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------

/// <summary>
/// Describes a single sprite animation clip: a contiguous range of frames on a
/// spritesheet, played at a given FPS.
/// </summary>
public class AnimationClip
{
    /// <summary>Unique clip identifier used as the key in <see cref="SpriteAnimator.Clips"/>.</summary>
    public string Name        { get; set; } = "";

    /// <summary>Width of each frame in the source texture (pixels).</summary>
    public int    FrameWidth  { get; set; }

    /// <summary>Height of each frame in the source texture (pixels).</summary>
    public int    FrameHeight { get; set; }

    /// <summary>Zero-based index of the first frame (inclusive).</summary>
    public int    StartFrame  { get; set; }

    /// <summary>Zero-based index of the last frame (inclusive).</summary>
    public int    EndFrame    { get; set; }

    /// <summary>Playback rate in frames per second.</summary>
    public float  Fps         { get; set; } = 12f;

    /// <summary>When true the clip restarts after the last frame; otherwise it stops on the last frame.</summary>
    public bool   Loop        { get; set; } = true;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/// <summary>
/// Drives a <see cref="SpriteRenderer"/> by advancing through the frames of an
/// <see cref="AnimationClip"/> each frame. Multiple clips are registered by name;
/// <see cref="Play"/> switches between them.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class SpriteAnimator : Component
{
    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------

    /// <summary>All registered clips, keyed by <see cref="AnimationClip.Name"/>.</summary>
    public Dictionary<string, AnimationClip> Clips { get; } = new(StringComparer.Ordinal);

    /// <summary>The clip currently bound for playback (may be null if nothing has been played yet).</summary>
    public AnimationClip? CurrentClip     { get; private set; }

    /// <summary>The name of the currently bound clip.</summary>
    public string?        CurrentClipName { get; private set; }

    /// <summary>
    /// Current absolute frame index within the source texture (not relative to
    /// <see cref="AnimationClip.StartFrame"/>).
    /// </summary>
    public int            CurrentFrame    { get; private set; }

    /// <summary>True while the animator is advancing frames.</summary>
    public bool           IsPlaying       { get; private set; }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fired once when a non-looping clip reaches its last frame.
    /// The string argument is the clip name.
    /// </summary>
    public event Action<string>? OnClipFinished;

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    private float   _elapsed;           // seconds accumulated since last frame advance
    private Action? _onCompleteOnce;    // callback registered by PlayOnce
    private bool    _paused;

    // -------------------------------------------------------------------------
    // Clip registration
    // -------------------------------------------------------------------------

    /// <summary>Register a clip. If a clip with the same name already exists it is replaced.</summary>
    public void AddClip(AnimationClip clip)
    {
        if (string.IsNullOrEmpty(clip.Name))
            throw new ArgumentException("AnimationClip.Name must not be empty.", nameof(clip));
        Clips[clip.Name] = clip;
    }

    // -------------------------------------------------------------------------
    // Playback control
    // -------------------------------------------------------------------------

    /// <summary>
    /// Begin playing the named clip from its first frame.
    /// If the clip is already active and <paramref name="restart"/> is false, the call is a no-op.
    /// </summary>
    public void Play(string name, bool restart = false)
    {
        if (!Clips.TryGetValue(name, out var clip))
            throw new KeyNotFoundException($"SpriteAnimator: no clip named '{name}'.");

        bool alreadyPlaying = CurrentClipName == name && IsPlaying;
        if (alreadyPlaying && !restart) return;

        CurrentClip     = clip;
        CurrentClipName = name;
        CurrentFrame    = clip.StartFrame;
        _elapsed        = 0f;
        IsPlaying       = true;
        _paused         = false;
        _onCompleteOnce = null;

        ApplyFrame();
    }

    /// <summary>
    /// Play the named clip exactly once (forces <see cref="AnimationClip.Loop"/> to false for
    /// this invocation) and invoke <paramref name="onComplete"/> when it finishes.
    /// </summary>
    public void PlayOnce(string name, Action? onComplete = null)
    {
        if (!Clips.TryGetValue(name, out var clip))
            throw new KeyNotFoundException($"SpriteAnimator: no clip named '{name}'.");

        // Temporarily treat the clip as non-looping for this play-through by
        // copying it so we don't mutate shared state.
        var oneShot = new AnimationClip
        {
            Name        = clip.Name,
            FrameWidth  = clip.FrameWidth,
            FrameHeight = clip.FrameHeight,
            StartFrame  = clip.StartFrame,
            EndFrame    = clip.EndFrame,
            Fps         = clip.Fps,
            Loop        = false
        };

        // Temporarily replace the clip in the dict so Play() picks it up.
        Clips[name]     = oneShot;
        _onCompleteOnce = onComplete;
        Play(name, restart: true);
        // Restore the original.
        Clips[name]     = clip;
    }

    /// <summary>Stop playback and reset to the first frame of the current clip.</summary>
    public void Stop()
    {
        IsPlaying       = false;
        _paused         = false;
        _elapsed        = 0f;
        _onCompleteOnce = null;
        if (CurrentClip != null)
        {
            CurrentFrame = CurrentClip.StartFrame;
            ApplyFrame();
        }
    }

    /// <summary>Freeze playback on the current frame without resetting elapsed time.</summary>
    public void Pause()
    {
        _paused = true;
    }

    /// <summary>Resume playback after a <see cref="Pause"/>.</summary>
    public void Resume()
    {
        if (IsPlaying) _paused = false;
    }

    // -------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------

    public override void Update(float dt)
    {
        if (!IsPlaying || _paused || CurrentClip == null) return;

        float frameDuration = CurrentClip.Fps > 0f ? 1f / CurrentClip.Fps : float.MaxValue;
        _elapsed += dt;

        while (_elapsed >= frameDuration)
        {
            _elapsed -= frameDuration;
            AdvanceFrame();

            // After AdvanceFrame IsPlaying may have been set to false for non-loop clips.
            if (!IsPlaying) break;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private void AdvanceFrame()
    {
        if (CurrentClip == null) return;

        int next = CurrentFrame + 1;

        if (next > CurrentClip.EndFrame)
        {
            if (CurrentClip.Loop)
            {
                CurrentFrame = CurrentClip.StartFrame;
            }
            else
            {
                // Clamp on the last frame and fire finish events.
                CurrentFrame = CurrentClip.EndFrame;
                IsPlaying    = false;

                string clipName = CurrentClipName ?? "";
                OnClipFinished?.Invoke(clipName);

                Action? cb = _onCompleteOnce;
                _onCompleteOnce = null;
                cb?.Invoke();

                ApplyFrame();
                return;
            }
        }
        else
        {
            CurrentFrame = next;
        }

        ApplyFrame();
    }

    /// <summary>
    /// Compute the <see cref="Rectangle"/> for <see cref="CurrentFrame"/> and push it
    /// to the sibling <see cref="SpriteRenderer"/>.
    /// </summary>
    private void ApplyFrame()
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr?.Texture == null || CurrentClip == null) return;

        int fw = CurrentClip.FrameWidth;
        int fh = CurrentClip.FrameHeight;
        if (fw <= 0 || fh <= 0) return;

        int columns = Math.Max(1, sr.Texture.Width / fw);
        int col     = CurrentFrame % columns;
        int row     = CurrentFrame / columns;

        sr.SourceRect = new Rectangle(col * fw, row * fh, fw, fh);
    }
}

namespace Floaty.Services;

/// <summary>
/// No-op <see cref="ISoundService"/> for platforms without an audio backend. Floaty stays silent;
/// the ring's shutter animation is unaffected, since that is pure UI.
/// </summary>
public sealed class NullSoundService : ISoundService
{
    public void Play(FloatySound sound) { }
}

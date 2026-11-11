using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>16.6 sounds. The .wav files are not in the project yet, so every sound is the documented fallback made
    /// in code: low noise crackle at the fire (3D), a 1 kHz on/off building alarm, a short hiss for a hot handle and
    /// white noise while spraying. Cough and fast breathing are subtitles (DebugOverlay) until the files arrive.</summary>
    public sealed class AudioFx : MonoBehaviour
    {
        private const int Rate = 22050;
        private RuntimeConfig.AudioSettings _cfg;
        private AudioSource _crackle, _alarm, _oneShot;
        private AudioClip _hiss, _spray;

        public bool AlarmOn => _alarm != null && _alarm.isPlaying;
        public bool CrackleOn => _crackle != null && _crackle.isPlaying;

        public void Init(Vector3 firePosition, RuntimeConfig rt)
        {
            _cfg = rt.Audio;
            _crackle = Source("Crackle", Noise(2f, brown: true), loop: true, spatial: true);
            _crackle.transform.position = firePosition + Vector3.up * 0.5f;
            _crackle.volume = _cfg.CrackleVolume;
            _alarm = Source("Alarm", Beep(_cfg.AlarmHz, _cfg.AlarmOnS, _cfg.AlarmOffS), loop: true, spatial: false);
            _oneShot = Source("OneShot", null, loop: false, spatial: false);
            _hiss = Noise(_cfg.HissS, brown: false, fadeOut: true);
            _spray = Noise(1f, brown: false);
        }

        public void SetFire(bool burning)
        {
            if (burning && !_crackle.isPlaying) _crackle.Play();
            if (!burning && _crackle.isPlaying) _crackle.Stop();
        }

        public void SetAlarm(bool on)
        {
            if (on && !_alarm.isPlaying) _alarm.Play();
            if (!on && _alarm.isPlaying) _alarm.Stop();
        }

        public void HotHandle() => _oneShot.PlayOneShot(_hiss, _cfg.Volume);
        public void Spray() => _oneShot.PlayOneShot(_spray, _cfg.SprayVolume);

        public void StopAll()
        {
            SetFire(false);
            SetAlarm(false);
            _oneShot.Stop();
        }

        private AudioSource Source(string name, AudioClip clip, bool loop, bool spatial)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = loop;
            s.playOnAwake = false;
            s.spatialBlend = spatial ? 1f : 0f;
            s.volume = _cfg.Volume;
            return s;
        }

        private static AudioClip Noise(float seconds, bool brown, bool fadeOut = false)
        {
            int n = Mathf.Max(1, (int)(seconds * Rate));
            var data = new float[n];
            var rnd = new System.Random(7);
            float last = 0f;
            for (int i = 0; i < n; i++)
            {
                float white = (float)(rnd.NextDouble() * 2 - 1);
                float v = brown ? (last = Mathf.Clamp(last + white * 0.05f, -1f, 1f)) * 2.5f : white * 0.5f;
                if (fadeOut) v *= 1f - (float)i / n;
                data[i] = Mathf.Clamp(v, -1f, 1f);
            }
            var clip = AudioClip.Create(brown ? "crackle" : "noise", n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip Beep(float hz, float onS, float offS)
        {
            int on = (int)(onS * Rate), n = on + (int)(offS * Rate);
            var data = new float[Mathf.Max(1, n)];
            for (int i = 0; i < on; i++) data[i] = Mathf.Sin(2 * Mathf.PI * hz * i / Rate) * 0.5f;
            var clip = AudioClip.Create("alarm", data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}

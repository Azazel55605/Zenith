#!/usr/bin/env python3
"""Renders the Zenith Chimes system sound pack (docs/design/sounds-concept.svg) from recipes into WAV files:
usr/share/zenith/sounds/chimes/EVENT.wav, mono 24 kHz 16-bit (the kernel plays them at 48 kHz stereo). Original and
reproducible: the same recipe always gives the same bytes. Usage: build-sounds.py DESTINATION"""
import math, random, struct, sys
from pathlib import Path

RATE = 24000
NOTE = {'B5': 987.77, 'F4': 349.23, 'B4': 493.88, 'A3': 220.0, 'D6': 1174.66, 'G3': 196.0, 'C4': 261.63, 'D4': 293.66, 'E4': 329.63, 'G4': 392.0, 'A4': 440.0, 'C5': 523.25, 'D5': 587.33,
        'E5': 659.25, 'G5': 783.99, 'A5': 880.0, 'C6': 1046.5, 'E6': 1318.5}
PEAK = 10 ** (-12 / 20)   # -12 dBFS


def env(t, attack, decay):
    return min(1.0, t / attack) * math.exp(-t / decay) if t >= 0 else 0.0


def bell(f, t, decay):
    return env(t, .005, decay) * (math.sin(2 * math.pi * f * t) + .35 * math.sin(2 * math.pi * 2 * f * t) + .15 * math.sin(2 * math.pi * 3 * f * t)) / 1.5


def glass(f, t):
    return env(t, .003, .09) * math.sin(2 * math.pi * f * t)


def marimba(f, t, seed):
    rnd = random.Random(seed + int(t * RATE))
    tick = env(t, .001, .006) * rnd.uniform(-1, 1) * .3
    return env(t, .004, .12) * math.sin(2 * math.pi * f * t) + tick


def soft_square(f, t):
    s = sum(math.sin(2 * math.pi * f * k * t) / k for k in (1, 3, 5))   # band-limited, low-passed
    return env(t, .006, .09) * s * .6


def pluck(f, t):
    return env(t, .004, .1) * (math.sin(2 * math.pi * f * t) + .2 * math.sin(2 * math.pi * 2 * f * t))


def chime(f, t, decay):
    """The plucked chime of the device sounds, longer: a soft 4 ms attack, the fundamental with a quiet octave and
    twelfth, and a second voice 0.3 % sharp that beats slowly against it, so a held note shimmers instead of beeping."""
    e = env(t, .004, decay)
    tone = math.sin(2 * math.pi * f * t) + .22 * math.sin(2 * math.pi * 2 * f * t) * math.exp(-t / (decay * .5)) + .06 * math.sin(2 * math.pi * 3 * f * t) * math.exp(-t / (decay * .3))
    return e * (tone + .35 * math.sin(2 * math.pi * f * 1.003 * t)) / 1.35


def pad(f, t, length):
    fade = max(0.0, 1 - t / length)
    s = sum(math.sin(2 * math.pi * f * k * t) / k for k in range(1, 6)) * .5
    return min(1.0, t / .08) * fade * fade * s


def noise(t, seed, decay, sweep):
    rnd = random.Random(seed * 100003 + int(t * RATE))
    return env(t, .004, decay) * rnd.uniform(-1, 1) * (1 - min(1, t * sweep))


def render(length, voice):
    n = int(length * RATE)
    samples = [voice(i / RATE) for i in range(n)]
    top = max(1e-9, max(abs(s) for s in samples))
    # Normalize to -12 dBFS, with a 5 ms fade out so nothing clicks.
    out = []
    for i, s in enumerate(samples):
        tail = min(1.0, (n - i) / (RATE * .005))
        out.append(int(round(s / top * PEAK * tail * 32767)))
    return out


def notes(seq, gap, length, voice):
    return render(length, lambda t: sum(voice(NOTE[note], t - k * gap) for k, note in enumerate(seq)))


SOUNDS = {
    'login': lambda: notes(['G4', 'C5', 'E5', 'G5'], .085, 1.2, lambda f, t: chime(f, t, .42)),
    'logout': lambda: notes(['G5', 'E5', 'C5', 'G4'], .1, 1.1, lambda f, t: chime(f, t, .36)),
    'notification': lambda: notes(['A5', 'E6'], .07, .32, glass),
    'attention': lambda: notes(['E5', 'E5'], .14, .36, lambda f, t: marimba(f, t, 7)),
    'error': lambda: notes(['C4', 'G3'], .11, .3, soft_square),
    'device-added': lambda: notes(['G4', 'D5'], .08, .28, pluck),
    'device-removed': lambda: notes(['D5', 'G4'], .08, .28, pluck),
    'done': lambda: notes(['E5', 'B5'], .07, .7, lambda f, t: chime(f, t, .24)),
    'volume': lambda: render(.04, lambda t: env(t, .001, .006) * math.sin(2 * math.pi * NOTE['A5'] * t)),
    'trash': lambda: notes(['A4', 'F4', 'D4'], .055, .5, lambda f, t: chime(f, t, .1)),
    'screenshot': lambda: notes(['E6', 'B5'], .045, .3, lambda f, t: chime(f, t, .06)),
    'shutdown': lambda: notes(['G5', 'E5', 'C5', 'G4', 'C4'], .16, 1.4, lambda f, t: chime(f, t, .55)),
}


def wav(samples):
    data = b''.join(struct.pack('<h', s) for s in samples)
    return (b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 1, 1, RATE, RATE * 2, 2, 16)
            + b'data' + struct.pack('<I', len(data)) + data)


def main(destination):
    target = Path(destination) / 'usr/share/zenith/sounds/chimes'
    target.mkdir(parents=True, exist_ok=True)
    for name, make in SOUNDS.items():
        (target / (name + '.wav')).write_bytes(wav(make()))
    (target / 'pack.tsv').write_text('ZENITH-SOUNDS\t1\nname\tZenith Chimes\n' + ''.join(f'event\t{n}\n' for n in SOUNDS))
    print(f'rendered {len(SOUNDS)} sounds into {target}')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '.')

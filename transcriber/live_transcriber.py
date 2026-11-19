"""RSP Audio Recorder - live transcription worker.

Follows the audio that pedro-audio-recorder-core.exe is writing and transcribes
it with faster-whisper while the recording is still running. The core writes
<name>.sources.wav (16 kHz, left = PC audio, right = microphone) next to the
mixed <name>.wav; each channel is transcribed separately so every line is
labelled with where it came from. (Without a sources file the mixed WAV is
used and lines carry no source.)

Started by the recorder app for every recording:

    python live_transcriber.py --wav "C:\\...\\audio-20261119-101500.wav"
                               --stop-event "Local\\RSPAudioStop_1234"
                               --parent-pid 1234

Output, next to the WAV (this is the transcript history the viewer reads):

  <name>.transcript.jsonl   append-only, one JSON object per line
      {"type": "meta", "audio": str, "started": iso8601}
      {"type": "model", "model": str, "device": str}
      {"type": "language", "language": "en", "probability": 0.98}
      {"type": "segment", "start": 12.3, "end": 15.9, "text": "...", "source": "pc" | "mic" | null}
      {"type": "done", "duration": 431.2}
      {"type": "error", "message": "..."}

  <name>.live.json          rewritten ~once per second while recording, deleted at the end
      {"status": "loading" | "listening" | "finishing" | "error",
       "message": str, "partials": {"pc": str, "mic": str}, "position": float, "updated": epoch}

Streaming approach (per source): audio after the last committed point is
re-transcribed about once a second; a word is committed once two consecutive
passes agree on it, and the uncommitted tail is published as the live line.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import sys
import time
import traceback
from datetime import datetime, timezone

import numpy as np

SR = 16000                 # whisper sample rate
TICK = 0.35                # seconds between file polls
MIN_NEW_AUDIO = 0.9        # re-transcribe after this much new audio
TRIM_AT = 12.0             # window longer than this: drop audio up to the last committed sentence
LANG_MIN_SPEECH = 4.0      # seconds of detected speech before trying to lock the language
LANG_MAX_SPEECH = 10.0     # after this much speech, lock the best guess even if unsure
HARD_LIMIT = 24.0          # window longer than this with no sentence end: cut at the commit point
IDLE_COMMIT = 1.6          # no new audio for this long (pause / silence): commit everything

DEBUG = os.environ.get("RSP_TRANSCRIBER_DEBUG") == "1"
LOG_DIR = os.path.join(os.environ.get("LOCALAPPDATA", "."), "RSP-Audio-Recorder")


def log(msg: str) -> None:
    line = f"{datetime.now():%Y-%m-%d %H:%M:%S} {msg}"
    try:
        sys.stderr.write(line + "\n")
        sys.stderr.flush()
    except Exception:
        pass
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        with open(os.path.join(LOG_DIR, "transcriber.log"), "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


# --------------------------------------------------------------------------- #
# Windows helpers
# --------------------------------------------------------------------------- #

SYNCHRONIZE = 0x00100000
_k32 = ctypes.WinDLL("kernel32", use_last_error=True) if os.name == "nt" else None


def open_event(name: str | None):
    if not name or not _k32:
        return None
    _k32.OpenEventW.restype = ctypes.c_void_p
    h = _k32.OpenEventW(SYNCHRONIZE, False, name)
    return h or None


def is_signaled(handle) -> bool:
    return bool(handle) and _k32.WaitForSingleObject(ctypes.c_void_p(handle), 0) == 0


def process_alive(pid: int | None) -> bool:
    if not pid or not _k32:
        return True
    _k32.OpenProcess.restype = ctypes.c_void_p
    h = _k32.OpenProcess(SYNCHRONIZE, False, pid)
    if not h:
        return False
    try:
        return _k32.WaitForSingleObject(ctypes.c_void_p(h), 0) != 0  # WAIT_TIMEOUT = still running
    finally:
        _k32.CloseHandle(ctypes.c_void_p(h))


def prepare_cuda_dlls() -> None:
    """ctranslate2 needs cuBLAS/cuDNN on Windows; torch's CUDA wheel ships them."""
    try:
        import torch

        lib = os.path.join(os.path.dirname(torch.__file__), "lib")
        if os.path.isdir(lib):
            os.add_dll_directory(lib)
            os.environ["PATH"] = lib + os.pathsep + os.environ.get("PATH", "")
    except Exception as exc:
        log(f"CUDA DLL setup skipped: {exc}")


def pick_device() -> str:
    try:
        import ctranslate2

        if ctranslate2.get_cuda_device_count() > 0:
            return "cuda"
    except Exception as exc:
        log(f"CUDA detection failed: {exc}")
    return "cpu"


# --------------------------------------------------------------------------- #
# Growing WAV reader (48 kHz stereo PCM16 from the recorder core -> 16 kHz mono)
# --------------------------------------------------------------------------- #

class WavTail:
    def __init__(self, path: str):
        self.path = path
        self.f = None
        self.rate = 48000
        self.channels = 2
        self.data_offset = 44
        self.pos = 0  # bytes of PCM consumed
        self.carry = b""
        self.taps = None
        self.hist = np.zeros(0, dtype=np.float32)
        self.phase = 0

    def _open(self) -> bool:
        if self.f:
            return True
        try:
            f = open(self.path, "rb")
        except OSError:
            return False
        head = f.read(44)
        if len(head) < 44 or head[:4] != b"RIFF" or head[8:12] != b"WAVE":
            f.close()
            return False
        self.channels = int.from_bytes(head[22:24], "little") or 2
        self.rate = int.from_bytes(head[24:28], "little") or 48000
        self.f = f
        self._design_filter()
        log(f"wav opened rate={self.rate} channels={self.channels}")
        return True

    def _design_filter(self) -> None:
        # Windowed-sinc low-pass at ~7.2 kHz before decimating to 16 kHz.
        self.step = self.rate / SR
        cutoff = 0.45 * SR / self.rate
        n = np.arange(-32, 33)
        taps = 2 * cutoff * np.sinc(2 * cutoff * n) * np.hamming(len(n))
        self.taps = (taps / taps.sum()).astype(np.float32)
        self.hist = np.zeros(len(self.taps) - 1, dtype=np.float32)

    def read(self) -> np.ndarray:
        """Newly written audio at 16 kHz, shape (samples, channels); possibly empty.

        16 kHz files (the sources file) are returned channel by channel; anything
        else (the mixed 48 kHz WAV) is mixed to mono and resampled: shape (n, 1).
        """
        empty = np.zeros((0, self.channels if self.rate == SR else 1), dtype=np.float32)
        if not self._open():
            return np.zeros((0, 1), dtype=np.float32)
        self.f.seek(self.data_offset + self.pos)
        new = self.f.read()
        if not new:
            return empty
        self.pos += len(new)
        raw = self.carry + new
        frame = 2 * self.channels
        usable = len(raw) // frame * frame
        self.carry = raw[usable:]
        pcm = np.frombuffer(raw[:usable], dtype=np.int16).reshape(-1, self.channels).astype(np.float32) / 32768.0
        if self.rate == SR:
            return pcm
        mono = pcm.mean(axis=1)
        # Filter with history so chunk boundaries are seamless.
        x = np.concatenate([self.hist, mono])
        y = np.convolve(x, self.taps, mode="valid")
        self.hist = x[-(len(self.taps) - 1):]
        if abs(self.step - round(self.step)) < 1e-6:
            k = int(round(self.step))
            out = y[self.phase::k]
            self.phase = (self.phase - len(y)) % k
        else:  # non-integer ratio (e.g. 44.1 kHz): linear interpolation
            idx = np.arange(self.phase, len(y) - 1, self.step)
            out = np.interp(idx, np.arange(len(y)), y).astype(np.float32)
            self.phase = (idx[-1] + self.step - len(y)) if len(idx) else self.phase - len(y)
        return out.astype(np.float32).reshape(-1, 1)

    def close(self) -> None:
        if self.f:
            self.f.close()
            self.f = None


# --------------------------------------------------------------------------- #
# Transcript writer
# --------------------------------------------------------------------------- #

class Output:
    def __init__(self, wav: str):
        stem = os.path.splitext(wav)[0]
        self.jsonl = stem + ".transcript.jsonl"
        self.live = stem + ".live.json"
        self.status = "loading"
        self.message = "Loading speech model…"
        self.partials: dict[str, str] = {}
        self.position = 0.0

    def append(self, **obj) -> None:
        with open(self.jsonl, "a", encoding="utf-8") as f:
            f.write(json.dumps(obj, ensure_ascii=False) + "\n")

    def publish(self, **changes) -> None:
        for k, v in changes.items():
            setattr(self, k, v)
        data = {"status": self.status, "message": self.message, "partials": self.partials,
                "position": round(self.position, 2), "updated": time.time()}
        tmp = self.live + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False)
            os.replace(tmp, self.live)
        except OSError as exc:  # viewer may be reading it; next tick retries
            log(f"live.json write skipped: {exc}")

    def set_partial(self, source: str | None, text: str) -> None:
        self.partials[source or "mixed"] = text

    def finish(self) -> None:
        try:
            os.remove(self.live)
        except OSError:
            pass


# --------------------------------------------------------------------------- #
# Streaming transcription
# --------------------------------------------------------------------------- #

_HALLUCINATIONS = re.compile(
    r"^(thank you\.?|thanks for watching!?|thank you for watching\.?|you|bye\.?|"
    r"subtitles by.*|gracias\.?|\.+|…)$", re.IGNORECASE)


def _norm(word: str) -> str:
    return re.sub(r"[^\w']", "", word.lower())


class W:
    """One recognised word with absolute session times."""
    __slots__ = ("start", "end", "text")

    def __init__(self, start: float, end: float, text: str):
        self.start, self.end, self.text = start, end, text


def words_from(segments, offset: float, limit: float) -> list[W]:
    """Flatten whisper segments to words, dropping likely hallucinations."""
    out: list[W] = []
    for s in segments:
        text = s.text.strip()
        if not text:
            continue
        if s.no_speech_prob > 0.6 and s.avg_logprob < -0.8:
            continue
        if s.compression_ratio > 2.4:  # repetition loop
            continue
        if _HALLUCINATIONS.match(text) and (s.end - s.start) < 2.5:
            continue
        for w in s.words or []:
            if not w.word.strip() or w.start > limit + 0.3:
                continue
            out.append(W(offset + w.start, offset + min(w.end, limit), w.word))
    return out


def join(words: list[W]) -> str:
    return "".join(w.text for w in words).strip()


class Language:
    """Language shared by all sources: locked once, from whichever source speaks first."""

    def __init__(self, value: str | None):
        self.value = value


class Streamer:
    """LocalAgreement streaming of one audio source: words are committed once two
    consecutive passes agree."""

    def __init__(self, model, out: Output, lang: Language, source: str | None):
        self.model = model
        self.out = out
        self.lang = lang
        self.source = source
        self.buf = np.zeros(0, dtype=np.float32)  # audio window being re-transcribed
        self.base = 0.0                           # session time of buf[0]
        self.unprocessed = 0.0                    # audio added since last pass
        self.wc: list[str] = []                   # committed words still inside the window (normalised)
        self.prev: list[W] = []                   # previous pass's uncommitted words
        self.line: list[W] = []                   # committed words not yet written as a segment
        self.tail: list[str] = []                 # last few committed words (normalised)
        self.history: list[str] = []              # last ~40 committed words (normalised)
        self.empty_passes = 0                     # consecutive passes that heard nothing
        self.context = ""                         # text already cut out of the window, used as prompt

    @property
    def duration(self) -> float:
        return self.base + len(self.buf) / SR

    def add(self, audio: np.ndarray) -> None:
        if len(audio):
            self.buf = np.concatenate([self.buf, audio])
            self.unprocessed += len(audio) / SR

    def due(self) -> bool:
        return self.unprocessed >= MIN_NEW_AUDIO

    def _detect_language(self) -> None:
        """Lock the language once there is enough actual speech to judge it.

        Detecting on a few seconds of room/mic noise gives confident-looking wrong
        answers (e.g. Norwegian for English), and a wrong lock makes whisper
        translate. So: collect VAD speech first, require a clear winner, and only
        settle for the best guess after plenty of speech.
        """
        if self.lang.value or len(self.buf) < SR * 2:
            return
        try:
            from faster_whisper.vad import VadOptions, get_speech_timestamps

            spans = get_speech_timestamps(self.buf, VadOptions(min_silence_duration_ms=300))
            speech = np.concatenate([self.buf[t["start"]:t["end"]] for t in spans]) if spans else np.zeros(0, np.float32)
            if len(speech) < SR * LANG_MIN_SPEECH:
                return
            lang, prob, _ = self.model.detect_language(speech[:SR * 30])
        except Exception as exc:
            log(f"language detection failed: {exc}")
            return
        enough = len(speech) >= SR * LANG_MAX_SPEECH
        if DEBUG:
            log(f"[{self.source}] language guess {lang} ({prob:.2f}) on {len(speech) / SR:.1f}s of speech")
        if prob >= 0.8 or enough:
            self.lang.value = lang
            self.out.append(type="language", language=lang, probability=round(float(prob), 3))
            log(f"language locked: {lang} ({prob:.2f}) after {len(speech) / SR:.1f}s of speech")

    def _flush_line(self) -> None:
        if not self.line:
            return
        text = join(self.line)
        if text:
            self.out.append(type="segment", start=round(self.line[0].start, 2),
                            end=round(self.line[-1].end, 2), text=text, source=self.source)
        self.line = []

    def _uncommitted(self, words: list[W]) -> list[W]:
        """Words of this pass that come after what is already committed.

        self.wc lists the committed words whose audio may still be in the window
        (after a cut, possibly only the end of that list is still audible). Word
        timings drift between passes, so they are matched by text, allowing up to
        two leading fragment words.
        """
        wc = self.wc
        if not wc:
            # Drop a leading fragment that repeats the last committed word.
            if words and self.tail and _norm(words[0].text) == self.tail[-1] and words[0].start < self.base + 1.0:
                return words[1:]
            return words

        def run(j: int, k: int) -> int:
            m = 0
            while k + m < len(wc) and j + m < len(words) and _norm(words[j + m].text) == wc[k + m]:
                m += 1
            return m

        # Best: some suffix wc[k:] appears in full at the start of this pass.
        best = None
        for k in range(len(wc)):
            for j in range(3):
                m = run(j, k)
                if m == len(wc) - k and m > 0 and (best is None or m > best[2]):
                    best = (j, k, m)
        if best:
            return words[best[0] + best[2]:]
        # Partial agreement (whisper re-worded a committed word): skip the same count.
        j, m = max(((j, run(j, 0)) for j in range(3)), key=lambda t: t[1])
        if m > 0:
            return words[j + len(wc):]
        # None of the committed words were heard again (whisper skipped the clipped
        # tail before the cut), so everything in this pass is new. Timing is no help
        # here: whisper places the first word at the very start of the window.
        return words

    def _commit(self, words: list[W]) -> None:
        for w in words:
            self.line.append(w)
            self.wc.append(_norm(w.text))
            self.history = (self.history + [_norm(w.text)])[-40:]
            self.tail = (self.tail + [_norm(w.text)])[-3:]
            # Write a segment at each sentence end (or every ~25 words).
            if w.text.rstrip().endswith((".", "?", "!")) or len(self.line) >= 25:
                self._flush_line()

    def _drop_repeat(self, new: list[W]) -> list[W]:
        """Drop a prefix of `new` that repeats the most recent committed words.

        With only a scrap of the last sentence left in the window, whisper tends to
        echo that sentence back from its prompt. A run of 3+ words identical to the
        end of the transcript (or all of `new`, if shorter) is that echo.
        """
        hist = self.history
        for k in range(min(len(new), len(hist)), 0, -1):
            if k < 3 and k != len(new):
                break
            if [_norm(w.text) for w in new[:k]] == hist[-k:] and (k >= 3 or k == len(new) >= 2):
                if DEBUG:
                    log(f"[{self.source}] dropped repeated words: {join(new[:k])}")
                return new[k:]
        return new

    def _remember(self, words: list[W]) -> None:
        """Words leaving the window become prompt context. (Text that is still in the
        window must not be in the prompt: whisper then skips it as already said.)"""
        text = join(words)
        if text:
            self.context = (self.context + " " + text)[-220:]

    def _trim(self, to: float) -> None:
        cut = int(max(0.0, min(to - self.base, len(self.buf) / SR)) * SR)
        if cut > 0:
            self.buf = self.buf[cut:]
            self.base += cut / SR

    def step(self, final: bool = False) -> None:
        """Re-transcribe the window, commit agreed words, publish the rest as the live line."""
        self.unprocessed = 0.0
        window = len(self.buf) / SR
        if window < 0.4:
            self.out.set_partial(self.source, "")
            return

        self._detect_language()
        segments, _ = self.model.transcribe(
            self.buf,
            language=self.lang.value,
            beam_size=5 if final else 1,
            vad_filter=True,
            vad_parameters={"min_silence_duration_ms": 400},
            condition_on_previous_text=False,
            initial_prompt=self.context or None,
            word_timestamps=True,
            temperature=0.0,
        )
        words = words_from(segments, self.base, window)
        new = self._drop_repeat(self._uncommitted(words))
        first_new = len(words) - len(new)
        if DEBUG:
            log(f"[{self.source}] pass base={self.base:.2f} win={window:.2f} wc={self.wc} "
                f"words={[w.text.strip() for w in words]} new={[w.text.strip() for w in new]} "
                f"prev={[w.text.strip() for w in self.prev]}")

        if final:
            self._commit(new)
            self._flush_line()
            self._remember(words)
            self.prev, self.wc = [], []
            self._trim(self.duration)
            self.out.set_partial(self.source, "")
            return

        # LocalAgreement: commit the longest common prefix with the previous pass.
        if not words:
            # An empty pass is either silence or a one-off miss (whisper sometimes
            # filters out a whole segment). Keep the pending words; only after two
            # empty passes in a row treat it as silence: keep what was last heard,
            # then drop all but a short tail of audio.
            self.empty_passes += 1
            if self.empty_passes >= 2 and window > 3.0:
                if self.prev and self.lang.value is not None:
                    self._commit(self.prev)
                self._flush_line()
                self._remember(self.prev)
                self._trim(self.duration - 1.0)
                self.wc, self.prev = [], []
            self.out.set_partial(self.source, join(self.line + self.prev))
            return
        self.empty_passes = 0

        agreed = 0
        while (agreed < len(new) and agreed < len(self.prev)
               and _norm(new[agreed].text) == _norm(self.prev[agreed].text)):
            agreed += 1
        if self.lang.value is None:
            agreed = 0  # show it live, but commit nothing until the language is known
        self._commit(new[:agreed])
        self.prev = new[agreed:]
        committed_upto = first_new + agreed  # words[:committed_upto] are committed

        if window > TRIM_AT:
            # Keep the window short: cut after the last committed sentence, or after
            # the last committed word when one sentence runs too long. Cut in the gap
            # before the next word, leaving ~0.6 s of lead-in (whisper tends to drop
            # a word that starts at the very first sample).
            def cut_after(i: int) -> float:
                end = words[i].end
                if i + 1 < len(words):
                    end = min(end, words[i + 1].start)
                return end - 0.6

            ends = [i for i in range(committed_upto)
                    if words[i].text.rstrip().endswith((".", "?", "!"))]
            if ends:
                i = ends[-1]
                cut = cut_after(i)
                self._remember(words[:i + 1])
                self._trim(cut)
                # Committed words that (partly) remain after the cut, lead-in included.
                self.wc = [_norm(w.text) for w in words[:committed_upto] if w.end > cut]
            elif window > HARD_LIMIT and committed_upto > 0:
                self._flush_line()
                cut = cut_after(committed_upto - 1)
                self._remember(words[:committed_upto])
                self._trim(cut)
                self.wc = [_norm(w.text) for w in words[:committed_upto] if w.end > cut]

        self.out.set_partial(self.source, join(self.line + self.prev))


# --------------------------------------------------------------------------- #
# Main loop
# --------------------------------------------------------------------------- #

def load_model(name: str, device: str):
    from faster_whisper import WhisperModel

    compute = "float16" if device == "cuda" else "int8"
    try:
        return WhisperModel(name, device=device, compute_type=compute, local_files_only=True)
    except Exception:
        log(f"model '{name}' not cached; downloading")
        return WhisperModel(name, device=device, compute_type=compute)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--wav", required=True)
    ap.add_argument("--stop-event")
    ap.add_argument("--parent-pid", type=int)
    ap.add_argument("--model", default=os.environ.get("RSP_WHISPER_MODEL", ""))
    ap.add_argument("--language", default=os.environ.get("RSP_WHISPER_LANGUAGE", ""))
    args = ap.parse_args()

    out = Output(args.wav)
    out.publish()
    # Written first so the viewer lists the recording while the model loads.
    out.append(type="meta", audio=os.path.basename(args.wav),
               started=datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"))
    log(f"start wav={args.wav}")
    stop = open_event(args.stop_event)

    try:
        prepare_cuda_dlls()
        device = pick_device()
        model_name = args.model or ("large-v3" if device == "cuda" else "small")
        t0 = time.time()
        try:
            model = load_model(model_name, device)
        except Exception as exc:
            if device != "cuda":
                raise
            log(f"GPU load failed ({exc}); falling back to CPU")
            device, model_name = "cpu", "small"
            model = load_model(model_name, device)
        log(f"model {model_name} on {device} loaded in {time.time() - t0:.1f}s")
        out.append(type="model", model=model_name, device=device)
        if args.language:
            out.append(type="language", language=args.language, probability=1.0)

        # Prefer the per-source file; the core creates it right after the main WAV.
        sources = os.path.splitext(args.wav)[0] + ".sources.wav"
        for _ in range(30):
            if os.path.exists(sources):
                break
            time.sleep(0.1)
        if os.path.exists(sources):
            tail = WavTail(sources)
            labels: list[str | None] = ["pc", "mic"]
        else:
            log("no sources file; transcribing the mixed recording")
            tail = WavTail(args.wav)
            labels = [None]
        lang = Language(args.language or None)
        streamers = [Streamer(model, out, lang, label) for label in labels]
        out.publish(status="listening", message="Listening")
        last_audio = time.time()
        committed_idle = True

        def position() -> float:
            return max(st.duration for st in streamers)

        while True:
            stopping = is_signaled(stop) or not process_alive(args.parent_pid)
            audio = tail.read()
            if len(audio):
                for i, st in enumerate(streamers):
                    st.add(audio[:, min(i, audio.shape[1] - 1)])
                last_audio = time.time()
                committed_idle = False

            if stopping:
                # Core finalises the files within a moment of the stop event.
                out.publish(status="finishing", message="Finishing transcript")
                time.sleep(0.6)
                audio = tail.read()
                for i, st in enumerate(streamers):
                    if len(audio):
                        st.add(audio[:, min(i, audio.shape[1] - 1)])
                    st.step(final=True)
                break

            idle = time.time() - last_audio > IDLE_COMMIT
            if idle and not committed_idle:
                for st in streamers:
                    st.step(final=True)  # paused: settle everything
                committed_idle = True
            else:
                for st in streamers:
                    if st.due():
                        st.step()
            out.publish(position=position())
            time.sleep(TICK)

        out.append(type="done", duration=round(position(), 2))
        tail.close()
        log(f"done duration={position():.1f}s")
        if labels != [None]:
            try:
                os.remove(sources)  # only needed while transcribing
            except OSError as exc:
                log(f"could not remove sources file: {exc}")
        return 0
    except Exception as exc:
        log("ERROR " + traceback.format_exc())
        out.append(type="error", message=str(exc))
        out.publish(status="error", message=str(exc))
        time.sleep(2)
        return 1
    finally:
        out.finish()


if __name__ == "__main__":
    sys.exit(main())

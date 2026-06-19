export type SpeechVoiceConfig = {
  lang: string;
  voiceName: string;
  voiceNameIncludes: string;
  voiceLang: string;
  preferLocalService: boolean;
  rate: number;
  pitch: number;
  volume: number;
};

export const finalResultSpeechConfig: SpeechVoiceConfig = {
  // Dev note: open DevTools Console and run:
  // speechSynthesis.getVoices().map(v => ({ name: v.name, lang: v.lang, local: v.localService, default: v.default }))
  // Then paste a matching voice name into voiceName below.
  lang: "vi-VN",
  voiceName: "",
  voiceNameIncludes: "",
  voiceLang: "vi-VN",
  preferLocalService: false,
  rate: 0.92,
  pitch: 1,
  volume: 1,
};

function clampSpeechValue(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

export function getAvailableSpeechVoices() {
  if (typeof window === "undefined" || !("speechSynthesis" in window)) {
    return [];
  }

  return window.speechSynthesis.getVoices();
}

export function logAvailableSpeechVoices() {
  const voices = getAvailableSpeechVoices();
  const rows = voices.map((voice) => ({
    name: voice.name,
    lang: voice.lang,
    local: voice.localService,
    default: voice.default,
  }));

  console.table(rows);
  return rows;
}

function getConfiguredSpeechVoice(config: SpeechVoiceConfig) {
  const voices = getAvailableSpeechVoices();
  const normalizedName = config.voiceName.trim().toLowerCase();
  const normalizedNameIncludes = config.voiceNameIncludes.trim().toLowerCase();
  const normalizedLang = config.voiceLang.trim().toLowerCase();

  if (normalizedName) {
    const exactVoice = voices.find((voice) => voice.name.toLowerCase() === normalizedName);
    if (exactVoice) return exactVoice;
  }

  if (normalizedNameIncludes) {
    const partialVoice = voices.find((voice) =>
      voice.name.toLowerCase().includes(normalizedNameIncludes),
    );
    if (partialVoice) return partialVoice;
  }

  const languageVoices = normalizedLang
    ? voices.filter((voice) => voice.lang.toLowerCase().startsWith(normalizedLang))
    : [];
  if (languageVoices.length === 0) {
    return null;
  }

  if (config.preferLocalService) {
    return languageVoices.find((voice) => voice.localService) ?? languageVoices[0];
  }

  return languageVoices[0];
}

export function applyFinalResultSpeechConfig(utterance: SpeechSynthesisUtterance) {
  const config = finalResultSpeechConfig;
  const voice = getConfiguredSpeechVoice(config);

  utterance.lang = voice?.lang ?? config.lang;
  utterance.voice = voice;
  utterance.rate = clampSpeechValue(config.rate, 0.1, 10);
  utterance.pitch = clampSpeechValue(config.pitch, 0, 2);
  utterance.volume = clampSpeechValue(config.volume, 0, 1);
}

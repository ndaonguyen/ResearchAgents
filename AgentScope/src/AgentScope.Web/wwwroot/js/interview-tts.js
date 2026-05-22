// Thin wrapper over the browser's SpeechSynthesis API for the interview page.
// Exposed as an ES module so Blazor can `JS.InvokeAsync("import", "./js/interview-tts.js")`.
//
// Notes:
// - Browsers gate autoplay until the user has interacted with the page; the Start
//   button on the interview page provides that interaction, so subsequent speak()
//   calls within the same tab session work without further user gestures.
// - When a new speak() is requested while one is in progress, we cancel the previous
//   one so the latest turn isn't queued behind a stale read.
// - Returns booleans so the caller can decide whether to surface "speech unavailable"
//   in the UI, instead of silently no-oping.

export function isAvailable() {
    return typeof window !== "undefined" && "speechSynthesis" in window;
}

export function speak(text) {
    if (!isAvailable() || !text) return false;
    if (window.speechSynthesis.speaking || window.speechSynthesis.pending) {
        window.speechSynthesis.cancel();
    }
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.rate = 1.0;
    utterance.pitch = 1.0;
    utterance.lang = "en-US";
    window.speechSynthesis.speak(utterance);
    return true;
}

export function cancel() {
    if (isAvailable()) {
        window.speechSynthesis.cancel();
    }
}

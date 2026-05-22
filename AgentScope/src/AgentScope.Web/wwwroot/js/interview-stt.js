// Browser SpeechRecognition wrapper for the interview page.
// Continuous + interim-results mode so the user sees their speech transcribed live
// in the answer field while they're still talking.
//
// Browser support:
// - Chrome/Edge: works (audio is sent to Google's cloud STT under the hood — privacy note)
// - Safari: works (Apple's STT)
// - Firefox: experimental, often unavailable
// Hide the mic button when isAvailable() returns false.

let recognition = null;
let dotnetRef = null;

export function isAvailable() {
    return typeof window !== "undefined"
        && !!(window.SpeechRecognition || window.webkitSpeechRecognition);
}

export function startListening(ref) {
    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Recognition) return false;

    // Stop any previous session before starting a new one.
    if (recognition) {
        try { recognition.stop(); } catch (_) { /* ignore */ }
    }

    dotnetRef = ref;
    recognition = new Recognition();
    recognition.continuous = true;
    recognition.interimResults = true;
    recognition.lang = "en-US";

    recognition.onresult = (event) => {
        let interim = "";
        let final = "";
        for (let i = event.resultIndex; i < event.results.length; i++) {
            const r = event.results[i];
            if (r.isFinal) final += r[0].transcript;
            else interim += r[0].transcript;
        }
        // Push both pieces back to Blazor; the page composes them into the textarea.
        if (dotnetRef) {
            dotnetRef.invokeMethodAsync("OnTranscript", final, interim).catch(() => {});
        }
    };

    recognition.onerror = (event) => {
        if (dotnetRef) {
            dotnetRef.invokeMethodAsync("OnRecognitionError", event.error || "unknown").catch(() => {});
        }
    };

    recognition.onend = () => {
        if (dotnetRef) {
            dotnetRef.invokeMethodAsync("OnRecognitionEnd").catch(() => {});
        }
    };

    try {
        recognition.start();
        return true;
    } catch (err) {
        // Some browsers throw if start() is called twice in quick succession.
        return false;
    }
}

export function stopListening() {
    if (recognition) {
        try { recognition.stop(); } catch (_) { /* ignore */ }
    }
}

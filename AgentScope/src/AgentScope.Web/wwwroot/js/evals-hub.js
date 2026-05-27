// SignalR client for the /evals page. Mirrors agent-hub.js but exposes:
//   - subscribeToAll() / unsubscribeFromAll() — keeps the job list live.
//   - subscribeToJob(id) / unsubscribeFromJob(id) — focused progress for one job.
//   - cancelJob(id) — invokes the hub's CancelEval method.
//
// Server pushes "eval.progress" and "eval.status" — both are forwarded into the
// Blazor component via the OnEvalEvent JSInvokable so the page can re-render.

let connection = null;
let dotnetRef = null;

export async function init(ref) {
    dotnetRef = ref;

    connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/agents")
        .withAutomaticReconnect()
        .build();

    connection.on("eval.progress", async (payload) => {
        try { await dotnetRef.invokeMethodAsync("OnEvalEvent", "progress", payload); }
        catch (err) { console.error("OnEvalEvent(progress) failed:", err); }
    });

    connection.on("eval.status", async (payload) => {
        try { await dotnetRef.invokeMethodAsync("OnEvalEvent", "status", payload); }
        catch (err) { console.error("OnEvalEvent(status) failed:", err); }
    });

    await connection.start();
    await connection.invoke("SubscribeToAllEvals");
}

export async function subscribeToJob(jobId) {
    if (!connection) return;
    try { await connection.invoke("SubscribeToEval", jobId); }
    catch (err) { console.error("SubscribeToEval failed:", err); }
}

export async function unsubscribeFromJob(jobId) {
    if (!connection) return;
    try { await connection.invoke("UnsubscribeFromEval", jobId); }
    catch (err) { console.error("UnsubscribeFromEval failed:", err); }
}

export async function cancelJob(jobId) {
    if (!connection) return;
    try { return await connection.invoke("CancelEval", jobId); }
    catch (err) { console.error("CancelEval failed:", err); return false; }
}

export async function dispose() {
    if (!connection) return;
    try { await connection.invoke("UnsubscribeFromAllEvals"); } catch { /* circuit closing */ }
    try { await connection.stop(); } catch { /* already stopped */ }
    connection = null;
    dotnetRef = null;
}

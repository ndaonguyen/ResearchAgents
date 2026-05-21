// Manages the SignalR connection to /hubs/agents and pushes incoming events
// back into the Blazor component via the OnEvent JSInvokable method.

let connection = null;
let dotnetRef = null;

export async function init(ref) {
    dotnetRef = ref;

    connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/agents")
        .withAutomaticReconnect()
        .build();

    connection.on("event", async (kind, payload) => {
        try {
            await dotnetRef.invokeMethodAsync("OnEvent", kind, payload);
        } catch (err) {
            console.error("OnEvent failed:", err);
        }
    });

    await connection.start();
}

export async function ask(question) {
    if (!connection) {
        console.warn("Hub connection not ready");
        return;
    }
    try {
        await connection.invoke("Ask", question);
    } catch (err) {
        console.error("Ask failed:", err);
    }
}

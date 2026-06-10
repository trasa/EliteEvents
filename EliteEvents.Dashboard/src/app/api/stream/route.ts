import { createSubscriber, EVENTS_CHANNEL } from "@/lib/redis";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Server-Sent Events: subscribe to the Redis channel your C# handler publishes to,
// and relay each message to the browser. One subscriber connection per client.
export async function GET(request: Request) {
  const encoder = new TextEncoder();
  const sub = createSubscriber();
  let closed = false;

  const stream = new ReadableStream<Uint8Array>({
    async start(controller) {
      const send = (data: string) => {
        if (closed) return;
        try {
          controller.enqueue(encoder.encode(data));
        } catch {
          /* controller already closed */
        }
      };

      send(": connected\n\n");

      await sub.subscribe(EVENTS_CHANNEL);
      sub.on("message", (_channel, message) => send(`data: ${message}\n\n`));

      // Heartbeat keeps intermediaries (Caddy, browsers) from dropping an idle stream.
      const heartbeat = setInterval(() => send(": ping\n\n"), 25000);

      const close = async () => {
        if (closed) return;
        closed = true;
        clearInterval(heartbeat);
        try {
          await sub.unsubscribe(EVENTS_CHANNEL);
        } catch {}
        try {
          await sub.quit();
        } catch {}
        try {
          controller.close();
        } catch {}
      };

      request.signal.addEventListener("abort", close);
    },
    cancel() {
      closed = true;
      sub.quit().catch(() => {});
    },
  });

  return new Response(stream, {
    headers: {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache, no-transform",
      Connection: "keep-alive",
    },
  });
}

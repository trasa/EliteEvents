"use client";

import { useEffect, useState } from "react";
import type { LiveEvent } from "@/lib/types";

export default function LiveTicker() {
  const [events, setEvents] = useState<LiveEvent[]>([]);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const es = new EventSource("/api/stream");
    es.onopen = () => setConnected(true);
    es.onerror = () => setConnected(false);
    es.onmessage = (e) => {
      try {
        const evt = JSON.parse(e.data) as LiveEvent;
        setEvents((prev) => [evt, ...prev].slice(0, 40));
      } catch {
        /* ignore malformed frames */
      }
    };
    return () => es.close();
  }, []);

  return (
    <section className="panel">
      <h2>
        Live Feed
        <span style={{ fontSize: "0.7rem", letterSpacing: 0 }} className="muted">
          <span className={`dot ${connected ? "on" : "off"}`} />
          {connected ? "connected" : "offline"}
        </span>
      </h2>

      {!events.length && (
        <p className="empty">
          Waiting for events&hellip; (the C# handler publishes to <code>eddn:events</code>)
        </p>
      )}

      <ul className="ticker">
        {events.map((evt, i) => (
          <li key={`${evt.ts}-${i}`}>
            <span className={`tag ${evt.type === "fsdjump" ? "jump" : ""}`}>
              {evt.type === "fsdjump" ? "jump" : evt.type}
            </span>
            <span>
              {evt.system ?? "?"}
              {evt.station ? <span className="muted"> &middot; {evt.station}</span> : null}
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}

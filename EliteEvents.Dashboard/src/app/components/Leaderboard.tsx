"use client";

import { useEffect, useState, useCallback } from "react";
import Link from "next/link";
import type { VisitedSystem } from "@/lib/types";

export default function Leaderboard() {
  const [systems, setSystems] = useState<VisitedSystem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const res = await fetch("/api/most-visited?limit=25", { cache: "no-store" });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = (await res.json()) as { systems: VisitedSystem[] };
      setSystems(data.systems ?? []);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "failed to load");
    } finally {
      setLoading(false);
    }
  }, []);

  // Refresh every 15s so the leaderboard tracks the live data.
  useEffect(() => {
    load();
    const id = setInterval(load, 15000);
    return () => clearInterval(id);
  }, [load]);

  return (
    <section className="panel">
      <h2>
        Most-Visited Systems
        <span className="muted" style={{ fontSize: "0.7rem", letterSpacing: 0 }}>
          this week
        </span>
      </h2>

      {error && <p className="empty">Can&apos;t reach the data feed ({error}).</p>}
      {loading && !systems.length && <p className="empty">Loading&hellip;</p>}
      {!loading && !error && !systems.length && (
        <p className="empty">No visits recorded yet.</p>
      )}

      {systems.length > 0 && (
        <table>
          <thead>
            <tr>
              <th className="rank">#</th>
              <th>System</th>
              <th className="num">Visits</th>
            </tr>
          </thead>
          <tbody>
            {systems.map((s, i) => (
              <tr key={s.system}>
                <td className="rank">{i + 1}</td>
                <td>
                  <Link href={`/system/${encodeURIComponent(s.system)}`}>{s.system}</Link>
                </td>
                <td className="num">{s.visits.toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

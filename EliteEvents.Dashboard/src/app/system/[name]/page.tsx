import Link from "next/link";
import { getSystemStations } from "@/lib/queries";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Server Component: reads Redis directly via the shared query lib (no self-HTTP),
// contrasting with the client-fetched leaderboard on the home page.
export default async function SystemPage({
  params,
}: {
  params: Promise<{ name: string }>;
}) {
  const { name } = await params;
  const system = decodeURIComponent(name).toUpperCase();
  const stations = await getSystemStations(system);

  return (
    <div className="wrap">
      <header className="site">
        <h1>{system}</h1>
        <p className="back">
          <Link href="/">&larr; back to leaderboard</Link>
        </p>
      </header>

      <section className="panel">
        <h2>Station Docking Activity</h2>
        {!stations.length ? (
          <p className="empty">No docking activity recorded for this system.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Station</th>
                <th>Type</th>
                <th className="num">Dockings</th>
                <th className="num">Last Seen</th>
              </tr>
            </thead>
            <tbody>
              {stations.map((s) => (
                <tr key={s.station}>
                  <td>{s.station}</td>
                  <td className="muted">{s.type}</td>
                  <td className="num">{s.count.toLocaleString()}</td>
                  <td className="num muted">
                    {s.lastSeen
                      ? new Date(s.lastSeen * 1000).toISOString().slice(0, 16).replace("T", " ")
                      : "\u2014"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}

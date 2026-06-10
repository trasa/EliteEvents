import { getRedis } from "./redis";
import type { VisitedSystem, StationDocking, CarrierDay } from "./types";

// systems:visits -> sorted set, member = SYSTEM (uppercased by the C# writer), score = visit count.
export async function getMostVisited(limit = 25): Promise<VisitedSystem[]> {
  const r = getRedis();
  const flat = await r.zrevrange("systems:visits", 0, limit - 1, "WITHSCORES");
  const out: VisitedSystem[] = [];
  for (let i = 0; i < flat.length; i += 2) {
    out.push({ system: flat[i], visits: Number(flat[i + 1]) });
  }
  return out;
}

// system:{SYSTEM}:stations        -> sorted set (member = station, score = last_seen)
// system:{SYSTEM}:station:{station} -> hash { count, type, last_seen }
export async function getSystemStations(systemName: string): Promise<StationDocking[]> {
  const r = getRedis();
  const system = systemName.toUpperCase();
  const stations = await r.zrevrange(`system:${system}:stations`, 0, -1);
  if (stations.length === 0) return [];

  const pipeline = r.pipeline();
  for (const s of stations) pipeline.hgetall(`system:${system}:station:${s}`);
  const results = await pipeline.exec();

  const out: StationDocking[] = [];
  results?.forEach(([err, data], idx) => {
    if (err || !data) return;
    const h = data as Record<string, string>;
    if (!h.count) return;
    out.push({
      station: stations[idx],
      type: h.type ?? "Unknown",
      count: Number(h.count ?? 0),
      lastSeen: Number(h.last_seen ?? 0),
    });
  });
  return out;
}

// carrier:{ID}:days        -> sorted set (member = date, score = unix seconds)
// carrier:{ID}:daily:{date} -> string counter
export async function getCarrierActivity(carrierId: string, days = 30): Promise<CarrierDay[]> {
  const r = getRedis();
  const id = carrierId.toUpperCase();
  const dates = await r.zrevrange(`carrier:${id}:days`, 0, days - 1);
  if (dates.length === 0) return [];

  const keys = dates.map((d) => `carrier:${id}:daily:${d}`);
  const counts = await r.mget(...keys);
  return dates.map((date, i) => ({ date, dockings: Number(counts[i] ?? 0) }));
}
